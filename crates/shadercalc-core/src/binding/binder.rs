use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use super::bound_tree::*;
use super::intrinsic::{Intrinsic, IntrinsicSignature};
use super::program::{BoundInteractive, BoundProgram, BoundWorksheet, NameMap, ScriptLine, WorksheetSource};
use super::symbols::{FunctionRef, FunctionSymbol, VariableKind, VariableRef, VariableSymbol};
use super::type_rules::{self, BinaryResolution, ConversionInfo};
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};
use crate::evaluation::evaluator::try_evaluate_constant;
use crate::evaluation::intrinsics;
use crate::semantics::SemanticsProfile;
use crate::syntax::parser::is_builtin_type_name;
use crate::syntax::tree::*;
use crate::types::{NumericType, ScalarKind, ShaderType, StructField, StructType};
use crate::units::UnitTag;
use crate::values::Value;

type Scope = HashMap<String, VariableRef>;

/// Type-checks syntax into the bound tree: resolves names and types, picks overloads (user functions and
/// intrinsics), inserts every implicit conversion, and reports problems for the whole text at once.
pub struct Binder<'a> {
    source_name: String,
    profile: SemanticsProfile,
    diagnostics: DiagnosticBag,
    type_names: NameMap<ShaderType>,
    structs: Vec<Arc<StructType>>,
    functions: HashMap<String, Vec<FunctionRef>>,
    function_order: Vec<FunctionRef>,
    globals: HashMap<String, VariableRef>,
    global_order: Vec<VariableRef>,
    /// Call edges by caller, in the order bodies were bound.
    calls: Vec<(FunctionRef, Vec<FunctionRef>)>,
    session_variables: Option<&'a HashMap<String, VariableRef>>,
    is_interactive: bool,
    scopes: Vec<Scope>,
    function: Option<FunctionRef>,
    loop_depth: i32,
    breakable_depth: i32,
}

/// Binds pasted code: functions, structs, typedefs, globals.
pub fn bind_program(
    unit: &CompilationUnitSyntax,
    source_name: &str,
    profile: &SemanticsProfile,
    parse_diagnostics: &[Diagnostic],
) -> Arc<BoundProgram> {
    let mut binder: Binder = Binder::new(source_name, profile, None, false);
    binder.diagnostics.add_range(parse_diagnostics);
    binder.declare_program(&unit.declarations);
    binder.into_program()
}

/// Binds a calculator line against a program and the session's variables.
pub fn bind_interactive(
    statements: &[&StatementSyntax],
    program: &BoundProgram,
    session_variables: &HashMap<String, VariableRef>,
    source_name: &str,
    profile: &SemanticsProfile,
) -> (BoundInteractive, Vec<Diagnostic>) {
    let mut binder: Binder = Binder::new(source_name, profile, Some(session_variables), true);
    binder.import(program);
    let line: BoundInteractive = binder.bind_line(statements);
    (line, binder.diagnostics.into_items())
}

/// Binds worksheet files as one program: declarations of every file are shared (types, then function signatures),
/// top-level lines are bound in file then line order (their variables become globals the functions can read),
/// then function bodies.
pub fn bind_worksheet(
    sources: &[WorksheetSource],
    profile: &SemanticsProfile,
    parse_diagnostics: &[Diagnostic],
) -> BoundWorksheet {
    let mut binder: Binder = Binder::new("", profile, None, true);
    binder.diagnostics.add_range(parse_diagnostics);
    for source in sources {
        binder.source_name = source.name.clone();
        for item in &source.items {
            if let ItemSyntax::Declaration(declaration) = item {
                binder.declare_type(declaration);
            }
        }
    }
    for source in sources {
        binder.source_name = source.name.clone();
        for item in &source.items {
            if let ItemSyntax::Declaration(DeclarationSyntax::Function(function)) = item {
                binder.declare_function(function);
            }
        }
    }

    let mut lines: Vec<ScriptLine> = Vec::new();
    for source in sources {
        binder.source_name = source.name.clone();
        for item in &source.items {
            match item {
                ItemSyntax::Declaration(DeclarationSyntax::GlobalVariable(global)) => {
                    binder.declare_global(&global.declaration)
                }
                ItemSyntax::Statement(statement) => {
                    let line: ScriptLine = binder.bind_script_line(&source.name, statement);
                    lines.push(line);
                }
                ItemSyntax::Declaration(_) => {}
            }
        }
    }

    binder.bind_function_bodies();
    binder.report_recursion();
    BoundWorksheet { program: binder.into_program(), lines }
}

/// The type a value gets when nothing else decides: literal int → int, literal float → float.
pub fn materialize(ty: &ShaderType) -> ShaderType {
    match ty {
        ShaderType::Numeric(numeric) => ShaderType::Numeric(numeric.with_kind(numeric.kind.materialized())),
        other => other.clone(),
    }
}

/// float, uint3, half2x2, dword, min16float... (half and min-precision types are 32-bit, as DXC compiles them by
/// default). Ok(None): not a built-in type name; Err: a built-in type that isn't supported.
pub fn try_parse_builtin_type(name: &str) -> Result<Option<ShaderType>, String> {
    if name == "vector" {
        return Ok(Some(ShaderType::vector(ScalarKind::Float, 4)));
    }
    if name == "matrix" {
        return Ok(Some(ShaderType::matrix(ScalarKind::Float, 4, 4)));
    }
    if !is_builtin_type_name(name) {
        return Ok(None);
    }
    let scalar_name: &str = name.trim_end_matches(['1', '2', '3', '4', 'x']);
    let kind: ScalarKind = match scalar_name {
        "bool" => ScalarKind::Bool,
        "int" | "int32_t" | "min16int" | "min12int" => ScalarKind::Int,
        "uint" | "dword" | "uint32_t" | "min16uint" => ScalarKind::UInt,
        "int64_t" => ScalarKind::Int64,
        "uint64_t" => ScalarKind::UInt64,
        "float" | "float32_t" | "half" | "min16float" | "min10float" => ScalarKind::Float,
        "double" | "float64_t" => ScalarKind::Double,
        _ => return Err(format!("'{name}' needs 16-bit types (-enable-16bit-types), which aren't supported")),
    };
    let suffix: &[u8] = &name.as_bytes()[scalar_name.len()..];
    let digit = |byte: u8| (byte - b'0') as usize;
    Ok(Some(match suffix.len() {
        0 => ShaderType::scalar(kind),
        1 => ShaderType::vector(kind, digit(suffix[0])),
        _ => ShaderType::matrix(kind, digit(suffix[0]), digit(suffix[2])),
    }))
}

fn always_returns(statement: &BoundStatement) -> bool {
    match &statement.kind {
        BoundStatementKind::Return(_) => true,
        BoundStatementKind::Block { statements, .. } => statements.iter().any(always_returns),
        BoundStatementKind::If { then, otherwise: Some(otherwise), .. } => {
            always_returns(then) && always_returns(otherwise)
        }
        BoundStatementKind::Loop { condition: None, body, .. } => !contains_break(body),
        BoundStatementKind::Switch { sections, .. } => {
            sections.iter().any(|section| section.is_default)
                && sections.iter().all(|section| section.statements.iter().any(always_returns))
        }
        _ => false,
    }
}

fn contains_break(statement: &BoundStatement) -> bool {
    match &statement.kind {
        BoundStatementKind::Jump { is_break: true } => true,
        BoundStatementKind::Block { statements, .. } => statements.iter().any(contains_break),
        BoundStatementKind::If { then, otherwise, .. } => {
            contains_break(then) || otherwise.as_ref().is_some_and(|otherwise| contains_break(otherwise))
        }
        _ => false,
    }
}

impl<'a> Binder<'a> {
    fn new(
        source_name: &str,
        profile: &SemanticsProfile,
        session_variables: Option<&'a HashMap<String, VariableRef>>,
        is_interactive: bool,
    ) -> Binder<'a> {
        Binder {
            source_name: source_name.to_string(),
            profile: profile.clone(),
            diagnostics: DiagnosticBag::new(),
            type_names: NameMap::default(),
            structs: Vec::new(),
            functions: HashMap::new(),
            function_order: Vec::new(),
            globals: HashMap::new(),
            global_order: Vec::new(),
            calls: Vec::new(),
            session_variables,
            is_interactive,
            scopes: vec![Scope::new()],
            function: None,
            loop_depth: 0,
            breakable_depth: 0,
        }
    }

    /// One top-level worksheet line. Its variables are globals (functions can read them); a new name assigned with
    /// `=` declares one of the value's type.
    fn bind_script_line(&mut self, source_name: &str, statement: &StatementSyntax) -> ScriptLine {
        let mut declared: Vec<VariableRef> = Vec::new();
        let line = |statement_bound: BoundStatement, shows_result: bool, declared: Vec<VariableRef>| ScriptLine {
            source: source_name.to_string(),
            span: statement.span,
            statement: statement_bound,
            shows_result,
            declared,
        };
        if let StatementKind::Expression(ExpressionSyntax {
            kind: ExpressionKind::Assignment { operator, target, value: assigned },
            ..
        }) = &statement.kind
            && operator == "="
            && let ExpressionKind::Name(name) = &target.kind
            && self.lookup_variable(name).is_none()
        {
            let value: BoundExpression = self.bind_expression(assigned);
            if value.is_error() || value.ty.is_void() {
                if !value.is_error() {
                    self.error(assigned.span, "this expression has no value");
                }
                return line(BoundStatement::empty(statement.span), false, declared);
            }
            let ty: ShaderType = materialize(&value.ty);
            let variable: VariableRef =
                VariableSymbol::new(name, ty.clone(), VariableKind::Uniform, false, target.span);
            self.declare_script_global(&variable);
            declared.push(variable.clone());
            let initializer: BoundExpression = self.convert(value, &ty, false, assigned.span);
            let declaration: BoundStatement = BoundStatement::new(
                statement.span,
                BoundStatementKind::VariableDeclaration { variable, initializer: Some(initializer) },
            );
            return line(declaration, false, declared);
        }

        if let StatementKind::VariableDeclaration(declaration) = &statement.kind {
            let is_static: bool = declaration.modifiers.contains(StorageModifiers::STATIC);
            let is_const: bool = declaration.modifiers.contains(StorageModifiers::CONST);
            let mut statements: Vec<BoundStatement> = Vec::new();
            for declarator in &declaration.declarators {
                let kind: VariableKind = if is_static { VariableKind::Static } else { VariableKind::Uniform };
                let (variable, initializer) =
                    self.declare_variable(&declaration.ty, declarator, kind, is_const, is_const && is_static);
                let Some(variable) = variable else {
                    continue;
                };
                self.declare_script_global(&variable);
                variable.set_initializer(initializer.clone());
                if let Some(initializer) = &initializer
                    && is_const
                    && is_static
                {
                    variable.set_constant_value(try_evaluate_constant(initializer, &self.profile));
                }
                declared.push(variable.clone());
                statements.push(BoundStatement::new(
                    declaration.span,
                    BoundStatementKind::VariableDeclaration { variable, initializer },
                ));
            }
            let bound: BoundStatement = if statements.len() == 1 {
                statements.pop().expect("one statement")
            } else {
                BoundStatement::block(statements, declaration.span, false)
            };
            return line(bound, false, declared);
        }

        let bound_statement: BoundStatement = self.bind_statement(statement);
        let shows_result: bool = matches!(&bound_statement.kind,
            BoundStatementKind::Expression(expression) if !expression.ty.is_void() && !expression.is_error());
        line(bound_statement, shows_result, declared)
    }

    fn declare_script_global(&mut self, variable: &VariableRef) {
        if self.globals.contains_key(&variable.name) {
            self.error(variable.span, format!("'{}' is already defined", variable.name));
            return;
        }
        self.globals.insert(variable.name.clone(), variable.clone());
        self.global_order.push(variable.clone());
    }

    fn declare_type(&mut self, declaration: &DeclarationSyntax) {
        match declaration {
            DeclarationSyntax::Struct(structure) => self.declare_struct(structure),
            DeclarationSyntax::Typedef(typedef) => {
                if let Some(aliased) = self.resolve_type(&typedef.ty) {
                    let with_dimensions: Option<ShaderType> =
                        self.with_array_dimensions(&aliased, &typedef.array_dimensions, typedef.name_span, None);
                    self.type_names.insert(typedef.name.clone(), with_dimensions.unwrap_or(aliased));
                }
            }
            _ => {}
        }
    }

    fn import(&mut self, program: &BoundProgram) {
        for structure in &program.structs {
            self.structs.push(structure.clone());
        }
        for (name, ty) in program.type_names.iter() {
            self.type_names.insert(name.clone(), ty.clone());
        }
        for global in &program.globals {
            self.globals.insert(global.name.clone(), global.clone());
            self.global_order.push(global.clone());
        }
        for function in &program.functions {
            self.add_function(function.clone());
        }
    }

    fn into_program(self) -> Arc<BoundProgram> {
        Arc::new(BoundProgram {
            structs: self.structs,
            type_names: self.type_names,
            globals: self.global_order,
            functions: self.function_order,
            diagnostics: self.diagnostics.into_items(),
        })
    }

    // ---------------------------------------------------------------- Diagnostics

    fn function_name(&self) -> Option<String> {
        self.function.as_ref().map(|function| function.name.clone())
    }

    fn error(&mut self, span: SourceSpan, message: impl Into<String>) {
        let diagnostic: Diagnostic =
            Diagnostic::new(DiagnosticSeverity::Error, message, self.source_name.clone(), span)
                .in_function(self.function_name());
        self.diagnostics.add(diagnostic);
    }

    fn warning(&mut self, span: SourceSpan, message: impl Into<String>) {
        let diagnostic: Diagnostic =
            Diagnostic::new(DiagnosticSeverity::Warning, message, self.source_name.clone(), span)
                .in_function(self.function_name());
        self.diagnostics.add(diagnostic);
    }

    // ---------------------------------------------------------------- Program

    fn declare_program(&mut self, declarations: &[DeclarationSyntax]) {
        // Types first, then signatures (so functions may be used before their definition), then globals, then bodies
        for declaration in declarations {
            self.declare_type(declaration);
        }
        for declaration in declarations {
            if let DeclarationSyntax::Function(function) = declaration {
                self.declare_function(function);
            }
        }
        for declaration in declarations {
            if let DeclarationSyntax::GlobalVariable(global) = declaration {
                self.declare_global(&global.declaration);
            }
        }
        self.bind_function_bodies();
        self.report_recursion();
    }

    fn bind_function_bodies(&mut self) {
        let functions: Vec<FunctionRef> = self.function_order.clone();
        for function in functions {
            let syntax: Arc<FunctionSyntax> = function.syntax();
            if syntax.body.is_some() {
                self.bind_function_body(&function);
            } else {
                self.source_name = function.source_name();
                self.error(syntax.name_span, format!("'{}' is declared but never defined", function.signature()));
            }
        }
    }

    fn declare_struct(&mut self, syntax: &StructSyntax) {
        let mut fields: Vec<(String, ShaderType)> = Vec::new();
        for field in &syntax.fields {
            let Some(field_type) = self.resolve_type(&field.ty) else {
                continue;
            };
            for declarator in &field.declarators {
                if declarator.initializer.is_some() {
                    self.warning(
                        declarator.name_span,
                        format!("default value of field '{}' is ignored (HLSL structs have none)", declarator.name),
                    );
                }
                if fields.iter().any(|(existing, _)| *existing == declarator.name) {
                    self.error(
                        declarator.name_span,
                        format!("'{}' already has a field '{}'", syntax.name, declarator.name),
                    );
                    continue;
                }
                if let Some(declarator_type) =
                    self.with_array_dimensions(&field_type, &declarator.array_dimensions, declarator.name_span, None)
                {
                    fields.push((declarator.name.clone(), declarator_type));
                }
            }
        }
        let struct_type: ShaderType = ShaderType::structure(&syntax.name, fields);
        if self.type_names.contains_key(&syntax.name) {
            self.error(syntax.name_span, format!("type '{}' is already defined", syntax.name));
            return;
        }
        if let ShaderType::Struct(structure) = &struct_type {
            self.structs.push(structure.clone());
        }
        self.type_names.insert(syntax.name.clone(), struct_type);
    }

    fn declare_function(&mut self, syntax: &FunctionSyntax) {
        let return_type: Option<ShaderType> = match &syntax.return_type {
            TypeSyntax::Named { name, .. } if name == "void" => Some(ShaderType::Void),
            other => self.resolve_type(other),
        };
        let Some(return_type) = return_type else {
            return;
        };
        let mut parameters: Vec<VariableRef> = Vec::new();
        for parameter in &syntax.parameters {
            let parameter_type: Option<ShaderType> = self.resolve_type(&parameter.ty);
            let with_dimensions: Option<ShaderType> = match parameter_type {
                Some(parameter_type) => {
                    self.with_array_dimensions(&parameter_type, &parameter.array_dimensions, parameter.name_span, None)
                }
                None => None,
            };
            let Some(with_dimensions) = with_dimensions else {
                return;
            };
            let symbol: VariableRef = VariableSymbol::parameter(
                &parameter.name,
                with_dimensions.clone(),
                parameter.mode,
                parameter.is_const,
                parameter.name_span,
            );
            if let Some(default_value) = &parameter.default_value {
                let bound: BoundExpression = self.bind_expression(default_value);
                let converted: BoundExpression = self.convert(bound, &with_dimensions, false, default_value.span);
                symbol.set_default_value(converted);
            }
            parameters.push(symbol);
        }

        // A definition completes an earlier prototype with the same parameter types
        let existing: Option<FunctionRef> = self.functions.get(&syntax.name).and_then(|overloads| {
            overloads
                .iter()
                .find(|candidate| {
                    candidate.parameters.len() == parameters.len()
                        && candidate.parameters.iter().zip(&parameters).all(|(left, right)| left.ty == right.ty)
                })
                .cloned()
        });
        if let Some(existing) = existing {
            if existing.syntax().body.is_some() && syntax.body.is_some() {
                self.error(syntax.name_span, format!("'{}' is already defined", existing.signature()));
            } else if syntax.body.is_some() {
                existing.attach_definition(Arc::new(syntax.clone()));
                existing.set_source_name(&self.source_name);
            }
            return;
        }
        let function: FunctionRef =
            FunctionSymbol::new(&syntax.name, return_type, parameters, Arc::new(syntax.clone()));
        function.set_source_name(&self.source_name);
        self.add_function(function);
    }

    fn add_function(&mut self, function: FunctionRef) {
        self.functions.entry(function.name.clone()).or_default().push(function.clone());
        self.function_order.push(function);
    }

    fn declare_global(&mut self, declaration: &VariableDeclarationSyntax) {
        if declaration.modifiers.contains(StorageModifiers::GROUP_SHARED) {
            self.warning(declaration.span, "groupshared is treated as an ordinary static variable (one thread)");
        }
        let is_static: bool = declaration.modifiers.contains(StorageModifiers::STATIC)
            || declaration.modifiers.contains(StorageModifiers::GROUP_SHARED);
        let is_const: bool = declaration.modifiers.contains(StorageModifiers::CONST);
        for declarator in &declaration.declarators {
            let kind: VariableKind = if is_static { VariableKind::Static } else { VariableKind::Uniform };
            let (symbol, initializer) =
                self.declare_variable(&declaration.ty, declarator, kind, is_const || !is_static, is_const && is_static);
            let Some(symbol) = symbol else {
                continue;
            };
            if self.globals.contains_key(&symbol.name) {
                self.error(declarator.name_span, format!("'{}' is already defined", symbol.name));
                continue;
            }
            self.globals.insert(symbol.name.clone(), symbol.clone());
            // Only static const folds; a uniform's initializer is just its default value
            if let Some(initializer) = &initializer
                && is_const
                && is_static
            {
                symbol.set_constant_value(try_evaluate_constant(initializer, &self.profile));
            }
            symbol.set_initializer(initializer);
            self.global_order.push(symbol);
        }
    }

    fn bind_function_body(&mut self, function: &FunctionRef) {
        self.source_name = function.source_name();
        self.function = Some(function.clone());
        self.calls.retain(|(caller, _)| caller.id != function.id);
        self.calls.push((function.clone(), Vec::new()));
        self.scopes = vec![Scope::new()];
        for parameter in &function.parameters {
            if !self.try_declare(parameter) {
                self.error(parameter.span, format!("parameter '{}' is declared twice", parameter.name));
            }
        }
        let syntax: Arc<FunctionSyntax> = function.syntax();
        let body: BoundStatement = self.bind_block(syntax.body.as_ref().expect("a definition has a body"), false);
        let returns: bool = always_returns(&body);
        function.set_body(Some(body));
        if !function.return_type.is_void() && !returns {
            self.warning(syntax.name_span, format!("not every path of '{}' returns a value", function.name));
        }
        self.function = None;
    }

    fn report_recursion(&mut self) {
        let mut reported: HashSet<usize> = HashSet::new();
        let callers: Vec<FunctionRef> = self.calls.iter().map(|(caller, _)| caller.clone()).collect();
        for function in callers {
            if !reported.contains(&function.id) && self.reaches(&function, &function, &mut HashSet::new()) {
                reported.insert(function.id);
                let diagnostic: Diagnostic = Diagnostic::new(
                    DiagnosticSeverity::Error,
                    format!("'{}' is recursive: HLSL doesn't allow recursion", function.name),
                    self.source_name.clone(),
                    function.syntax().name_span,
                )
                .in_function(Some(function.name.clone()));
                self.diagnostics.add(diagnostic);
            }
        }
    }

    fn reaches(&self, from: &FunctionRef, target: &FunctionRef, visited: &mut HashSet<usize>) -> bool {
        let Some((_, callees)) = self.calls.iter().find(|(caller, _)| caller.id == from.id) else {
            return false;
        };
        for callee in callees {
            if callee.id == target.id || (visited.insert(callee.id) && self.reaches(callee, target, visited)) {
                return true;
            }
        }
        false
    }

    // ---------------------------------------------------------------- Calculator lines

    fn bind_line(&mut self, statements: &[&StatementSyntax]) -> BoundInteractive {
        let mut bound: Vec<BoundStatement> = Vec::new();
        let mut declared: Vec<VariableRef> = Vec::new();
        let mut result_statement: Option<usize> = None;
        let mut result_variable: Option<VariableRef> = None;
        for statement in statements {
            result_statement = None;
            result_variable = None;
            // `x = value` with a new name declares a session variable of the value's type
            if let StatementKind::Expression(ExpressionSyntax {
                kind: ExpressionKind::Assignment { operator, target, value: assigned },
                ..
            }) = &statement.kind
                && operator == "="
                && let ExpressionKind::Name(name) = &target.kind
                && self.lookup_variable(name).is_none()
            {
                let value: BoundExpression = self.bind_expression(assigned);
                if value.is_error() || value.ty.is_void() {
                    if value.ty.is_void() && !value.is_error() {
                        self.error(assigned.span, "this expression has no value");
                    }
                    continue;
                }
                let ty: ShaderType = materialize(&value.ty);
                let variable: VariableRef =
                    VariableSymbol::new(name, ty.clone(), VariableKind::Session, false, target.span);
                self.try_declare(&variable);
                declared.push(variable.clone());
                let initializer: BoundExpression = self.convert(value, &ty, false, assigned.span);
                bound.push(BoundStatement::new(
                    statement.span,
                    BoundStatementKind::VariableDeclaration {
                        variable: variable.clone(),
                        initializer: Some(initializer),
                    },
                ));
                result_variable = Some(variable);
                continue;
            }
            if let StatementKind::VariableDeclaration(declaration) = &statement.kind {
                let is_const: bool = declaration.modifiers.contains(StorageModifiers::CONST);
                for declarator in &declaration.declarators {
                    let (variable, initializer) =
                        self.declare_variable(&declaration.ty, declarator, VariableKind::Session, is_const, is_const);
                    let Some(variable) = variable else {
                        continue;
                    };
                    self.try_declare(&variable);
                    declared.push(variable.clone());
                    bound.push(BoundStatement::new(
                        declaration.span,
                        BoundStatementKind::VariableDeclaration { variable: variable.clone(), initializer },
                    ));
                    result_variable = Some(variable);
                }
                continue;
            }
            let bound_statement: BoundStatement = self.bind_statement(statement);
            let shows: bool = matches!(&bound_statement.kind,
                BoundStatementKind::Expression(expression) if !expression.ty.is_void() && !expression.is_error());
            bound.push(bound_statement);
            if shows {
                result_statement = Some(bound.len() - 1);
            }
        }
        BoundInteractive { statements: bound, result_statement, result_variable, declared_variables: declared }
    }

    // ---------------------------------------------------------------- Types

    fn resolve_type(&mut self, syntax: &TypeSyntax) -> Option<ShaderType> {
        match syntax {
            TypeSyntax::Named { name, span } => {
                match try_parse_builtin_type(name) {
                    Ok(Some(builtin)) => return Some(builtin),
                    Err(error) => {
                        self.error(*span, error);
                        return None;
                    }
                    Ok(None) => {}
                }
                if let Some(declared) = self.type_names.get(name) {
                    return Some(declared.clone());
                }
                self.error(*span, format!("unknown type '{name}'"));
                None
            }
            TypeSyntax::Generic { name, element, dimensions, span } => {
                let element_type: Option<ShaderType> = self.resolve_type(element);
                let scalar: NumericType = match &element_type {
                    Some(ShaderType::Numeric(numeric)) if numeric.is_scalar() => *numeric,
                    Some(other) => {
                        self.error(element.span(), format!("{name}<> needs a scalar type, got {other}"));
                        return None;
                    }
                    None => return None,
                };
                let expected: usize = if name == "vector" { 1 } else { 2 };
                if dimensions.len() != expected {
                    self.error(*span, format!("{name}<> takes {} arguments", expected + 1));
                    return None;
                }
                let sizes: Vec<i32> =
                    dimensions.iter().map(|dimension| self.constant_int(dimension, 1, 4).unwrap_or(0)).collect();
                if sizes.contains(&0) {
                    return None;
                }
                Some(if name == "vector" {
                    ShaderType::vector(scalar.kind, sizes[0] as usize)
                } else {
                    ShaderType::matrix(scalar.kind, sizes[0] as usize, sizes[1] as usize)
                })
            }
            TypeSyntax::Auto { span } => {
                self.error(*span, "'auto' needs an initializer to take its type from");
                None
            }
        }
    }

    fn with_array_dimensions(
        &mut self,
        element: &ShaderType,
        dimensions: &[Option<ExpressionSyntax>],
        span: SourceSpan,
        unsized_length: Option<usize>,
    ) -> Option<ShaderType> {
        let mut result: ShaderType = element.clone();
        for index in (0..dimensions.len()).rev() {
            let length: Option<usize> = match &dimensions[index] {
                None => {
                    if index == 0 {
                        unsized_length
                    } else {
                        None
                    }
                }
                Some(dimension) => self.constant_int(dimension, 1, 1 << 20).map(|length| length as usize),
            };
            let Some(length) = length else {
                if dimensions[index].is_none() {
                    self.error(span, "an array without a length needs an initializer list");
                }
                return None;
            };
            result = ShaderType::array(result, length);
        }
        Some(result)
    }

    /// A compile-time integer in [minimum, maximum] (array lengths, vector sizes).
    fn constant_int(&mut self, syntax: &ExpressionSyntax, minimum: i32, maximum: i32) -> Option<i32> {
        let bound: BoundExpression = self.bind_expression(syntax);
        if bound.is_error() {
            return None;
        }
        let value: Option<Value> = match &bound.ty {
            ShaderType::Numeric(numeric)
                if numeric.is_scalar() && (numeric.kind.is_integer() || numeric.kind == ScalarKind::Bool) =>
            {
                try_evaluate_constant(&bound, &self.profile)
            }
            _ => None,
        };
        let Some(value) = value else {
            self.error(syntax.span, "needs a constant integer");
            return None;
        };
        let number: i64 = value.get_number(0) as i64;
        if number < minimum as i64 || number > maximum as i64 {
            self.error(syntax.span, format!("{number} is out of range [{minimum}, {maximum}]"));
            return None;
        }
        Some(number as i32)
    }

    // ---------------------------------------------------------------- Variables

    /// Declares one declarator: type (auto, array lengths from the initializer) and converted initializer.
    fn declare_variable(
        &mut self,
        type_syntax: &TypeSyntax,
        declarator: &DeclaratorSyntax,
        kind: VariableKind,
        is_const: bool,
        require_initializer: bool,
    ) -> (Option<VariableRef>, Option<BoundExpression>) {
        let ty: ShaderType;
        let mut initializer: Option<BoundExpression> = None;
        if let TypeSyntax::Auto { .. } = type_syntax {
            let expression: &ExpressionSyntax = match &declarator.initializer {
                Some(expression) if !matches!(expression.kind, ExpressionKind::InitializerList(_)) => expression,
                _ => {
                    self.error(declarator.name_span, "'auto' needs an initializer expression");
                    return (None, None);
                }
            };
            let bound: BoundExpression = self.bind_expression(expression);
            if bound.is_error() {
                return (None, None);
            }
            let materialized: ShaderType = materialize(&bound.ty);
            let Some(with_dimensions) =
                self.with_array_dimensions(&materialized, &declarator.array_dimensions, declarator.name_span, None)
            else {
                return (None, None);
            };
            ty = with_dimensions;
            initializer = Some(self.convert(bound, &ty, false, expression.span));
        } else {
            let Some(element) = self.resolve_type(type_syntax) else {
                return (None, None);
            };
            let mut unsized_length: Option<usize> = None;
            if let (Some(None), Some(ExpressionSyntax { kind: ExpressionKind::InitializerList(_), span: list_span })) =
                (declarator.array_dimensions.first(), &declarator.initializer)
            {
                let inner: Option<ShaderType> =
                    self.with_array_dimensions(&element, &declarator.array_dimensions[1..], declarator.name_span, None);
                let per_element: usize = inner.map_or(1, |inner| inner.component_count());
                let list: &ExpressionSyntax = declarator.initializer.as_ref().expect("checked above");
                let components: usize = self.count_initializer_components(list);
                if !components.is_multiple_of(per_element) {
                    self.error(
                        *list_span,
                        format!("{components} values don't fill whole elements of {per_element} components"),
                    );
                    return (None, None);
                }
                unsized_length = Some(components / per_element);
            }
            let Some(with_dimensions) = self.with_array_dimensions(
                &element,
                &declarator.array_dimensions,
                declarator.name_span,
                unsized_length,
            ) else {
                return (None, None);
            };
            ty = with_dimensions;
            match &declarator.initializer {
                Some(list @ ExpressionSyntax { kind: ExpressionKind::InitializerList(_), .. }) => {
                    initializer = Some(self.bind_initializer_list(list, &ty));
                }
                Some(expression) => {
                    let bound: BoundExpression = self.bind_expression(expression);
                    initializer = Some(self.convert(bound, &ty, false, expression.span));
                }
                None => {}
            }
        }

        if initializer.is_none() && require_initializer {
            self.error(declarator.name_span, format!("const '{}' needs an initializer", declarator.name));
        }
        (Some(VariableSymbol::new(&declarator.name, ty, kind, is_const, declarator.name_span)), initializer)
    }

    fn count_initializer_components(&mut self, list: &ExpressionSyntax) -> usize {
        let ExpressionKind::InitializerList(elements) = &list.kind else {
            return 0;
        };
        let mut count: usize = 0;
        for element in elements {
            if let ExpressionKind::InitializerList(_) = element.kind {
                count += self.count_initializer_components(element);
                continue;
            }
            // Binding twice is harmless: expressions in initializers have no binder side effects
            let bound: BoundExpression = self.bind_expression(element);
            count += bound.ty.component_count();
        }
        count
    }

    /// `{ a, { b, c } }`: HLSL flattens every level; the component count must match the type.
    fn bind_initializer_list(&mut self, list: &ExpressionSyntax, ty: &ShaderType) -> BoundExpression {
        let mut sources: Vec<BoundExpression> = Vec::new();
        self.flatten_initializer(list, &mut sources);
        if sources.iter().any(BoundExpression::is_error) {
            return BoundExpression::error(list.span);
        }
        let count: usize = sources.iter().map(|source| source.ty.component_count()).sum();
        if count != ty.component_count() {
            self.error(list.span, format!("{} needs {} values, the list has {}", ty, ty.component_count(), count));
            return BoundExpression::error(list.span);
        }
        BoundExpression::new(
            ty.clone(),
            list.span,
            BoundExpressionKind::Construct { sources, is_initializer_list: true },
        )
    }

    fn flatten_initializer(&mut self, current: &ExpressionSyntax, sources: &mut Vec<BoundExpression>) {
        let ExpressionKind::InitializerList(elements) = &current.kind else {
            return;
        };
        for element in elements {
            if let ExpressionKind::InitializerList(_) = element.kind {
                self.flatten_initializer(element, sources);
            } else {
                let bound: BoundExpression = self.bind_expression(element);
                sources.push(bound);
            }
        }
    }

    fn lookup_variable(&self, name: &str) -> Option<VariableRef> {
        for scope in self.scopes.iter().rev() {
            if let Some(variable) = scope.get(name) {
                return Some(variable.clone());
            }
        }
        if let Some(session) = self.session_variables.and_then(|variables| variables.get(name)) {
            return Some(session.clone());
        }
        self.globals.get(name).cloned()
    }

    fn try_declare(&mut self, variable: &VariableRef) -> bool {
        let scope: &mut Scope = self.scopes.last_mut().expect("a scope is always open");
        if scope.contains_key(&variable.name) {
            return false;
        }
        scope.insert(variable.name.clone(), variable.clone());
        true
    }

    // ---------------------------------------------------------------- Statements

    fn bind_block(&mut self, block: &BlockSyntax, new_scope: bool) -> BoundStatement {
        if new_scope {
            self.scopes.push(Scope::new());
        }
        let statements: Vec<BoundStatement> =
            block.statements.iter().map(|statement| self.bind_statement(statement)).collect();
        if new_scope {
            self.scopes.pop();
        }
        BoundStatement::block(statements, block.span, true)
    }

    fn bind_statement(&mut self, statement: &StatementSyntax) -> BoundStatement {
        match &statement.kind {
            StatementKind::Block(block) => self.bind_block(block, true),
            StatementKind::Empty => BoundStatement::empty(statement.span),
            StatementKind::VariableDeclaration(declaration) => self.bind_local_declaration(declaration),
            StatementKind::Expression(expression) => {
                let bound: BoundExpression = self.bind_expression(expression);
                BoundStatement::new(statement.span, BoundStatementKind::Expression(bound))
            }
            StatementKind::If { condition, then, otherwise } => {
                let condition: BoundExpression = self.bind_condition(condition);
                let then: BoundStatement = self.bind_statement(then);
                let otherwise: Option<Box<BoundStatement>> =
                    otherwise.as_ref().map(|otherwise| Box::new(self.bind_statement(otherwise)));
                BoundStatement::new(
                    statement.span,
                    BoundStatementKind::If { condition, then: Box::new(then), otherwise },
                )
            }
            StatementKind::For { initializer, condition, step, body } => {
                self.scopes.push(Scope::new());
                let initializer: Option<Box<BoundStatement>> =
                    initializer.as_ref().map(|initializer| Box::new(self.bind_statement(initializer)));
                let condition: Option<BoundExpression> =
                    condition.as_ref().map(|condition| self.bind_condition(condition));
                let step: Option<BoundExpression> = step.as_ref().map(|step| self.bind_expression(step));
                let body: BoundStatement = self.bind_loop_body(body);
                self.scopes.pop();
                BoundStatement::new(
                    statement.span,
                    BoundStatementKind::Loop { initializer, condition, step, body: Box::new(body), is_do_while: false },
                )
            }
            StatementKind::While { condition, body, is_do_while } => {
                let condition: BoundExpression = self.bind_condition(condition);
                let body: BoundStatement = self.bind_loop_body(body);
                BoundStatement::new(
                    statement.span,
                    BoundStatementKind::Loop {
                        initializer: None,
                        condition: Some(condition),
                        step: None,
                        body: Box::new(body),
                        is_do_while: *is_do_while,
                    },
                )
            }
            StatementKind::Switch { value, sections } => self.bind_switch(value, sections, statement.span),
            StatementKind::Return(value) => self.bind_return(value.as_ref(), statement.span),
            StatementKind::Jump(keyword) => {
                if keyword == "discard" {
                    self.error(statement.span, "discard only exists in pixel shaders");
                } else if keyword == "break" && self.breakable_depth == 0 {
                    self.error(statement.span, "break outside a loop or switch");
                } else if keyword == "continue" && self.loop_depth == 0 {
                    self.error(statement.span, "continue outside a loop");
                }
                BoundStatement::new(statement.span, BoundStatementKind::Jump { is_break: keyword == "break" })
            }
        }
    }

    fn bind_loop_body(&mut self, body: &StatementSyntax) -> BoundStatement {
        self.loop_depth += 1;
        self.breakable_depth += 1;
        let bound: BoundStatement = self.bind_statement(body);
        self.loop_depth -= 1;
        self.breakable_depth -= 1;
        bound
    }

    fn bind_local_declaration(&mut self, declaration: &VariableDeclarationSyntax) -> BoundStatement {
        let mut declarations: Vec<BoundStatement> = Vec::new();
        let is_const: bool = declaration.modifiers.contains(StorageModifiers::CONST);
        for declarator in &declaration.declarators {
            let kind: VariableKind = if self.is_interactive && self.function.is_none() {
                VariableKind::Session
            } else {
                VariableKind::Local
            };
            let (variable, initializer) = self.declare_variable(&declaration.ty, declarator, kind, is_const, is_const);
            let Some(variable) = variable else {
                continue;
            };
            if !self.try_declare(&variable) {
                self.error(declarator.name_span, format!("'{}' is already declared in this scope", variable.name));
            }
            if let Some(initializer) = &initializer
                && is_const
            {
                variable.set_constant_value(try_evaluate_constant(initializer, &self.profile));
            }
            declarations.push(BoundStatement::new(
                declaration.span,
                BoundStatementKind::VariableDeclaration { variable, initializer },
            ));
        }
        if declarations.len() == 1 {
            declarations.pop().expect("one declaration")
        } else {
            BoundStatement::block(declarations, declaration.span, false)
        }
    }

    fn bind_condition(&mut self, syntax: &ExpressionSyntax) -> BoundExpression {
        let condition: BoundExpression = self.bind_expression(syntax);
        if condition.is_error() {
            return condition;
        }
        let is_scalar: bool = matches!(&condition.ty, ShaderType::Numeric(numeric) if numeric.size() == 1);
        if !is_scalar {
            self.error(
                syntax.span,
                format!("a condition must be a scalar, got {} (use any(), all() or select())", condition.ty),
            );
            return BoundExpression::error(syntax.span);
        }
        self.convert(condition, &ShaderType::bool(), false, syntax.span)
    }

    fn bind_switch(
        &mut self,
        value_syntax: &ExpressionSyntax,
        sections: &[SwitchSectionSyntax],
        span: SourceSpan,
    ) -> BoundStatement {
        let mut value: BoundExpression = self.bind_expression(value_syntax);
        if !value.is_error() {
            match &value.ty {
                ShaderType::Numeric(numeric)
                    if numeric.is_scalar() && (numeric.kind.is_integer() || numeric.kind == ScalarKind::Bool) =>
                {
                    let target: ShaderType = materialize(&value.ty);
                    value = self.convert(value, &target, false, value_syntax.span);
                }
                other => {
                    let message: String = format!("switch needs an integer, got {other}");
                    self.error(value_syntax.span, message);
                }
            }
        }
        let mut seen: HashSet<i64> = HashSet::new();
        let mut bound_sections: Vec<BoundSwitchSection> = Vec::new();
        self.breakable_depth += 1;
        self.scopes.push(Scope::new());
        let mut has_default: bool = false;
        for section in sections {
            let mut labels: Vec<i64> = Vec::new();
            let mut is_default: bool = false;
            for label in &section.labels {
                let Some(label) = label else {
                    if has_default {
                        self.error(section.span, "switch has two default labels");
                    }
                    is_default = true;
                    has_default = true;
                    continue;
                };
                let constant: Option<i32> = self.constant_int(label, i32::MIN, i32::MAX);
                if let Some(constant) = constant {
                    if !seen.insert(constant as i64) {
                        self.error(label.span, format!("duplicate case {constant}"));
                    }
                    labels.push(constant as i64);
                }
            }
            let statements: Vec<BoundStatement> =
                section.statements.iter().map(|statement| self.bind_statement(statement)).collect();
            bound_sections.push(BoundSwitchSection { labels, is_default, statements });
        }
        self.scopes.pop();
        self.breakable_depth -= 1;
        BoundStatement::new(span, BoundStatementKind::Switch { value, sections: bound_sections })
    }

    fn bind_return(&mut self, value: Option<&ExpressionSyntax>, span: SourceSpan) -> BoundStatement {
        let Some(function) = self.function.clone() else {
            self.error(span, "return outside a function");
            return BoundStatement::new(span, BoundStatementKind::Return(None));
        };
        let Some(value) = value else {
            if !function.return_type.is_void() {
                self.error(span, format!("'{}' must return a {}", function.name, function.return_type));
            }
            return BoundStatement::new(span, BoundStatementKind::Return(None));
        };
        let bound: BoundExpression = self.bind_expression(value);
        if function.return_type.is_void() {
            self.error(value.span, format!("'{}' returns void", function.name));
            return BoundStatement::new(span, BoundStatementKind::Return(None));
        }
        let converted: BoundExpression = self.convert(bound, &function.return_type, false, value.span);
        BoundStatement::new(span, BoundStatementKind::Return(Some(converted)))
    }

    // ---------------------------------------------------------------- Conversions

    /// Converts to a type (implicit or cast), reporting impossible conversions and truncation warnings.
    fn convert(
        &mut self,
        expression: BoundExpression,
        ty: &ShaderType,
        is_explicit: bool,
        span: SourceSpan,
    ) -> BoundExpression {
        if expression.is_error() {
            return expression;
        }
        let conversion: ConversionInfo = type_rules::classify(&expression.ty, ty, is_explicit);
        if !conversion.is_possible {
            let message: String = if expression.ty.is_void() {
                "this expression has no value".to_string()
            } else {
                format!("can't convert {} to {}", expression.ty, ty)
            };
            self.error(span, message);
            return BoundExpression::error(span);
        }
        if conversion.kind == ConversionKind::Identity {
            return expression;
        }
        if let Some(warning) = &conversion.warning
            && !is_explicit
        {
            self.warning(span, warning.clone());
        }
        BoundExpression::conversion(expression, ty.clone(), conversion.kind, is_explicit, span)
    }

    // ---------------------------------------------------------------- Expressions

    fn bind_expression(&mut self, syntax: &ExpressionSyntax) -> BoundExpression {
        match &syntax.kind {
            ExpressionKind::Literal { kind, bits, .. } => {
                BoundExpression::literal(Value::scalar(*kind, *bits, UnitTag::BARE), syntax.span)
            }
            ExpressionKind::UnitLiteral { si_value, dimension, unit_text } => {
                BoundExpression::unit_literal(*si_value, *dimension, unit_text.clone(), syntax.span)
            }
            ExpressionKind::Name(name) => self.bind_name(name, syntax.span),
            ExpressionKind::Unary { operator, operand } => self.bind_unary(operator, operand, syntax.span),
            ExpressionKind::Increment { operator, is_prefix, target } => {
                self.bind_increment(operator, *is_prefix, target, syntax.span)
            }
            ExpressionKind::Binary { operator, left, right } => self.bind_binary(operator, left, right, syntax.span),
            ExpressionKind::Assignment { operator, target, value } => {
                self.bind_assignment(operator, target, value, syntax.span)
            }
            ExpressionKind::Conditional { condition, when_true, when_false } => {
                self.bind_conditional(condition, when_true, when_false, syntax.span)
            }
            ExpressionKind::Call { name, name_span, arguments } => {
                self.bind_call(name, *name_span, arguments, syntax.span)
            }
            ExpressionKind::Constructor { ty, arguments } => match self.resolve_type(ty) {
                None => BoundExpression::error(syntax.span),
                Some(ty) => self.bind_constructor(&ty, arguments, syntax.span),
            },
            ExpressionKind::Cast { ty, array_dimensions, operand } => {
                self.bind_cast(ty, array_dimensions, operand, syntax.span)
            }
            ExpressionKind::Member { target, member, member_span } => {
                self.bind_member(target, member, *member_span, syntax.span)
            }
            ExpressionKind::Index { target, index } => self.bind_index(target, index, syntax.span),
            ExpressionKind::InitializerList(_) => {
                self.error(syntax.span, "an initializer list can only initialize a declaration");
                BoundExpression::error(syntax.span)
            }
        }
    }

    fn bind_name(&mut self, name: &str, span: SourceSpan) -> BoundExpression {
        if let Some(variable) = self.lookup_variable(name) {
            return BoundExpression::variable(variable, span);
        }
        if self.functions.contains_key(name) || intrinsics::try_get(name).is_some() {
            self.error(span, format!("'{name}' is a function: call it with (...)"));
        } else {
            self.error(span, format!("unknown name '{name}'"));
        }
        BoundExpression::error(span)
    }

    fn bind_unary(&mut self, operator: &str, operand_syntax: &ExpressionSyntax, span: SourceSpan) -> BoundExpression {
        let operand: BoundExpression = self.bind_expression(operand_syntax);
        if operand.is_error() {
            return operand;
        }
        let ShaderType::Numeric(numeric) = operand.ty.clone() else {
            self.error(span, format!("'{}' needs a number, got {}", operator, operand.ty));
            return BoundExpression::error(span);
        };
        match operator {
            "!" => BoundExpression::new(
                numeric.with_kind(ScalarKind::Bool).into(),
                span,
                BoundExpressionKind::Unary { operator: UnaryOperator::LogicalNot, operand: Box::new(operand) },
            ),
            "~" => {
                if numeric.kind.is_float() {
                    self.error(span, format!("'~' needs integers, got {numeric}"));
                    return BoundExpression::error(span);
                }
                let integer: ShaderType =
                    if numeric.kind == ScalarKind::Bool { numeric.with_kind(ScalarKind::Int) } else { numeric }.into();
                let converted: BoundExpression = self.convert(operand, &integer, false, span);
                BoundExpression::new(
                    integer,
                    span,
                    BoundExpressionKind::Unary { operator: UnaryOperator::BitwiseNot, operand: Box::new(converted) },
                )
            }
            _ => {
                let arithmetic: ShaderType =
                    if numeric.kind == ScalarKind::Bool { numeric.with_kind(ScalarKind::Int) } else { numeric }.into();
                let converted: BoundExpression = self.convert(operand, &arithmetic, false, span);
                let unary: UnaryOperator = if operator == "-" { UnaryOperator::Negate } else { UnaryOperator::Plus };
                BoundExpression::new(
                    arithmetic,
                    span,
                    BoundExpressionKind::Unary { operator: unary, operand: Box::new(converted) },
                )
            }
        }
    }

    fn bind_increment(
        &mut self,
        operator: &str,
        is_prefix: bool,
        target_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        let target: BoundExpression = self.bind_expression(target_syntax);
        if target.is_error() || !self.check_assignable(&target, target_syntax.span) {
            return BoundExpression::error(span);
        }
        if !matches!(&target.ty, ShaderType::Numeric(numeric) if numeric.kind != ScalarKind::Bool) {
            self.error(span, format!("'{}' needs a number, got {}", operator, target.ty));
            return BoundExpression::error(span);
        }
        BoundExpression::new(
            target.ty.clone(),
            span,
            BoundExpressionKind::Increment { target: Box::new(target), is_increment: operator == "++", is_prefix },
        )
    }

    fn bind_binary(
        &mut self,
        operator: &str,
        left_syntax: &ExpressionSyntax,
        right_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        if operator == "," {
            let left: BoundExpression = self.bind_expression(left_syntax);
            let right: BoundExpression = self.bind_expression(right_syntax);
            return BoundExpression::new(
                right.ty.clone(),
                span,
                BoundExpressionKind::Comma { left: Box::new(left), right: Box::new(right) },
            );
        }
        let left: BoundExpression = self.bind_expression(left_syntax);
        let right: BoundExpression = self.bind_expression(right_syntax);
        if left.is_error() || right.is_error() {
            return BoundExpression::error(span);
        }

        if operator == "&&" || operator == "||" {
            let is_single = |ty: &ShaderType| matches!(ty, ShaderType::Numeric(numeric) if numeric.size() == 1);
            if !is_single(&left.ty) || !is_single(&right.ty) {
                let alternative: &str = if operator == "&&" { "and()" } else { "or()" };
                self.error(span, format!("'{operator}' needs scalars in HLSL 2021; use {alternative} for vectors"));
                return BoundExpression::error(span);
            }
            let left: BoundExpression = self.convert(left, &ShaderType::bool(), false, left_syntax.span);
            let right: BoundExpression = self.convert(right, &ShaderType::bool(), false, right_syntax.span);
            return BoundExpression::new(
                ShaderType::bool(),
                span,
                BoundExpressionKind::Logical { is_and: operator == "&&", left: Box::new(left), right: Box::new(right) },
            );
        }

        let Some(operation) = type_rules::parse_binary_operator(operator) else {
            self.error(span, format!("unknown operator '{operator}'"));
            return BoundExpression::error(span);
        };
        self.make_binary(operation, left, right, span)
    }

    fn make_binary(
        &mut self,
        operation: BinaryOperator,
        left: BoundExpression,
        right: BoundExpression,
        span: SourceSpan,
    ) -> BoundExpression {
        let (ShaderType::Numeric(left_type), ShaderType::Numeric(right_type)) = (&left.ty, &right.ty) else {
            self.error(span, format!("'{}' can't combine {} and {}", type_rules::symbol(operation), left.ty, right.ty));
            return BoundExpression::error(span);
        };
        let mut warning: Option<String> = None;
        let resolved: BinaryResolution =
            match type_rules::resolve_binary(operation, left_type, right_type, &mut warning) {
                Ok(resolved) => resolved,
                Err(error) => {
                    self.error(span, error);
                    return BoundExpression::error(span);
                }
            };
        if let Some(warning) = warning {
            self.warning(span, warning);
        }
        let left: BoundExpression = self.convert_quietly(left, &resolved.operand_type.into());
        let right: BoundExpression = self.convert_quietly(right, &resolved.right_type.into());
        BoundExpression::new(
            resolved.result_type.into(),
            span,
            BoundExpressionKind::Binary { operator: operation, left: Box::new(left), right: Box::new(right) },
        )
    }

    /// Operand conversion whose truncation warning was already reported for the whole operation.
    fn convert_quietly(&mut self, expression: BoundExpression, ty: &ShaderType) -> BoundExpression {
        let conversion: ConversionInfo = type_rules::classify(&expression.ty, ty, true);
        if conversion.kind == ConversionKind::Identity {
            return expression;
        }
        let span: SourceSpan = expression.span;
        BoundExpression::conversion(expression, ty.clone(), conversion.kind, false, span)
    }

    fn bind_assignment(
        &mut self,
        operator: &str,
        target_syntax: &ExpressionSyntax,
        value_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        let target: BoundExpression = self.bind_expression(target_syntax);
        if target.is_error() || !self.check_assignable(&target, target_syntax.span) {
            return BoundExpression::error(span);
        }
        if let ExpressionKind::InitializerList(_) = value_syntax.kind {
            let value: BoundExpression = self.bind_initializer_list(value_syntax, &target.ty);
            return BoundExpression::new(
                target.ty.clone(),
                span,
                BoundExpressionKind::Assignment { target: Box::new(target), value: Box::new(value) },
            );
        }
        let value: BoundExpression = self.bind_expression(value_syntax);
        if operator == "=" {
            let converted: BoundExpression = self.convert(value, &target.ty, false, value_syntax.span);
            return BoundExpression::new(
                target.ty.clone(),
                span,
                BoundExpressionKind::Assignment { target: Box::new(target), value: Box::new(converted) },
            );
        }

        let operation: Option<BinaryOperator> = type_rules::parse_binary_operator(&operator[..operator.len() - 1]);
        let Some(operation) = operation.filter(|_| !value.is_error()) else {
            return BoundExpression::error(span);
        };
        let (ShaderType::Numeric(target_type), ShaderType::Numeric(value_type)) = (&target.ty, &value.ty) else {
            self.error(span, format!("'{}' can't combine {} and {}", operator, target.ty, value.ty));
            return BoundExpression::error(span);
        };
        let target_type: NumericType = *target_type;
        let mut warning: Option<String> = None;
        let resolved: BinaryResolution =
            match type_rules::resolve_binary(operation, &target_type, value_type, &mut warning) {
                Ok(resolved) => resolved,
                Err(error) => {
                    self.error(span, error);
                    return BoundExpression::error(span);
                }
            };
        if let Some(warning) = warning {
            self.warning(span, warning);
        }
        let operation_type: ShaderType = resolved.operand_type.into();
        if !type_rules::classify(&resolved.result_type.into(), &target_type.into(), false).is_possible {
            self.error(span, format!("can't store {} in {}", resolved.result_type, target_type));
            return BoundExpression::error(span);
        }
        let converted: BoundExpression = self.convert_quietly(value, &resolved.right_type.into());
        BoundExpression::new(
            target.ty.clone(),
            span,
            BoundExpressionKind::CompoundAssignment {
                operator: operation,
                target: Box::new(target),
                value: Box::new(converted),
                operation_type,
            },
        )
    }

    fn check_assignable(&mut self, target: &BoundExpression, span: SourceSpan) -> bool {
        match &target.kind {
            BoundExpressionKind::Variable(variable) => {
                if variable.is_const
                    && !(variable.kind == VariableKind::Uniform && self.is_interactive && self.function.is_none())
                {
                    let message: String = if variable.kind == VariableKind::Uniform {
                        format!("'{}' is a uniform: read-only in functions (make it static to write it)", variable.name)
                    } else {
                        format!("'{}' is const", variable.name)
                    };
                    self.error(span, message);
                    return false;
                }
                true
            }
            BoundExpressionKind::Swizzle { operand, indices, text } => {
                let distinct: HashSet<usize> = indices.iter().copied().collect();
                if distinct.len() != indices.len() {
                    self.error(span, format!("can't assign to '.{text}': it repeats a component"));
                    return false;
                }
                self.check_assignable(operand, span)
            }
            BoundExpressionKind::Index { operand, .. } | BoundExpressionKind::Field { operand, .. } => {
                self.check_assignable(operand, span)
            }
            _ => {
                self.error(span, "this expression can't be assigned");
                false
            }
        }
    }

    fn bind_conditional(
        &mut self,
        condition_syntax: &ExpressionSyntax,
        true_syntax: &ExpressionSyntax,
        false_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        let condition: BoundExpression = self.bind_expression(condition_syntax);
        let when_true: BoundExpression = self.bind_expression(true_syntax);
        let when_false: BoundExpression = self.bind_expression(false_syntax);
        if condition.is_error() || when_true.is_error() || when_false.is_error() {
            return BoundExpression::error(span);
        }
        if !matches!(&condition.ty, ShaderType::Numeric(numeric) if numeric.size() == 1) {
            self.error(
                condition_syntax.span,
                format!("?: needs a scalar condition in HLSL 2021, got {}; use select() for vectors", condition.ty),
            );
            return BoundExpression::error(span);
        }
        let bool_condition: BoundExpression =
            self.convert(condition, &ShaderType::bool(), false, condition_syntax.span);
        let mut common: Option<ShaderType> =
            if when_true.ty == when_false.ty { Some(when_true.ty.clone()) } else { None };
        if common.is_none()
            && let (ShaderType::Numeric(true_type), ShaderType::Numeric(false_type)) = (&when_true.ty, &when_false.ty)
        {
            let mut warning: Option<String> = None;
            common = type_rules::combine_shapes(
                true_type,
                false_type,
                type_rules::common_kind(true_type.kind, false_type.kind),
                &mut warning,
            )
            .map(ShaderType::from);
            if let Some(warning) = warning {
                self.warning(span, warning);
            }
        }
        let Some(common) = common else {
            self.error(span, format!("?: branches have incompatible types {} and {}", when_true.ty, when_false.ty));
            return BoundExpression::error(span);
        };
        let when_true: BoundExpression = self.convert_quietly(when_true, &common);
        let when_false: BoundExpression = self.convert_quietly(when_false, &common);
        BoundExpression::new(
            when_true.ty.clone(),
            span,
            BoundExpressionKind::Conditional {
                condition: Box::new(bool_condition),
                when_true: Box::new(when_true),
                when_false: Box::new(when_false),
            },
        )
    }

    fn bind_cast(
        &mut self,
        type_syntax: &TypeSyntax,
        array_dimensions: &[ExpressionSyntax],
        operand_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        let Some(ty) = self.resolve_type(type_syntax) else {
            return BoundExpression::error(span);
        };
        let dimensions: Vec<Option<ExpressionSyntax>> = array_dimensions.iter().cloned().map(Some).collect();
        let ty: Option<ShaderType> = self.with_array_dimensions(&ty, &dimensions, span, None);
        let operand: BoundExpression = self.bind_expression(operand_syntax);
        match ty {
            None => BoundExpression::error(span),
            Some(ty) => self.convert(operand, &ty, true, span),
        }
    }

    // ---- Calls ----

    fn bind_call(
        &mut self,
        name: &str,
        name_span: SourceSpan,
        argument_syntax: &[ExpressionSyntax],
        span: SourceSpan,
    ) -> BoundExpression {
        // Constructors: float3(...), a typedef'd vector type...
        if let Ok(Some(builtin)) = try_parse_builtin_type(name) {
            return self.bind_constructor(&builtin, argument_syntax, span);
        }
        match self.type_names.get(name).cloned() {
            Some(ShaderType::Struct(_)) => {
                self.error(
                    name_span,
                    format!("HLSL structs have no constructors: use {{ ... }} in a declaration, or ({name})0"),
                );
                return BoundExpression::error(span);
            }
            Some(declared) => return self.bind_constructor(&declared, argument_syntax, span),
            None => {}
        }

        let arguments: Vec<BoundExpression> =
            argument_syntax.iter().map(|argument| self.bind_expression(argument)).collect();
        if arguments.iter().any(BoundExpression::is_error) {
            return BoundExpression::error(span);
        }
        if let Some(overloads) = self.functions.get(name).cloned() {
            return self.bind_user_call(name, name_span, &overloads, arguments, span);
        }
        if let Some(intrinsic) = intrinsics::try_get(name) {
            return self.bind_intrinsic_call(name, name_span, intrinsic, arguments, span);
        }
        let message: String = if intrinsics::is_unsupported(name) {
            format!("'{name}' only exists inside a GPU pipeline and isn't supported here")
        } else {
            format!("unknown function '{name}'")
        };
        self.error(name_span, message);
        BoundExpression::error(span)
    }

    fn bind_constructor(
        &mut self,
        ty: &ShaderType,
        argument_syntax: &[ExpressionSyntax],
        span: SourceSpan,
    ) -> BoundExpression {
        let ShaderType::Numeric(numeric) = ty else {
            self.error(span, format!("{ty} can't be constructed with (...)"));
            return BoundExpression::error(span);
        };
        let arguments: Vec<BoundExpression> =
            argument_syntax.iter().map(|argument| self.bind_expression(argument)).collect();
        if arguments.iter().any(BoundExpression::is_error) {
            return BoundExpression::error(span);
        }
        for argument in &arguments {
            if argument.ty.as_numeric().is_none() {
                self.error(argument.span, format!("{}(...) takes numbers, got {}", numeric, argument.ty));
                return BoundExpression::error(span);
            }
        }
        let count: usize = arguments.iter().map(|argument| argument.ty.component_count()).sum();
        if count != numeric.size() && !(arguments.len() == 1 && count == 1) {
            self.error(span, format!("{} needs {} components, got {}", numeric, numeric.size(), count));
            return BoundExpression::error(span);
        }
        BoundExpression::new(
            ty.clone(),
            span,
            BoundExpressionKind::Construct { sources: arguments, is_initializer_list: false },
        )
    }

    fn bind_user_call(
        &mut self,
        name: &str,
        name_span: SourceSpan,
        overloads: &[FunctionRef],
        arguments: Vec<BoundExpression>,
        span: SourceSpan,
    ) -> BoundExpression {
        let mut viable: Vec<(FunctionRef, i32)> = Vec::new();
        for candidate in overloads {
            if arguments.len() > candidate.parameters.len() || arguments.len() < candidate.required_parameter_count() {
                continue;
            }
            let mut cost: i32 = 0;
            let mut index: usize = 0;
            while index < arguments.len() && cost != i32::MAX {
                let parameter: &VariableRef = &candidate.parameters[index];
                let inward: ConversionInfo = type_rules::classify(&arguments[index].ty, &parameter.ty, false);
                let outward: ConversionInfo = type_rules::classify(&parameter.ty, &arguments[index].ty, false);
                let mode: ParameterMode = parameter.parameter_mode();
                let is_possible: bool = match mode {
                    ParameterMode::In => inward.is_possible,
                    ParameterMode::Out => outward.is_possible,
                    ParameterMode::InOut => inward.is_possible && outward.is_possible,
                };
                cost = if is_possible {
                    cost + if mode == ParameterMode::Out { outward.cost } else { inward.cost }
                } else {
                    i32::MAX
                };
                index += 1;
            }
            if cost != i32::MAX {
                viable.push((candidate.clone(), cost));
            }
        }
        let argument_list: String =
            arguments.iter().map(|argument| argument.ty.to_string()).collect::<Vec<String>>().join(", ");
        if viable.is_empty() {
            let candidates: Vec<String> = overloads.iter().map(|overload| overload.signature()).collect();
            self.error(
                name_span,
                format!("no '{}' takes ({}); candidates: {}", name, argument_list, candidates.join("; ")),
            );
            return BoundExpression::error(span);
        }
        let best: i32 = viable.iter().map(|(_, cost)| *cost).min().expect("viable is not empty");
        let best_overloads: Vec<FunctionRef> =
            viable.into_iter().filter(|(_, cost)| *cost == best).map(|(function, _)| function).collect();
        if best_overloads.len() > 1 {
            let signatures: Vec<String> = best_overloads.iter().map(|overload| overload.signature()).collect();
            self.error(
                name_span,
                format!("call of '{}({})' is ambiguous: {}", name, argument_list, signatures.join("; ")),
            );
            return BoundExpression::error(span);
        }

        let function: FunctionRef = best_overloads.into_iter().next().expect("one best overload");
        let explicit_count: usize = arguments.len();
        let mut converted: Vec<BoundExpression> = Vec::new();
        let mut remaining = arguments.into_iter();
        for parameter in &function.parameters {
            let Some(argument) = remaining.next() else {
                let default_value: Arc<BoundExpression> =
                    parameter.default_value().expect("missing arguments have defaults");
                converted.push((*default_value).clone());
                continue;
            };
            if parameter.parameter_mode() == ParameterMode::In {
                let argument_span: SourceSpan = argument.span;
                converted.push(self.convert(argument, &parameter.ty, false, argument_span));
            } else {
                if !self.check_assignable(&argument, argument.span) {
                    return BoundExpression::error(span);
                }
                converted.push(argument);
            }
        }
        if let Some(caller) = &self.function
            && let Some((_, callees)) = self.calls.iter_mut().find(|(function, _)| function.id == caller.id)
            && !callees.iter().any(|callee| callee.id == function.id)
        {
            callees.push(function.clone());
        }
        BoundExpression::new(
            function.return_type.clone(),
            span,
            BoundExpressionKind::Call { function, arguments: converted, explicit_count },
        )
    }

    fn bind_intrinsic_call(
        &mut self,
        name: &str,
        name_span: SourceSpan,
        intrinsic: &'static Intrinsic,
        arguments: Vec<BoundExpression>,
        span: SourceSpan,
    ) -> BoundExpression {
        let argument_types: Vec<ShaderType> = arguments.iter().map(|argument| argument.ty.clone()).collect();
        let signature: IntrinsicSignature = match (intrinsic.resolve)(&argument_types) {
            Ok(signature) => signature,
            Err(error) => {
                let list: Vec<String> = argument_types.iter().map(ShaderType::to_string).collect();
                self.error(name_span, format!("{}({}): {}", name, list.join(", "), error));
                return BoundExpression::error(span);
            }
        };
        let mut converted: Vec<BoundExpression> = Vec::new();
        let mut constants: Vec<Option<Value>> = Vec::new();
        for (index, argument) in arguments.into_iter().enumerate() {
            if signature.modes[index] == ParameterMode::In {
                let argument_span: SourceSpan = argument.span;
                let bound: BoundExpression =
                    self.convert(argument, &signature.parameter_types[index], false, argument_span);
                let constant: Option<Value> =
                    if bound.is_error() { None } else { try_evaluate_constant(&bound, &self.profile) };
                converted.push(bound);
                constants.push(constant);
            } else {
                let fits: bool =
                    type_rules::classify(&signature.parameter_types[index], &argument.ty, false).is_possible;
                if !self.check_assignable(&argument, argument.span) || !fits {
                    if !fits {
                        self.error(
                            argument.span,
                            format!(
                                "{} writes a {} here, which can't go into {}",
                                name, signature.parameter_types[index], argument.ty
                            ),
                        );
                    }
                    return BoundExpression::error(span);
                }
                converted.push(argument);
                constants.push(None);
            }
        }
        if converted.iter().any(BoundExpression::is_error) {
            return BoundExpression::error(span);
        }
        BoundExpression::new(
            signature.return_type.clone(),
            span,
            BoundExpressionKind::IntrinsicCall {
                intrinsic,
                signature,
                arguments: converted,
                constant_arguments: constants,
            },
        )
    }

    // ---- Members and indexing ----

    fn bind_member(
        &mut self,
        target_syntax: &ExpressionSyntax,
        member: &str,
        member_span: SourceSpan,
        span: SourceSpan,
    ) -> BoundExpression {
        let target: BoundExpression = self.bind_expression(target_syntax);
        if target.is_error() {
            return target;
        }
        match target.ty.clone() {
            ShaderType::Struct(structure) => {
                let Some(field) = structure.find_field(member).cloned() else {
                    self.error(member_span, format!("'{}' has no field '{}'", structure.name, member));
                    return BoundExpression::error(span);
                };
                BoundExpression::new(
                    field.ty.clone(),
                    span,
                    BoundExpressionKind::Field { operand: Box::new(target), field },
                )
            }
            ShaderType::Numeric(matrix) if matrix.is_matrix() => {
                self.bind_matrix_swizzle(target, matrix, member, member_span, span)
            }
            ShaderType::Numeric(numeric) => self.bind_swizzle(target, numeric, member, member_span, span),
            other => {
                self.error(member_span, format!("{other} has no member '{member}'"));
                BoundExpression::error(span)
            }
        }
    }

    fn bind_swizzle(
        &mut self,
        target: BoundExpression,
        numeric: NumericType,
        text: &str,
        member_span: SourceSpan,
        span: SourceSpan,
    ) -> BoundExpression {
        let is_color: bool = text.contains(['r', 'g', 'b', 'a']);
        let set: &str = if is_color { "rgba" } else { "xyzw" };
        let indices: Vec<i64> =
            text.chars().map(|character| set.find(character).map_or(-1, |index| index as i64)).collect();
        if text.chars().count() > 4 || indices.iter().any(|index| *index < 0 || *index >= numeric.size() as i64) {
            let message: String = if indices.iter().any(|index| *index < 0) {
                format!("invalid swizzle '.{text}' (use xyzw or rgba, not both)")
            } else {
                format!("'.{}' reads past the {} components of {}", text, numeric.size(), numeric)
            };
            self.error(member_span, message);
            return BoundExpression::error(span);
        }
        let indices: Vec<usize> = indices.into_iter().map(|index| index as usize).collect();
        BoundExpression::new(
            ShaderType::scalar_or_vector(numeric.kind, indices.len()),
            span,
            BoundExpressionKind::Swizzle { operand: Box::new(target), indices, text: text.to_string() },
        )
    }

    /// m._m01 (0-based) or m._12 (1-based), chained: m._m00_m11.
    fn bind_matrix_swizzle(
        &mut self,
        target: BoundExpression,
        matrix: NumericType,
        member: &str,
        member_span: SourceSpan,
        span: SourceSpan,
    ) -> BoundExpression {
        let text: Vec<char> = member.chars().collect();
        let mut indices: Vec<usize> = Vec::new();
        let mut position: usize = 0;
        while position < text.len() {
            let is_zero_based: bool = position + 1 < text.len() && text[position] == '_' && text[position + 1] == 'm';
            let start: usize = position + if is_zero_based { 2 } else { 1 };
            if text[position] != '_'
                || start + 2 > text.len()
                || !text[start].is_ascii_digit()
                || !text[start + 1].is_ascii_digit()
            {
                self.error(member_span, format!("invalid matrix member '.{member}' (use ._m01 or ._12)"));
                return BoundExpression::error(span);
            }
            let offset: i64 = if is_zero_based { 0 } else { 1 };
            let row: i64 = (text[start] as i64 - '0' as i64) - offset;
            let column: i64 = (text[start + 1] as i64 - '0' as i64) - offset;
            if row < 0 || column < 0 || row >= matrix.rows as i64 || column >= matrix.columns as i64 {
                self.error(member_span, format!("'.{member}' is outside {matrix}"));
                return BoundExpression::error(span);
            }
            indices.push(row as usize * matrix.columns + column as usize);
            position = start + 2;
        }
        if indices.len() > 4 {
            self.error(member_span, format!("'.{member}' selects more than 4 elements"));
            return BoundExpression::error(span);
        }
        BoundExpression::new(
            ShaderType::scalar_or_vector(matrix.kind, indices.len()),
            span,
            BoundExpressionKind::Swizzle { operand: Box::new(target), indices, text: member.to_string() },
        )
    }

    fn bind_index(
        &mut self,
        target_syntax: &ExpressionSyntax,
        index_syntax: &ExpressionSyntax,
        span: SourceSpan,
    ) -> BoundExpression {
        let target: BoundExpression = self.bind_expression(target_syntax);
        let index: BoundExpression = self.bind_expression(index_syntax);
        if target.is_error() || index.is_error() {
            return BoundExpression::error(span);
        }
        let index_kind: ScalarKind = match &index.ty {
            ShaderType::Numeric(index_type) if index_type.size() == 1 && !index_type.kind.is_float() => index_type.kind,
            other => {
                self.error(index_syntax.span, format!("an index must be an integer, got {other}"));
                return BoundExpression::error(span);
            }
        };
        let target_kind: ScalarKind = if index_kind == ScalarKind::UInt { ScalarKind::UInt } else { ScalarKind::Int };
        let converted_index: BoundExpression =
            self.convert(index, &ShaderType::scalar(target_kind), false, index_syntax.span);
        let (element_type, length): (ShaderType, usize) = match &target.ty {
            ShaderType::Array(array) => (array.element.clone(), array.length),
            ShaderType::Numeric(matrix) if matrix.is_matrix() => {
                (ShaderType::vector(matrix.kind, matrix.columns), matrix.rows)
            }
            ShaderType::Numeric(vector) if vector.is_vector() => (ShaderType::scalar(vector.kind), vector.size()),
            _ => (ShaderType::Void, 0),
        };
        if length == 0 {
            self.error(span, format!("{} can't be indexed", target.ty));
            return BoundExpression::error(span);
        }
        if let Some(constant) = try_evaluate_constant(&converted_index, &self.profile) {
            let number: i64 = constant.get_number(0) as i64;
            if number < 0 || number >= length as i64 {
                self.error(index_syntax.span, format!("index {} is out of range [0, {}]", number, length - 1));
                return BoundExpression::error(span);
            }
        }
        BoundExpression::new(
            element_type,
            span,
            BoundExpressionKind::Index { operand: Box::new(target), index: Box::new(converted_index), length },
        )
    }
}

/// The field of a struct value, used by evaluation and emission.
pub fn field_components(field: &StructField) -> std::ops::Range<usize> {
    field.component_offset..field.component_offset + field.ty.component_count()
}
