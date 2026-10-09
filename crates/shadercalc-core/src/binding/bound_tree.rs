use super::intrinsic::{Intrinsic, IntrinsicSignature};
use super::symbols::{FunctionRef, VariableRef};
use crate::diagnostics::SourceSpan;
use crate::types::{ScalarKind, ShaderType, StructField};
use crate::units::Dimension;
use crate::values::Value;

// ---- Expressions: every node carries its static type; conversions are explicit nodes ----

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum UnaryOperator {
    Plus,
    Negate,
    LogicalNot,
    BitwiseNot,
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum BinaryOperator {
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum ConversionKind {
    Identity,
    /// Same shape, other component kind.
    Numeric,
    /// Scalar to vector / matrix (or, cast only, to a struct or array).
    Splat,
    /// Keeps the leading components: float4 → float3, float4 → float, float4x4 → float3x3.
    Truncation,
    /// Explicit cast between same-size layouts: float4 ↔ float2x2, struct ↔ struct.
    Flat,
}

#[derive(Clone, Debug)]
pub struct BoundExpression {
    pub ty: ShaderType,
    pub span: SourceSpan,
    pub kind: BoundExpressionKind,
}

#[derive(Clone, Debug)]
pub enum BoundExpressionKind {
    /// Placeholder after an error, so binding can go on and report more.
    Error,
    Literal(Value),
    /// `3 km` in a calculator line: a literal float in SI that carries a unit.
    UnitLiteral {
        si_value: f64,
        dimension: Dimension,
        unit_text: String,
    },
    Variable(VariableRef),
    Unary {
        operator: UnaryOperator,
        operand: Box<BoundExpression>,
    },
    /// Both operands are already converted to the operation type (shifts: the right one to the left one's kind);
    /// comparisons produce bools of the same shape.
    Binary {
        operator: BinaryOperator,
        left: Box<BoundExpression>,
        right: Box<BoundExpression>,
    },
    /// Short-circuit && / || on scalar bools (HLSL 2021).
    Logical {
        is_and: bool,
        left: Box<BoundExpression>,
        right: Box<BoundExpression>,
    },
    /// The value is already converted to the target's type.
    Assignment {
        target: Box<BoundExpression>,
        value: Box<BoundExpression>,
    },
    /// `a op= b`: (a converted to the operation type) op b, converted back to a's type.
    CompoundAssignment {
        operator: BinaryOperator,
        target: Box<BoundExpression>,
        value: Box<BoundExpression>,
        operation_type: ShaderType,
    },
    Increment {
        target: Box<BoundExpression>,
        is_increment: bool,
        is_prefix: bool,
    },
    Conditional {
        condition: Box<BoundExpression>,
        when_true: Box<BoundExpression>,
        when_false: Box<BoundExpression>,
    },
    /// A user function call. `in` arguments are converted to the parameter type; out/inout arguments are places.
    /// Arguments past `explicit_count` are the parameters' default values.
    Call {
        function: FunctionRef,
        arguments: Vec<BoundExpression>,
        explicit_count: usize,
    },
    IntrinsicCall {
        intrinsic: &'static Intrinsic,
        signature: IntrinsicSignature,
        /// Converted to the resolved parameter types (out arguments stay places).
        arguments: Vec<BoundExpression>,
        /// Compile-time values of constant arguments, as DXC folds them (pow(x, 2) becomes x * x).
        constant_arguments: Vec<Option<Value>>,
    },
    Conversion {
        operand: Box<BoundExpression>,
        kind: ConversionKind,
        is_explicit: bool,
    },
    /// float3(a, b.xy), or an initializer list: every component of the sources, in order, converted to the
    /// target's component kinds. A single scalar source fills every component.
    Construct {
        sources: Vec<BoundExpression>,
        is_initializer_list: bool,
    },
    /// Vector/scalar swizzle (.xzy, .rgb) or matrix element selection (._m00_m11): indices into the flattened
    /// components.
    Swizzle {
        operand: Box<BoundExpression>,
        indices: Vec<usize>,
        text: String,
    },
    /// array[i], vector[i], matrix[i] (a row). `length` is the number of elements that can be indexed.
    Index {
        operand: Box<BoundExpression>,
        index: Box<BoundExpression>,
        length: usize,
    },
    Field {
        operand: Box<BoundExpression>,
        field: StructField,
    },
    Comma {
        left: Box<BoundExpression>,
        right: Box<BoundExpression>,
    },
}

impl BoundExpression {
    pub fn new(ty: ShaderType, span: SourceSpan, kind: BoundExpressionKind) -> BoundExpression {
        BoundExpression { ty, span, kind }
    }

    pub fn error(span: SourceSpan) -> BoundExpression {
        BoundExpression { ty: ShaderType::Void, span, kind: BoundExpressionKind::Error }
    }

    pub fn literal(value: Value, span: SourceSpan) -> BoundExpression {
        BoundExpression { ty: value.ty.clone(), span, kind: BoundExpressionKind::Literal(value) }
    }

    pub fn unit_literal(si_value: f64, dimension: Dimension, unit_text: String, span: SourceSpan) -> BoundExpression {
        BoundExpression {
            ty: ShaderType::scalar(ScalarKind::LiteralFloat),
            span,
            kind: BoundExpressionKind::UnitLiteral { si_value, dimension, unit_text },
        }
    }

    pub fn variable(variable: VariableRef, span: SourceSpan) -> BoundExpression {
        BoundExpression { ty: variable.ty.clone(), span, kind: BoundExpressionKind::Variable(variable) }
    }

    pub fn conversion(
        operand: BoundExpression,
        ty: ShaderType,
        kind: ConversionKind,
        is_explicit: bool,
        span: SourceSpan,
    ) -> BoundExpression {
        BoundExpression {
            ty,
            span,
            kind: BoundExpressionKind::Conversion { operand: Box::new(operand), kind, is_explicit },
        }
    }

    pub fn is_error(&self) -> bool {
        matches!(self.kind, BoundExpressionKind::Error)
    }

    pub fn children(&self) -> Vec<&BoundExpression> {
        use BoundExpressionKind::*;
        match &self.kind {
            Unary { operand, .. } => vec![operand],
            Binary { left, right, .. } | Logical { left, right, .. } | Comma { left, right } => vec![left, right],
            Assignment { target, value } | CompoundAssignment { target, value, .. } => vec![target, value],
            Increment { target, .. } => vec![target],
            Conditional { condition, when_true, when_false } => vec![condition, when_true, when_false],
            Call { arguments, .. } | IntrinsicCall { arguments, .. } => arguments.iter().collect(),
            Conversion { operand, .. } | Swizzle { operand, .. } | Field { operand, .. } => vec![operand],
            Construct { sources, .. } => sources.iter().collect(),
            Index { operand, index, .. } => vec![operand, index],
            Error | Literal(_) | UnitLiteral { .. } | Variable(_) => Vec::new(),
        }
    }
}

// ---- Statements ----

#[derive(Clone, Debug)]
pub struct BoundSwitchSection {
    pub labels: Vec<i64>,
    pub is_default: bool,
    pub statements: Vec<BoundStatement>,
}

#[derive(Clone, Debug)]
pub struct BoundStatement {
    pub span: SourceSpan,
    pub kind: BoundStatementKind,
}

#[derive(Clone, Debug)]
pub enum BoundStatementKind {
    /// `is_scope` is false for `float a, b;`: several declarations, no braces.
    Block {
        statements: Vec<BoundStatement>,
        is_scope: bool,
    },
    VariableDeclaration {
        variable: VariableRef,
        initializer: Option<BoundExpression>,
    },
    Expression(BoundExpression),
    If {
        condition: BoundExpression,
        then: Box<BoundStatement>,
        otherwise: Option<Box<BoundStatement>>,
    },
    Loop {
        initializer: Option<Box<BoundStatement>>,
        condition: Option<BoundExpression>,
        step: Option<BoundExpression>,
        body: Box<BoundStatement>,
        is_do_while: bool,
    },
    Switch {
        value: BoundExpression,
        sections: Vec<BoundSwitchSection>,
    },
    Return(Option<BoundExpression>),
    Jump {
        is_break: bool,
    },
}

impl BoundStatement {
    pub fn new(span: SourceSpan, kind: BoundStatementKind) -> BoundStatement {
        BoundStatement { span, kind }
    }

    pub fn block(statements: Vec<BoundStatement>, span: SourceSpan, is_scope: bool) -> BoundStatement {
        BoundStatement { span, kind: BoundStatementKind::Block { statements, is_scope } }
    }

    pub fn empty(span: SourceSpan) -> BoundStatement {
        BoundStatement::block(Vec::new(), span, true)
    }

    /// The expressions a statement holds directly or through nested statements.
    pub fn expressions(&self) -> Vec<&BoundExpression> {
        let mut found: Vec<&BoundExpression> = Vec::new();
        self.collect_expressions(&mut found);
        found
    }

    fn collect_expressions<'a>(&'a self, found: &mut Vec<&'a BoundExpression>) {
        match &self.kind {
            BoundStatementKind::Block { statements, .. } => {
                for statement in statements {
                    statement.collect_expressions(found);
                }
            }
            BoundStatementKind::VariableDeclaration { initializer: Some(initializer), .. } => found.push(initializer),
            BoundStatementKind::VariableDeclaration { initializer: None, .. } => {}
            BoundStatementKind::Expression(expression) => found.push(expression),
            BoundStatementKind::If { condition, then, otherwise } => {
                found.push(condition);
                then.collect_expressions(found);
                if let Some(otherwise) = otherwise {
                    otherwise.collect_expressions(found);
                }
            }
            BoundStatementKind::Loop { initializer, condition, step, body, .. } => {
                if let Some(initializer) = initializer {
                    initializer.collect_expressions(found);
                }
                found.extend(condition.iter());
                found.extend(step.iter());
                body.collect_expressions(found);
            }
            BoundStatementKind::Switch { value, sections } => {
                found.push(value);
                for section in sections {
                    for statement in &section.statements {
                        statement.collect_expressions(found);
                    }
                }
            }
            BoundStatementKind::Return(Some(value)) => found.push(value),
            BoundStatementKind::Return(None) | BoundStatementKind::Jump { .. } => {}
        }
    }
}
