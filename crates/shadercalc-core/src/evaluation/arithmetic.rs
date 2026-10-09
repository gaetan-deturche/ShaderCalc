use crate::binding::bound_tree::{BinaryOperator, UnaryOperator};
use crate::binding::type_rules;
use crate::semantics::{Lowering, SemanticsProfile};
use crate::types::ScalarKind;
use crate::values::scalars;

/// Float arithmetic for one kind, done in double and rounded after every operation: for + - * / and sqrt this gives
/// the correctly rounded float result (53 ≥ 2·24 + 2 bits), i.e. exactly what IEEE float hardware computes.
#[derive(Clone, Copy)]
pub struct FloatOps<'a> {
    kind: ScalarKind,
    /// Literal-only math, which DXC folds at compile time (in double).
    is_literal: bool,
    profile: &'a SemanticsProfile,
}

impl<'a> FloatOps<'a> {
    pub fn new(kind: ScalarKind, profile: &'a SemanticsProfile) -> FloatOps<'a> {
        let is_literal: bool = kind == ScalarKind::LiteralFloat;
        // DXC's literal float is 64-bit, Slang's a float
        let literal_kind: ScalarKind =
            if profile.literal_float_bits == 32 { ScalarKind::Float } else { ScalarKind::Double };
        let kind: ScalarKind = if is_literal { literal_kind } else { kind };
        FloatOps { kind, is_literal, profile }
    }

    pub fn is_double(&self) -> bool {
        self.kind == ScalarKind::Double
    }

    pub fn lowering(&self) -> Lowering {
        self.profile.lowering
    }

    /// Rounds to the kind's precision (and flushes denormals if the profile says so).
    pub fn round(&self, value: f64) -> f64 {
        match self.kind {
            ScalarKind::Float => scalars::flush(value as f32, self.profile) as f64,
            // Slang's half: computed in float, then stored as a half
            ScalarKind::Half => scalars::half_to_float(scalars::float_to_half(value as f32)) as f64,
            _ => value,
        }
    }

    pub fn add(&self, left: f64, right: f64) -> f64 {
        self.round(left + right)
    }

    pub fn subtract(&self, left: f64, right: f64) -> f64 {
        self.round(left - right)
    }

    pub fn multiply(&self, left: f64, right: f64) -> f64 {
        self.round(left * right)
    }

    pub fn divide(&self, left: f64, right: f64) -> f64 {
        self.round(left / right)
    }

    pub fn negate(&self, value: f64) -> f64 {
        -value
    }

    /// Float `%`: WARP's (q - trunc(q)) * b, or C's exact fmod (computing it in double loses nothing). DXC folds
    /// literal operands with fmod (-0.0 % 7 is -0).
    pub fn remainder(&self, left: f64, right: f64) -> f64 {
        if !self.profile.float_remainder_from_quotient || self.is_literal {
            return self.round(left % right);
        }
        let quotient: f64 = self.divide(left, right);
        self.multiply(self.subtract(quotient, quotient.trunc()), right)
    }

    pub fn fma(&self, a: f64, b: f64, c: f64) -> f64 {
        if self.kind == ScalarKind::Float {
            scalars::flush((a as f32).mul_add(b as f32, c as f32), self.profile) as f64
        } else {
            self.round(a.mul_add(b, c))
        }
    }

    /// DXIL FMad: fused when the profile says the backend fuses it.
    pub fn mad(&self, a: f64, b: f64, c: f64) -> f64 {
        if self.profile.fuse_mad { self.fma(a, b, c) } else { self.add(self.multiply(a, b), c) }
    }

    pub fn sqrt(&self, value: f64) -> f64 {
        self.round(value.sqrt())
    }

    /// Applies a math function at the kind's precision (the f32 function for float).
    pub fn apply(&self, single: fn(f32) -> f32, wide: fn(f64) -> f64, value: f64) -> f64 {
        if self.kind == ScalarKind::Double { wide(value) } else { self.round(single(value as f32) as f64) }
    }

    /// Applies a two-argument math function at the kind's precision.
    pub fn apply_binary(&self, single: fn(f32, f32) -> f32, wide: fn(f64, f64) -> f64, left: f64, right: f64) -> f64 {
        if self.kind == ScalarKind::Double {
            wide(left, right)
        } else {
            self.round(single(left as f32, right as f32) as f64)
        }
    }

    /// IEEE minNum / maxNum: a NaN operand yields the other one (DXIL FMin / FMax, C's fmin / fmax).
    pub fn min(left: f64, right: f64) -> f64 {
        if left.is_nan() {
            right
        } else if right.is_nan() || left < right {
            left
        } else {
            right
        }
    }

    pub fn max(left: f64, right: f64) -> f64 {
        if left.is_nan() {
            right
        } else if right.is_nan() || left > right {
            left
        } else {
            right
        }
    }

    /// FMin / FMax on encoded bits, as WARP computes them (x86 MINSS-like): a NaN second operand returns the first
    /// untouched (even a denormal); a NaN first operand returns the second, flushed.
    pub fn min_max_bits(kind: ScalarKind, left: u64, right: u64, is_min: bool, profile: &SemanticsProfile) -> u64 {
        let left_value: f64 = scalars::read_float(kind, left, profile);
        let right_value: f64 = scalars::read_float(kind, right, profile);
        if right_value.is_nan() {
            return left;
        }
        if left_value.is_nan() {
            return scalars::encode_float(kind, right_value, profile);
        }
        let result: f64 = if is_min {
            if left_value < right_value { left_value } else { right_value }
        } else if left_value > right_value {
            left_value
        } else {
            right_value
        };
        scalars::encode_float(kind, result, profile)
    }

    /// DXIL Frc: x - floor(x), kept below 1 (WARP returns 0.99999994 where the subtraction rounds up to 1).
    pub fn frc(&self, value: f64) -> f64 {
        let fraction: f64 = self.subtract(value, value.floor());
        if fraction >= 1.0 {
            if self.kind == ScalarKind::Float {
                f32::from_bits(1.0f32.to_bits() - 1) as f64
            } else {
                f64::from_bits(1.0f64.to_bits() - 1)
            }
        } else {
            fraction
        }
    }

    /// DXIL Saturate: NaN → 0.
    pub fn saturate(value: f64) -> f64 {
        if value.is_nan() || value <= 0.0 {
            0.0
        } else if value > 1.0 {
            1.0
        } else {
            value
        }
    }
}

/// Evaluates `left op right` for one component. `fault` reports undefined behaviour (x / 0).
pub fn binary(
    operation: BinaryOperator,
    kind: ScalarKind,
    left: u64,
    right: u64,
    profile: &SemanticsProfile,
    fault: &mut Option<String>,
) -> u64 {
    *fault = None;
    if type_rules::is_comparison(operation) {
        return scalars::from_bool(compare(operation, kind, left, right, profile));
    }
    if kind.is_float() {
        let math: FloatOps = FloatOps::new(kind, profile);
        let a: f64 = scalars::read_float(kind, left, profile);
        let b: f64 = scalars::read_float(kind, right, profile);
        let result: f64 = match operation {
            BinaryOperator::Add => math.add(a, b),
            BinaryOperator::Subtract => math.subtract(a, b),
            BinaryOperator::Multiply => math.multiply(a, b),
            BinaryOperator::Divide => math.divide(a, b),
            BinaryOperator::Remainder => math.remainder(a, b),
            _ => panic!("{operation:?} on {kind:?}"),
        };
        let encoded_kind: ScalarKind = if kind == ScalarKind::LiteralFloat { ScalarKind::Double } else { kind };
        return scalars::encode_float(encoded_kind, result, profile);
    }
    if kind == ScalarKind::Bool {
        return match operation {
            BinaryOperator::BitwiseAnd => left & right,
            BinaryOperator::BitwiseOr => left | right,
            BinaryOperator::BitwiseXor => left ^ right,
            _ => panic!("{operation:?} on bool"),
        };
    }
    integer_binary(operation, kind, left, right, profile, fault)
}

fn width(kind: ScalarKind, profile: &SemanticsProfile) -> u32 {
    match kind {
        ScalarKind::Int | ScalarKind::UInt => 32,
        ScalarKind::LiteralInt => profile.literal_int_bits,
        _ => 64,
    }
}

/// An unsuffixed integer literal in Slang is an int, or an int64_t when it doesn't fit.
fn literal_width(kind: ScalarKind, left: u64, right: u64, profile: &SemanticsProfile) -> u32 {
    let fits = |bits: u64| i32::try_from(bits as i64).is_ok();
    let width: u32 = width(kind, profile);
    if kind == ScalarKind::LiteralInt && width == 32 && !(fits(left) && fits(right)) { 64 } else { width }
}

fn integer_binary(
    operation: BinaryOperator,
    kind: ScalarKind,
    left: u64,
    right: u64,
    profile: &SemanticsProfile,
    fault: &mut Option<String>,
) -> u64 {
    *fault = None;
    let width: u32 = literal_width(kind, left, right, profile);
    let is_signed: bool = matches!(kind, ScalarKind::Int | ScalarKind::Int64 | ScalarKind::LiteralInt);
    // Work in 64 bits, then wrap to the width
    let signed_left: i64 =
        if width == 32 { if is_signed { left as i32 as i64 } else { left as u32 as i64 } } else { left as i64 };
    let signed_right: i64 =
        if width == 32 { if is_signed { right as i32 as i64 } else { right as u32 as i64 } } else { right as i64 };
    let unsigned_left: u64 = if width == 32 { left as u32 as u64 } else { left };
    let unsigned_right: u64 = if width == 32 { right as u32 as u64 } else { right };

    let raw: u64 = match operation {
        BinaryOperator::Add => unsigned_left.wrapping_add(unsigned_right),
        BinaryOperator::Subtract => unsigned_left.wrapping_sub(unsigned_right),
        BinaryOperator::Multiply => unsigned_left.wrapping_mul(unsigned_right),
        BinaryOperator::Divide | BinaryOperator::Remainder => {
            let is_division: bool = operation == BinaryOperator::Divide;
            let maximum: i64 = if width == 32 { i32::MAX as i64 } else { i64::MAX };
            if unsigned_right == 0 {
                // D3D defines udiv by 0 as all ones; signed division by 0 is undefined (WARP: INT_MAX)
                *fault = Some(if profile.has_reference() {
                    "integer division by zero (undefined: the GPU result is implementation-defined)".to_string()
                } else {
                    "integer division by zero (undefined in C++: x86 traps)".to_string()
                });
                if is_signed && profile.undefined_signed_division_is_max { maximum as u64 } else { u64::MAX }
            } else if is_signed {
                let minimum: i64 = if width == 32 { i32::MIN as i64 } else { i64::MIN };
                if signed_left == minimum && signed_right == -1 {
                    *fault = Some("signed division overflow (INT_MIN / -1, undefined)".to_string());
                    if profile.undefined_signed_division_is_max {
                        maximum as u64
                    } else if is_division {
                        minimum as u64
                    } else {
                        0
                    }
                } else if is_division {
                    (signed_left / signed_right) as u64
                } else {
                    (signed_left % signed_right) as u64
                }
            } else if is_division {
                unsigned_left / unsigned_right
            } else {
                unsigned_left % unsigned_right
            }
        }
        BinaryOperator::BitwiseAnd => unsigned_left & unsigned_right,
        BinaryOperator::BitwiseOr => unsigned_left | unsigned_right,
        BinaryOperator::BitwiseXor => unsigned_left ^ unsigned_right,
        BinaryOperator::ShiftLeft | BinaryOperator::ShiftRight => {
            let mut count: u64 = unsigned_right;
            if profile.mask_shift_count {
                count &= (width - 1) as u64;
            } else if count >= width as u64 {
                *fault = Some(format!("shift count {count} ≥ {width} (undefined in C++)"));
                return wrap_result(
                    kind,
                    width,
                    if operation == BinaryOperator::ShiftRight && is_signed && signed_left < 0 { u64::MAX } else { 0 },
                );
            }
            // 64-bit shifts, as the C# shift operators mask the count to 63
            let count: u32 = (count & 63) as u32;
            if operation == BinaryOperator::ShiftLeft {
                unsigned_left << count
            } else if is_signed {
                (signed_left >> count) as u64
            } else {
                unsigned_left >> count
            }
        }
        _ => panic!("{operation:?} on {kind:?}"),
    };
    wrap_result(kind, width, raw)
}

fn wrap_result(kind: ScalarKind, width: u32, raw: u64) -> u64 {
    match kind {
        ScalarKind::Int => scalars::from_int(raw as i32),
        ScalarKind::UInt => raw as u32 as u64,
        ScalarKind::LiteralInt if width == 32 => scalars::from_int(raw as i32),
        _ => raw,
    }
}

pub fn compare(operation: BinaryOperator, kind: ScalarKind, left: u64, right: u64, profile: &SemanticsProfile) -> bool {
    if kind.is_float() {
        let a: f64 = scalars::read_float(kind, left, profile);
        let b: f64 = scalars::read_float(kind, right, profile);
        return match operation {
            BinaryOperator::Equal => a == b,
            // `!=` is unordered-or-not-equal: true when a NaN is involved
            BinaryOperator::NotEqual => !(a == b),
            BinaryOperator::Less => a < b,
            BinaryOperator::LessOrEqual => a <= b,
            BinaryOperator::Greater => a > b,
            _ => a >= b,
        };
    }
    let order: std::cmp::Ordering = match kind {
        ScalarKind::UInt | ScalarKind::UInt64 | ScalarKind::Bool => left.cmp(&right),
        _ => (left as i64).cmp(&(right as i64)),
    };
    match operation {
        BinaryOperator::Equal => order.is_eq(),
        BinaryOperator::NotEqual => order.is_ne(),
        BinaryOperator::Less => order.is_lt(),
        BinaryOperator::LessOrEqual => order.is_le(),
        BinaryOperator::Greater => order.is_gt(),
        _ => order.is_ge(),
    }
}

pub fn unary(operation: UnaryOperator, kind: ScalarKind, value: u64, profile: &SemanticsProfile) -> u64 {
    match operation {
        UnaryOperator::Plus => value,
        UnaryOperator::LogicalNot => scalars::from_bool(!scalars::is_true(kind, value, profile)),
        UnaryOperator::BitwiseNot => match kind {
            ScalarKind::Int => scalars::from_int(!(value as i32)),
            ScalarKind::UInt => !(value as u32) as u64,
            _ => !value,
        },
        UnaryOperator::Negate => match kind {
            // Negation flips the sign bit (NaN keeps its payload); a float denormal is flushed first
            ScalarKind::Half => value ^ 0x8000,
            ScalarKind::Float => scalars::from_float(scalars::flush(scalars::to_float(value), profile)) ^ 0x8000_0000,
            ScalarKind::Double | ScalarKind::LiteralFloat => value ^ 0x8000_0000_0000_0000,
            ScalarKind::Int => scalars::from_int((value as i32).wrapping_neg()),
            ScalarKind::UInt => 0u32.wrapping_sub(value as u32) as u64,
            _ => 0u64.wrapping_sub(value),
        },
    }
}
