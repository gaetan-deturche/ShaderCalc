use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use super::arithmetic;
use super::intrinsic_context::IntrinsicContext;
use super::unit_checker::UnitChecker;
use crate::binding::bound_tree::*;
use crate::binding::program::{BoundInteractive, BoundProgram, ScriptLine};
use crate::binding::symbols::{FunctionRef, VariableKind, VariableRef};
use crate::binding::type_rules::{self, ConversionInfo};
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};
use crate::semantics::SemanticsProfile;
use crate::syntax::tree::ParameterMode;
use crate::types::{ScalarKind, ShaderType};
use crate::units::{Dimension, UnitTag};
use crate::values::{Value, scalars};

/// An evaluation failure at a place in the code.
#[derive(Clone, Debug, PartialEq)]
pub struct EvaluationError {
    pub message: String,
    pub span: SourceSpan,
    /// File and function the error happened in (set by the innermost call it escapes; None: top level).
    pub source_name: Option<String>,
    pub function_name: Option<String>,
}

/// Why an evaluation stopped early.
#[derive(Clone, Debug, PartialEq)]
pub enum Interrupt {
    Error(EvaluationError),
    /// Constant folding met something that isn't constant.
    NotConstant,
    Cancelled,
}

pub type EvalResult<T> = Result<T, Interrupt>;

fn failure<T>(message: impl Into<String>, span: SourceSpan) -> EvalResult<T> {
    Err(Interrupt::Error(EvaluationError { message: message.into(), span, source_name: None, function_name: None }))
}

#[derive(Clone, Debug)]
pub struct EvaluationOptions {
    /// Report every bare literal that adopts a unit.
    pub strict_units: bool,
    /// Statements + expressions evaluated before giving up (infinite loops).
    pub max_steps: i64,
    pub cancellation: Option<Arc<AtomicBool>>,
}

impl Default for EvaluationOptions {
    fn default() -> EvaluationOptions {
        EvaluationOptions { strict_units: false, max_steps: 50_000_000, cancellation: None }
    }
}

impl EvaluationOptions {
    pub fn is_cancelled(&self) -> bool {
        self.cancellation.as_ref().is_some_and(|flag| flag.load(Ordering::Relaxed))
    }
}

/// Values of variables, keyed by symbol.
pub type Storage = HashMap<VariableRef, Value>;

struct Frame {
    function: Option<FunctionRef>,
    locals: Storage,
}

impl Frame {
    fn new(function: Option<FunctionRef>) -> Frame {
        Frame { function, locals: Storage::new() }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Flow {
    Normal,
    Break,
    Continue,
    Return,
}

/// Where a variable's value lives: a frame's locals (by frame index) or the shared storage.
#[derive(Clone)]
enum PlaceRoot {
    Local { frame: usize, variable: VariableRef },
    Storage(VariableRef),
}

/// Where a write goes: some components of a variable's storage.
#[derive(Clone)]
struct Place {
    root: PlaceRoot,
    components: Vec<usize>,
    ty: ShaderType,
}

const MAX_CALL_DEPTH: usize = 64;

/// The value of a constant expression (literals, static consts, pure operations), or None.
pub fn try_evaluate_constant(expression: &BoundExpression, profile: &SemanticsProfile) -> Option<Value> {
    let options: EvaluationOptions = EvaluationOptions { max_steps: 100_000, ..EvaluationOptions::default() };
    let mut storage: Storage = Storage::new();
    let mut diagnostics: DiagnosticBag = DiagnosticBag::new();
    let mut evaluator: Evaluator = Evaluator::create(profile, &options, &mut storage, &mut diagnostics, "", true);
    evaluator.evaluate(expression).ok()
}

/// "50,000,000".
fn group_thousands(number: i64) -> String {
    let digits: String = number.unsigned_abs().to_string();
    let mut grouped: String = String::new();
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            grouped.push(',');
        }
        grouped.push(digit);
    }
    if number < 0 { format!("-{grouped}") } else { grouped }
}

/// Executes the bound tree. Variables live in per-call frames keyed by symbol; globals, uniforms and session
/// variables in a storage the caller owns. Every write copies, so values are never shared between variables.
pub struct Evaluator<'a> {
    profile: SemanticsProfile,
    options: &'a EvaluationOptions,
    storage: &'a mut Storage,
    diagnostics: &'a mut DiagnosticBag,
    line_source_name: String,
    is_constant_mode: bool,
    steps: i64,
    call_depth: usize,
    return_value: Option<Value>,
    /// Frames of the calls in progress; the first one is the top level.
    frames: Vec<Frame>,
    last_result: Option<Value>,
    current_source: Option<String>,
    /// Calls the reference can't be trusted on (WARP's own limits), in the order met.
    reference_limits: Vec<String>,
}

impl<'a> Evaluator<'a> {
    /// `line_source_name`: file named in diagnostics of top-level code (functions report their own).
    pub fn new(
        profile: &SemanticsProfile,
        options: &'a EvaluationOptions,
        storage: &'a mut Storage,
        diagnostics: &'a mut DiagnosticBag,
        line_source_name: &str,
    ) -> Evaluator<'a> {
        Evaluator::create(profile, options, storage, diagnostics, line_source_name, false)
    }

    fn create(
        profile: &SemanticsProfile,
        options: &'a EvaluationOptions,
        storage: &'a mut Storage,
        diagnostics: &'a mut DiagnosticBag,
        line_source_name: &str,
        is_constant_mode: bool,
    ) -> Evaluator<'a> {
        Evaluator {
            profile: profile.clone(),
            options,
            storage,
            diagnostics,
            line_source_name: line_source_name.to_string(),
            is_constant_mode,
            steps: 0,
            call_depth: 0,
            return_value: None,
            frames: vec![Frame::new(None)],
            last_result: None,
            current_source: None,
            reference_limits: Vec::new(),
        }
    }

    pub fn profile(&self) -> &SemanticsProfile {
        &self.profile
    }

    /// Globals, uniforms and session variables as they stand.
    pub fn storage(&self) -> &Storage {
        self.storage
    }

    /// Why WARP's result can't be trusted for what ran so far (empty when it can).
    pub fn take_reference_limits(&mut self) -> Vec<String> {
        std::mem::take(&mut self.reference_limits)
    }

    /// Initializes static globals (once per evaluation, like a shader invocation) and uniforms not set yet.
    pub fn initialize_globals(&mut self, program: &BoundProgram) -> EvalResult<()> {
        for global in &program.globals {
            if global.kind == VariableKind::Uniform && self.storage.contains_key(global) {
                continue;
            }
            let value: Value = match global.initializer() {
                None => Value::zero(global.ty.clone(), UnitTag::BARE),
                Some(initializer) => self.evaluate(&initializer)?,
            };
            self.storage.insert(global.clone(), value);
        }
        Ok(())
    }

    /// Runs a calculator line; returns the value it shows (None for none).
    pub fn run(&mut self, line: &BoundInteractive) -> EvalResult<Option<Value>> {
        self.frames = vec![Frame::new(None)];
        for statement in &line.statements {
            let flow: Flow = self.execute(statement)?;
            if flow != Flow::Normal {
                break;
            }
        }
        if let Some(variable) = &line.result_variable {
            return self.read_variable(variable, variable.span).map(Some);
        }
        Ok(if line.result().is_none() { None } else { self.last_result.clone() })
    }

    /// Worksheet: every global starts at zero (consts at their value); declaration lines then set them in order.
    pub fn zero_globals(&mut self, program: &BoundProgram) {
        for global in &program.globals {
            let value: Value = global.constant_value().unwrap_or_else(|| Value::zero(global.ty.clone(), UnitTag::BARE));
            self.storage.insert(global.clone(), value);
        }
    }

    /// Runs one worksheet line; returns the value it shows (its expression, or the variable it declares).
    pub fn run_line(&mut self, line: &ScriptLine) -> EvalResult<Option<Value>> {
        self.frames.truncate(1);
        self.last_result = None;
        self.execute(&line.statement)?;
        if line.shows_result {
            return Ok(self.last_result.clone());
        }
        match line.declared.last() {
            Some(variable) => self.read_variable(variable, line.span).map(Some),
            None => Ok(None),
        }
    }

    /// File named in diagnostics of top-level code (functions report their own file).
    pub fn current_source(&self) -> String {
        self.current_source.clone().unwrap_or_else(|| self.line_source_name.clone())
    }

    pub fn set_current_source(&mut self, source: &str) {
        self.current_source = Some(source.to_string());
    }

    /// Calls a function directly with argument values (already of the parameter types).
    pub fn call(&mut self, function: &FunctionRef, arguments: &[Value]) -> EvalResult<Value> {
        let mut frame: Frame = Frame::new(Some(function.clone()));
        for (index, parameter) in function.parameters.iter().enumerate() {
            let value: Value = match arguments.get(index) {
                Some(argument) => argument.clone(),
                None => Value::zero(parameter.ty.clone(), UnitTag::BARE),
            };
            frame.locals.insert(parameter.clone(), value);
        }
        let span: SourceSpan = function.syntax().name_span;
        self.invoke(function, frame, span).map(|(result, _)| result)
    }

    // ---------------------------------------------------------------- Diagnostics

    fn frame(&self) -> &Frame {
        self.frames.last().expect("a frame is always active")
    }

    fn source_name(&self) -> String {
        match &self.frame().function {
            Some(function) => function.source_name(),
            None => self.current_source(),
        }
    }

    fn report(&mut self, severity: DiagnosticSeverity, message: String, span: SourceSpan) {
        if !self.is_constant_mode {
            let function: Option<String> = self.frame().function.as_ref().map(|function| function.name.clone());
            let diagnostic: Diagnostic =
                Diagnostic::new(severity, message, self.source_name(), span).in_function(function);
            self.diagnostics.add(diagnostic);
        }
    }

    /// Adds what a unit checker reported, at the operation's span.
    fn flush_units(&mut self, units: &UnitChecker, span: SourceSpan) {
        for (severity, message) in units.take_reports() {
            self.report(severity, message, span);
        }
    }

    fn units(&self) -> UnitChecker {
        UnitChecker::new(self.options.strict_units)
    }

    fn step(&mut self, span: SourceSpan) -> EvalResult<()> {
        self.steps += 1;
        if self.steps > self.options.max_steps {
            return failure(
                format!("stopped after {} steps (infinite loop?)", group_thousands(self.options.max_steps)),
                span,
            );
        }
        if (self.steps & 0xFFF) == 0 && self.options.is_cancelled() {
            return Err(Interrupt::Cancelled);
        }
        Ok(())
    }

    // ---------------------------------------------------------------- Statements

    fn execute(&mut self, statement: &BoundStatement) -> EvalResult<Flow> {
        self.step(statement.span)?;
        match &statement.kind {
            BoundStatementKind::Block { statements, .. } => {
                for inner in statements {
                    let flow: Flow = self.execute(inner)?;
                    if flow != Flow::Normal {
                        return Ok(flow);
                    }
                }
                Ok(Flow::Normal)
            }
            BoundStatementKind::VariableDeclaration { variable, initializer } => {
                let value: Value = match initializer {
                    None => Value::zero(variable.ty.clone(), UnitTag::BARE),
                    Some(initializer) => self.evaluate(initializer)?,
                };
                if matches!(variable.kind, VariableKind::Session | VariableKind::Uniform | VariableKind::Static) {
                    self.storage.insert(variable.clone(), value);
                } else {
                    self.frames.last_mut().expect("a frame is always active").locals.insert(variable.clone(), value);
                }
                Ok(Flow::Normal)
            }
            BoundStatementKind::Expression(expression) => {
                let value: Value = self.evaluate(expression)?;
                self.last_result = Some(value);
                Ok(Flow::Normal)
            }
            BoundStatementKind::If { condition, then, otherwise } => {
                if self.evaluate(condition)?.get_bool(0, &self.profile) {
                    return self.execute(then);
                }
                match otherwise {
                    None => Ok(Flow::Normal),
                    Some(otherwise) => self.execute(otherwise),
                }
            }
            BoundStatementKind::Loop { initializer, condition, step, body, is_do_while } => {
                self.execute_loop(initializer.as_deref(), condition.as_ref(), step.as_ref(), body, *is_do_while)
            }
            BoundStatementKind::Switch { value, sections } => self.execute_switch(value, sections),
            BoundStatementKind::Return(value) => {
                self.return_value = match value {
                    None => None,
                    Some(value) => Some(self.evaluate(value)?),
                };
                Ok(Flow::Return)
            }
            BoundStatementKind::Jump { is_break } => Ok(if *is_break { Flow::Break } else { Flow::Continue }),
        }
    }

    fn execute_loop(
        &mut self,
        initializer: Option<&BoundStatement>,
        condition: Option<&BoundExpression>,
        step: Option<&BoundExpression>,
        body: &BoundStatement,
        is_do_while: bool,
    ) -> EvalResult<Flow> {
        if let Some(initializer) = initializer {
            self.execute(initializer)?;
        }
        let mut is_first: bool = true;
        loop {
            if !(is_do_while && is_first)
                && let Some(condition) = condition
                && !self.evaluate(condition)?.get_bool(0, &self.profile)
            {
                return Ok(Flow::Normal);
            }
            is_first = false;
            let flow: Flow = self.execute(body)?;
            if flow == Flow::Break {
                return Ok(Flow::Normal);
            }
            if flow == Flow::Return {
                return Ok(flow);
            }
            if let Some(step) = step {
                self.evaluate(step)?;
            }
        }
    }

    fn execute_switch(&mut self, value: &BoundExpression, sections: &[BoundSwitchSection]) -> EvalResult<Flow> {
        let selector: i64 = self.evaluate(value)?.get_number(0) as i64;
        let start: Option<usize> = sections
            .iter()
            .position(|section| section.labels.contains(&selector))
            .or_else(|| sections.iter().position(|section| section.is_default));
        let Some(start) = start else {
            return Ok(Flow::Normal);
        };
        // Fall through the following sections until a break
        for section in &sections[start..] {
            for statement in &section.statements {
                let flow: Flow = self.execute(statement)?;
                if flow == Flow::Break {
                    return Ok(Flow::Normal);
                }
                if flow != Flow::Normal {
                    return Ok(flow);
                }
            }
        }
        Ok(Flow::Normal)
    }

    // ---------------------------------------------------------------- Expressions

    pub fn evaluate(&mut self, expression: &BoundExpression) -> EvalResult<Value> {
        self.step(expression.span)?;
        match &expression.kind {
            BoundExpressionKind::Literal(value) => Ok(value.clone()),
            BoundExpressionKind::UnitLiteral { si_value, dimension, .. } => {
                Ok(Value::scalar(ScalarKind::LiteralFloat, scalars::from_double(*si_value), UnitTag::of(*dimension)))
            }
            BoundExpressionKind::Variable(variable) => self.read_variable(variable, expression.span),
            BoundExpressionKind::Unary { operator, operand } => {
                self.evaluate_unary(*operator, operand, &expression.ty, expression.span)
            }
            BoundExpressionKind::Binary { operator, left, right } => {
                let left: Value = self.evaluate(left)?;
                let right: Value = self.evaluate(right)?;
                Ok(self.evaluate_binary(*operator, &left, &right, &expression.ty, expression.span))
            }
            BoundExpressionKind::Logical { is_and, left, right } => {
                let left: bool = self.evaluate(left)?.get_bool(0, &self.profile);
                if left != *is_and {
                    return Ok(Value::from_bool(left));
                }
                Ok(Value::from_bool(self.evaluate(right)?.get_bool(0, &self.profile)))
            }
            BoundExpressionKind::Assignment { target, value } => {
                self.not_in_constant_mode()?;
                let place: Place = self.resolve_place(target)?;
                let value: Value = self.evaluate(value)?;
                self.write(&place, &value);
                Ok(self.read(&place))
            }
            BoundExpressionKind::CompoundAssignment { operator, target, value, operation_type } => {
                self.evaluate_compound_assignment(*operator, target, value, operation_type, expression.span)
            }
            BoundExpressionKind::Increment { target, is_increment, is_prefix } => {
                self.evaluate_increment(target, *is_increment, *is_prefix, expression.span)
            }
            BoundExpressionKind::Conditional { condition, when_true, when_false } => {
                if self.evaluate(condition)?.get_bool(0, &self.profile) {
                    self.evaluate(when_true)
                } else {
                    self.evaluate(when_false)
                }
            }
            BoundExpressionKind::Call { function, arguments, .. } => {
                self.not_in_constant_mode()?;
                self.evaluate_call(function, arguments, expression.span)
            }
            BoundExpressionKind::IntrinsicCall { intrinsic, signature, arguments, constant_arguments } => {
                let mut values: Vec<Value> = Vec::with_capacity(arguments.len());
                let mut outputs: Vec<Option<Place>> = vec![None; arguments.len()];
                for (index, argument) in arguments.iter().enumerate() {
                    if signature.modes[index] == ParameterMode::In {
                        values.push(self.evaluate(argument)?);
                    } else {
                        self.not_in_constant_mode()?;
                        outputs[index] = Some(self.resolve_place(argument)?);
                        values.push(Value::zero(signature.parameter_types[index].clone(), UnitTag::BARE));
                    }
                }
                let context: IntrinsicContext = IntrinsicContext::new(
                    signature,
                    &expression.ty,
                    constant_arguments,
                    &self.profile,
                    self.options.strict_units,
                    arguments.len(),
                );
                let result: Value = (intrinsic.implementation)(&context, &values);
                let reports: Vec<(DiagnosticSeverity, String)> = context.units.take_reports();
                let produced: Vec<Option<Value>> = context.outputs.into_inner();
                for limit in context.reference_limits.into_inner() {
                    if !self.reference_limits.contains(&limit) {
                        self.reference_limits.push(limit);
                    }
                }
                for (severity, message) in reports {
                    self.report(severity, message, expression.span);
                }
                for (place, output) in outputs.iter().zip(produced) {
                    if let (Some(place), Some(output)) = (place, output) {
                        let converted: Value = self.convert_implicitly(output, &place.ty);
                        self.write(place, &converted);
                    }
                }
                Ok(result)
            }
            BoundExpressionKind::Conversion { operand, kind, .. } => {
                let value: Value = self.evaluate(operand)?;
                Ok(self.convert_value(value, &expression.ty, *kind))
            }
            BoundExpressionKind::Construct { sources, .. } => self.evaluate_construct(sources, &expression.ty),
            BoundExpressionKind::Swizzle { operand, indices, .. } => {
                let value: Value = self.evaluate(operand)?;
                Ok(select(&value, indices, &expression.ty))
            }
            BoundExpressionKind::Index { operand, index, length } => {
                let value: Value = self.evaluate(operand)?;
                let index_value: Value = self.evaluate(index)?;
                let element: usize = checked_index(&index_value, *length, expression.span)?;
                let size: usize = expression.ty.component_count();
                let indices: Vec<usize> = (element * size..element * size + size).collect();
                Ok(select(&value, &indices, &expression.ty))
            }
            BoundExpressionKind::Field { operand, field } => {
                let value: Value = self.evaluate(operand)?;
                let indices: Vec<usize> =
                    (field.component_offset..field.component_offset + field.ty.component_count()).collect();
                Ok(select(&value, &indices, &expression.ty))
            }
            BoundExpressionKind::Comma { left, right } => {
                self.evaluate(left)?;
                self.evaluate(right)
            }
            BoundExpressionKind::Error => failure("can't evaluate an expression that has errors", expression.span),
        }
    }

    fn not_in_constant_mode(&self) -> EvalResult<()> {
        if self.is_constant_mode { Err(Interrupt::NotConstant) } else { Ok(()) }
    }

    fn read_variable(&self, variable: &VariableRef, span: SourceSpan) -> EvalResult<Value> {
        if self.is_constant_mode {
            return variable.constant_value().ok_or(Interrupt::NotConstant);
        }
        // A copy: the storage is written in place, and an earlier read must not change with it
        if let Some(local) = self.frame().locals.get(variable).or_else(|| self.storage.get(variable)) {
            return Ok(local.clone());
        }
        if let Some(constant) = variable.constant_value() {
            return Ok(constant);
        }
        failure(format!("'{}' has no value yet", variable.name), span)
    }

    fn evaluate_unary(
        &mut self,
        operator: UnaryOperator,
        operand: &BoundExpression,
        ty: &ShaderType,
        span: SourceSpan,
    ) -> EvalResult<Value> {
        let operand: Value = self.evaluate(operand)?;
        let units: UnitChecker = self.units();
        let mut bits: Vec<u64> = vec![0; operand.bits.len()];
        let mut tags: Vec<UnitTag> = vec![UnitTag::BARE; bits.len()];
        for index in 0..bits.len() {
            bits[index] = arithmetic::unary(operator, operand.kind_at(index), operand.bits[index], &self.profile);
            tags[index] = match operator {
                UnaryOperator::LogicalNot => {
                    UnitTag { dimension: Dimension::NONE, is_adoptable: operand.units[index].is_adoptable }
                }
                UnaryOperator::BitwiseNot => units.drop(operand.units[index], "'~'"),
                _ => operand.units[index],
            };
        }
        self.flush_units(&units, span);
        Ok(Value::new(ty.clone(), bits, tags))
    }

    /// Component-wise binary operation on two values already converted to the operation's shape and kind.
    fn evaluate_binary(
        &mut self,
        operation: BinaryOperator,
        left: &Value,
        right: &Value,
        result_type: &ShaderType,
        span: SourceSpan,
    ) -> Value {
        let units: UnitChecker = self.units();
        let context: String = format!("'{}'", type_rules::symbol(operation));
        let count: usize = result_type.component_count();
        let mut bits: Vec<u64> = vec![0; count];
        let mut tags: Vec<UnitTag> = vec![UnitTag::BARE; count];
        for index in 0..count {
            let left_index: usize = if left.bits.len() == 1 { 0 } else { index };
            let right_index: usize = if right.bits.len() == 1 { 0 } else { index };
            let mut fault: Option<String> = None;
            bits[index] = arithmetic::binary(
                operation,
                left.kind_at(left_index),
                left.bits[left_index],
                right.bits[right_index],
                &self.profile,
                &mut fault,
            );
            if let Some(fault) = fault {
                units.report(DiagnosticSeverity::Warning, fault);
            }
            let left_unit: UnitTag = left.units[left_index];
            let right_unit: UnitTag = right.units[right_index];
            tags[index] = match operation {
                BinaryOperator::Multiply => UnitChecker::multiply(left_unit, right_unit),
                BinaryOperator::Divide => UnitChecker::divide(left_unit, right_unit),
                BinaryOperator::BitwiseAnd | BinaryOperator::BitwiseOr | BinaryOperator::BitwiseXor => {
                    let left_adoptable: bool = units.drop(left_unit, &context).is_adoptable;
                    let right_adoptable: bool = units.drop(right_unit, &context).is_adoptable;
                    UnitTag { dimension: Dimension::NONE, is_adoptable: left_adoptable && right_adoptable }
                }
                BinaryOperator::ShiftLeft | BinaryOperator::ShiftRight => units.drop(left_unit, &context),
                _ if type_rules::is_comparison(operation) => UnitTag {
                    dimension: Dimension::NONE,
                    is_adoptable: units.same(left_unit, right_unit, &context).is_adoptable,
                },
                _ => units.same(left_unit, right_unit, &context),
            };
        }
        self.flush_units(&units, span);
        Value::new(result_type.clone(), bits, tags)
    }

    fn evaluate_compound_assignment(
        &mut self,
        operator: BinaryOperator,
        target: &BoundExpression,
        value: &BoundExpression,
        operation_type: &ShaderType,
        span: SourceSpan,
    ) -> EvalResult<Value> {
        self.not_in_constant_mode()?;
        let place: Place = self.resolve_place(target)?;
        let current: Value = self.convert_implicitly(self.read(&place), operation_type);
        let value: Value = self.evaluate(value)?;
        let result: Value = self.evaluate_binary(operator, &current, &value, operation_type, span);
        let stored: Value = self.convert_implicitly(result, &place.ty);
        self.write(&place, &stored);
        Ok(self.read(&place))
    }

    fn evaluate_increment(
        &mut self,
        target: &BoundExpression,
        is_increment: bool,
        is_prefix: bool,
        span: SourceSpan,
    ) -> EvalResult<Value> {
        self.not_in_constant_mode()?;
        let place: Place = self.resolve_place(target)?;
        let before: Value = self.read(&place);
        let one: Value = self.convert_value(
            Value::scalar(ScalarKind::LiteralInt, 1, UnitTag::BARE),
            &before.ty,
            ConversionKind::Splat,
        );
        // The 1 takes the variable's unit
        let unit_one: Value = Value::new(one.ty.clone(), one.bits.clone(), before.units.clone());
        let operation: BinaryOperator = if is_increment { BinaryOperator::Add } else { BinaryOperator::Subtract };
        let after: Value = self.evaluate_binary(operation, &before, &unit_one, &before.ty.clone(), span);
        self.write(&place, &after);
        Ok(if is_prefix { after } else { before })
    }

    fn evaluate_construct(&mut self, sources: &[BoundExpression], ty: &ShaderType) -> EvalResult<Value> {
        let mut components: Vec<(u64, ScalarKind, UnitTag)> = Vec::new();
        for source in sources {
            let value: Value = self.evaluate(source)?;
            for index in 0..value.bits.len() {
                components.push((value.bits[index], value.kind_at(index), value.units[index]));
            }
        }
        let count: usize = ty.component_count();
        let mut bits: Vec<u64> = vec![0; count];
        let mut units: Vec<UnitTag> = vec![UnitTag::BARE; count];
        for index in 0..count {
            // A single scalar fills every component
            let (source_bits, source_kind, unit) = components[if components.len() == 1 { 0 } else { index }];
            bits[index] = scalars::convert(source_kind, ty.kind_at(index), source_bits, &self.profile);
            units[index] = unit;
        }
        Ok(Value::new(ty.clone(), bits, units))
    }

    // ---------------------------------------------------------------- Conversions

    fn convert_implicitly(&self, value: Value, ty: &ShaderType) -> Value {
        let conversion: ConversionInfo = type_rules::classify(&value.ty, ty, true);
        if conversion.kind == ConversionKind::Identity { value } else { self.convert_value(value, ty, conversion.kind) }
    }

    pub fn convert_value(&self, value: Value, ty: &ShaderType, kind: ConversionKind) -> Value {
        if kind == ConversionKind::Identity && value.ty == *ty {
            return value;
        }
        let count: usize = ty.component_count();
        let mut sources: Vec<usize> = vec![0; count];
        match (kind, &value.ty, ty) {
            (ConversionKind::Splat, _, _) => {
                // all zeros: every component comes from the scalar
            }
            (ConversionKind::Truncation, ShaderType::Numeric(from), ShaderType::Numeric(to))
                if from.is_matrix() && to.is_matrix() =>
            {
                for row in 0..to.rows {
                    for column in 0..to.columns {
                        sources[row * to.columns + column] = row * from.columns + column;
                    }
                }
            }
            _ => {
                // Numeric, flat and vector truncation keep components in order
                for (index, source) in sources.iter_mut().enumerate() {
                    *source = index;
                }
            }
        }
        let mut bits: Vec<u64> = vec![0; count];
        let mut units: Vec<UnitTag> = vec![UnitTag::BARE; count];
        for index in 0..count {
            bits[index] = scalars::convert(
                value.kind_at(sources[index]),
                ty.kind_at(index),
                value.bits[sources[index]],
                &self.profile,
            );
            units[index] = value.units[sources[index]];
        }
        Value::new(ty.clone(), bits, units)
    }

    // ---------------------------------------------------------------- Places (assignment targets)

    fn resolve_place(&mut self, target: &BoundExpression) -> EvalResult<Place> {
        match &target.kind {
            BoundExpressionKind::Variable(variable) => {
                let frame: usize = self.frames.len() - 1;
                let (root, length): (PlaceRoot, usize) = if let Some(local) = self.frames[frame].locals.get(variable) {
                    (PlaceRoot::Local { frame, variable: variable.clone() }, local.bits.len())
                } else if let Some(stored) = self.storage.get(variable) {
                    (PlaceRoot::Storage(variable.clone()), stored.bits.len())
                } else {
                    return failure(format!("'{}' has no storage", variable.name), target.span);
                };
                Ok(Place { root, components: (0..length).collect(), ty: target.ty.clone() })
            }
            BoundExpressionKind::Swizzle { operand, indices, .. } => {
                let place: Place = self.resolve_place(operand)?;
                let components: Vec<usize> = indices.iter().map(|index| place.components[*index]).collect();
                Ok(Place { root: place.root, components, ty: target.ty.clone() })
            }
            BoundExpressionKind::Index { operand, index, length } => {
                let place: Place = self.resolve_place(operand)?;
                let index_value: Value = self.evaluate(index)?;
                let element: usize = checked_index(&index_value, *length, target.span)?;
                let size: usize = target.ty.component_count();
                let components: Vec<usize> = place.components.iter().skip(element * size).take(size).copied().collect();
                Ok(Place { root: place.root, components, ty: target.ty.clone() })
            }
            BoundExpressionKind::Field { operand, field } => {
                let place: Place = self.resolve_place(operand)?;
                let components: Vec<usize> = place
                    .components
                    .iter()
                    .skip(field.component_offset)
                    .take(field.ty.component_count())
                    .copied()
                    .collect();
                Ok(Place { root: place.root, components, ty: target.ty.clone() })
            }
            _ => failure("this expression can't be assigned", target.span),
        }
    }

    fn storage_of(&self, root: &PlaceRoot) -> &Value {
        match root {
            PlaceRoot::Local { frame, variable } => &self.frames[*frame].locals[variable],
            PlaceRoot::Storage(variable) => &self.storage[variable],
        }
    }

    fn storage_of_mut(&mut self, root: &PlaceRoot) -> &mut Value {
        match root {
            PlaceRoot::Local { frame, variable } => {
                self.frames[*frame].locals.get_mut(variable).expect("resolved local")
            }
            PlaceRoot::Storage(variable) => self.storage.get_mut(variable).expect("resolved storage"),
        }
    }

    fn read(&self, place: &Place) -> Value {
        let storage: &Value = self.storage_of(&place.root);
        let bits: Vec<u64> = place.components.iter().map(|index| storage.bits[*index]).collect();
        let units: Vec<UnitTag> = place.components.iter().map(|index| storage.units[*index]).collect();
        Value::new(place.ty.clone(), bits, units)
    }

    /// Writes a value of the place's type. The written components take the value's units.
    fn write(&mut self, place: &Place, value: &Value) {
        let storage: &mut Value = self.storage_of_mut(&place.root);
        for (index, component) in place.components.iter().enumerate() {
            storage.bits[*component] = value.bits[index];
            storage.units[*component] = value.units[index];
        }
    }

    // ---------------------------------------------------------------- Calls

    fn evaluate_call(
        &mut self,
        function: &FunctionRef,
        arguments: &[BoundExpression],
        span: SourceSpan,
    ) -> EvalResult<Value> {
        let mut frame: Frame = Frame::new(Some(function.clone()));
        let mut write_backs: Vec<(Place, VariableRef)> = Vec::new();
        for (parameter, argument) in function.parameters.iter().zip(arguments) {
            if parameter.parameter_mode() == ParameterMode::In {
                let value: Value = self.evaluate(argument)?;
                frame.locals.insert(parameter.clone(), value);
                continue;
            }
            // out / inout: copy in (inout) now, copy out after the call, in parameter order
            let place: Place = self.resolve_place(argument)?;
            let initial: Value = if parameter.parameter_mode() == ParameterMode::InOut {
                self.convert_implicitly(self.read(&place), &parameter.ty)
            } else {
                Value::zero(parameter.ty.clone(), UnitTag::BARE)
            };
            frame.locals.insert(parameter.clone(), initial);
            write_backs.push((place, parameter.clone()));
        }

        let (result, frame) = self.invoke(function, frame, span)?;
        for (place, parameter) in write_backs {
            let value: Value = self.convert_implicitly(frame.locals[&parameter].clone(), &place.ty);
            self.write(&place, &value);
        }
        Ok(result)
    }

    fn invoke(&mut self, function: &FunctionRef, frame: Frame, span: SourceSpan) -> EvalResult<(Value, Frame)> {
        let Some(body) = function.body() else {
            return failure(format!("'{}' has no body", function.name), span);
        };
        if self.call_depth >= MAX_CALL_DEPTH {
            return failure("calls nested too deep (recursion isn't allowed in HLSL)", span);
        }
        self.frames.push(frame);
        self.call_depth += 1;
        self.return_value = None;
        let outcome: EvalResult<Value> = self.execute(&body).and_then(|flow| {
            if function.return_type.is_void() {
                return Ok(Value::void());
            }
            match self.return_value.take() {
                Some(value) if flow == Flow::Return => Ok(value),
                _ => {
                    failure(format!("'{}' ended without returning a value", function.name), function.syntax().name_span)
                }
            }
        });
        self.call_depth -= 1;
        let frame: Frame = self.frames.pop().expect("the callee's frame");
        match outcome {
            Ok(value) => Ok((value, frame)),
            Err(Interrupt::Error(mut error)) => {
                if error.source_name.is_none() {
                    error.source_name = Some(function.source_name());
                    error.function_name = Some(function.name.clone());
                }
                Err(Interrupt::Error(error))
            }
            Err(other) => Err(other),
        }
    }
}

fn select(value: &Value, indices: &[usize], ty: &ShaderType) -> Value {
    let bits: Vec<u64> = indices.iter().map(|index| value.bits[*index]).collect();
    let units: Vec<UnitTag> = indices.iter().map(|index| value.units[*index]).collect();
    Value::new(ty.clone(), bits, units)
}

fn checked_index(index: &Value, length: usize, span: SourceSpan) -> EvalResult<usize> {
    let number: i64 =
        if index.kind_at(0) == ScalarKind::UInt { index.bits[0] as u32 as i64 } else { index.get_number(0) as i64 };
    if number < 0 || number >= length as i64 {
        return failure(
            format!("index {} is out of range [0, {}] (undefined on a GPU)", number, length as i64 - 1),
            span,
        );
    }
    Ok(number as usize)
}
