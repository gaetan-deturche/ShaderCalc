use std::collections::HashSet;
use std::fmt;
use std::sync::Arc;

use super::emitter::{
    Harness, HarnessTrace, HlslEmitter, component_paths, declaration, ordered_inputs, store_component,
};
use super::warp_device::{ComputeTimings, ENTRY_POINT, ROOT_SIGNATURE, ReferenceError, ReferenceMode, WarpDevice};
use crate::binding::bound_tree::{BoundExpression, BoundExpressionKind, BoundStatement};
use crate::binding::intrinsic::IntrinsicPrecision;
use crate::binding::program::{BoundInteractive, BoundProgram};
use crate::binding::symbols::{FunctionRef, VariableKind};
use crate::evaluation::evaluator::Storage;
use crate::session::LineResult;
use crate::trace::{CallSite, CallTrace, LineTrace, call_sites, trace_points};
use crate::types::{ScalarKind, ShaderType};
use crate::units::UnitTag;
use crate::values::{Value, scalars};

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum ReferenceVerdict {
    /// Bit-identical.
    Match,
    /// Differs only through functions the GPU approximates (sin, exp2...), within the tolerance.
    WithinTolerance,
    Mismatch,
    /// Differs where WARP itself is known to be wrong (sinh, sin of huge angles...): `message` says why.
    WarpLimit,
    /// The line can't be checked (no value, errors, DXC rejected the code...).
    NotChecked,
}

impl ReferenceVerdict {
    /// How bad: the worse of two verdicts decides a line made of several values.
    fn severity(self) -> u8 {
        match self {
            ReferenceVerdict::Match | ReferenceVerdict::NotChecked => 0,
            ReferenceVerdict::WithinTolerance => 1,
            ReferenceVerdict::WarpLimit => 2,
            ReferenceVerdict::Mismatch => 3,
        }
    }

    fn worse(self, other: ReferenceVerdict) -> ReferenceVerdict {
        if other.severity() > self.severity() { other } else { self }
    }
}

/// The reference's verdict on one trace entry (`LineTrace::entries`, same order), and its values.
#[derive(Clone, Debug, PartialEq)]
pub struct TraceCheck {
    pub verdict: ReferenceVerdict,
    pub values: Vec<Value>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ReferenceOutcome {
    pub verdict: ReferenceVerdict,
    pub reference_value: Option<Value>,
    pub max_ulps: i64,
    pub uses_approximations: bool,
    pub message: Option<String>,
    pub hlsl: String,
    pub timings: Option<ComputeTimings>,
    /// A traced line: each entry checked.
    pub trace: Vec<TraceCheck>,
}

impl fmt::Display for ReferenceOutcome {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.verdict {
            ReferenceVerdict::Match => write!(formatter, "= reference"),
            ReferenceVerdict::WithinTolerance => write!(formatter, "≈ reference ({} ulp)", self.max_ulps),
            ReferenceVerdict::Mismatch => {
                let reference: String = match &self.reference_value {
                    Some(value) => value.to_string(),
                    None => self.message.clone().unwrap_or_default(),
                };
                let approximations: String = if self.uses_approximations {
                    format!(" ({} ulp, approximate functions involved)", self.max_ulps)
                } else {
                    String::new()
                };
                write!(formatter, "≠ reference: {reference}{approximations}")
            }
            ReferenceVerdict::WarpLimit => {
                let reference: String = self.reference_value.as_ref().map(Value::to_string).unwrap_or_default();
                write!(formatter, "⊘ WARP limit: WARP gives {reference} ({})", self.message.as_deref().unwrap_or(""))
            }
            ReferenceVerdict::NotChecked => write!(formatter, "not checked: {}", self.message.as_deref().unwrap_or("")),
        }
    }
}

/// Largest float difference, in units in the last place, accepted when approximate functions are involved.
pub const APPROXIMATE_TOLERANCE_ULPS: i64 = 64;

/// Absolute difference also accepted then: D3D allows sin/cos 0.0008 and log2 2^-21 near 1, where ulps explode
/// because the result is tiny.
pub const APPROXIMATE_TOLERANCE_ABSOLUTE: f64 = 0.0008;

pub fn not_checked(message: impl Into<String>, hlsl: String) -> ReferenceOutcome {
    ReferenceOutcome {
        verdict: ReferenceVerdict::NotChecked,
        reference_value: None,
        max_ulps: 0,
        uses_approximations: false,
        message: Some(message.into()),
        hlsl,
        timings: None,
        trace: Vec::new(),
    }
}

/// Runs a calculator or worksheet line on the HLSL reference (DXC + WARP) and compares the bits. The line's
/// literals, uniforms and session variables are fed through the input buffer, already converted to their concrete
/// types, so WARP executes the math instead of DXC folding it, while DXC's literal rules stay intact.
pub fn check(line: &LineResult, mode: ReferenceMode) -> ReferenceOutcome {
    // Unit errors don't stop the numeric check; only a missing value (and nothing traced) does
    let value: Option<&Value> = line.value.as_ref().filter(|value| !value.ty.is_void());
    let trace: Option<&LineTrace> = line.trace.as_ref().filter(|trace| !trace.entries.is_empty());
    let (Some(bound), Some(program), true) = (&line.line, &line.program, value.is_some() || trace.is_some()) else {
        return not_checked(
            if line.has_errors() { "the line has errors" } else { "the line has no value" },
            String::new(),
        );
    };
    // Output: the value, then for a trace how many words of it the shader wrote, then the trace
    let result_words: usize = value.map_or(0, |value| value.to_words().len());
    let trace_words: usize = trace.map_or(0, |trace| {
        trace.entries.iter().flat_map(|entry| &entry.values).map(|value| value.to_words().len()).sum()
    });
    // Problems elsewhere (another line, an unused function) don't matter: if the code this line needs is broken,
    // emitting or compiling it fails below
    let harness = build_harness(program, bound, value.map(|value| &value.ty), &line.inputs, trace, trace_words);
    let (hlsl, inputs) = match harness {
        Ok(built) => built,
        Err(message) => return not_checked(message, String::new()),
    };

    let output_words: usize = if trace.is_some() { result_words + 1 + trace_words } else { result_words };
    let device: Arc<WarpDevice> = match WarpDevice::shared() {
        Ok(device) => device,
        Err(error) => return not_checked(error.to_string(), hlsl),
    };
    let (words, timings) = match device.run_cached(&hlsl, &inputs, output_words, mode) {
        Ok((words, timings, _)) => (words, timings),
        Err(error @ (ReferenceError::Compile(_) | ReferenceError::Unavailable(_) | ReferenceError::Crashed(_))) => {
            return not_checked(error.to_string(), hlsl);
        }
    };

    let uses_approximations: bool = uses_approximations(bound);
    let limits: &[String] = &line.reference_limits;
    let mut verdict: ReferenceVerdict = ReferenceVerdict::Match;
    let mut max_ulps: i64 = 0;
    let mut reference_value: Option<Value> = None;
    if let Some(value) = value {
        let reference: Value = from_words(&value.ty, &words[..result_words]);
        (verdict, max_ulps) = compare(value, &reference, uses_approximations, limits);
        reference_value = Some(reference);
    }
    let mut checks: Vec<TraceCheck> = Vec::new();
    let mut message: Option<String> = None;
    if let Some(trace) = trace {
        let compared: TraceComparison =
            compare_trace(trace, &words[result_words..], trace_words, uses_approximations, limits);
        verdict = verdict.worse(compared.verdict);
        max_ulps = max_ulps.max(compared.max_ulps);
        checks = compared.checks;
        message = compared.message;
    }
    if verdict == ReferenceVerdict::WarpLimit && message.is_none() {
        message = Some(limits.join("; "));
    }
    ReferenceOutcome {
        verdict,
        reference_value,
        max_ulps,
        uses_approximations,
        message,
        hlsl,
        timings: Some(timings),
        trace: checks,
    }
}

struct TraceComparison {
    verdict: ReferenceVerdict,
    max_ulps: i64,
    checks: Vec<TraceCheck>,
    message: Option<String>,
}

/// A trace against the shader's: `words` starts with the count of words it wrote, then its entries.
fn compare_trace(
    trace: &LineTrace,
    words: &[u32],
    trace_words: usize,
    uses_approximations: bool,
    limits: &[String],
) -> TraceComparison {
    let mut compared: TraceComparison =
        TraceComparison { verdict: ReferenceVerdict::Match, max_ulps: 0, checks: Vec::new(), message: None };
    let mut word: usize = 1;
    for entry in &trace.entries {
        let mut check: TraceCheck = TraceCheck { verdict: ReferenceVerdict::Match, values: Vec::new() };
        for ours in &entry.values {
            let count: usize = ours.to_words().len();
            let theirs: Value = from_words(&ours.ty, &words[word..word + count]);
            word += count;
            let (value_verdict, ulps) = compare(ours, &theirs, uses_approximations, limits);
            check.verdict = check.verdict.worse(value_verdict);
            compared.max_ulps = compared.max_ulps.max(ulps);
            check.values.push(theirs);
        }
        compared.verdict = compared.verdict.worse(check.verdict);
        compared.checks.push(check);
    }
    let written: usize = words[0] as usize;
    if written != trace_words && !trace.is_truncated {
        compared.verdict = ReferenceVerdict::Mismatch;
        compared.message =
            Some(format!("WARP ran it differently: {written} words of values, the interpreter's {trace_words}"));
    }
    compared
}

/// Runs one call a worksheet line makes on the reference (`path` leads to it, as for `worksheet::trace_call`) and
/// checks every value its body computed (`call`'s trace).
pub fn check_call(
    line: &LineResult,
    path: &[(usize, usize)],
    call: &CallTrace,
    mode: ReferenceMode,
) -> ReferenceOutcome {
    let (Some(bound), Some(program)) = (&line.line, &line.program) else {
        return not_checked("the line has errors", String::new());
    };
    if call.trace.entries.is_empty() {
        return not_checked("the call computed nothing to check", String::new());
    }
    let trace_words: usize =
        call.trace.entries.iter().flat_map(|entry| &entry.values).map(|value| value.to_words().len()).sum();
    let (hlsl, inputs) = match build_call_harness(program, bound, &line.inputs, path, &call.trace, trace_words) {
        Ok(built) => built,
        Err(message) => return not_checked(message, String::new()),
    };
    let device: Arc<WarpDevice> = match WarpDevice::shared() {
        Ok(device) => device,
        Err(error) => return not_checked(error.to_string(), hlsl),
    };
    let (words, timings) = match device.run_cached(&hlsl, &inputs, 1 + trace_words, mode) {
        Ok((words, timings, _)) => (words, timings),
        Err(error @ (ReferenceError::Compile(_) | ReferenceError::Unavailable(_) | ReferenceError::Crashed(_))) => {
            return not_checked(error.to_string(), hlsl);
        }
    };
    let uses_approximations: bool = uses_approximations(bound);
    let limits: &[String] = &line.reference_limits;
    let compared: TraceComparison = compare_trace(&call.trace, &words, trace_words, uses_approximations, limits);
    let message: Option<String> =
        compared.message.or_else(|| (compared.verdict == ReferenceVerdict::WarpLimit).then(|| limits.join("; ")));
    ReferenceOutcome {
        verdict: compared.verdict,
        reference_value: None,
        max_ulps: compared.max_ulps,
        uses_approximations,
        message,
        hlsl,
        timings: Some(timings),
        trace: compared.checks,
    }
}

/// The shader for one call: the line with its code, and a copy of each function on the path (`RefTraced<level>_<name>`
/// with a `refOn` switch: on for the chosen run only, counted in `refRun<level>`); the last copy writes its trace.
fn build_call_harness(
    program: &BoundProgram,
    line: &BoundInteractive,
    inputs: &Storage,
    path: &[(usize, usize)],
    trace: &LineTrace,
    trace_words: usize,
) -> Result<(String, Vec<u32>), String> {
    let [statement] = line.statements.as_slice() else {
        return Err("the line has several statements".to_string());
    };
    // Each level's function, the node calling it, and its body
    let mut functions: Vec<FunctionRef> = Vec::new();
    let mut nodes: Vec<*const BoundExpression> = Vec::new();
    let mut bodies: Vec<Arc<BoundStatement>> = Vec::new();
    for (level, (site, _)) in path.iter().enumerate() {
        let (sites, ids) = if level == 0 { call_sites(statement) } else { call_sites(&bodies[level - 1]) };
        let target: &CallSite = sites.get(*site).ok_or("the call isn't in the code")?;
        let node: *const BoundExpression =
            ids.iter().find(|(_, id)| **id == *site).map(|(node, _)| *node).ok_or("the call isn't in the code")?;
        bodies.push(target.function.body().ok_or("the function has no body")?);
        functions.push(target.function.clone());
        nodes.push(node);
    }
    let (Some(last_body), Some((_, first_run))) = (bodies.last(), path.first()) else {
        return Err("no call to look inside".to_string());
    };
    let names: Vec<String> =
        functions.iter().enumerate().map(|(level, function)| format!("RefTraced{level}_{}", function.name)).collect();

    let mut emitter: HlslEmitter = HlslEmitter::with_harness();
    emitter.text.push_str(
        "RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n\n",
    );
    emitter.text.push_str("static uint refTraceCursor = 0;\n");
    for level in 0..path.len() {
        emitter.text.push_str(&format!("static uint refRun{level} = 0;\n"));
    }
    emitter.emit_program(program, true);
    // Deepest first: each copy calls the next one
    let (points, ids) = trace_points(last_body);
    if points.len() != trace.points.len() {
        return Err("the trace doesn't match the function's code".to_string());
    }
    let harness_trace: HarnessTrace = HarnessTrace {
        ids,
        points,
        base: 1,
        capacity: trace_words,
        is_active: false,
        guard: Some("refOn".to_string()),
    };
    emitter.harness.as_mut().expect("a harness").trace = Some(harness_trace);
    for level in (0..path.len()).rev() {
        emitter.redirects.clear();
        let is_last: bool = level + 1 == path.len();
        if !is_last {
            let switch: String = format!("refOn && (refRun{} ++ == {}u)", level + 1, path[level + 1].1);
            emitter.redirects.insert(nodes[level + 1], (names[level + 1].clone(), switch));
        }
        if let Some(trace) = emitter.harness.as_mut().and_then(|harness| harness.trace.as_mut()) {
            trace.is_active = is_last;
        }
        emitter.emit_copy(&functions[level], &names[level], "bool refOn");
    }
    if let Some(trace) = emitter.harness.as_mut().and_then(|harness| harness.trace.as_mut()) {
        trace.is_active = false;
    }
    emitter.redirects.clear();
    emitter.redirects.insert(nodes[0], (names[0].clone(), format!("(refRun0 ++ == {first_run}u)")));

    emitter
        .text
        .push_str(&format!("[RootSignature(\"{ROOT_SIGNATURE}\")]\n[numthreads(1, 1, 1)]\nvoid {ENTRY_POINT}()\n{{\n"));
    emit_inputs(&mut emitter, inputs);
    emitter.harness.as_mut().expect("a harness").is_emitting_line = true;
    emitter.text.push_str("    {\n");
    for statement in &line.statements {
        emitter.emit_statement(statement, 2);
    }
    emitter.text.push_str("    }\n    RefOutput[0] = refTraceCursor;\n}\n");
    if let Some(failure) = emitter.failure {
        return Err(failure);
    }
    let inputs: Vec<u32> = emitter.harness.map(|harness| harness.inputs).unwrap_or_default();
    Ok((emitter.text, inputs))
}

/// Uniforms and calculator variables hold runtime data: loaded from the input buffer.
fn emit_inputs(emitter: &mut HlslEmitter, inputs: &Storage) {
    for (variable, value) in ordered_inputs(inputs) {
        if variable.kind == VariableKind::Session {
            emitter.text.push_str(&format!("    {};\n", declaration(&variable.ty, &variable.name)));
        }
        for (component, path) in component_paths(&variable.ty, &variable.name).into_iter().enumerate() {
            let load: String = emitter.load_scalar(value.kind_at(component), value.bits[component]);
            emitter.text.push_str(&format!("    {path} = {load};\n"));
        }
    }
}

/// One value against the reference's: its verdict and distance.
fn compare(ours: &Value, reference: &Value, uses_approximations: bool, limits: &[String]) -> (ReferenceVerdict, i64) {
    if ours.to_words() == reference.to_words() || both_nan(ours, reference) {
        return (ReferenceVerdict::Match, 0);
    }
    let max_ulps: i64 = max_ulps(ours, reference);
    let is_close: bool = max_ulps <= APPROXIMATE_TOLERANCE_ULPS
        || max_absolute_difference(ours, reference) <= APPROXIMATE_TOLERANCE_ABSOLUTE;
    let verdict: ReferenceVerdict = if uses_approximations && is_close {
        ReferenceVerdict::WithinTolerance
    } else if !limits.is_empty() {
        ReferenceVerdict::WarpLimit
    } else {
        ReferenceVerdict::Mismatch
    };
    (verdict, max_ulps)
}

/// Whether the line reaches a function GPUs approximate (directly or through the functions it calls).
fn uses_approximations(line: &BoundInteractive) -> bool {
    let mut visited: HashSet<usize> = HashSet::new();
    line.statements.iter().any(|statement| statement_reaches_approximation(statement, &mut visited))
}

fn statement_reaches_approximation(statement: &BoundStatement, visited: &mut HashSet<usize>) -> bool {
    statement.expressions().into_iter().any(|expression| expression_reaches_approximation(expression, visited))
}

fn expression_reaches_approximation(expression: &BoundExpression, visited: &mut HashSet<usize>) -> bool {
    match &expression.kind {
        BoundExpressionKind::IntrinsicCall { intrinsic, .. }
            if intrinsic.precision == IntrinsicPrecision::Approximate =>
        {
            return true;
        }
        BoundExpressionKind::Call { function, .. } if visited.insert(function.id) => {
            if let Some(body) = function.body()
                && statement_reaches_approximation(&body, visited)
            {
                return true;
            }
        }
        _ => {}
    }
    expression.children().into_iter().any(|child| expression_reaches_approximation(child, visited))
}

/// Builds the compute shader for one line; returns it with the input words it reads. A traced line also writes
/// every trace entry (`trace_words` of them) after the result and the count of words it wrote.
fn build_harness(
    program: &BoundProgram,
    line: &BoundInteractive,
    result_type: Option<&ShaderType>,
    inputs: &Storage,
    trace: Option<&LineTrace>,
    trace_words: usize,
) -> Result<(String, Vec<u32>), String> {
    let mut emitter: HlslEmitter = HlslEmitter::with_harness();
    let result_words: usize =
        result_type.map_or(0, |ty| ty.component_kinds().iter().map(|kind| kind_words(*kind)).sum());
    if let Some(trace) = trace {
        // The interpreter traced its own copy of the statement, numbered the same way
        let [statement] = line.statements.as_slice() else {
            return Err("a traced line has one statement".to_string());
        };
        let (points, ids) = trace_points(statement);
        if points.len() != trace.points.len() {
            return Err("the trace doesn't match the line's code".to_string());
        }
        let harness_trace: HarnessTrace =
            HarnessTrace { ids, points, base: result_words + 1, capacity: trace_words, is_active: false, guard: None };
        emitter.harness.as_mut().expect("a harness").trace = Some(harness_trace);
    }
    emitter.text.push_str(
        "RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n\n",
    );
    emitter.emit_program(program, true);
    emitter
        .text
        .push_str(&format!("[RootSignature(\"{ROOT_SIGNATURE}\")]\n[numthreads(1, 1, 1)]\nvoid {ENTRY_POINT}()\n{{\n"));

    emit_inputs(&mut emitter, inputs);

    let harness: &mut Harness = emitter.harness.as_mut().expect("a harness");
    harness.is_emitting_line = true;
    if let Some(trace) = harness.trace.as_mut() {
        trace.is_active = true;
    }
    if let Some(result_type) = result_type {
        emitter.text.push_str(&format!("    {};\n", declaration(result_type, "result")));
    }
    if trace.is_some() {
        emitter.text.push_str("    uint refTraceCursor = 0;\n");
    }
    // A scope of its own: the line may redeclare a session variable (int a[4] after int a[3])
    emitter.text.push_str("    {\n");
    let result: Option<&BoundExpression> = line.result();
    let body_count: usize = if result.is_some() { line.statements.len() - 1 } else { line.statements.len() };
    for statement in &line.statements[..body_count] {
        emitter.emit_statement(statement, 2);
    }
    if result_type.is_some() {
        let result_expression: String = match result {
            Some(result) => emitter.expression(result),
            None => {
                line.result_variable.as_ref().map(|variable| variable.name.clone()).ok_or("the line has no value")?
            }
        };
        emitter.text.push_str(&format!("        result = {result_expression};\n"));
    } else if let Some(result) = result {
        let text: String = emitter.expression(result);
        emitter.text.push_str(&format!("        {text};\n"));
    }
    // `a = b = 3`: the variables it wrote, after it
    if result.is_some()
        && let Some(statement) = line.statements.last()
    {
        emitter.emit_trace_after(statement, 2);
    }
    emitter.text.push_str("    }\n");

    if let Some(result_type) = result_type {
        let mut word: usize = 0;
        for (component, path) in component_paths(result_type, "result").into_iter().enumerate() {
            let (store, count) = store_component(result_type.kind_at(component), &path, &word.to_string());
            emitter.text.push_str(&format!("    {store}\n"));
            word += count;
        }
    }
    if trace.is_some() {
        emitter.text.push_str(&format!("    RefOutput[{result_words}] = refTraceCursor;\n"));
    }
    emitter.text.push_str("}\n");
    if let Some(failure) = emitter.failure {
        return Err(failure);
    }
    let inputs: Vec<u32> = emitter.harness.map(|harness| harness.inputs).unwrap_or_default();
    Ok((emitter.text, inputs))
}

fn kind_words(kind: ScalarKind) -> usize {
    if kind.is_64bit() { 2 } else { 1 }
}

/// Rebuilds a value from the shader's output words (64-bit kinds: low word first).
pub fn from_words(ty: &ShaderType, words: &[u32]) -> Value {
    let kinds: Vec<ScalarKind> = ty.component_kinds();
    let mut bits: Vec<u64> = vec![0; kinds.len()];
    let mut word: usize = 0;
    for (index, kind) in kinds.iter().enumerate() {
        let low: u64 = words[word] as u64;
        word += 1;
        bits[index] = if *kind == ScalarKind::Int {
            scalars::from_int(low as u32 as i32)
        } else if kind.is_64bit() {
            let high: u64 = words[word] as u64;
            word += 1;
            low | (high << 32)
        } else {
            low
        };
    }
    Value::new(ty.clone(), bits, vec![UnitTag::BARE; kinds.len()])
}

fn both_nan(ours: &Value, reference: &Value) -> bool {
    (0..ours.bits.len()).all(|index| {
        ours.bits[index] == reference.bits[index]
            || (ours.kind_at(index).is_float()
                && ours.get_number(index).is_nan()
                && reference.get_number(index).is_nan())
    })
}

/// Largest distance between float components, in representable values (0 for integers that match).
pub fn max_ulps(ours: &Value, reference: &Value) -> i64 {
    let mut worst: i64 = 0;
    for index in 0..ours.bits.len() {
        if ours.bits[index] == reference.bits[index] {
            continue;
        }
        match ours.kind_at(index) {
            ScalarKind::Float => {
                worst = worst.max((ordered(ours.bits[index] as u32) - ordered(reference.bits[index] as u32)).abs());
            }
            ScalarKind::Double => {
                let distance: i128 =
                    (ordered64(ours.bits[index]) as i128 - ordered64(reference.bits[index]) as i128).abs();
                worst = worst.max(distance.min(i64::MAX as i128) as i64);
            }
            _ => return i64::MAX,
        }
    }
    worst
}

/// Largest absolute difference between float components (infinity when a NaN or integer differs).
pub fn max_absolute_difference(ours: &Value, reference: &Value) -> f64 {
    let mut worst: f64 = 0.0;
    for index in 0..ours.bits.len() {
        if ours.bits[index] == reference.bits[index] {
            continue;
        }
        let difference: f64 = if ours.kind_at(index).is_float() {
            (ours.get_number(index) - reference.get_number(index)).abs()
        } else {
            f64::INFINITY
        };
        worst = worst.max(if difference.is_nan() { f64::INFINITY } else { difference });
    }
    worst
}

fn ordered(bits: u32) -> i64 {
    if bits & 0x8000_0000 != 0 { -((bits & 0x7FFF_FFFF) as i64) } else { bits as i64 }
}

fn ordered64(bits: u64) -> i64 {
    if bits & 0x8000_0000_0000_0000 != 0 { -((bits & 0x7FFF_FFFF_FFFF_FFFF) as i64) } else { bits as i64 }
}
