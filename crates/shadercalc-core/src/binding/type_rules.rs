use super::bound_tree::{BinaryOperator, ConversionKind};
use crate::types::{NumericType, ScalarKind, ShaderType};

/// How one type converts to another: whether it is allowed, how, how good a match it is, and any warning.
#[derive(Clone, Debug, PartialEq)]
pub struct ConversionInfo {
    pub is_possible: bool,
    pub kind: ConversionKind,
    pub cost: i32,
    pub warning: Option<String>,
}

impl ConversionInfo {
    pub fn impossible() -> ConversionInfo {
        ConversionInfo { is_possible: false, kind: ConversionKind::Identity, cost: i32::MAX, warning: None }
    }

    fn possible(kind: ConversionKind, cost: i32, warning: Option<String>) -> ConversionInfo {
        ConversionInfo { is_possible: true, kind, cost, warning }
    }
}

pub const TRUNCATION_COST: i32 = 1000;

/// HLSL type rules as DXC applies them: literal adoption, promotion, conversions, operator result types.
pub fn classify(from: &ShaderType, to: &ShaderType, is_explicit: bool) -> ConversionInfo {
    if from == to {
        return ConversionInfo::possible(ConversionKind::Identity, 0, None);
    }
    if from.is_void() || to.is_void() {
        return ConversionInfo::impossible();
    }

    if let (ShaderType::Numeric(source), ShaderType::Numeric(target)) = (from, to) {
        let kind_cost: i32 = kind_cost(source.kind, target.kind);
        let truncation = || Some(format!("implicit truncation of {source} to {target}"));
        if source.shape == target.shape && source.rows == target.rows && source.columns == target.columns {
            return ConversionInfo::possible(ConversionKind::Numeric, kind_cost, None);
        }
        if source.is_scalar() {
            return ConversionInfo::possible(ConversionKind::Splat, 10 + kind_cost, None);
        }
        if target.is_scalar() {
            return ConversionInfo::possible(ConversionKind::Truncation, TRUNCATION_COST + kind_cost, truncation());
        }
        if source.is_vector() && target.is_vector() && target.size() < source.size() {
            return ConversionInfo::possible(ConversionKind::Truncation, TRUNCATION_COST + kind_cost, truncation());
        }
        if source.is_matrix() && target.is_matrix() && target.rows <= source.rows && target.columns <= source.columns {
            return ConversionInfo::possible(ConversionKind::Truncation, TRUNCATION_COST + kind_cost, truncation());
        }
        // Vector <-> matrix, implicitly too: same size (float2x2 <-> float4), or a row/column matrix that may
        // truncate (float1x3 -> float2, float4 -> float1x2). Never float2x2 -> float2 or float1x4 -> float2x2.
        if source.is_vector() != target.is_vector() && !source.is_scalar() && !target.is_scalar() {
            let matrix: &NumericType = if source.is_matrix() { source } else { target };
            let is_row_or_column: bool = matrix.rows == 1 || matrix.columns == 1;
            if source.size() == target.size() {
                return ConversionInfo::possible(
                    ConversionKind::Flat,
                    if is_row_or_column { 20 } else { 50 } + kind_cost,
                    None,
                );
            }
            if is_row_or_column && target.size() < source.size() {
                return ConversionInfo::possible(ConversionKind::Flat, TRUNCATION_COST + kind_cost, truncation());
            }
        }
        return ConversionInfo::impossible();
    }

    if !is_explicit {
        return ConversionInfo::impossible();
    }
    // (S)0, (float[4])0
    if from.is_scalar() {
        return ConversionInfo::possible(ConversionKind::Splat, 50, None);
    }
    if to.component_count() <= from.component_count() {
        ConversionInfo::possible(ConversionKind::Flat, 50, None)
    } else {
        ConversionInfo::impossible()
    }
}

/// Ranking used by overload resolution: 0 exact, small for literal adoption and promotions.
pub fn kind_cost(from: ScalarKind, to: ScalarKind) -> i32 {
    if from == to {
        return 0;
    }
    if from == ScalarKind::LiteralInt {
        return match to {
            ScalarKind::Int => 1,
            ScalarKind::UInt | ScalarKind::Int64 | ScalarKind::UInt64 => 2,
            ScalarKind::LiteralFloat => 2,
            _ => 3,
        };
    }
    if from == ScalarKind::LiteralFloat {
        return match to {
            ScalarKind::Float => 1,
            ScalarKind::Half | ScalarKind::Double => 2,
            _ => 4,
        };
    }
    if from == ScalarKind::Bool || to == ScalarKind::Bool {
        return 4;
    }
    // Widening (int -> uint/int64/float, float -> double) beats narrowing
    if to.rank() > from.rank() { 2 } else { 3 }
}

/// The kind both operands of an arithmetic operation take.
pub fn promote_kinds(left: ScalarKind, right: ScalarKind) -> ScalarKind {
    if left == right {
        return if left == ScalarKind::Bool { ScalarKind::Int } else { left };
    }
    if left.is_literal() && right.is_literal() {
        return if left == ScalarKind::LiteralFloat || right == ScalarKind::LiteralFloat {
            ScalarKind::LiteralFloat
        } else {
            ScalarKind::LiteralInt
        };
    }
    if left.is_literal() {
        return adapt_literal(left, right);
    }
    if right.is_literal() {
        return adapt_literal(right, left);
    }
    let left_kind: ScalarKind = if left == ScalarKind::Bool { ScalarKind::Int } else { left };
    let right_kind: ScalarKind = if right == ScalarKind::Bool { ScalarKind::Int } else { right };
    if left_kind.rank() >= right_kind.rank() { left_kind } else { right_kind }
}

/// A literal meeting a concrete kind: it adopts it, except that a float literal makes integers float.
fn adapt_literal(literal: ScalarKind, other: ScalarKind) -> ScalarKind {
    if other == ScalarKind::Bool {
        return if literal == ScalarKind::LiteralFloat { ScalarKind::Float } else { ScalarKind::Int };
    }
    if literal == ScalarKind::LiteralFloat && other.is_integer() { ScalarKind::Float } else { other }
}

/// Common kind that keeps bools (for ?:, select, comparisons of bools).
pub fn common_kind(left: ScalarKind, right: ScalarKind) -> ScalarKind {
    if left == ScalarKind::Bool && right == ScalarKind::Bool { ScalarKind::Bool } else { promote_kinds(left, right) }
}

/// Shape of a component-wise operation between two numeric types: a scalar takes the other's shape; vectors and
/// matrices of different sizes truncate to the smaller one (with a warning). None when they can't combine.
pub fn combine_shapes(
    left: &NumericType,
    right: &NumericType,
    kind: ScalarKind,
    warning: &mut Option<String>,
) -> Option<NumericType> {
    *warning = None;
    if left.is_scalar() {
        return Some(right.with_kind(kind));
    }
    if right.is_scalar() {
        return Some(left.with_kind(kind));
    }
    if left.is_vector() && right.is_vector() {
        if left.size() != right.size() {
            *warning = Some(format!("implicit truncation of vector type ({left} and {right})"));
        }
        return Some(NumericType::vector(kind, left.size().min(right.size())));
    }
    if left.is_matrix() && right.is_matrix() {
        if left.rows != right.rows || left.columns != right.columns {
            *warning = Some(format!("implicit truncation of matrix type ({left} and {right})"));
        }
        return Some(NumericType::matrix(kind, left.rows.min(right.rows), left.columns.min(right.columns)));
    }
    // vector with a 1xN / Nx1 matrix of the same size behaves as the vector
    let vector: &NumericType = if left.is_vector() { left } else { right };
    let matrix: &NumericType = if left.is_matrix() { left } else { right };
    if (matrix.rows == 1 || matrix.columns == 1) && matrix.size() == vector.size() {
        return Some(vector.with_kind(kind));
    }
    None
}

pub fn is_comparison(operation: BinaryOperator) -> bool {
    matches!(
        operation,
        BinaryOperator::Equal
            | BinaryOperator::NotEqual
            | BinaryOperator::Less
            | BinaryOperator::LessOrEqual
            | BinaryOperator::Greater
            | BinaryOperator::GreaterOrEqual
    )
}

pub fn is_bitwise(operation: BinaryOperator) -> bool {
    matches!(operation, BinaryOperator::BitwiseAnd | BinaryOperator::BitwiseOr | BinaryOperator::BitwiseXor)
}

pub fn is_shift(operation: BinaryOperator) -> bool {
    matches!(operation, BinaryOperator::ShiftLeft | BinaryOperator::ShiftRight)
}

/// Operand, right operand and result types of a binary operator.
pub struct BinaryResolution {
    pub operand_type: NumericType,
    pub right_type: NumericType,
    pub result_type: NumericType,
}

/// Operand and result types of a binary operator, or an error message. `warning` gets any truncation warning.
pub fn resolve_binary(
    operation: BinaryOperator,
    left: &NumericType,
    right: &NumericType,
    warning: &mut Option<String>,
) -> Result<BinaryResolution, String> {
    *warning = None;
    let kind: ScalarKind;
    if is_shift(operation) {
        // The left operand decides the type; a literal on the left adopts the right one's kind
        kind = if left.kind == ScalarKind::Bool {
            ScalarKind::Int
        } else if left.kind.is_literal() && !right.kind.is_literal() {
            promote_kinds(left.kind, right.kind)
        } else {
            left.kind
        };
        if !kind.is_integer() || !(right.kind.is_integer() || right.kind == ScalarKind::Bool) {
            return Err(format!("'{}' needs integer operands, got {} and {}", symbol(operation), left, right));
        }
    } else if is_bitwise(operation) {
        kind = if left.kind == ScalarKind::Bool && right.kind == ScalarKind::Bool {
            ScalarKind::Bool
        } else {
            promote_kinds(left.kind, right.kind)
        };
        if kind.is_float() {
            return Err(format!("'{}' needs integer operands, got {} and {}", symbol(operation), left, right));
        }
    } else if matches!(operation, BinaryOperator::Equal | BinaryOperator::NotEqual) {
        kind = common_kind(left.kind, right.kind);
    } else {
        kind = promote_kinds(left.kind, right.kind);
        if operation == BinaryOperator::Remainder && kind == ScalarKind::Double {
            return Err("'%' can't be used with doubles, cast to float first".to_string());
        }
    }

    let Some(shape) = combine_shapes(left, right, kind, warning) else {
        return Err(format!("'{}' can't combine {} and {}", symbol(operation), left, right));
    };
    let result_type: NumericType = if is_comparison(operation) { shape.with_kind(ScalarKind::Bool) } else { shape };
    Ok(BinaryResolution { operand_type: shape, right_type: shape, result_type })
}

pub fn symbol(operation: BinaryOperator) -> &'static str {
    match operation {
        BinaryOperator::Add => "+",
        BinaryOperator::Subtract => "-",
        BinaryOperator::Multiply => "*",
        BinaryOperator::Divide => "/",
        BinaryOperator::Remainder => "%",
        BinaryOperator::BitwiseAnd => "&",
        BinaryOperator::BitwiseOr => "|",
        BinaryOperator::BitwiseXor => "^",
        BinaryOperator::ShiftLeft => "<<",
        BinaryOperator::ShiftRight => ">>",
        BinaryOperator::Equal => "==",
        BinaryOperator::NotEqual => "!=",
        BinaryOperator::Less => "<",
        BinaryOperator::LessOrEqual => "<=",
        BinaryOperator::Greater => ">",
        BinaryOperator::GreaterOrEqual => ">=",
    }
}

pub fn parse_binary_operator(text: &str) -> Option<BinaryOperator> {
    Some(match text {
        "+" => BinaryOperator::Add,
        "-" => BinaryOperator::Subtract,
        "*" => BinaryOperator::Multiply,
        "/" => BinaryOperator::Divide,
        "%" => BinaryOperator::Remainder,
        "&" => BinaryOperator::BitwiseAnd,
        "|" => BinaryOperator::BitwiseOr,
        "^" => BinaryOperator::BitwiseXor,
        "<<" => BinaryOperator::ShiftLeft,
        ">>" => BinaryOperator::ShiftRight,
        "==" => BinaryOperator::Equal,
        "!=" => BinaryOperator::NotEqual,
        "<" => BinaryOperator::Less,
        "<=" => BinaryOperator::LessOrEqual,
        ">" => BinaryOperator::Greater,
        ">=" => BinaryOperator::GreaterOrEqual,
        _ => return None,
    })
}
