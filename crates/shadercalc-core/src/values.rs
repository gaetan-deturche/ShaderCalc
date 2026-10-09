use std::fmt;

use half::f16;

use crate::semantics::SemanticsProfile;
use crate::types::{ScalarKind, ShaderType};
use crate::units::{DERIVED_NAMES, Dimension, UnitTag};

/// Encoding of one scalar component in a u64: Int is sign-extended, UInt zero-extended, floats keep their exact
/// IEEE bits (so NaN payloads survive), literals are int64 / double.
pub mod scalars {
    use super::*;

    pub fn from_bool(value: bool) -> u64 {
        value as u64
    }

    pub fn from_int(value: i32) -> u64 {
        value as i64 as u64
    }

    pub fn from_uint(value: u32) -> u64 {
        value as u64
    }

    pub fn from_int64(value: i64) -> u64 {
        value as u64
    }

    pub fn from_half(value: f16) -> u64 {
        value.to_bits() as u64
    }

    pub fn from_float(value: f32) -> u64 {
        value.to_bits() as u64
    }

    pub fn from_double(value: f64) -> u64 {
        value.to_bits()
    }

    pub fn to_float(bits: u64) -> f32 {
        f32::from_bits(bits as u32)
    }

    pub fn to_double(bits: u64) -> f64 {
        f64::from_bits(bits)
    }

    pub fn to_half(bits: u64) -> f16 {
        f16::from_bits(bits as u16)
    }

    /// A float kind's value as a double (exact for half, float and double).
    pub fn float_value(kind: ScalarKind, bits: u64) -> f64 {
        match kind {
            ScalarKind::Half => to_half(bits).to_f64(),
            ScalarKind::Float => to_float(bits) as f64,
            _ => to_double(bits),
        }
    }

    /// An integer or bool kind's value as a signed 64-bit integer (UInt64 reinterpreted).
    pub fn integer_value(kind: ScalarKind, bits: u64) -> i64 {
        match kind {
            ScalarKind::UInt => bits as u32 as i64,
            _ => bits as i64,
        }
    }

    /// Any kind's value as a double, for display and unit arithmetic.
    pub fn to_number(kind: ScalarKind, bits: u64) -> f64 {
        match kind {
            ScalarKind::Bool => {
                if bits != 0 {
                    1.0
                } else {
                    0.0
                }
            }
            ScalarKind::UInt64 => bits as f64,
            _ if kind.is_float() => float_value(kind, bits),
            _ => integer_value(kind, bits) as f64,
        }
    }

    pub fn is_true(kind: ScalarKind, bits: u64) -> bool {
        if kind.is_float() {
            let value: f64 = float_value(kind, bits);
            value != 0.0 || value.is_nan()
        } else {
            bits != 0
        }
    }

    /// Wraps an integer result to the kind's width.
    pub fn wrap_integer(kind: ScalarKind, value: i64) -> u64 {
        match kind {
            ScalarKind::Bool => (value != 0) as u64,
            ScalarKind::Int => value as i32 as i64 as u64,
            ScalarKind::UInt => value as u32 as u64,
            _ => value as u64,
        }
    }

    /// Encodes a float result in the kind (rounding to its precision).
    pub fn encode_float(kind: ScalarKind, value: f64, profile: &SemanticsProfile) -> u64 {
        match kind {
            ScalarKind::Half => from_half(f16::from_f64(value)),
            ScalarKind::Float => from_float(flush(value as f32, profile)),
            _ => from_double(value),
        }
    }

    pub fn flush(value: f32, profile: &SemanticsProfile) -> f32 {
        if profile.flush_float_denormals && value.is_subnormal() {
            if value.is_sign_negative() { -0.0 } else { 0.0 }
        } else {
            value
        }
    }

    /// A float operand as an arithmetic operation sees it: 32-bit denormals flushed when the profile says so.
    pub fn read_float(kind: ScalarKind, bits: u64, profile: &SemanticsProfile) -> f64 {
        match kind {
            ScalarKind::Float => flush(to_float(bits), profile) as f64,
            ScalarKind::LiteralFloat => to_double(bits),
            _ => float_value(kind, bits),
        }
    }

    /// HLSL conversion between scalar kinds (casts, implicit conversions, constructors).
    pub fn convert(from: ScalarKind, to: ScalarKind, bits: u64, profile: &SemanticsProfile) -> u64 {
        if from == to {
            return bits;
        }
        if to == ScalarKind::Bool {
            return if from == ScalarKind::Float {
                from_bool(read_float(from, bits, profile) != 0.0)
            } else {
                from_bool(is_true(from, bits))
            };
        }
        if from == ScalarKind::Bool {
            return if to.is_float() {
                let target: ScalarKind = if to == ScalarKind::LiteralFloat { ScalarKind::Double } else { to };
                encode_float(target, bits as f64, profile)
            } else {
                bits
            };
        }

        if from.is_float() {
            let value: f64 = read_float(from, bits, profile);
            return match to {
                ScalarKind::Int => from_int(float_to_integer(value, i32::MIN as f64, i32::MAX as f64, profile) as i32),
                ScalarKind::UInt => from_uint(float_to_integer(value, 0.0, u32::MAX as f64, profile) as u32),
                ScalarKind::Int64 | ScalarKind::LiteralInt => {
                    from_int64(float_to_integer(value, i64::MIN as f64, i64::MAX as f64, profile) as i64)
                }
                ScalarKind::UInt64 => float_to_uint64(value),
                ScalarKind::LiteralFloat => from_double(value),
                _ => encode_float(to, value, profile),
            };
        }

        // From an integer kind
        if to.is_float() {
            // int -> float must round once, from the exact integer (not via double for 64-bit values)
            if to == ScalarKind::Float
                && from == ScalarKind::UInt
                && bits as u32 >= 0x8000_0000
                && profile.unsigned_to_float_via_signed
            {
                return from_float(((bits as u32 - 0x8000_0000) as i32 as f32) + 2_147_483_648.0f32);
            }
            if to == ScalarKind::Float {
                let single: f32 = match from {
                    ScalarKind::UInt64 => bits as f32,
                    ScalarKind::UInt => bits as u32 as f32,
                    _ => bits as i64 as f32,
                };
                return from_float(single);
            }
            if to == ScalarKind::Half {
                let value: f64 = match from {
                    ScalarKind::UInt64 => bits as f64,
                    ScalarKind::UInt => bits as u32 as f64,
                    _ => bits as i64 as f64,
                };
                return from_half(f16::from_f64(value));
            }
            return from_double(if from == ScalarKind::UInt64 { bits as f64 } else { bits as i64 as f64 });
        }
        match to {
            ScalarKind::Int => from_int(bits as i32),
            ScalarKind::UInt => from_uint(bits as u32),
            ScalarKind::Int64 | ScalarKind::UInt64 | ScalarKind::LiteralInt => {
                if from == ScalarKind::UInt {
                    bits as u32 as u64
                } else {
                    bits
                }
            }
            _ => bits,
        }
    }

    fn float_to_integer(value: f64, minimum: f64, maximum: f64, profile: &SemanticsProfile) -> f64 {
        if value.is_nan() {
            return 0.0;
        }
        let truncated: f64 = value.trunc();
        if profile.saturate_float_to_int { truncated.clamp(minimum, maximum) } else { truncated }
    }

    fn float_to_uint64(value: f64) -> u64 {
        if value.is_nan() || value <= 0.0 {
            return 0;
        }
        if value >= 18_446_744_073_709_551_615.0 { u64::MAX } else { value.trunc() as u64 }
    }
}

/// A typed value: every scalar component flattened (matrices row by row, then arrays, then struct fields), each with
/// its exact bits (see [`scalars`]) and its unit.
#[derive(Clone, Debug, PartialEq)]
pub struct Value {
    pub ty: ShaderType,
    pub bits: Vec<u64>,
    pub units: Vec<UnitTag>,
}

impl Value {
    pub fn new(ty: ShaderType, bits: Vec<u64>, units: Vec<UnitTag>) -> Value {
        assert!(
            bits.len() == ty.component_count() && units.len() == bits.len(),
            "{} needs {} components, got {} bits and {} units",
            ty,
            ty.component_count(),
            bits.len(),
            units.len()
        );
        Value { ty, bits, units }
    }

    pub fn void() -> Value {
        Value { ty: ShaderType::Void, bits: Vec::new(), units: Vec::new() }
    }

    pub fn zero(ty: ShaderType, unit: UnitTag) -> Value {
        let count: usize = ty.component_count();
        Value { ty, bits: vec![0; count], units: vec![unit; count] }
    }

    pub fn scalar(kind: ScalarKind, bits: u64, unit: UnitTag) -> Value {
        Value { ty: ShaderType::scalar(kind), bits: vec![bits], units: vec![unit] }
    }

    pub fn from_bool(value: bool) -> Value {
        Value::scalar(ScalarKind::Bool, scalars::from_bool(value), UnitTag::BARE)
    }

    pub fn from_int(value: i32) -> Value {
        Value::scalar(ScalarKind::Int, scalars::from_int(value), UnitTag::BARE)
    }

    pub fn from_uint(value: u32) -> Value {
        Value::scalar(ScalarKind::UInt, scalars::from_uint(value), UnitTag::BARE)
    }

    pub fn from_float(value: f32) -> Value {
        Value::scalar(ScalarKind::Float, scalars::from_float(value), UnitTag::BARE)
    }

    pub fn from_double(value: f64) -> Value {
        Value::scalar(ScalarKind::Double, scalars::from_double(value), UnitTag::BARE)
    }

    pub fn kind_at(&self, index: usize) -> ScalarKind {
        self.ty.kind_at(index)
    }

    pub fn get_float(&self, index: usize) -> f32 {
        scalars::to_float(self.bits[index])
    }

    pub fn get_bool(&self, index: usize) -> bool {
        scalars::is_true(self.kind_at(index), self.bits[index])
    }

    /// Component as a double, whatever its kind (display, unit maths).
    pub fn get_number(&self, index: usize) -> f64 {
        scalars::to_number(self.kind_at(index), self.bits[index])
    }

    /// The value's components as 32-bit words, the way a shader stores them (64-bit kinds: low word first).
    pub fn to_words(&self) -> Vec<u32> {
        let mut words: Vec<u32> = Vec::with_capacity(self.bits.len());
        for (index, bits) in self.bits.iter().enumerate() {
            words.push(*bits as u32);
            if self.kind_at(index).is_64bit() {
                words.push((*bits >> 32) as u32);
            }
        }
        words
    }

    pub fn format(&self, include_units: bool) -> String {
        let mut text: String = String::new();
        let mut index: usize = 0;
        append_value(&mut text, self, &self.ty, &mut index);
        if include_units {
            text.push_str(&format_units(self));
        }
        text
    }
}

impl fmt::Display for Value {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.format(true))
    }
}

// ---- Display ----

pub fn format_component(kind: ScalarKind, bits: u64) -> String {
    match kind {
        ScalarKind::Bool => (if bits != 0 { "true" } else { "false" }).to_string(),
        ScalarKind::Int => (bits as i32).to_string(),
        ScalarKind::UInt => (bits as u32).to_string(),
        ScalarKind::Int64 | ScalarKind::LiteralInt => (bits as i64).to_string(),
        ScalarKind::UInt64 => bits.to_string(),
        ScalarKind::Half => format_double(scalars::to_half(bits).to_f64()),
        ScalarKind::Float => format_float(scalars::to_float(bits)),
        ScalarKind::Double | ScalarKind::LiteralFloat => format_double(scalars::to_double(bits)),
    }
}

/// Shortest round-trip text, in fixed notation unless the exponent is large (scientific "1E+09", "1.5E-05").
pub fn format_float(value: f32) -> String {
    match special_real(value as f64) {
        Some(text) => text,
        None => format_shortest(&format!("{value:e}"), 9),
    }
}

pub fn format_double(value: f64) -> String {
    match special_real(value) {
        Some(text) => text,
        None => format_shortest(&format!("{value:e}"), 17),
    }
}

fn special_real(value: f64) -> Option<String> {
    if value.is_nan() {
        Some("NaN".to_string())
    } else if value == f64::INFINITY {
        Some("+INF".to_string())
    } else if value == f64::NEG_INFINITY {
        Some("-INF".to_string())
    } else if value == 0.0 {
        Some(if value.is_sign_negative() { "-0" } else { "0" }.to_string())
    } else {
        None
    }
}

/// `scientific` is Rust's shortest `{:e}` text ("-1.2345e-7"). Fixed notation is used while the decimal point
/// sits within max(digit count, `precision`) digits and no more than 3 zeros follow it.
fn format_shortest(scientific: &str, precision: usize) -> String {
    let (sign, unsigned): (&str, &str) =
        if let Some(rest) = scientific.strip_prefix('-') { ("-", rest) } else { ("", scientific) };
    let (mantissa, exponent_text): (&str, &str) = unsigned.split_once('e').expect("{:e} has an exponent");
    let digits: String = mantissa.chars().filter(|character| *character != '.').collect();
    let exponent: i32 = exponent_text.parse().expect("{:e} exponent is an integer");
    // Position of the decimal point relative to the first digit
    let scale: i32 = exponent + 1;
    let max_digits: i32 = digits.len().max(precision) as i32;

    let mut text: String = sign.to_string();
    if scale > max_digits || scale < -3 {
        text.push_str(&digits[..1]);
        if digits.len() > 1 {
            text.push('.');
            text.push_str(&digits[1..]);
        }
        let exponent_sign: char = if exponent < 0 { '-' } else { '+' };
        text.push_str(&format!("E{exponent_sign}{:02}", exponent.abs()));
    } else if scale > 0 {
        let integer_digits: usize = scale as usize;
        if digits.len() <= integer_digits {
            text.push_str(&digits);
            text.push_str(&"0".repeat(integer_digits - digits.len()));
        } else {
            text.push_str(&digits[..integer_digits]);
            text.push('.');
            text.push_str(&digits[integer_digits..]);
        }
    } else {
        text.push_str("0.");
        text.push_str(&"0".repeat((-scale) as usize));
        text.push_str(&digits);
    }
    text
}

fn append_value(text: &mut String, value: &Value, ty: &ShaderType, index: &mut usize) {
    match ty {
        ShaderType::Numeric(scalar) if scalar.is_scalar() => {
            text.push_str(&format_component(scalar.kind, value.bits[*index]));
            *index += 1;
        }
        ShaderType::Numeric(numeric) => {
            text.push_str(&numeric.to_string());
            text.push('(');
            for component in 0..numeric.size() {
                if component > 0 {
                    text.push_str(", ");
                }
                text.push_str(&format_component(numeric.kind, value.bits[*index]));
                *index += 1;
            }
            text.push(')');
        }
        ShaderType::Array(array) => {
            text.push('{');
            for element in 0..array.length {
                if element > 0 {
                    text.push_str(", ");
                }
                append_value(text, value, &array.element, index);
            }
            text.push('}');
        }
        ShaderType::Struct(structure) => {
            text.push_str("{ ");
            for (position, field) in structure.fields.iter().enumerate() {
                if position > 0 {
                    text.push_str(", ");
                }
                text.push_str(&field.name);
                text.push_str(" = ");
                append_value(text, value, &field.ty, index);
            }
            text.push_str(" }");
        }
        ShaderType::Void => {}
    }
}

/// " m/s" when every component shares one unit, one unit per component otherwise.
fn format_units(value: &Value) -> String {
    if value.units.is_empty() || value.units.iter().all(|unit| unit.dimension.is_none()) {
        return String::new();
    }
    if value.units.iter().all(|unit| unit.dimension == value.units[0].dimension) {
        return format!(" {}", describe_dimension(&value.units[0].dimension));
    }
    let parts: Vec<String> = value
        .units
        .iter()
        .map(|unit| if unit.dimension.is_none() { "1".to_string() } else { describe_dimension(&unit.dimension) })
        .collect();
    format!(" [{}]", parts.join(", "))
}

pub fn describe_dimension(dimension: &Dimension) -> String {
    match DERIVED_NAMES.get(dimension) {
        Some(name) => format!("{dimension} ({name})"),
        None => dimension.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_like_round_trip_text() {
        let floats: [(f32, &str); 14] = [
            (1e8, "100000000"),
            (1e9, "1E+09"),
            (123456789.0, "123456790"),
            (1e-4, "0.0001"),
            (1e-5, "1E-05"),
            (1.5e-5, "1.5E-05"),
            (-60.0, "-60"),
            (3.4028235e38, "3.4028235E+38"),
            (1e-45, "1E-45"),
            (0.099975586, "0.099975586"),
            (-0.0, "-0"),
            (1.0 / 3.0, "0.33333334"),
            (4294967296.0, "4.2949673E+09"),
            (16777216.0, "16777216"),
        ];
        for (value, expected) in floats {
            assert_eq!(format_float(value), expected);
        }
        let doubles: [(f64, &str); 7] = [
            (1e16, "10000000000000000"),
            (1e17, "1E+17"),
            (123456789012345680000.0, "1.2345678901234568E+20"),
            (1.0 / 3.0, "0.3333333333333333"),
            (5e-324, "5E-324"),
            (0.0999755859375, "0.0999755859375"),
            (1e300, "1E+300"),
        ];
        for (value, expected) in doubles {
            assert_eq!(format_double(value), expected);
        }
    }
}
