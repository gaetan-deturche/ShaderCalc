use std::ops::BitOr;

use crate::diagnostics::SourceSpan;
use crate::types::ScalarKind;
use crate::units::Dimension;

// ---- Types ----

#[derive(Clone, Debug)]
pub enum TypeSyntax {
    /// A type by name: float3, uint2x2, a struct or a typedef. C++ spellings (unsigned int, std::int32_t) are
    /// normalised.
    Named { name: String, span: SourceSpan },
    /// vector<T, N> or matrix<T, R, C>.
    Generic { name: String, element: Box<TypeSyntax>, dimensions: Vec<ExpressionSyntax>, span: SourceSpan },
    /// C++ `auto`: the binder takes the initializer's type.
    Auto { span: SourceSpan },
}

impl TypeSyntax {
    pub fn span(&self) -> SourceSpan {
        match self {
            TypeSyntax::Named { span, .. } | TypeSyntax::Generic { span, .. } | TypeSyntax::Auto { span } => *span,
        }
    }
}

// ---- Expressions ----

#[derive(Clone, Debug)]
pub struct ExpressionSyntax {
    pub kind: ExpressionKind,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub enum ExpressionKind {
    /// A number or bool literal, already typed: unsuffixed numbers have the literal kinds.
    Literal {
        kind: ScalarKind,
        bits: u64,
        text: String,
    },
    /// Calculator only: `3 km/h`, the number converted to SI with its dimension.
    UnitLiteral {
        si_value: f64,
        dimension: Dimension,
        unit_text: String,
    },
    Name(String),
    Unary {
        operator: String,
        operand: Box<ExpressionSyntax>,
    },
    /// ++x, --x, x++, x--.
    Increment {
        operator: String,
        is_prefix: bool,
        target: Box<ExpressionSyntax>,
    },
    Binary {
        operator: String,
        left: Box<ExpressionSyntax>,
        right: Box<ExpressionSyntax>,
    },
    Assignment {
        operator: String,
        target: Box<ExpressionSyntax>,
        value: Box<ExpressionSyntax>,
    },
    Conditional {
        condition: Box<ExpressionSyntax>,
        when_true: Box<ExpressionSyntax>,
        when_false: Box<ExpressionSyntax>,
    },
    /// name(args): a user function, an intrinsic, or a constructor like float3(...).
    Call {
        name: String,
        name_span: SourceSpan,
        arguments: Vec<ExpressionSyntax>,
    },
    /// vector<float, 3>(...).
    Constructor {
        ty: TypeSyntax,
        arguments: Vec<ExpressionSyntax>,
    },
    /// (T)x, (T[2])x, static_cast<T>(x).
    Cast {
        ty: TypeSyntax,
        array_dimensions: Vec<ExpressionSyntax>,
        operand: Box<ExpressionSyntax>,
    },
    /// x.member: a swizzle or a struct field.
    Member {
        target: Box<ExpressionSyntax>,
        member: String,
        member_span: SourceSpan,
    },
    Index {
        target: Box<ExpressionSyntax>,
        index: Box<ExpressionSyntax>,
    },
    /// { a, b, { c } }: flattened when bound, as HLSL does.
    InitializerList(Vec<ExpressionSyntax>),
}

impl ExpressionSyntax {
    pub fn new(kind: ExpressionKind, span: SourceSpan) -> ExpressionSyntax {
        ExpressionSyntax { kind, span }
    }
}

// ---- Statements ----

#[derive(Clone, Debug)]
pub struct BlockSyntax {
    pub statements: Vec<StatementSyntax>,
    pub span: SourceSpan,
}

/// One declarator: `name[2][3] = init`. A None dimension is `[]` (length taken from the initializer).
#[derive(Clone, Debug)]
pub struct DeclaratorSyntax {
    pub name: String,
    pub name_span: SourceSpan,
    pub array_dimensions: Vec<Option<ExpressionSyntax>>,
    pub initializer: Option<ExpressionSyntax>,
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct StorageModifiers(u8);

impl StorageModifiers {
    pub const NONE: StorageModifiers = StorageModifiers(0);
    pub const CONST: StorageModifiers = StorageModifiers(1);
    pub const STATIC: StorageModifiers = StorageModifiers(2);
    pub const GROUP_SHARED: StorageModifiers = StorageModifiers(4);
    pub const UNIFORM: StorageModifiers = StorageModifiers(8);

    pub fn contains(self, flag: StorageModifiers) -> bool {
        self.0 & flag.0 == flag.0
    }
}

impl BitOr for StorageModifiers {
    type Output = StorageModifiers;

    fn bitor(self, other: StorageModifiers) -> StorageModifiers {
        StorageModifiers(self.0 | other.0)
    }
}

#[derive(Clone, Debug)]
pub struct VariableDeclarationSyntax {
    pub modifiers: StorageModifiers,
    pub ty: TypeSyntax,
    pub declarators: Vec<DeclaratorSyntax>,
    pub span: SourceSpan,
}

/// A case label list (None label = default) and the statements that follow.
#[derive(Clone, Debug)]
pub struct SwitchSectionSyntax {
    pub labels: Vec<Option<ExpressionSyntax>>,
    pub statements: Vec<StatementSyntax>,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub struct StatementSyntax {
    pub kind: StatementKind,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub enum StatementKind {
    Block(BlockSyntax),
    VariableDeclaration(VariableDeclarationSyntax),
    Expression(ExpressionSyntax),
    If {
        condition: ExpressionSyntax,
        then: Box<StatementSyntax>,
        otherwise: Option<Box<StatementSyntax>>,
    },
    For {
        initializer: Option<Box<StatementSyntax>>,
        condition: Option<ExpressionSyntax>,
        step: Option<ExpressionSyntax>,
        body: Box<StatementSyntax>,
    },
    While {
        condition: ExpressionSyntax,
        body: Box<StatementSyntax>,
        is_do_while: bool,
    },
    Switch {
        value: ExpressionSyntax,
        sections: Vec<SwitchSectionSyntax>,
    },
    Return(Option<ExpressionSyntax>),
    Jump(String),
    Empty,
}

impl StatementSyntax {
    pub fn new(kind: StatementKind, span: SourceSpan) -> StatementSyntax {
        StatementSyntax { kind, span }
    }
}

// ---- Declarations ----

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum ParameterMode {
    In,
    Out,
    InOut,
}

#[derive(Clone, Debug)]
pub struct ParameterSyntax {
    pub mode: ParameterMode,
    pub is_const: bool,
    pub ty: TypeSyntax,
    pub name: String,
    pub name_span: SourceSpan,
    pub array_dimensions: Vec<Option<ExpressionSyntax>>,
    pub default_value: Option<ExpressionSyntax>,
    pub span: SourceSpan,
}

/// A function definition, or a prototype when `body` is None.
#[derive(Clone, Debug)]
pub struct FunctionSyntax {
    pub return_type: TypeSyntax,
    pub name: String,
    pub name_span: SourceSpan,
    pub parameters: Vec<ParameterSyntax>,
    pub body: Option<BlockSyntax>,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub struct StructSyntax {
    pub name: String,
    pub name_span: SourceSpan,
    pub fields: Vec<VariableDeclarationSyntax>,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub struct TypedefSyntax {
    pub ty: TypeSyntax,
    pub name: String,
    pub name_span: SourceSpan,
    pub array_dimensions: Vec<Option<ExpressionSyntax>>,
    pub span: SourceSpan,
}

/// A global variable (static const, cbuffer member, uniform...).
#[derive(Clone, Debug)]
pub struct GlobalVariableSyntax {
    pub declaration: VariableDeclarationSyntax,
    pub span: SourceSpan,
}

#[derive(Clone, Debug)]
pub enum DeclarationSyntax {
    Function(FunctionSyntax),
    Struct(StructSyntax),
    Typedef(TypedefSyntax),
    GlobalVariable(GlobalVariableSyntax),
}

/// A top-level item of a calculator line or a worksheet file: a declaration or a statement.
#[derive(Clone, Debug)]
pub enum ItemSyntax {
    Declaration(DeclarationSyntax),
    Statement(StatementSyntax),
}

#[derive(Clone, Debug, Default)]
pub struct CompilationUnitSyntax {
    pub declarations: Vec<DeclarationSyntax>,
}
