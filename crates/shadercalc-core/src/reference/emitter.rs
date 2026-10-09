use std::collections::HashMap;

use crate::binding::bound_tree::*;
use crate::binding::program::BoundProgram;
use crate::binding::symbols::{FunctionRef, VariableKind, VariableRef};
use crate::binding::type_rules;
use crate::evaluation::evaluator::try_evaluate_constant;
use crate::semantics::SemanticsProfile;
use crate::syntax::tree::ParameterMode;
use crate::trace::{TracePoint, TracePointKind};
use crate::types::{ScalarKind, ShaderType};
use crate::values::{Value, format_double, format_float, scalars};

/// Turns a line's literals into input-buffer loads (the reference harness), so DXC can't fold them.
pub struct Harness {
    /// Only the line's own code gets its literals fed through the buffer, never function bodies.
    pub is_emitting_line: bool,
    /// The words the shader reads from its input buffer.
    pub inputs: Vec<u32>,
    pub trace: Option<HarnessTrace>,
}

/// Traced code (a line, or a call's body): its trace points by statement node, and where the shader writes their
/// values (in execution order from output word `base`, `capacity` words at most: what the interpreter recorded;
/// `refTraceCursor` counts). Stores are emitted while `is_active`, and only run when `guard` (an HLSL bool) holds.
pub struct HarnessTrace {
    pub ids: HashMap<*const BoundStatement, usize>,
    pub points: Vec<TracePoint>,
    pub base: usize,
    pub capacity: usize,
    pub is_active: bool,
    pub guard: Option<String>,
}

/// HLSL storing one component in the output buffer at `index` (an HLSL expression), and the words it takes.
pub fn store_component(kind: ScalarKind, path: &str, index: &str) -> (String, usize) {
    match kind {
        ScalarKind::Bool => (format!("RefOutput[{index}] = {path} ? 1u : 0u;"), 1),
        ScalarKind::UInt => (format!("RefOutput[{index}] = {path};"), 1),
        ScalarKind::Double => (
            format!(
                "{{ uint low, high; asuint({path}, low, high); RefOutput[{index}] = low; RefOutput[{index} + 1] = high; }}"
            ),
            2,
        ),
        ScalarKind::Int64 | ScalarKind::UInt64 => (
            format!("RefOutput[{index}] = (uint)({path}); RefOutput[{index} + 1] = (uint)((uint64_t)({path}) >> 32);"),
            2,
        ),
        _ => (format!("RefOutput[{index}] = asuint({path});"), 1),
    }
}

/// Writes the bound tree back as plain HLSL that DXC accepts: C++ forms come out normalised (references become
/// in/inout, auto its type, std:: gone), macros expanded, unit literals as SI numbers. Implicit conversions are
/// left to DXC, so the reference also checks the interpreter's typing.
pub struct HlslEmitter {
    pub text: String,
    pub harness: Option<Harness>,
    /// Set when the tree holds something that can't be written as HLSL (an expression with errors).
    pub failure: Option<String>,
    /// Calls emitted to another function (by node): its name and one more argument (a traced copy and its switch).
    pub redirects: HashMap<*const BoundExpression, (String, String)>,
}

/// `float3 name`, `float name[2][3]`.
pub fn declaration(ty: &ShaderType, name: &str) -> String {
    match ty {
        ShaderType::Array(array) => format!("{} {}{}", array.innermost_element(), name, array.dimensions()),
        _ => format!("{ty} {name}"),
    }
}

fn cast_type(ty: &ShaderType) -> String {
    match ty {
        ShaderType::Array(array) => format!("{}{}", array.innermost_element(), array.dimensions()),
        _ => ty.to_string(),
    }
}

fn unary_symbol(operation: UnaryOperator) -> &'static str {
    match operation {
        UnaryOperator::Negate => "-",
        UnaryOperator::LogicalNot => "!",
        UnaryOperator::BitwiseNot => "~",
        UnaryOperator::Plus => "+",
    }
}

/// A literal as HLSL source, of its exact kind.
pub fn literal(value: &Value) -> String {
    let bits: u64 = value.bits[0];
    match value.kind_at(0) {
        ScalarKind::Bool => (if bits != 0 { "true" } else { "false" }).to_string(),
        ScalarKind::LiteralInt => (bits as i64).to_string(),
        ScalarKind::Int => (bits as i32).to_string(),
        ScalarKind::UInt => format!("{}u", bits as u32),
        ScalarKind::Int64 => format!("{}ll", bits as i64),
        ScalarKind::UInt64 => format!("{bits}ull"),
        ScalarKind::LiteralFloat => real_literal(scalars::to_double(bits)),
        ScalarKind::Double => real_literal(scalars::to_double(bits)) + "L",
        ScalarKind::Half | ScalarKind::Float => float_literal(scalars::to_float(bits)),
    }
}

fn with_point(text: String) -> String {
    if text.contains('.') || text.contains('E') { text } else { text + ".0" }
}

fn real_literal(value: f64) -> String {
    with_point(format_double(value))
}

fn float_literal(value: f32) -> String {
    if !value.is_finite() {
        return format!("asfloat(0x{:08X}u)", value.to_bits());
    }
    with_point(format_float(value)) + "f"
}

impl HlslEmitter {
    pub fn new() -> HlslEmitter {
        HlslEmitter { text: String::new(), harness: None, failure: None, redirects: HashMap::new() }
    }

    pub fn with_harness() -> HlslEmitter {
        HlslEmitter {
            text: String::new(),
            harness: Some(Harness { is_emitting_line: false, inputs: Vec::new(), trace: None }),
            failure: None,
            redirects: HashMap::new(),
        }
    }

    // ---- Program ----

    pub fn emit_program(&mut self, program: &BoundProgram, uniforms_as_statics: bool) {
        for structure in &program.structs {
            self.text.push_str(&format!("struct {}\n{{\n", structure.name));
            for field in &structure.fields {
                self.text.push_str(&format!("    {};\n", declaration(&field.ty, &field.name)));
            }
            self.text.push_str("};\n\n");
        }

        // Prototypes first: functions may call each other in any order (default values go here, once)
        for function in &program.functions {
            let signature: String = self.signature(function, true);
            self.text.push_str(&signature);
            self.text.push_str(";\n");
        }
        self.text.push('\n');

        for global in &program.globals {
            let mut prefix: String = String::new();
            if (global.kind == VariableKind::Uniform && uniforms_as_statics) || global.kind == VariableKind::Static {
                prefix.push_str("static ");
            }
            if global.is_const && global.kind == VariableKind::Static {
                prefix.push_str("const ");
            }
            self.text.push_str(&prefix);
            self.text.push_str(&declaration(&global.ty, &global.name));
            if let Some(initializer) = global.initializer()
                && !(global.kind == VariableKind::Uniform && uniforms_as_statics)
            {
                let text: String = self.initializer(&initializer);
                self.text.push_str(" = ");
                self.text.push_str(&text);
            }
            self.text.push_str(";\n");
        }
        self.text.push('\n');

        for function in &program.functions {
            let Some(body) = function.body() else {
                continue;
            };
            let signature: String = self.signature(function, false);
            self.text.push_str(&signature);
            self.text.push('\n');
            self.emit_statement(&body, 0);
            self.text.push('\n');
        }
    }

    /// A copy of a function under another name, with one more parameter, no default values.
    pub fn emit_copy(&mut self, function: &FunctionRef, name: &str, extra_parameter: &str) {
        let Some(body) = function.body() else {
            return;
        };
        let signature: String = self.signature(function, false);
        let open: usize = signature.find('(').expect("a parameter list");
        let close: usize = signature.rfind(')').expect("a parameter list");
        let parameters: &str = &signature[open + 1..close];
        let separator: &str = if parameters.is_empty() { "" } else { ", " };
        self.text.push_str(&format!("{} {name}({parameters}{separator}{extra_parameter})\n", function.return_type));
        self.emit_statement(&body, 0);
        self.text.push('\n');
    }

    fn signature(&mut self, function: &FunctionRef, with_defaults: bool) -> String {
        let mut parameters: Vec<String> = Vec::new();
        for parameter in &function.parameters {
            let mode: &str = match parameter.parameter_mode() {
                ParameterMode::Out => "out ",
                ParameterMode::InOut => "inout ",
                ParameterMode::In => "",
            };
            let mut text: String = format!("{}{}", mode, declaration(&parameter.ty, &parameter.name));
            if with_defaults && let Some(default_value) = parameter.default_value() {
                text.push_str(&format!(" = {}", self.expression(&default_value)));
            }
            parameters.push(text);
        }
        format!("{} {}({})", function.return_type, function.name, parameters.join(", "))
    }

    // ---- Statements ----

    pub fn emit_statement(&mut self, statement: &BoundStatement, depth: usize) {
        let indent: String = " ".repeat(depth * 4);
        match &statement.kind {
            BoundStatementKind::Block { statements, is_scope: false } => {
                for inner in statements {
                    self.emit_statement(inner, depth);
                }
            }
            BoundStatementKind::Block { statements, is_scope: true } => {
                self.text.push_str(&format!("{indent}{{\n"));
                for inner in statements {
                    self.emit_statement(inner, depth + 1);
                }
                self.text.push_str(&format!("{indent}}}\n"));
            }
            BoundStatementKind::If { condition, then, otherwise } => {
                let condition: String = self.expression(condition);
                self.text.push_str(&format!("{indent}if ({condition})\n"));
                self.emit_nested(then, depth);
                if let Some(otherwise) = otherwise {
                    self.text.push_str(&format!("{indent}else\n"));
                    self.emit_nested(otherwise, depth);
                }
            }
            BoundStatementKind::Loop { condition, body, is_do_while: true, .. } => {
                self.text.push_str(&format!("{indent}do\n"));
                self.emit_loop_body(statement, body, depth);
                let condition: String = self.expression(condition.as_ref().expect("do-while has a condition"));
                self.text.push_str(&format!("{indent}while ({condition});\n"));
            }
            BoundStatementKind::Loop { initializer, condition, step, body, is_do_while: false } => {
                self.text.push_str(&format!("{indent}{{\n"));
                if let Some(initializer) = initializer {
                    self.emit_statement(initializer, depth + 1);
                }
                let condition: String =
                    condition.as_ref().map(|condition| self.expression(condition)).unwrap_or_default();
                let step: String = step.as_ref().map(|step| self.expression(step)).unwrap_or_default();
                self.text.push_str(&format!("{indent}    for (; {condition}; {step})\n"));
                self.emit_loop_body(statement, body, depth + 1);
                self.text.push_str(&format!("{indent}}}\n"));
            }
            BoundStatementKind::Switch { value, sections } => {
                let value: String = self.expression(value);
                self.text.push_str(&format!("{indent}switch ({value})\n{indent}{{\n"));
                for section in sections {
                    for label in &section.labels {
                        self.text.push_str(&format!("{indent}case {label}:\n"));
                    }
                    if section.is_default {
                        self.text.push_str(&format!("{indent}default:\n"));
                    }
                    self.text.push_str(&format!("{indent}{{\n"));
                    for inner in &section.statements {
                        self.emit_statement(inner, depth + 2);
                    }
                    self.text.push_str(&format!("{indent}}}\n"));
                }
                self.text.push_str(&format!("{indent}}}\n"));
            }
            BoundStatementKind::Return(Some(value)) if self.trace_point(statement).is_some() => {
                let text: String = self.expression(value);
                let store: String = self.trace_store(&[("refTraceValue".to_string(), value.ty.clone())], depth + 1);
                let returned: String = declaration(&value.ty, "refTraceValue");
                self.text.push_str(&format!(
                    "{indent}{{\n{indent}    {returned} = {text};\n{store}{indent}    return refTraceValue;\n{indent}}}\n"
                ));
            }
            BoundStatementKind::Return(value) => {
                let text: String = match value {
                    None => "return;\n".to_string(),
                    Some(value) => format!("return {};\n", self.expression(value)),
                };
                self.text.push_str(&indent);
                self.text.push_str(&text);
            }
            BoundStatementKind::Jump { is_break } => {
                self.text.push_str(&format!("{indent}{}\n", if *is_break { "break;" } else { "continue;" }));
            }
            BoundStatementKind::VariableDeclaration { variable, initializer } => {
                let mut text: String = format!(
                    "{indent}{}{}",
                    if variable.is_const { "const " } else { "" },
                    declaration(&variable.ty, &variable.name)
                );
                if let Some(initializer) = initializer {
                    text.push_str(" = ");
                    text.push_str(&self.initializer(initializer));
                }
                text.push_str(";\n");
                self.text.push_str(&text);
                if self.trace_point(statement).is_some() {
                    let store: String = self.trace_store(&[(variable.name.clone(), variable.ty.clone())], depth);
                    self.text.push_str(&store);
                }
            }
            BoundStatementKind::Expression(expression) => {
                let text: String = self.expression(expression);
                let kind: Option<TracePointKind> =
                    self.trace_point(statement).map(|point| self.trace().points[point].kind.clone());
                if let Some(TracePointKind::Value { .. }) = kind {
                    let store: String =
                        self.trace_store(&[("refTraceValue".to_string(), expression.ty.clone())], depth + 1);
                    let value: String = declaration(&expression.ty, "refTraceValue");
                    self.text.push_str(&format!("{indent}{{\n{indent}    {value} = {text};\n{store}{indent}}}\n"));
                } else {
                    self.text.push_str(&format!("{indent}{text};\n"));
                    self.emit_trace_after(statement, depth);
                }
            }
        }
    }

    /// After a traced statement that writes variables: their values, as its trace entry.
    pub fn emit_trace_after(&mut self, statement: &BoundStatement, depth: usize) {
        let Some(point) = self.trace_point(statement) else {
            return;
        };
        let variables: Vec<(String, ShaderType)> = match &self.trace().points[point].kind {
            TracePointKind::Writes { variables } => {
                variables.iter().map(|variable| (variable.name.clone(), variable.ty.clone())).collect()
            }
            _ => return,
        };
        let store: String = self.trace_store(&variables, depth);
        self.text.push_str(&store);
    }

    /// A loop's body; in a traced line, after writing the loop's variables (each iteration's entry).
    fn emit_loop_body(&mut self, loop_statement: &BoundStatement, body: &BoundStatement, depth: usize) {
        let Some(point) = self.trace_point(loop_statement) else {
            self.emit_nested(body, depth);
            return;
        };
        let indent: String = " ".repeat(depth * 4);
        let variables: Vec<(String, ShaderType)> = self.trace().points[point]
            .kind
            .variables()
            .iter()
            .map(|variable| (variable.name.clone(), variable.ty.clone()))
            .collect();
        let store: String = self.trace_store(&variables, depth + 1);
        self.text.push_str(&format!("{indent}{{\n{store}"));
        self.emit_statement(body, depth + 1);
        self.text.push_str(&format!("{indent}}}\n"));
    }

    fn trace(&self) -> &HarnessTrace {
        self.harness.as_ref().and_then(|harness| harness.trace.as_ref()).expect("a traced line")
    }

    /// The trace point of a statement of the traced code, while emitting it.
    fn trace_point(&self, statement: &BoundStatement) -> Option<usize> {
        let trace: &HarnessTrace = self.harness.as_ref()?.trace.as_ref().filter(|trace| trace.is_active)?;
        trace.ids.get(&(statement as *const BoundStatement)).copied()
    }

    /// Writes values (variables, by name) as the next trace entry when it fits, and counts its words regardless.
    fn trace_store(&self, values: &[(String, ShaderType)], depth: usize) -> String {
        let trace: &HarnessTrace = self.trace();
        let indent: String = " ".repeat(depth * 4);
        let mut stores: String = String::new();
        let mut words: usize = 0;
        for (name, ty) in values {
            for (component, path) in component_paths(ty, name).into_iter().enumerate() {
                let index: String = format!("{}u + refTraceCursor + {words}u", trace.base);
                let (store, count) = store_component(ty.kind_at(component), &path, &index);
                stores.push_str(&format!("{indent}    {store}\n"));
                words += count;
            }
        }
        let store: String = format!(
            "{indent}if (refTraceCursor + {words}u <= {}u)\n{indent}{{\n{stores}{indent}}}\n{indent}refTraceCursor += {words}u;\n",
            trace.capacity
        );
        match &trace.guard {
            Some(guard) => format!("{indent}if ({guard})\n{indent}{{\n{store}{indent}}}\n"),
            None => store,
        }
    }

    fn emit_nested(&mut self, statement: &BoundStatement, depth: usize) {
        let is_block: bool = matches!(statement.kind, BoundStatementKind::Block { .. });
        if !is_block && self.trace_point(statement).is_some() {
            // A traced statement writes its values after itself: braces keep them in the if's body
            let indent: String = " ".repeat(depth * 4);
            self.text.push_str(&format!("{indent}{{\n"));
            self.emit_statement(statement, depth + 1);
            self.text.push_str(&format!("{indent}}}\n"));
            return;
        }
        self.emit_statement(statement, if is_block { depth } else { depth + 1 });
    }

    fn initializer(&mut self, initializer: &BoundExpression) -> String {
        match &initializer.kind {
            BoundExpressionKind::Construct { sources, is_initializer_list: true } => {
                let elements: Vec<String> = sources.iter().map(|source| self.expression(source)).collect();
                format!("{{ {} }}", elements.join(", "))
            }
            BoundExpressionKind::Conversion { operand, is_explicit: false, .. }
                if matches!(operand.kind, BoundExpressionKind::Construct { is_initializer_list: true, .. }) =>
            {
                self.initializer(operand)
            }
            _ => self.expression(initializer),
        }
    }

    // ---- Expressions ----

    /// An expression as HLSL. In a harness's line, literal arguments become buffer loads.
    pub fn expression(&mut self, expression: &BoundExpression) -> String {
        if !self.harness.as_ref().is_some_and(|harness| harness.is_emitting_line) {
            return self.base_expression(expression);
        }
        // pow(x, 2.0) must keep its literal 2, or DXC would no longer turn it into x * x
        if let BoundExpressionKind::IntrinsicCall { intrinsic, arguments, .. } = &expression.kind
            && !intrinsic.constant_sensitive_arguments.is_empty()
        {
            let mut texts: Vec<String> = Vec::new();
            for (index, argument) in arguments.iter().enumerate() {
                let text: String = if intrinsic.constant_sensitive_arguments.contains(&index) {
                    self.without_loads(|emitter| emitter.expression(argument))
                } else {
                    self.expression(argument)
                };
                texts.push(text);
            }
            return format!("{}({})", intrinsic.name, texts.join(", "));
        }
        // float3(1, 2, 3): its literal components, converted to the component kind
        if let BoundExpressionKind::Construct { sources, is_initializer_list: false } = &expression.kind
            && let ShaderType::Numeric(target) = &expression.ty
        {
            let mut texts: Vec<String> = Vec::new();
            for source in sources {
                let constant: Option<Value> = match &source.ty {
                    ShaderType::Numeric(numeric) if numeric.kind.is_literal() => {
                        try_evaluate_constant(source, &SemanticsProfile::HLSL)
                    }
                    _ => None,
                };
                let text: String = match constant {
                    Some(constant) => {
                        let bits: u64 = scalars::convert(
                            constant.kind_at(0),
                            target.kind,
                            constant.bits[0],
                            &SemanticsProfile::HLSL,
                        );
                        self.load_scalar(target.kind, bits)
                    }
                    None => self.expression(source),
                };
                texts.push(text);
            }
            return format!("{}({})", expression.ty, texts.join(", "));
        }
        // Typed literals (1e-40f, 0x1u, 2.5L) too, or DXC would fold them with compile-time rules
        if let BoundExpressionKind::Literal(value) = &expression.kind
            && !value.kind_at(0).is_literal()
        {
            return self.load_value(value);
        }
        if let BoundExpressionKind::Conversion { operand, .. } = &expression.kind
            && let ShaderType::Numeric(target) = &expression.ty
            && !target.kind.is_literal()
            && let ShaderType::Numeric(source) = &operand.ty
            && source.kind.is_literal()
            && let Some(constant) = try_evaluate_constant(expression, &SemanticsProfile::HLSL)
        {
            return self.load_value(&constant);
        }
        self.base_expression(expression)
    }

    /// Emits with the line's literals kept as literals (arguments DXC lowers differently when constant).
    fn without_loads(&mut self, emit: impl FnOnce(&mut HlslEmitter) -> String) -> String {
        let was_emitting_line: bool = self.harness.as_ref().is_some_and(|harness| harness.is_emitting_line);
        if let Some(harness) = self.harness.as_mut() {
            harness.is_emitting_line = false;
        }
        let text: String = emit(self);
        if let Some(harness) = self.harness.as_mut() {
            harness.is_emitting_line = was_emitting_line;
        }
        text
    }

    /// The node itself; its children go through `expression`.
    fn base_expression(&mut self, expression: &BoundExpression) -> String {
        use BoundExpressionKind::*;
        match &expression.kind {
            Literal(value) => literal(value),
            UnitLiteral { si_value, .. } => real_literal(*si_value),
            Variable(variable) => variable.name.clone(),
            Unary { operator, operand } => format!("({}{})", unary_symbol(*operator), self.expression(operand)),
            Binary { operator, left, right } => {
                let left: String = self.expression(left);
                let right: String = self.expression(right);
                format!("({} {} {})", left, type_rules::symbol(*operator), right)
            }
            Logical { is_and, left, right } => {
                let left: String = self.expression(left);
                let right: String = self.expression(right);
                format!("({} {} {})", left, if *is_and { "&&" } else { "||" }, right)
            }
            Assignment { target, value } => {
                let target: String = self.expression(target);
                let value: String = self.expression(value);
                format!("({target} = {value})")
            }
            CompoundAssignment { operator, target, value, .. } => {
                let target: String = self.expression(target);
                let value: String = self.expression(value);
                format!("({} {}= {})", target, type_rules::symbol(*operator), value)
            }
            Increment { target, is_increment, is_prefix } => {
                let target: String = self.expression(target);
                let symbol: &str = if *is_increment { "++" } else { "--" };
                if *is_prefix { format!("({symbol}{target})") } else { format!("({target}{symbol})") }
            }
            Conditional { condition, when_true, when_false } => {
                let condition: String = self.expression(condition);
                let when_true: String = self.expression(when_true);
                let when_false: String = self.expression(when_false);
                format!("({condition} ? {when_true} : {when_false})")
            }
            // Default arguments are filled in by the binder; DXC fills them in itself
            Call { arguments, .. } if self.redirects.contains_key(&(expression as *const BoundExpression)) => {
                // Every argument, defaults included: the extra one comes after them
                let mut texts: Vec<String> = arguments.iter().map(|argument| self.expression(argument)).collect();
                let (name, extra) = self.redirects[&(expression as *const BoundExpression)].clone();
                texts.push(extra);
                format!("{name}({})", texts.join(", "))
            }
            Call { function, arguments, explicit_count } => {
                let texts: Vec<String> =
                    arguments[..*explicit_count].iter().map(|argument| self.expression(argument)).collect();
                format!("{}({})", function.name, texts.join(", "))
            }
            IntrinsicCall { intrinsic, arguments, .. } => {
                let texts: Vec<String> = arguments.iter().map(|argument| self.expression(argument)).collect();
                format!("{}({})", intrinsic.name, texts.join(", "))
            }
            Conversion { operand, is_explicit: true, .. } => {
                format!("(({})({}))", cast_type(&expression.ty), self.expression(operand))
            }
            Conversion { operand, .. } => self.expression(operand),
            Construct { sources, .. } => {
                let texts: Vec<String> = sources.iter().map(|source| self.expression(source)).collect();
                format!("{}({})", expression.ty, texts.join(", "))
            }
            Swizzle { operand, text, .. } => format!("{}.{}", self.expression(operand), text),
            Index { operand, index, .. } => {
                let operand: String = self.expression(operand);
                let index: String = self.expression(index);
                format!("{operand}[{index}]")
            }
            Field { operand, field } => format!("{}.{}", self.expression(operand), field.name),
            Comma { left, right } => {
                let left: String = self.expression(left);
                let right: String = self.expression(right);
                format!("({left}, {right})")
            }
            Error => {
                self.failure = Some("can't emit an expression that has errors".to_string());
                "0".to_string()
            }
        }
    }

    // ---- Harness input loads ----

    /// A constant value read from the input buffer.
    pub fn load_value(&mut self, value: &Value) -> String {
        let components: Vec<String> =
            (0..value.bits.len()).map(|index| self.load_scalar(value.kind_at(index), value.bits[index])).collect();
        if value.ty.is_scalar() { components[0].clone() } else { format!("{}({})", value.ty, components.join(", ")) }
    }

    /// One component read from the input buffer.
    pub fn load_scalar(&mut self, kind: ScalarKind, bits: u64) -> String {
        let harness: &mut Harness = self.harness.as_mut().expect("loads need a harness");
        let word: usize = harness.inputs.len();
        let read = |offset: usize| format!("RefInput[{}]", word + offset);
        harness.inputs.push(bits as u32);
        if kind.is_64bit() {
            harness.inputs.push((bits >> 32) as u32);
        }
        match kind {
            ScalarKind::Bool => format!("({} != 0u)", read(0)),
            ScalarKind::Int => format!("asint({})", read(0)),
            ScalarKind::UInt => read(0),
            ScalarKind::Float | ScalarKind::Half => format!("asfloat({})", read(0)),
            ScalarKind::Double => format!("asdouble({}, {})", read(0), read(1)),
            ScalarKind::Int64 => format!("((int64_t)(((uint64_t){} << 32) | (uint64_t){}))", read(1), read(0)),
            ScalarKind::UInt64 => format!("(((uint64_t){} << 32) | (uint64_t){})", read(1), read(0)),
            ScalarKind::LiteralInt | ScalarKind::LiteralFloat => panic!("can't load a {kind:?}"),
        }
    }
}

impl Default for HlslEmitter {
    fn default() -> HlslEmitter {
        HlslEmitter::new()
    }
}

/// HLSL access path of every flattened component: v[2], m[1][0], s.Field[3].x...
pub fn component_paths(ty: &ShaderType, root: &str) -> Vec<String> {
    let mut paths: Vec<String> = Vec::new();
    collect_paths(ty, root, &mut paths);
    paths
}

fn collect_paths(ty: &ShaderType, root: &str, paths: &mut Vec<String>) {
    match ty {
        ShaderType::Numeric(numeric) if numeric.is_scalar() => paths.push(root.to_string()),
        ShaderType::Numeric(vector) if vector.is_vector() => {
            for index in 0..vector.size() {
                paths.push(format!("{root}[{index}]"));
            }
        }
        ShaderType::Numeric(matrix) => {
            for row in 0..matrix.rows {
                for column in 0..matrix.columns {
                    paths.push(format!("{root}[{row}][{column}]"));
                }
            }
        }
        ShaderType::Array(array) => {
            for index in 0..array.length {
                collect_paths(&array.element, &format!("{root}[{index}]"), paths);
            }
        }
        ShaderType::Struct(structure) => {
            for field in &structure.fields {
                collect_paths(&field.ty, &format!("{root}.{}", field.name), paths);
            }
        }
        ShaderType::Void => {}
    }
}

/// Input values in a stable order (declaration order of their symbols), so the same line emits the same shader.
pub fn ordered_inputs(inputs: &crate::evaluation::evaluator::Storage) -> Vec<(&VariableRef, &Value)> {
    let mut ordered: Vec<(&VariableRef, &Value)> = inputs.iter().collect();
    ordered.sort_by_key(|(variable, _)| variable.id);
    ordered
}
