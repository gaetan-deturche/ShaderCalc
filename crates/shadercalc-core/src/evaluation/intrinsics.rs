use std::collections::HashMap;
use std::sync::LazyLock;

use half::f16;

use super::arithmetic::{self, FloatOps};
use super::intrinsic_context::IntrinsicContext;
use super::unit_checker::UnitChecker;
use crate::binding::bound_tree::{BinaryOperator, UnaryOperator};
use crate::binding::intrinsic::{Intrinsic, IntrinsicPrecision, IntrinsicResolution, IntrinsicSignature};
use crate::binding::type_rules;
use crate::diagnostics::DiagnosticSeverity;
use crate::semantics::{RoundTies, SemanticsProfile};
use crate::syntax::tree::ParameterMode;
use crate::types::{NumericType, ScalarKind, ShaderType};
use crate::units::{Dimension, UnitTag};
use crate::values::{Value, scalars};

/// HLSL intrinsics: overload resolution plus an implementation that follows DXC's lowering to DXIL op for op
/// (exp(x) = exp2(x · log2 e), fmod via frac, normalize via rsqrt(dot), mul as an FMad chain...), so results
/// match a DXC + WARP build bit for bit, except for the functions WARP approximates.
#[derive(Clone, Copy, PartialEq, Eq)]
enum KindClass {
    /// half/float; integers and literals become float; double is rejected (no such DXIL op).
    Float,
    FloatOrDouble,
    /// Any numeric kind; bool becomes int, literals int / float.
    Numeric,
    Integer,
    /// Any kind, bool included.
    Any,
}

// Constants exactly as DXC emits them (float precision)
const LOG2_OF_E: f64 = f32::from_bits(0x3FB8AA3B) as f64;
const LN_OF_2: f64 = f32::from_bits(0x3F317218) as f64;
const LOG10_OF_2: f64 = f32::from_bits(0x3E9A209B) as f64;
const PI: f64 = f32::from_bits(0x40490FDB) as f64;
const HALF_PI: f64 = f32::from_bits(0x3FC90FDB) as f64;
const DEGREES_PER_RADIAN: f64 = f32::from_bits(0x42652EE1) as f64;
const RADIANS_PER_DEGREE: f64 = f32::from_bits(0x3C8EFA35) as f64;
const COLOR_TO_BYTE: f64 = f32::from_bits(0x437F0080) as f64;

/// Intrinsics that exist in HLSL but make no sense outside a GPU pipeline.
const UNSUPPORTED: [&str; 28] = [
    "ddx",
    "ddy",
    "ddx_coarse",
    "ddy_coarse",
    "ddx_fine",
    "ddy_fine",
    "fwidth",
    "clip",
    "noise",
    "abort",
    "errorf",
    "printf",
    "GroupMemoryBarrier",
    "GroupMemoryBarrierWithGroupSync",
    "AllMemoryBarrier",
    "DeviceMemoryBarrier",
    "InterlockedAdd",
    "InterlockedMin",
    "InterlockedMax",
    "InterlockedAnd",
    "InterlockedOr",
    "InterlockedXor",
    "InterlockedExchange",
    "WaveActiveSum",
    "WaveReadLaneAt",
    "WaveGetLaneIndex",
    "EvaluateAttributeAtSample",
    "GetRenderTargetSampleCount",
];

struct Table {
    intrinsics: Vec<Intrinsic>,
    by_name: HashMap<&'static str, usize>,
}

static TABLE: LazyLock<Table> = LazyLock::new(|| {
    let intrinsics: Vec<Intrinsic> = build();
    let by_name: HashMap<&'static str, usize> =
        intrinsics.iter().enumerate().map(|(index, intrinsic)| (intrinsic.name, index)).collect();
    Table { intrinsics, by_name }
});

pub fn try_get(name: &str) -> Option<&'static Intrinsic> {
    let table: &'static Table = &TABLE;
    table.by_name.get(name).map(|index| &table.intrinsics[*index])
}

pub fn is_unsupported(name: &str) -> bool {
    UNSUPPORTED.contains(&name)
}

/// Every intrinsic name, in table order.
pub fn names() -> impl Iterator<Item = &'static str> {
    let table: &'static Table = &TABLE;
    table.intrinsics.iter().map(|intrinsic| intrinsic.name)
}

type Resolver = Box<dyn Fn(&[ShaderType]) -> IntrinsicResolution + Send + Sync>;

fn add(
    table: &mut Vec<Intrinsic>,
    name: &'static str,
    precision: IntrinsicPrecision,
    resolve: impl Fn(&[ShaderType]) -> IntrinsicResolution + Send + Sync + 'static,
    implementation: impl Fn(&IntrinsicContext, &[Value]) -> Value + Send + Sync + 'static,
) {
    table.push(Intrinsic {
        name,
        precision,
        resolve: Box::new(resolve),
        implementation: Box::new(implementation),
        constant_sensitive_arguments: Vec::new(),
    });
}

fn add_transcendental(table: &mut Vec<Intrinsic>, name: &'static str, single: fn(f32) -> f32, wide: fn(f64) -> f64) {
    add(table, name, IntrinsicPrecision::Approximate, same(1, KindClass::Float, None), move |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.apply(single, wide, operands[0]),
            |units| context.units.dimensionless(units[0], name),
            None,
        )
    });
}

fn bool_type(ty: NumericType) -> ShaderType {
    ty.with_kind(ScalarKind::Bool).into()
}

fn int_type(ty: NumericType) -> ShaderType {
    ty.with_kind(ScalarKind::Int).into()
}

fn uint_type(ty: NumericType) -> ShaderType {
    ty.with_kind(ScalarKind::UInt).into()
}

/// 2^x through the CRT's pow, the value the reference tolerances were measured against.
fn exp2_single(value: f32) -> f32 {
    2.0f32.powf(value)
}

fn exp2_wide(value: f64) -> f64 {
    2.0f64.powf(value)
}

fn build() -> Vec<Intrinsic> {
    use IntrinsicPrecision::{Approximate, Exact};
    let mut table: Vec<Intrinsic> = Vec::new();
    let t: &mut Vec<Intrinsic> = &mut table;

    // ---- Rounding and simple float functions (exact) ----
    add(t, "floor", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |_, operands| operands[0].floor(), keep, None)
    });
    add(t, "ceil", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |_, operands| operands[0].ceil(), keep, None)
    });
    add(t, "trunc", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |_, operands| operands[0].trunc(), keep, None)
    });
    add(t, "round", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |_, operands| match context.profile.round_ties {
                RoundTies::ToEven => operands[0].round_ties_even(),
                RoundTies::AwayFromZero => operands[0].round(),
            },
            keep,
            None,
        )
    });
    add(t, "frac", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |math, operands| math.frc(operands[0]), keep, None)
    });
    add(t, "saturate", Exact, same(1, KindClass::FloatOrDouble, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |_, operands| FloatOps::saturate(operands[0]),
            |units| context.units.dimensionless(units[0], "saturate"),
            None,
        )
    });
    add(t, "sqrt", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.sqrt(operands[0]),
            |units| context.units.power(units[0], 0.5, "sqrt"),
            None,
        )
    });
    add(t, "rsqrt", Approximate, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| rsqrt(math, operands[0]),
            |units| context.units.power(units[0], -0.5, "rsqrt"),
            None,
        )
    });
    add(t, "rcp", Exact, same(1, KindClass::FloatOrDouble, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.divide(1.0, operands[0]),
            |units| context.units.power(units[0], -1.0, "rcp"),
            None,
        )
    });
    add(t, "degrees", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |math, operands| math.multiply(operands[0], DEGREES_PER_RADIAN), keep, None)
    });
    add(t, "radians", Exact, same(1, KindClass::Float, None), |context, arguments| {
        map_float(context, arguments, |math, operands| math.multiply(operands[0], RADIANS_PER_DEGREE), keep, None)
    });

    // ---- Transcendentals: DXIL Exp/Log are base 2; WARP approximates them all ----
    add_transcendental(t, "sin", f32::sin, f64::sin);
    add_transcendental(t, "cos", f32::cos, f64::cos);
    add_transcendental(t, "tan", f32::tan, f64::tan);
    add_transcendental(t, "asin", f32::asin, f64::asin);
    add_transcendental(t, "acos", f32::acos, f64::acos);
    add_transcendental(t, "atan", f32::atan, f64::atan);
    add_transcendental(t, "sinh", f32::sinh, f64::sinh);
    add_transcendental(t, "cosh", f32::cosh, f64::cosh);
    add_transcendental(t, "tanh", f32::tanh, f64::tanh);
    add_transcendental(t, "exp2", exp2_single, exp2_wide);
    add_transcendental(t, "log2", f32::log2, f64::log2);
    add(t, "exp", Approximate, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| exp2(math, math.multiply(operands[0], LOG2_OF_E)),
            |units| context.units.dimensionless(units[0], "exp"),
            None,
        )
    });
    add(t, "log", Approximate, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.multiply(log2(math, operands[0]), LN_OF_2),
            |units| context.units.dimensionless(units[0], "log"),
            None,
        )
    });
    add(t, "log10", Approximate, same(1, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.multiply(log2(math, operands[0]), LOG10_OF_2),
            |units| context.units.dimensionless(units[0], "log10"),
            None,
        )
    });
    add(t, "pow", Approximate, same(2, KindClass::Float, None), pow);
    t.last_mut().expect("pow was added").constant_sensitive_arguments = vec![1];
    add(t, "ldexp", Approximate, same(2, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.multiply(exp2(math, operands[1]), operands[0]),
            |units| {
                context.units.dimensionless(units[1], "ldexp's exponent");
                units[0]
            },
            None,
        )
    });
    add(t, "atan2", Approximate, same(2, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| atan2(math, operands[0], operands[1]),
            |units| UnitTag {
                dimension: Dimension::NONE,
                is_adoptable: context.units.same(units[0], units[1], "atan2").is_adoptable,
            },
            None,
        )
    });
    add(
        t,
        "sincos",
        Approximate,
        outputs_like(KindClass::Float, vec![ParameterMode::In, ParameterMode::Out, ParameterMode::Out]),
        |context, arguments| {
            let units = |tags: &[UnitTag]| context.units.dimensionless(tags[0], "sincos");
            let sine: Value = map_float(
                context,
                &arguments[..1],
                |math, operands| math.apply(f32::sin, f64::sin, operands[0]),
                units,
                None,
            );
            context.set_output(1, sine);
            let cosine: Value = map_float(
                context,
                &arguments[..1],
                |math, operands| math.apply(f32::cos, f64::cos, operands[0]),
                units,
                None,
            );
            context.set_output(2, cosine);
            Value::void()
        },
    );

    // ---- Combining functions ----
    add(t, "fmod", Exact, same(2, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| fmod(math, operands[0], operands[1]),
            |units| context.units.same(units[0], units[1], "fmod"),
            None,
        )
    });
    add(t, "lerp", Exact, same(3, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.add(math.multiply(math.subtract(operands[1], operands[0]), operands[2]), operands[0]),
            |units| {
                context.units.dimensionless(units[2], "lerp's weight");
                context.units.same(units[0], units[1], "lerp")
            },
            None,
        )
    });
    add(t, "smoothstep", Exact, same(3, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| {
                // t * (t * (3 - t * 2)), as DXC lowers it without fast math
                let ratio: f64 = FloatOps::saturate(
                    math.divide(math.subtract(operands[2], operands[0]), math.subtract(operands[1], operands[0])),
                );
                math.multiply(ratio, math.multiply(ratio, math.subtract(3.0, math.multiply(ratio, 2.0))))
            },
            |units| {
                context.units.same(units[0], units[1], "smoothstep");
                UnitTag {
                    dimension: Dimension::NONE,
                    is_adoptable: context.units.same(units[0], units[2], "smoothstep").is_adoptable,
                }
            },
            None,
        )
    });
    add(t, "step", Exact, same(2, KindClass::Float, None), |context, arguments| {
        map_float(
            context,
            arguments,
            |_, operands| if operands[1] < operands[0] { 0.0 } else { 1.0 },
            |units| UnitTag {
                dimension: Dimension::NONE,
                is_adoptable: context.units.same(units[0], units[1], "step").is_adoptable,
            },
            None,
        )
    });
    add(t, "min", Exact, same(2, KindClass::Numeric, None), |context, arguments| min_max(context, arguments, true));
    add(t, "max", Exact, same(2, KindClass::Numeric, None), |context, arguments| min_max(context, arguments, false));
    add(t, "clamp", Exact, same(3, KindClass::Numeric, None), clamp);
    add(t, "abs", Exact, same(1, KindClass::Numeric, None), abs);
    add(t, "sign", Exact, same(1, KindClass::Numeric, Some(int_type)), sign);
    add(t, "mad", Exact, same(3, KindClass::Numeric, None), mad);
    add(t, "fma", Exact, resolve_double_only(3), |context, arguments| {
        map_float(
            context,
            arguments,
            |math, operands| math.fma(operands[0], operands[1], operands[2]),
            |units| context.units.same(UnitChecker::multiply(units[0], units[1]), units[2], "fma"),
            None,
        )
    });
    add(t, "isnan", Exact, same(1, KindClass::Float, Some(bool_type)), |context, arguments| {
        let result: ShaderType = result_kind(arguments, ScalarKind::Bool);
        map_float(
            context,
            arguments,
            |_, operands| if operands[0].is_nan() { 1.0 } else { 0.0 },
            drop_silently,
            Some(result),
        )
    });
    add(t, "isinf", Exact, same(1, KindClass::Float, Some(bool_type)), |context, arguments| {
        let result: ShaderType = result_kind(arguments, ScalarKind::Bool);
        map_float(
            context,
            arguments,
            |_, operands| if operands[0].is_infinite() { 1.0 } else { 0.0 },
            drop_silently,
            Some(result),
        )
    });
    add(t, "isfinite", Exact, same(1, KindClass::Float, Some(bool_type)), |context, arguments| {
        let result: ShaderType = result_kind(arguments, ScalarKind::Bool);
        map_float(
            context,
            arguments,
            |_, operands| if operands[0].is_finite() { 1.0 } else { 0.0 },
            drop_silently,
            Some(result),
        )
    });
    add(
        t,
        "modf",
        Exact,
        outputs_like(KindClass::Float, vec![ParameterMode::In, ParameterMode::Out]),
        |context, arguments| {
            let integral: Value = map_float(context, &arguments[..1], |_, operands| operands[0].trunc(), keep, None);
            context.set_output(1, integral);
            map_float(
                context,
                &arguments[..1],
                |math, operands| math.subtract(operands[0], operands[0].trunc()),
                keep,
                None,
            )
        },
    );
    add(t, "frexp", Exact, outputs_like(KindClass::Float, vec![ParameterMode::In, ParameterMode::Out]), frexp);

    // ---- Logic ----
    add(t, "any", Exact, reduce(KindClass::Any, ScalarKind::Bool), |_, arguments| {
        Value::from_bool((0..arguments[0].bits.len()).any(|index| arguments[0].get_bool(index)))
    });
    add(t, "all", Exact, reduce(KindClass::Any, ScalarKind::Bool), |_, arguments| {
        Value::from_bool((0..arguments[0].bits.len()).all(|index| arguments[0].get_bool(index)))
    });
    add(t, "and", Exact, same(2, KindClass::Any, Some(bool_type)), |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::Bool),
            arguments,
            |bits, kinds| {
                scalars::from_bool(scalars::is_true(kinds[0], bits[0]) && scalars::is_true(kinds[1], bits[1]))
            },
            drop_silently,
        )
    });
    add(t, "or", Exact, same(2, KindClass::Any, Some(bool_type)), |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::Bool),
            arguments,
            |bits, kinds| {
                scalars::from_bool(scalars::is_true(kinds[0], bits[0]) || scalars::is_true(kinds[1], bits[1]))
            },
            drop_silently,
        )
    });
    add(t, "select", Exact, resolve_select, |context, arguments| {
        map_bits(
            context,
            arguments[1].ty.clone(),
            arguments,
            |bits, kinds| if scalars::is_true(kinds[0], bits[0]) { bits[1] } else { bits[2] },
            |units| context.units.same(units[1], units[2], "select"),
        )
    });

    // ---- Vectors and matrices ----
    add(t, "dot", Exact, resolve_dot, dot);
    add(t, "cross", Exact, resolve_cross, cross);
    add(t, "length", Exact, resolve_length(1), |context, arguments| length(context, &arguments[0]));
    add(t, "distance", Exact, resolve_length(2), |context, arguments| {
        let difference: Value = subtract(context, &arguments[0], &arguments[1]);
        length(context, &difference)
    });
    add(t, "normalize", Approximate, same(1, KindClass::Float, None), normalize);
    add(t, "reflect", Exact, same(2, KindClass::Float, None), reflect);
    add(t, "refract", Exact, resolve_refract, refract);
    add(t, "faceforward", Exact, same(3, KindClass::Float, None), face_forward);
    add(t, "mul", Exact, resolve_mul, mul);
    add(t, "transpose", Exact, resolve_transpose, transpose);
    add(t, "determinant", Exact, resolve_determinant, determinant);
    add(t, "lit", Approximate, resolve_lit, lit);
    add(t, "dst", Exact, resolve_dst, dst);

    // ---- Bits ----
    add(t, "countbits", Exact, same(1, KindClass::Integer, Some(uint_type)), |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::UInt),
            arguments,
            |bits, _| (bits[0] as u32).count_ones() as u64,
            |units| context.units.drop(units[0], "countbits"),
        )
    });
    add(t, "reversebits", Exact, same(1, KindClass::Integer, None), |context, arguments| {
        map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| {
                scalars::convert(ScalarKind::UInt, kinds[0], (bits[0] as u32).reverse_bits() as u64, context.profile)
            },
            |units| context.units.drop(units[0], "reversebits"),
        )
    });
    add(t, "firstbitlow", Exact, same(1, KindClass::Integer, None), |context, arguments| {
        map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| {
                let word: u32 = bits[0] as u32;
                let position: u32 = if word == 0 { u32::MAX } else { word.trailing_zeros() };
                scalars::convert(ScalarKind::UInt, kinds[0], position as u64, context.profile)
            },
            |units| context.units.drop(units[0], "firstbitlow"),
        )
    });
    add(t, "firstbithigh", Exact, same(1, KindClass::Integer, None), |context, arguments| {
        map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| {
                let mut word: u32 = bits[0] as u32;
                // Signed: the first bit that differs from the sign bit
                if kinds[0] == ScalarKind::Int && (word as i32) < 0 {
                    word = !word;
                }
                let position: u32 = if word == 0 { u32::MAX } else { 31 - word.leading_zeros() };
                scalars::convert(ScalarKind::UInt, kinds[0], position as u64, context.profile)
            },
            |units| context.units.drop(units[0], "firstbithigh"),
        )
    });
    add(t, "asuint", Exact, resolve_as_uint, as_uint);
    add(t, "asint", Exact, resolve_reinterpret(ScalarKind::Int), |context, arguments| {
        reinterpret(context, &arguments[0], ScalarKind::Int)
    });
    add(t, "asfloat", Exact, resolve_reinterpret(ScalarKind::Float), |context, arguments| {
        reinterpret(context, &arguments[0], ScalarKind::Float)
    });
    add(t, "asdouble", Exact, resolve_as_double, |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::Double),
            arguments,
            |bits, _| (bits[0] as u32 as u64) | ((bits[1] as u32 as u64) << 32),
            drop_silently,
        )
    });
    add(t, "f32tof16", Exact, same(1, KindClass::Float, Some(uint_type)), |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::UInt),
            arguments,
            |bits, _| f32_to_f16(read(context, ScalarKind::Float, bits[0]) as f32, context.profile),
            |units| context.units.drop(units[0], "f32tof16"),
        )
    });
    add(t, "f16tof32", Exact, resolve_f16_to_f32, |context, arguments| {
        map_bits(
            context,
            result_kind(arguments, ScalarKind::Float),
            arguments,
            |bits, _| scalars::from_float(f16::from_bits(bits[0] as u16).to_f32()),
            drop_silently,
        )
    });
    add(t, "D3DCOLORtoUBYTE4", Exact, resolve_color_to_ubyte4, color_to_ubyte4);

    table
}

// ---------------------------------------------------------------- Resolution

fn fail(message: impl Into<String>) -> IntrinsicResolution {
    Err(message.into())
}

/// All arguments share one type T (scalars splat to the vector/matrix shape); returns T unless told otherwise.
fn same(arity: usize, kind_class: KindClass, returns: Option<fn(NumericType) -> ShaderType>) -> Resolver {
    Box::new(move |arguments: &[ShaderType]| {
        if arguments.len() != arity {
            let plural: &str = if arity == 1 { "" } else { "s" };
            return fail(format!("takes {} argument{}, got {}", arity, plural, arguments.len()));
        }
        let common: NumericType = try_common_type(arguments, kind_class)?;
        let return_type: ShaderType = match returns {
            Some(returns) => returns(common),
            None => common.into(),
        };
        Ok(IntrinsicSignature::all_in(vec![common.into(); arity], return_type))
    })
}

/// sincos(x, out s, out c), modf(x, out ip), frexp(x, out e): every parameter has x's type.
fn outputs_like(kind_class: KindClass, modes: Vec<ParameterMode>) -> Resolver {
    Box::new(move |arguments: &[ShaderType]| {
        if arguments.len() != modes.len() {
            return fail(format!("takes {} arguments, got {}", modes.len(), arguments.len()));
        }
        let common: NumericType = try_common_type(&arguments[..1], kind_class)?;
        let return_type: ShaderType = if modes.len() == 3 { ShaderType::Void } else { common.into() };
        Ok(IntrinsicSignature { parameter_types: vec![common.into(); modes.len()], modes: modes.clone(), return_type })
    })
}

fn reduce(kind_class: KindClass, result_kind: ScalarKind) -> Resolver {
    Box::new(move |arguments: &[ShaderType]| {
        if arguments.len() != 1 {
            return fail(format!("takes 1 argument, got {}", arguments.len()));
        }
        let common: NumericType = try_common_type(arguments, kind_class)?;
        Ok(IntrinsicSignature::all_in(vec![common.into()], ShaderType::scalar(result_kind)))
    })
}

fn try_common_type(arguments: &[ShaderType], kind_class: KindClass) -> Result<NumericType, String> {
    let mut result: Option<NumericType> = None;
    for argument in arguments {
        let ShaderType::Numeric(numeric) = argument else {
            return Err(format!("doesn't take {argument}"));
        };
        let Some(current) = result else {
            result = Some(*numeric);
            continue;
        };
        let kind: ScalarKind = type_rules::common_kind(current.kind, numeric.kind);
        if !current.is_scalar()
            && !numeric.is_scalar()
            && (current.shape != numeric.shape || current.rows != numeric.rows || current.columns != numeric.columns)
        {
            return Err(format!("arguments have different sizes ({current} and {numeric})"));
        }
        result = Some((if current.is_scalar() { *numeric } else { current }).with_kind(kind));
    }
    let Some(result) = result else {
        return Err("needs arguments".to_string());
    };

    let adjusted: ScalarKind = match kind_class {
        KindClass::Float | KindClass::FloatOrDouble => match result.kind {
            ScalarKind::Double => ScalarKind::Double,
            ScalarKind::Half => ScalarKind::Half,
            _ => ScalarKind::Float,
        },
        KindClass::Numeric => match result.kind {
            ScalarKind::Bool | ScalarKind::LiteralInt => ScalarKind::Int,
            ScalarKind::LiteralFloat => ScalarKind::Float,
            other => other,
        },
        KindClass::Integer => {
            if matches!(result.kind, ScalarKind::LiteralInt | ScalarKind::Bool) {
                ScalarKind::Int
            } else {
                result.kind
            }
        }
        KindClass::Any => result.kind.materialized(),
    };
    if kind_class == KindClass::Float && adjusted == ScalarKind::Double {
        return Err("doesn't support double (no DXIL instruction for it)".to_string());
    }
    if kind_class == KindClass::Integer && !adjusted.is_integer() {
        return Err(format!("needs integers, got {result}"));
    }
    if kind_class == KindClass::Integer && matches!(adjusted, ScalarKind::Int64 | ScalarKind::UInt64) {
        return Err("doesn't support 64-bit integers here".to_string());
    }
    Ok(result.with_kind(adjusted))
}

fn resolve_double_only(arity: usize) -> Resolver {
    let resolve: Resolver = same(arity, KindClass::FloatOrDouble, None);
    Box::new(move |arguments: &[ShaderType]| {
        let resolution: IntrinsicResolution = resolve(arguments);
        match &resolution {
            Ok(signature) if matches!(&signature.return_type, ShaderType::Numeric(numeric) if numeric.kind == ScalarKind::Double) => {
                resolution
            }
            _ => fail("only takes double"),
        }
    })
}

fn resolve_select(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() != 3 {
        return fail(format!("takes 3 arguments, got {}", arguments.len()));
    }
    let values: NumericType = try_common_type(&arguments[1..], KindClass::Any)?;
    let ShaderType::Numeric(condition) = &arguments[0] else {
        return fail(format!("needs a bool condition, got {}", arguments[0]));
    };
    let shape: NumericType = if condition.is_scalar() {
        values
    } else if values.is_scalar() {
        condition.with_kind(values.kind)
    } else {
        values
    };
    if !condition.is_scalar() && !values.is_scalar() && condition.size() != values.size() {
        return fail(format!("condition {condition} and values {values} have different sizes"));
    }
    Ok(IntrinsicSignature::all_in(
        vec![shape.with_kind(ScalarKind::Bool).into(), shape.into(), shape.into()],
        shape.into(),
    ))
}

fn resolve_dot(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() != 2 {
        return fail(format!("takes 2 arguments, got {}", arguments.len()));
    }
    let common: NumericType = try_common_type(arguments, KindClass::Numeric)?;
    if common.is_matrix() {
        return fail("needs vectors, got a matrix");
    }
    Ok(IntrinsicSignature::all_in(vec![common.into(), common.into()], ShaderType::scalar(common.kind)))
}

fn resolve_cross(arguments: &[ShaderType]) -> IntrinsicResolution {
    let resolution: IntrinsicResolution = same(2, KindClass::Float, None)(arguments);
    match &resolution {
        Err(_) => resolution,
        Ok(signature) if matches!(&signature.return_type, ShaderType::Numeric(numeric) if numeric.is_vector() && numeric.size() == 3) => {
            resolution
        }
        Ok(_) => fail("needs float3 vectors"),
    }
}

fn resolve_length(arity: usize) -> Resolver {
    let resolve: Resolver = same(arity, KindClass::Float, None);
    Box::new(move |arguments: &[ShaderType]| {
        let signature: IntrinsicSignature = resolve(arguments)?;
        let ShaderType::Numeric(common) = signature.return_type else {
            return fail("needs vectors");
        };
        if common.is_matrix() {
            return fail("needs vectors, got a matrix");
        }
        Ok(IntrinsicSignature::all_in(signature.parameter_types, ShaderType::scalar(common.kind)))
    })
}

fn resolve_refract(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() != 3 {
        return fail(format!("takes 3 arguments, got {}", arguments.len()));
    }
    let common: NumericType = try_common_type(&arguments[..2], KindClass::Float)?;
    Ok(IntrinsicSignature::all_in(vec![common.into(), common.into(), ShaderType::scalar(common.kind)], common.into()))
}

fn resolve_mul(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() != 2 {
        return fail(format!("takes 2 arguments, got {}", arguments.len()));
    }
    let (ShaderType::Numeric(left), ShaderType::Numeric(right)) = (&arguments[0], &arguments[1]) else {
        return fail("needs numeric arguments");
    };
    let kind: ScalarKind = match type_rules::promote_kinds(left.kind, right.kind) {
        ScalarKind::LiteralInt => ScalarKind::Int,
        ScalarKind::LiteralFloat => ScalarKind::Float,
        other => other,
    };
    let left_type: NumericType = left.with_kind(kind);
    let right_type: NumericType = right.with_kind(kind);
    if left.is_scalar() || right.is_scalar() {
        let shape: NumericType = if left.is_scalar() { right_type } else { left_type };
        return Ok(IntrinsicSignature::all_in(vec![left_type.into(), right_type.into()], shape.into()));
    }
    // A vector is a row on the left, a column on the right
    let left_columns: usize = if left.is_vector() { left.size() } else { left.columns };
    let right_rows: usize = if right.is_vector() { right.size() } else { right.rows };
    if left_columns != right_rows {
        return fail(format!("can't multiply {left} by {right}: {left_columns} columns vs {right_rows} rows"));
    }
    let result: ShaderType = match (left.is_vector(), right.is_vector()) {
        (true, true) => ShaderType::scalar(kind),
        (true, false) => ShaderType::vector(kind, right.columns),
        (false, true) => ShaderType::vector(kind, left.rows),
        (false, false) => ShaderType::matrix(kind, left.rows, right.columns),
    };
    Ok(IntrinsicSignature::all_in(vec![left_type.into(), right_type.into()], result))
}

fn resolve_transpose(arguments: &[ShaderType]) -> IntrinsicResolution {
    match arguments {
        [ShaderType::Numeric(matrix)] if matrix.is_matrix() => {
            let materialized: NumericType = matrix.with_kind(matrix.kind.materialized());
            Ok(IntrinsicSignature::all_in(
                vec![materialized.into()],
                ShaderType::matrix(materialized.kind, matrix.columns, matrix.rows),
            ))
        }
        _ => fail("takes one matrix"),
    }
}

fn resolve_determinant(arguments: &[ShaderType]) -> IntrinsicResolution {
    match arguments {
        [ShaderType::Numeric(matrix)] if matrix.is_matrix() && matrix.rows == matrix.columns => {
            if matrix.kind == ScalarKind::Double {
                return fail("doesn't support double");
            }
            Ok(IntrinsicSignature::all_in(vec![matrix.with_kind(ScalarKind::Float).into()], ShaderType::float()))
        }
        _ => fail("takes one square matrix"),
    }
}

fn resolve_lit(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() == 3 && arguments.iter().all(ShaderType::is_scalar) {
        Ok(IntrinsicSignature::all_in(
            vec![ShaderType::float(), ShaderType::float(), ShaderType::float()],
            ShaderType::vector(ScalarKind::Float, 4),
        ))
    } else {
        fail("takes 3 scalars")
    }
}

fn resolve_dst(arguments: &[ShaderType]) -> IntrinsicResolution {
    let float4: ShaderType = ShaderType::vector(ScalarKind::Float, 4);
    if arguments.len() == 2 {
        Ok(IntrinsicSignature::all_in(vec![float4.clone(), float4.clone()], float4))
    } else {
        fail("takes 2 vectors")
    }
}

fn resolve_reinterpret(target: ScalarKind) -> Resolver {
    Box::new(move |arguments: &[ShaderType]| reinterpret_signature(arguments, target))
}

fn reinterpret_signature(arguments: &[ShaderType], target: ScalarKind) -> IntrinsicResolution {
    let [ShaderType::Numeric(numeric)] = arguments else {
        return fail("takes one numeric argument");
    };
    let kind: ScalarKind = numeric.kind.materialized();
    if !matches!(kind, ScalarKind::Int | ScalarKind::UInt | ScalarKind::Float) {
        return fail(format!("needs 32-bit int, uint or float, got {numeric}"));
    }
    let source: NumericType = numeric.with_kind(kind);
    Ok(IntrinsicSignature::all_in(vec![source.into()], source.with_kind(target).into()))
}

fn resolve_as_uint(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() == 3 {
        // asuint(double value, out uint low, out uint high)
        let ShaderType::Numeric(value) = &arguments[0] else {
            return fail("with 3 arguments takes a double and two out uints");
        };
        if value.kind != ScalarKind::Double {
            return fail("with 3 arguments takes a double and two out uints");
        }
        let words: ShaderType = value.with_kind(ScalarKind::UInt).into();
        return Ok(IntrinsicSignature {
            parameter_types: vec![(*value).into(), words.clone(), words],
            modes: vec![ParameterMode::In, ParameterMode::Out, ParameterMode::Out],
            return_type: ShaderType::Void,
        });
    }
    reinterpret_signature(arguments, ScalarKind::UInt)
}

fn resolve_as_double(arguments: &[ShaderType]) -> IntrinsicResolution {
    if arguments.len() != 2 {
        return fail("takes two uints (low, high)");
    }
    let Ok(common) = try_common_type(arguments, KindClass::Integer) else {
        return fail("takes two uints (low, high)");
    };
    let words: NumericType = common.with_kind(ScalarKind::UInt);
    Ok(IntrinsicSignature::all_in(vec![words.into(), words.into()], words.with_kind(ScalarKind::Double).into()))
}

fn resolve_f16_to_f32(arguments: &[ShaderType]) -> IntrinsicResolution {
    let [ShaderType::Numeric(numeric)] = arguments else {
        return fail("takes one uint");
    };
    let words: NumericType = numeric.with_kind(ScalarKind::UInt);
    Ok(IntrinsicSignature::all_in(vec![words.into()], words.with_kind(ScalarKind::Float).into()))
}

fn resolve_color_to_ubyte4(arguments: &[ShaderType]) -> IntrinsicResolution {
    let float4: NumericType = NumericType::vector(ScalarKind::Float, 4);
    if arguments.len() == 1 {
        Ok(IntrinsicSignature::all_in(vec![float4.into()], float4.with_kind(ScalarKind::Int).into()))
    } else {
        fail("takes a float4")
    }
}

// ---------------------------------------------------------------- Implementation helpers

fn keep(units: &[UnitTag]) -> UnitTag {
    units[0]
}

fn drop_silently(units: &[UnitTag]) -> UnitTag {
    UnitTag { dimension: Dimension::NONE, is_adoptable: units.iter().all(|unit| unit.is_adoptable) }
}

fn result_kind(arguments: &[Value], kind: ScalarKind) -> ShaderType {
    arguments[0].ty.as_numeric().expect("numeric argument").with_kind(kind).into()
}

/// Component-wise function of float arguments (all of the same type after conversion). Non-float results (isnan,
/// sign) are encoded from the returned number.
fn map_float(
    context: &IntrinsicContext,
    arguments: &[Value],
    compute: impl Fn(&FloatOps, &[f64]) -> f64,
    units: impl Fn(&[UnitTag]) -> UnitTag,
    result_type: Option<ShaderType>,
) -> Value {
    let ty: ShaderType = result_type.unwrap_or_else(|| arguments[0].ty.clone());
    let count: usize = ty.component_count();
    let operand_kind: ScalarKind = arguments[0].kind_at(0);
    let math: FloatOps = FloatOps::new(operand_kind, context.profile);
    let mut bits: Vec<u64> = vec![0; count];
    let mut tags: Vec<UnitTag> = vec![UnitTag::BARE; count];
    let mut operands: Vec<f64> = vec![0.0; arguments.len()];
    let mut operand_units: Vec<UnitTag> = vec![UnitTag::BARE; arguments.len()];
    for component in 0..count {
        for (argument_index, argument) in arguments.iter().enumerate() {
            let index: usize = if argument.bits.len() == 1 { 0 } else { component };
            operands[argument_index] = read(context, operand_kind, argument.bits[index]);
            operand_units[argument_index] = argument.units[index];
        }
        bits[component] = encode(ty.kind_at(component), compute(&math, &operands), context.profile);
        tags[component] = units(&operand_units);
    }
    Value::new(ty, bits, tags)
}

/// Component-wise function of raw bits (integers, bools, reinterpretation).
fn map_bits(
    context: &IntrinsicContext,
    result_type: ShaderType,
    arguments: &[Value],
    compute: impl Fn(&[u64], &[ScalarKind]) -> u64,
    units: impl Fn(&[UnitTag]) -> UnitTag,
) -> Value {
    let _ = context;
    let count: usize = result_type.component_count();
    let mut bits: Vec<u64> = vec![0; count];
    let mut tags: Vec<UnitTag> = vec![UnitTag::BARE; count];
    let mut operands: Vec<u64> = vec![0; arguments.len()];
    let mut kinds: Vec<ScalarKind> = vec![ScalarKind::Int; arguments.len()];
    let mut operand_units: Vec<UnitTag> = vec![UnitTag::BARE; arguments.len()];
    for component in 0..count {
        for (argument_index, argument) in arguments.iter().enumerate() {
            let index: usize = if argument.bits.len() == 1 { 0 } else { component };
            operands[argument_index] = argument.bits[index];
            kinds[argument_index] = argument.kind_at(index);
            operand_units[argument_index] = argument.units[index];
        }
        bits[component] = compute(&operands, &kinds);
        tags[component] = units(&operand_units);
    }
    Value::new(result_type, bits, tags)
}

fn encode(kind: ScalarKind, number: f64, profile: &SemanticsProfile) -> u64 {
    match kind {
        ScalarKind::Bool => scalars::from_bool(number != 0.0),
        ScalarKind::Int => scalars::from_int(number as i32),
        ScalarKind::UInt => number as u32 as u64,
        ScalarKind::Int64 | ScalarKind::LiteralInt => number as i64 as u64,
        ScalarKind::UInt64 => number as u64,
        _ => scalars::encode_float(kind, number, profile),
    }
}

// ---- DXIL op semantics ----

fn exp2(math: &FloatOps, value: f64) -> f64 {
    math.apply(exp2_single, exp2_wide, value)
}

fn log2(math: &FloatOps, value: f64) -> f64 {
    math.apply(f32::log2, f64::log2, value)
}

/// DXIL Rsqrt, taken as the correctly rounded 1/sqrt(x).
fn rsqrt(math: &FloatOps, value: f64) -> f64 {
    math.round(1.0 / value.sqrt())
}

// ---- Formulas from DXC's lowering ----

fn pow(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    // DXC turns pow(x, 2) (after constant folding) into x * x; everything else is exp2(log2(x) * y)
    let is_square: bool = context.is_constant(1, 2.0);
    let has_constant_exponent: bool = context.constant_arguments[1].is_some();
    map_float(
        context,
        arguments,
        |math, operands| {
            if is_square {
                math.multiply(operands[0], operands[0])
            } else {
                exp2(math, math.multiply(log2(math, operands[0]), operands[1]))
            }
        },
        |units| {
            context.units.dimensionless(units[1], "pow's exponent");
            if units[0].dimension.is_none() {
                return UnitTag {
                    dimension: Dimension::NONE,
                    is_adoptable: units[0].is_adoptable && units[1].is_adoptable,
                };
            }
            if !has_constant_exponent {
                context.report(
                    DiagnosticSeverity::Error,
                    format!("pow of {} needs a constant exponent", units[0].dimension),
                );
                return UnitTag::of(Dimension::NONE);
            }
            let exponent: f64 = context.constant_arguments[1].as_ref().expect("constant exponent").get_number(0);
            context.units.power(units[0], exponent, "pow")
        },
        None,
    )
}

/// atan(y / x) plus quadrant fix-ups, as DXC lowers atan2.
fn atan2(math: &FloatOps, y: f64, x: f64) -> f64 {
    let angle: f64 = math.apply(f32::atan, f64::atan, math.divide(y, x));
    let x_negative: bool = x < 0.0;
    let x_zero: bool = x == 0.0;
    let y_non_negative: bool = y >= 0.0;
    let y_negative: bool = y < 0.0;
    let mut result: f64 = if y_non_negative && x_negative { math.add(angle, PI) } else { angle };
    result = if y_negative && x_negative { math.add(angle, -PI) } else { result };
    result = if y_negative && x_zero { -HALF_PI } else { result };
    if y_non_negative && x_zero { HALF_PI } else { result }
}

/// fmod(x, y) = ±frac(|x / y|) · y (not C's exact fmod).
fn fmod(math: &FloatOps, x: f64, y: f64) -> f64 {
    let quotient: f64 = math.divide(x, y);
    let is_positive: bool = quotient >= -quotient;
    let fraction: f64 = math.frc(quotient.abs());
    math.multiply(if is_positive { fraction } else { -fraction }, y)
}

fn min_max(context: &IntrinsicContext, arguments: &[Value], is_min: bool) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let units = |tags: &[UnitTag]| context.units.same(tags[0], tags[1], if is_min { "min" } else { "max" });
    if kind.is_float() {
        return map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| FloatOps::min_max_bits(kinds[0], bits[0], bits[1], is_min, context.profile),
            units,
        );
    }
    map_bits(
        context,
        arguments[0].ty.clone(),
        arguments,
        |bits, kinds| {
            let left_is_less: bool =
                arithmetic::compare(BinaryOperator::Less, kinds[0], bits[0], bits[1], context.profile);
            if left_is_less == is_min { bits[0] } else { bits[1] }
        },
        units,
    )
}

fn clamp(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let units = |tags: &[UnitTag]| {
        context.units.same(tags[0], tags[2], "clamp");
        context.units.same(tags[0], tags[1], "clamp")
    };
    if kind.is_float() {
        // FMin(FMax(x, low), high)
        return map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| {
                let raised: u64 = FloatOps::min_max_bits(kinds[0], bits[0], bits[1], false, context.profile);
                FloatOps::min_max_bits(kinds[0], raised, bits[2], true, context.profile)
            },
            units,
        );
    }
    map_bits(
        context,
        arguments[0].ty.clone(),
        arguments,
        |bits, kinds| {
            let raised: u64 = if arithmetic::compare(BinaryOperator::Less, kinds[0], bits[0], bits[1], context.profile)
            {
                bits[1]
            } else {
                bits[0]
            };
            if arithmetic::compare(BinaryOperator::Greater, kinds[0], raised, bits[2], context.profile) {
                bits[2]
            } else {
                raised
            }
        },
        units,
    )
}

fn abs(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    if kind.is_float() {
        // FAbs clears the sign bit (NaN payload kept); a float denormal is flushed first
        let sign_bit: u64 = match kind {
            ScalarKind::Half => 0x8000,
            ScalarKind::Float => 0x8000_0000,
            _ => 0x8000_0000_0000_0000,
        };
        return map_bits(
            context,
            arguments[0].ty.clone(),
            arguments,
            |bits, kinds| {
                let operand: u64 = if kinds[0] == ScalarKind::Float {
                    scalars::from_float(scalars::flush(scalars::to_float(bits[0]), context.profile))
                } else {
                    bits[0]
                };
                operand & !sign_bit
            },
            keep,
        );
    }
    // IMax(x, -x): abs(INT_MIN) stays INT_MIN
    map_bits(
        context,
        arguments[0].ty.clone(),
        arguments,
        |bits, kinds| {
            if matches!(kinds[0], ScalarKind::UInt | ScalarKind::UInt64) {
                return bits[0];
            }
            let negated: u64 = arithmetic::unary(UnaryOperator::Negate, kinds[0], bits[0], context.profile);
            if arithmetic::compare(BinaryOperator::Less, kinds[0], bits[0], negated, context.profile) {
                negated
            } else {
                bits[0]
            }
        },
        keep,
    )
}

fn sign(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let result: ShaderType = result_kind(arguments, ScalarKind::Int);
    map_bits(
        context,
        result,
        arguments,
        |bits, kinds| {
            let zero: u64 = scalars::convert(ScalarKind::Int, kinds[0], 0, context.profile);
            let is_positive: bool =
                arithmetic::compare(BinaryOperator::Greater, kinds[0], bits[0], zero, context.profile);
            let is_negative: bool = arithmetic::compare(BinaryOperator::Less, kinds[0], bits[0], zero, context.profile);
            scalars::from_int(is_positive as i32 - is_negative as i32)
        },
        drop_silently,
    )
}

fn mad(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let units = |tags: &[UnitTag]| context.units.same(UnitChecker::multiply(tags[0], tags[1]), tags[2], "mad");
    if kind.is_float() {
        return map_float(
            context,
            arguments,
            |math, operands| math.mad(operands[0], operands[1], operands[2]),
            units,
            None,
        );
    }
    map_bits(
        context,
        arguments[0].ty.clone(),
        arguments,
        |bits, kinds| {
            let mut fault: Option<String> = None;
            let product: u64 =
                arithmetic::binary(BinaryOperator::Multiply, kinds[0], bits[0], bits[1], context.profile, &mut fault);
            arithmetic::binary(BinaryOperator::Add, kinds[0], product, bits[2], context.profile, &mut fault)
        },
        units,
    )
}

fn frexp(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    // DXC: exponent and mantissa straight from the float bits; the mantissa loses the sign
    let exponent: Value = map_float(
        context,
        &arguments[..1],
        |_, operands| {
            if operands[0] == 0.0 {
                return 0.0;
            }
            let bits: i32 = (operands[0] as f32).to_bits() as i32;
            (((bits & 0x7F80_0000) - 0x3F00_0000) >> 23) as f64
        },
        drop_silently,
        None,
    );
    context.set_output(1, exponent);
    map_float(
        context,
        &arguments[..1],
        |_, operands| {
            if operands[0] == 0.0 {
                return 0.0;
            }
            let bits: u32 = (operands[0] as f32).to_bits();
            f32::from_bits((bits & 0x007F_FFFF) | 0x3F00_0000) as f64
        },
        drop_silently,
        None,
    )
}

/// DXIL Dot2/3/4 (float) or an IMad chain (integers); a scalar dot is a multiply.
fn dot_float(math: &FloatOps, left: &[f64], right: &[f64]) -> f64 {
    let mut sum: f64 = math.multiply(left[0], right[0]);
    for index in 1..left.len() {
        sum = math.add(sum, math.multiply(left[index], right[index]));
    }
    sum
}

fn dot(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let left: &Value = &arguments[0];
    let right: &Value = &arguments[1];
    let kind: ScalarKind = left.kind_at(0);
    let size: usize = left.bits.len();
    let mut unit: UnitTag = UnitChecker::multiply(left.units[0], right.units[0]);
    for index in 1..size {
        unit = context.units.same(unit, UnitChecker::multiply(left.units[index], right.units[index]), "dot");
    }

    let bits: u64;
    if kind.is_float() {
        let math: FloatOps = FloatOps::new(kind, context.profile);
        let left_components: Vec<f64> = (0..size).map(|index| read(context, kind, left.bits[index])).collect();
        let right_components: Vec<f64> = (0..size).map(|index| read(context, kind, right.bits[index])).collect();
        bits = scalars::encode_float(kind, dot_float(&math, &left_components, &right_components), context.profile);
    } else {
        let mut fault: Option<String> = None;
        let mut sum: u64 = arithmetic::binary(
            BinaryOperator::Multiply,
            kind,
            left.bits[0],
            right.bits[0],
            context.profile,
            &mut fault,
        );
        for index in 1..size {
            let product: u64 = arithmetic::binary(
                BinaryOperator::Multiply,
                kind,
                left.bits[index],
                right.bits[index],
                context.profile,
                &mut fault,
            );
            sum = arithmetic::binary(BinaryOperator::Add, kind, product, sum, context.profile, &mut fault);
        }
        bits = sum;
    }
    Value::scalar(kind, bits, unit)
}

fn cross(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let a: Vec<f64> = (0..3).map(|index| read(context, kind, arguments[0].bits[index])).collect();
    let b: Vec<f64> = (0..3).map(|index| read(context, kind, arguments[1].bits[index])).collect();
    let term =
        |i: usize, j: usize, k: usize, l: usize| math.subtract(math.multiply(a[i], b[j]), math.multiply(a[k], b[l]));
    let result: [f64; 3] = [term(1, 2, 2, 1), term(2, 0, 0, 2), term(0, 1, 1, 0)];
    let units: Vec<UnitTag> = (0..3)
        .map(|index| UnitChecker::multiply(arguments[0].units[(index + 1) % 3], arguments[1].units[(index + 2) % 3]))
        .collect();
    let bits: Vec<u64> =
        result.iter().map(|component| scalars::encode_float(kind, *component, context.profile)).collect();
    Value::new(arguments[0].ty.clone(), bits, units)
}

/// length: |x| for a scalar, else sqrt of the sum of squares (fmul/fadd, not the Dot op).
fn length(context: &IntrinsicContext, vector: &Value) -> Value {
    let kind: ScalarKind = vector.kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let mut unit: UnitTag = vector.units[0];
    for index in 1..vector.units.len() {
        unit = context.units.same(unit, vector.units[index], "length");
    }
    let components: Vec<f64> = (0..vector.bits.len()).map(|index| read(context, kind, vector.bits[index])).collect();
    let magnitude: f64 =
        if components.len() == 1 { components[0].abs() } else { math.sqrt(dot_float(&math, &components, &components)) };
    Value::scalar(kind, scalars::encode_float(kind, magnitude, context.profile), unit)
}

fn subtract(context: &IntrinsicContext, left: &Value, right: &Value) -> Value {
    let kind: ScalarKind = left.kind_at(0);
    let mut bits: Vec<u64> = vec![0; left.bits.len()];
    let mut units: Vec<UnitTag> = vec![UnitTag::BARE; left.bits.len()];
    let mut fault: Option<String> = None;
    for index in 0..bits.len() {
        bits[index] = arithmetic::binary(
            BinaryOperator::Subtract,
            kind,
            left.bits[index],
            right.bits[index],
            context.profile,
            &mut fault,
        );
        units[index] = context.units.same(left.units[index], right.units[index], "distance");
    }
    Value::new(left.ty.clone(), bits, units)
}

fn normalize(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let vector: &Value = &arguments[0];
    let kind: ScalarKind = vector.kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let components: Vec<f64> = (0..vector.bits.len()).map(|index| read(context, kind, vector.bits[index])).collect();
    // v * rsqrt(dot(v, v))
    let inverse_length: f64 = rsqrt(&math, dot_float(&math, &components, &components));
    let mut unit: UnitTag = vector.units[0];
    for index in 1..vector.units.len() {
        unit = context.units.same(unit, vector.units[index], "normalize");
    }
    let direction: UnitTag = UnitTag { dimension: Dimension::NONE, is_adoptable: unit.is_adoptable };
    let bits: Vec<u64> = components
        .iter()
        .map(|component| scalars::encode_float(kind, math.multiply(inverse_length, *component), context.profile))
        .collect();
    Value::new(vector.ty.clone(), bits, vec![direction; components.len()])
}

fn components(context: &IntrinsicContext, value: &Value) -> Vec<f64> {
    (0..value.bits.len()).map(|index| read(context, value.kind_at(index), value.bits[index])).collect()
}

fn reflect(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let incident: Vec<f64> = components(context, &arguments[0]);
    let normal: Vec<f64> = components(context, &arguments[1]);
    let dot: f64 =
        if incident.len() == 1 { math.multiply(incident[0], normal[0]) } else { dot_float(&math, &incident, &normal) };
    // i - n * (dot(i, n) * 2)
    let twice_dot: f64 = math.multiply(dot, 2.0);
    let result: Vec<u64> = incident
        .iter()
        .enumerate()
        .map(|(index, component)| {
            scalars::encode_float(
                kind,
                math.subtract(*component, math.multiply(normal[index], twice_dot)),
                context.profile,
            )
        })
        .collect();
    for normal_unit in &arguments[1].units {
        context.units.dimensionless(*normal_unit, "reflect's normal");
    }
    Value::new(arguments[0].ty.clone(), result, arguments[0].units.clone())
}

fn refract(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let incident: Vec<f64> = components(context, &arguments[0]);
    let normal: Vec<f64> = components(context, &arguments[1]);
    let eta: f64 = read(context, kind, arguments[2].bits[0]);
    let dot: f64 =
        if incident.len() == 1 { math.multiply(incident[0], normal[0]) } else { dot_float(&math, &incident, &normal) };
    // k = 1 - (1 - d*d) * eta*eta; r = k >= 0 ? i*eta - (sqrt(k) + d*eta) * n : 0
    let k: f64 =
        math.subtract(1.0, math.multiply(math.subtract(1.0, math.multiply(dot, dot)), math.multiply(eta, eta)));
    let refracts: bool = k >= 0.0;
    let scale: f64 = math.add(math.sqrt(k), math.multiply(dot, eta));
    let result: Vec<u64> = incident
        .iter()
        .enumerate()
        .map(|(index, component)| {
            let value: f64 = if refracts {
                math.subtract(math.multiply(*component, eta), math.multiply(scale, normal[index]))
            } else {
                0.0
            };
            scalars::encode_float(kind, value, context.profile)
        })
        .collect();
    for unit in arguments[1].units.iter().chain(std::iter::once(&arguments[2].units[0])) {
        context.units.dimensionless(*unit, "refract's normal and eta");
    }
    Value::new(arguments[0].ty.clone(), result, arguments[0].units.clone())
}

fn face_forward(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let kind: ScalarKind = arguments[0].kind_at(0);
    let math: FloatOps = FloatOps::new(kind, context.profile);
    let incident: Vec<f64> = components(context, &arguments[1]);
    let reference: Vec<f64> = components(context, &arguments[2]);
    let dot: f64 = if incident.len() == 1 {
        math.multiply(incident[0], reference[0])
    } else {
        dot_float(&math, &incident, &reference)
    };
    let keep_sign: bool = dot < 0.0;
    let bits: Vec<u64> = arguments[0]
        .bits
        .iter()
        .map(|component| {
            if keep_sign {
                *component
            } else {
                arithmetic::unary(UnaryOperator::Negate, kind, *component, context.profile)
            }
        })
        .collect();
    Value::new(arguments[0].ty.clone(), bits, arguments[0].units.clone())
}

/// A float operand as the GPU reads it (denormals flushed per the profile).
fn read(context: &IntrinsicContext, kind: ScalarKind, bits: u64) -> f64 {
    scalars::read_float(kind, bits, context.profile)
}

fn read_float(context: &IntrinsicContext, value: &Value, index: usize) -> f64 {
    read(context, ScalarKind::Float, value.bits[index])
}

/// mul: scalar products are multiplies; otherwise each result is an FMad chain (first term a plain multiply), as
/// DXC emits it. A vector is a row on the left, a column on the right.
fn mul(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let left: &Value = &arguments[0];
    let right: &Value = &arguments[1];
    let left_type: NumericType = *left.ty.as_numeric().expect("numeric");
    let right_type: NumericType = *right.ty.as_numeric().expect("numeric");
    let kind: ScalarKind = left_type.kind;
    let result_type: ShaderType = context.result_type.clone();
    let mut fault: Option<String> = None;

    if left_type.is_scalar() || right_type.is_scalar() {
        let vector: &Value = if left_type.is_scalar() { right } else { left };
        let scalar: &Value = if left_type.is_scalar() { left } else { right };
        let mut products: Vec<u64> = vec![0; vector.bits.len()];
        let mut product_units: Vec<UnitTag> = vec![UnitTag::BARE; vector.bits.len()];
        for index in 0..products.len() {
            products[index] = arithmetic::binary(
                BinaryOperator::Multiply,
                kind,
                vector.bits[index],
                scalar.bits[0],
                context.profile,
                &mut fault,
            );
            product_units[index] = UnitChecker::multiply(vector.units[index], scalar.units[0]);
        }
        return Value::new(result_type, products, product_units);
    }

    let rows: usize = if left_type.is_vector() { 1 } else { left_type.rows };
    let inner: usize = if left_type.is_vector() { left_type.size() } else { left_type.columns };
    let columns: usize = if right_type.is_vector() { 1 } else { right_type.columns };
    let left_index = |row: usize, k: usize| row * inner + k;
    let right_index = |k: usize, column: usize| if right_type.is_vector() { k } else { k * columns + column };

    let mut bits: Vec<u64> = vec![0; rows * columns];
    let mut units: Vec<UnitTag> = vec![UnitTag::BARE; rows * columns];
    let math: FloatOps = FloatOps::new(kind, context.profile);
    for row in 0..rows {
        for column in 0..columns {
            let mut unit: UnitTag =
                UnitChecker::multiply(left.units[left_index(row, 0)], right.units[right_index(0, column)]);
            for k in 1..inner {
                unit = context.units.same(
                    unit,
                    UnitChecker::multiply(left.units[left_index(row, k)], right.units[right_index(k, column)]),
                    "mul",
                );
            }
            units[row * columns + column] = unit;

            if kind.is_float() {
                let mut accumulator: f64 = math.multiply(
                    read(context, kind, left.bits[left_index(row, 0)]),
                    read(context, kind, right.bits[right_index(0, column)]),
                );
                for k in 1..inner {
                    accumulator = math.mad(
                        read(context, kind, left.bits[left_index(row, k)]),
                        read(context, kind, right.bits[right_index(k, column)]),
                        accumulator,
                    );
                }
                bits[row * columns + column] = scalars::encode_float(kind, accumulator, context.profile);
            } else {
                let mut accumulator: u64 = arithmetic::binary(
                    BinaryOperator::Multiply,
                    kind,
                    left.bits[left_index(row, 0)],
                    right.bits[right_index(0, column)],
                    context.profile,
                    &mut fault,
                );
                for k in 1..inner {
                    let product: u64 = arithmetic::binary(
                        BinaryOperator::Multiply,
                        kind,
                        left.bits[left_index(row, k)],
                        right.bits[right_index(k, column)],
                        context.profile,
                        &mut fault,
                    );
                    accumulator = arithmetic::binary(
                        BinaryOperator::Add,
                        kind,
                        product,
                        accumulator,
                        context.profile,
                        &mut fault,
                    );
                }
                bits[row * columns + column] = accumulator;
            }
        }
    }
    Value::new(result_type, bits, units)
}

fn transpose(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let matrix: NumericType = *arguments[0].ty.as_numeric().expect("numeric");
    let count: usize = matrix.size();
    let mut bits: Vec<u64> = vec![0; count];
    let mut units: Vec<UnitTag> = vec![UnitTag::BARE; count];
    for row in 0..matrix.rows {
        for column in 0..matrix.columns {
            bits[column * matrix.rows + row] = arguments[0].bits[row * matrix.columns + column];
            units[column * matrix.rows + row] = arguments[0].units[row * matrix.columns + column];
        }
    }
    Value::new(context.result_type.clone(), bits, units)
}

/// Laplace expansion along the first row, folded ((t0 - t1) + t2) - t3, as DXC expands it.
fn determinant(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let matrix: NumericType = *arguments[0].ty.as_numeric().expect("numeric");
    let math: FloatOps = FloatOps::new(ScalarKind::Float, context.profile);
    let size: usize = matrix.rows;
    let mut elements: Vec<Vec<f64>> = vec![vec![0.0; size]; size];
    for row in 0..size {
        for column in 0..size {
            elements[row][column] = read_float(context, &arguments[0], row * size + column);
        }
    }
    let indices: Vec<usize> = (0..size).collect();
    let result: f64 = determinant_of(&math, &elements, &indices, &indices);

    let units: &[UnitTag] = &arguments[0].units;
    let unit: Option<Dimension> = if units.iter().all(|tag| tag.dimension == units[0].dimension) {
        units[0].dimension.power(size as f64)
    } else {
        None
    };
    if unit.is_none() {
        context.report(
            DiagnosticSeverity::Warning,
            "determinant of a matrix with mixed units: unit not tracked".to_string(),
        );
    }
    Value::scalar(
        ScalarKind::Float,
        scalars::encode_float(ScalarKind::Float, result, context.profile),
        UnitTag { dimension: unit.unwrap_or(Dimension::NONE), is_adoptable: units.iter().all(|tag| tag.is_adoptable) },
    )
}

fn determinant_of(math: &FloatOps, elements: &[Vec<f64>], rows: &[usize], columns: &[usize]) -> f64 {
    if rows.len() == 1 {
        return elements[rows[0]][columns[0]];
    }
    if rows.len() == 2 {
        return math.subtract(
            math.multiply(elements[rows[0]][columns[0]], elements[rows[1]][columns[1]]),
            math.multiply(elements[rows[0]][columns[1]], elements[rows[1]][columns[0]]),
        );
    }
    let mut result: f64 = 0.0;
    for column in 0..columns.len() {
        let minor_columns: Vec<usize> =
            columns.iter().enumerate().filter(|(index, _)| *index != column).map(|(_, value)| *value).collect();
        let term: f64 = math
            .multiply(elements[rows[0]][columns[column]], determinant_of(math, elements, &rows[1..], &minor_columns));
        result = if column == 0 {
            term
        } else if column % 2 == 1 {
            math.subtract(result, term)
        } else {
            math.add(term, result)
        };
    }
    result
}

fn lit(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let math: FloatOps = FloatOps::new(ScalarKind::Float, context.profile);
    let normal_dot_light: f64 = read_float(context, &arguments[0], 0);
    let normal_dot_half: f64 = read_float(context, &arguments[1], 0);
    let exponent: f64 = read_float(context, &arguments[2], 0);
    let specular: f64 = if normal_dot_light < 0.0 || normal_dot_half < 0.0 {
        0.0
    } else {
        exp2(&math, math.multiply(exponent, log2(&math, normal_dot_half)))
    };
    // The diffuse term is a select, not arithmetic: its bits are copied (a denormal survives)
    let one: u64 = scalars::from_float(1.0);
    let diffuse: u64 = if normal_dot_light < 0.0 { 0 } else { arguments[0].bits[0] };
    let bits: Vec<u64> = vec![one, diffuse, scalars::encode_float(ScalarKind::Float, specular, context.profile), one];
    Value::new(context.result_type.clone(), bits, vec![UnitTag::BARE; 4])
}

fn dst(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let math: FloatOps = FloatOps::new(ScalarKind::Float, context.profile);
    // (1, a.y * b.y, a.z, b.w): z and w are copies, so their bits pass through unflushed
    let product: f64 = math.multiply(read_float(context, &arguments[0], 1), read_float(context, &arguments[1], 1));
    let bits: Vec<u64> = vec![
        scalars::from_float(1.0),
        scalars::encode_float(ScalarKind::Float, product, context.profile),
        arguments[0].bits[2],
        arguments[1].bits[3],
    ];
    let units: Vec<UnitTag> = vec![
        UnitTag::BARE,
        UnitChecker::multiply(arguments[0].units[1], arguments[1].units[1]),
        arguments[0].units[2],
        arguments[1].units[3],
    ];
    Value::new(context.result_type.clone(), bits, units)
}

// ---- Bits ----

/// DXIL LegacyF32ToF16: WARP rounds toward zero (so finite overflow gives ±65504), NaN → 0x7FFF.
fn f32_to_f16(value: f32, profile: &SemanticsProfile) -> u64 {
    if !profile.half_conversion_toward_zero {
        return f16::from_f32(value).to_bits() as u64;
    }
    let bits: u32 = value.to_bits();
    let sign: u32 = (bits >> 16) & 0x8000;
    let magnitude: u32 = bits & 0x7FFF_FFFF;
    if magnitude > 0x7F80_0000 {
        return (sign | 0x7FFF) as u64;
    }
    if magnitude == 0x7F80_0000 {
        return (sign | 0x7C00) as u64;
    }
    let exponent: i32 = (magnitude >> 23) as i32 - 127;
    if exponent < -14 {
        // Half denormal: a multiple of 2^-24, truncated
        return (sign | (f32::from_bits(magnitude) * 16_777_216.0f32).trunc() as u32) as u64;
    }
    if exponent > 15 {
        return (sign | 0x7BFF) as u64;
    }
    (sign | (((exponent + 15) as u32) << 10) | ((magnitude & 0x7F_FFFF) >> 13)) as u64
}

fn reinterpret(context: &IntrinsicContext, value: &Value, target: ScalarKind) -> Value {
    let ty: ShaderType = value.ty.as_numeric().expect("numeric").with_kind(target).into();
    let bits: Vec<u64> = value
        .bits
        .iter()
        .map(|component| match target {
            ScalarKind::Int => scalars::from_int(*component as u32 as i32),
            _ => *component as u32 as u64,
        })
        .collect();
    let name: &str = match target {
        ScalarKind::Int => "asint",
        ScalarKind::Float => "asfloat",
        _ => "asuint",
    };
    let changes_kind: bool = value.kind_at(0) != target;
    let units: Vec<UnitTag> =
        value.units.iter().map(|unit| if changes_kind { context.units.drop(*unit, name) } else { *unit }).collect();
    Value::new(ty, bits, units)
}

fn as_uint(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    if arguments.len() == 3 {
        let words: ShaderType = arguments[0].ty.as_numeric().expect("numeric").with_kind(ScalarKind::UInt).into();
        let units: Vec<UnitTag> = vec![UnitTag::BARE; words.component_count()];
        let low: Vec<u64> = arguments[0].bits.iter().map(|bits| *bits as u32 as u64).collect();
        let high: Vec<u64> = arguments[0].bits.iter().map(|bits| (*bits >> 32) as u32 as u64).collect();
        context.set_output(1, Value::new(words.clone(), low, units.clone()));
        context.set_output(2, Value::new(words, high, units));
        return Value::void();
    }
    reinterpret(context, &arguments[0], ScalarKind::UInt)
}

fn color_to_ubyte4(context: &IntrinsicContext, arguments: &[Value]) -> Value {
    let math: FloatOps = FloatOps::new(ScalarKind::Float, context.profile);
    let order: [usize; 4] = [2, 1, 0, 3];
    let bits: Vec<u64> = order
        .iter()
        .map(|index| {
            let scaled: f32 = math.multiply(read_float(context, &arguments[0], *index), COLOR_TO_BYTE) as f32;
            scalars::convert(ScalarKind::Float, ScalarKind::Int, scalars::from_float(scaled), context.profile)
        })
        .collect();
    Value::new(context.result_type.clone(), bits, vec![UnitTag::BARE; 4])
}
