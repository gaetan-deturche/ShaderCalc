use std::collections::HashSet;
use std::fmt;
use std::sync::Arc;

use super::emitter::{HlslEmitter, component_paths, declaration, ordered_inputs};
use super::warp_device::{ComputeTimings, ENTRY_POINT, ROOT_SIGNATURE, ReferenceError, ReferenceMode, WarpDevice};
use crate::binding::bound_tree::{BoundExpression, BoundExpressionKind, BoundStatement};
use crate::binding::intrinsic::IntrinsicPrecision;
use crate::binding::program::{BoundInteractive, BoundProgram};
use crate::binding::symbols::VariableKind;
use crate::evaluation::evaluator::Storage;
use crate::session::LineResult;
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
    /// The line can't be checked (no value, errors, DXC rejected the code...).
    NotChecked,
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
}

impl fmt::Display for ReferenceOutcome {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.verdict {
            ReferenceVerdict::Match => write!(formatter, "= reference"),
            ReferenceVerdict::WithinTolerance => write!(formatter, "≈ reference ({} ulp)", self.max_ulps),
            ReferenceVerdict::Mismatch => {
                let reference: String = self.reference_value.as_ref().map(Value::to_string).unwrap_or_default();
                let approximations: String = if self.uses_approximations {
                    format!(" ({} ulp, approximate functions involved)", self.max_ulps)
                } else {
                    String::new()
                };
                write!(formatter, "≠ reference: {reference}{approximations}")
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

fn not_checked(message: impl Into<String>, hlsl: String) -> ReferenceOutcome {
    ReferenceOutcome {
        verdict: ReferenceVerdict::NotChecked,
        reference_value: None,
        max_ulps: 0,
        uses_approximations: false,
        message: Some(message.into()),
        hlsl,
        timings: None,
    }
}

/// Runs a calculator or worksheet line on the HLSL reference (DXC + WARP) and compares the bits. Literal call
/// arguments, uniforms and session variables are fed through the input buffer, already converted to their concrete
/// types, so WARP executes the math instead of DXC folding it, while DXC's literal rules stay intact.
pub fn check(line: &LineResult, mode: ReferenceMode) -> ReferenceOutcome {
    // Unit errors don't stop the numeric check; only a missing value does
    let (Some(value), Some(bound), Some(program)) = (&line.value, &line.line, &line.program) else {
        return not_checked(
            if line.has_errors() { "the line has errors" } else { "the line has no value" },
            String::new(),
        );
    };
    if value.ty.is_void() {
        return not_checked("the line has no value", String::new());
    }
    // Problems elsewhere (another line, an unused function) don't matter: if the code this line needs is broken,
    // emitting or compiling it fails below
    let (hlsl, inputs) = match build_harness(program, bound, &value.ty, &line.inputs) {
        Ok(built) => built,
        Err(message) => return not_checked(message, String::new()),
    };

    let output_words: usize = value.to_words().len();
    let device: Arc<WarpDevice> = match WarpDevice::shared() {
        Ok(device) => device,
        Err(error) => return not_checked(error.to_string(), hlsl),
    };
    let (words, timings) = match device.run_cached(&hlsl, &inputs, output_words, mode) {
        Ok((words, timings, _)) => (words, timings),
        Err(error @ ReferenceError::Compile(_)) | Err(error @ ReferenceError::Unavailable(_)) => {
            return not_checked(error.to_string(), hlsl);
        }
    };

    let reference: Value = from_words(&value.ty, &words);
    let uses_approximations: bool = uses_approximations(bound);
    let ours: Vec<u32> = value.to_words();
    if ours == words || both_nan(value, &reference) {
        return ReferenceOutcome {
            verdict: ReferenceVerdict::Match,
            reference_value: Some(reference),
            max_ulps: 0,
            uses_approximations,
            message: None,
            hlsl,
            timings: Some(timings),
        };
    }
    let max_ulps: i64 = max_ulps(value, &reference);
    let is_close: bool = max_ulps <= APPROXIMATE_TOLERANCE_ULPS
        || max_absolute_difference(value, &reference) <= APPROXIMATE_TOLERANCE_ABSOLUTE;
    let verdict: ReferenceVerdict =
        if uses_approximations && is_close { ReferenceVerdict::WithinTolerance } else { ReferenceVerdict::Mismatch };
    ReferenceOutcome {
        verdict,
        reference_value: Some(reference),
        max_ulps,
        uses_approximations,
        message: None,
        hlsl,
        timings: Some(timings),
    }
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

/// Builds the compute shader for one line; returns it with the input words it reads.
fn build_harness(
    program: &BoundProgram,
    line: &BoundInteractive,
    result_type: &ShaderType,
    inputs: &Storage,
) -> Result<(String, Vec<u32>), String> {
    let mut emitter: HlslEmitter = HlslEmitter::with_harness();
    emitter.text.push_str(
        "RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n\n",
    );
    emitter.emit_program(program, true);
    emitter
        .text
        .push_str(&format!("[RootSignature(\"{ROOT_SIGNATURE}\")]\n[numthreads(1, 1, 1)]\nvoid {ENTRY_POINT}()\n{{\n"));

    // Uniforms and calculator variables hold runtime data: load them
    for (variable, value) in ordered_inputs(inputs) {
        if variable.kind == VariableKind::Session {
            emitter.text.push_str(&format!("    {};\n", declaration(&variable.ty, &variable.name)));
        }
        for (component, path) in component_paths(&variable.ty, &variable.name).into_iter().enumerate() {
            let load: String = emitter.load_scalar(value.kind_at(component), value.bits[component]);
            emitter.text.push_str(&format!("    {path} = {load};\n"));
        }
    }

    emitter.harness.as_mut().expect("a harness").is_emitting_line = true;
    let result: Option<&BoundExpression> = line.result();
    let body_count: usize = if result.is_some() { line.statements.len() - 1 } else { line.statements.len() };
    for statement in &line.statements[..body_count] {
        emitter.emit_statement(statement, 1);
    }
    let result_expression: String = match result {
        Some(result) => emitter.expression(result),
        None => line.result_variable.as_ref().map(|variable| variable.name.clone()).ok_or("the line has no value")?,
    };
    emitter.text.push_str(&format!("    {};\n", declaration(result_type, "result")));
    emitter.text.push_str(&format!("    result = {result_expression};\n"));

    let mut word: usize = 0;
    for (component, path) in component_paths(result_type, "result").into_iter().enumerate() {
        let store: String = match result_type.kind_at(component) {
            ScalarKind::Bool => {
                word += 1;
                format!("    RefOutput[{}] = {} ? 1u : 0u;\n", word - 1, path)
            }
            ScalarKind::UInt => {
                word += 1;
                format!("    RefOutput[{}] = {};\n", word - 1, path)
            }
            ScalarKind::Double => {
                word += 2;
                format!(
                    "    {{ uint low, high; asuint({}, low, high); RefOutput[{}] = low; RefOutput[{}] = high; }}\n",
                    path,
                    word - 2,
                    word - 1
                )
            }
            ScalarKind::Int64 | ScalarKind::UInt64 => {
                word += 2;
                format!(
                    "    RefOutput[{}] = (uint)({}); RefOutput[{}] = (uint)((uint64_t)({}) >> 32);\n",
                    word - 2,
                    path,
                    word - 1,
                    path
                )
            }
            _ => {
                word += 1;
                format!("    RefOutput[{}] = asuint({});\n", word - 1, path)
            }
        };
        emitter.text.push_str(&store);
    }
    emitter.text.push_str("}\n");
    if let Some(failure) = emitter.failure {
        return Err(failure);
    }
    let inputs: Vec<u32> = emitter.harness.map(|harness| harness.inputs).unwrap_or_default();
    Ok((emitter.text, inputs))
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
