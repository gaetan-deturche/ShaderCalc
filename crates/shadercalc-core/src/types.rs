use std::fmt;
use std::hash::{Hash, Hasher};
use std::sync::Arc;

/// Scalar component kinds. Unsuffixed literals have their own kinds, as in DXC: they are 64-bit (int64 / double)
/// until an operation or a conversion gives them a concrete type.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum ScalarKind {
    Bool,
    Int,
    UInt,
    Int64,
    UInt64,
    Half,
    Float,
    Double,
    LiteralInt,
    LiteralFloat,
}

impl ScalarKind {
    pub fn is_float(self) -> bool {
        matches!(self, ScalarKind::Half | ScalarKind::Float | ScalarKind::Double | ScalarKind::LiteralFloat)
    }

    pub fn is_integer(self) -> bool {
        matches!(
            self,
            ScalarKind::Int | ScalarKind::UInt | ScalarKind::Int64 | ScalarKind::UInt64 | ScalarKind::LiteralInt
        )
    }

    pub fn is_signed(self) -> bool {
        matches!(self, ScalarKind::Int | ScalarKind::Int64 | ScalarKind::LiteralInt) || self.is_float()
    }

    pub fn is_literal(self) -> bool {
        matches!(self, ScalarKind::LiteralInt | ScalarKind::LiteralFloat)
    }

    pub fn is_64bit(self) -> bool {
        matches!(
            self,
            ScalarKind::Int64
                | ScalarKind::UInt64
                | ScalarKind::Double
                | ScalarKind::LiteralInt
                | ScalarKind::LiteralFloat
        )
    }

    /// Promotion rank: in a binary operation the operand with the higher rank decides the kind.
    pub fn rank(self) -> i32 {
        match self {
            ScalarKind::Bool => 0,
            ScalarKind::LiteralInt => 1,
            ScalarKind::Int => 2,
            ScalarKind::UInt => 3,
            ScalarKind::Int64 => 4,
            ScalarKind::UInt64 => 5,
            ScalarKind::LiteralFloat => 6,
            ScalarKind::Half => 7,
            ScalarKind::Float => 8,
            ScalarKind::Double => 9,
        }
    }

    /// The kind a literal becomes when nothing else decides: int and float, like DXC.
    pub fn materialized(self) -> ScalarKind {
        match self {
            ScalarKind::LiteralInt => ScalarKind::Int,
            ScalarKind::LiteralFloat => ScalarKind::Float,
            _ => self,
        }
    }

    pub fn keyword(self) -> &'static str {
        match self {
            ScalarKind::Bool => "bool",
            ScalarKind::Int => "int",
            ScalarKind::UInt => "uint",
            ScalarKind::Int64 => "int64_t",
            ScalarKind::UInt64 => "uint64_t",
            ScalarKind::Half => "half",
            ScalarKind::Float => "float",
            ScalarKind::Double => "double",
            ScalarKind::LiteralInt => "literal int",
            ScalarKind::LiteralFloat => "literal float",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum NumericShape {
    Scalar,
    Vector,
    Matrix,
}

/// Scalar, vector (1 row) or matrix. Matrix components are flattened row by row, like HLSL's `m[row][column]`.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct NumericType {
    pub kind: ScalarKind,
    pub shape: NumericShape,
    pub rows: usize,
    pub columns: usize,
}

impl NumericType {
    pub fn scalar(kind: ScalarKind) -> NumericType {
        NumericType { kind, shape: NumericShape::Scalar, rows: 1, columns: 1 }
    }

    pub fn vector(kind: ScalarKind, size: usize) -> NumericType {
        NumericType { kind, shape: NumericShape::Vector, rows: 1, columns: size }
    }

    pub fn matrix(kind: ScalarKind, rows: usize, columns: usize) -> NumericType {
        NumericType { kind, shape: NumericShape::Matrix, rows, columns }
    }

    /// Same kind and shape family, other component count (scalar for 1, vector otherwise).
    pub fn scalar_or_vector(kind: ScalarKind, size: usize) -> NumericType {
        if size == 1 { NumericType::scalar(kind) } else { NumericType::vector(kind, size) }
    }

    pub fn is_scalar(&self) -> bool {
        self.shape == NumericShape::Scalar
    }

    pub fn is_vector(&self) -> bool {
        self.shape == NumericShape::Vector
    }

    pub fn is_matrix(&self) -> bool {
        self.shape == NumericShape::Matrix
    }

    /// Component count of a scalar or vector.
    pub fn size(&self) -> usize {
        self.rows * self.columns
    }

    pub fn with_kind(&self, kind: ScalarKind) -> NumericType {
        NumericType { kind, ..*self }
    }
}

impl fmt::Display for NumericType {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.shape {
            NumericShape::Scalar => write!(formatter, "{}", self.kind.keyword()),
            NumericShape::Vector if self.kind.is_literal() => {
                write!(formatter, "vector<{}, {}>", self.kind.keyword(), self.columns)
            }
            NumericShape::Vector => write!(formatter, "{}{}", self.kind.keyword(), self.columns),
            NumericShape::Matrix if self.kind.is_literal() => {
                write!(formatter, "matrix<{}, {}, {}>", self.kind.keyword(), self.rows, self.columns)
            }
            NumericShape::Matrix => write!(formatter, "{}{}x{}", self.kind.keyword(), self.rows, self.columns),
        }
    }
}

#[derive(Debug)]
pub struct ArrayType {
    pub element: ShaderType,
    pub length: usize,
    component_kinds: Vec<ScalarKind>,
}

impl ArrayType {
    /// `float[3]`; declarations put the length after the name instead.
    pub fn innermost_element(&self) -> &ShaderType {
        match &self.element {
            ShaderType::Array(inner) => inner.innermost_element(),
            element => element,
        }
    }

    pub fn dimensions(&self) -> String {
        let inner: String = match &self.element {
            ShaderType::Array(inner) => inner.dimensions(),
            _ => String::new(),
        };
        format!("[{}]{}", self.length, inner)
    }
}

#[derive(Debug, Clone)]
pub struct StructField {
    pub name: String,
    pub ty: ShaderType,
    pub component_offset: usize,
}

/// A struct; two struct types are the same only if they are the same declaration.
#[derive(Debug)]
pub struct StructType {
    pub name: String,
    pub fields: Vec<StructField>,
    pub component_count: usize,
    component_kinds: Vec<ScalarKind>,
}

impl StructType {
    pub fn find_field(&self, name: &str) -> Option<&StructField> {
        self.fields.iter().find(|field| field.name == name)
    }
}

/// Every HLSL type the interpreter knows. Values store their components flattened, in this order.
#[derive(Clone, Debug)]
pub enum ShaderType {
    Numeric(NumericType),
    Array(Arc<ArrayType>),
    Struct(Arc<StructType>),
    Void,
}

impl ShaderType {
    pub fn scalar(kind: ScalarKind) -> ShaderType {
        ShaderType::Numeric(NumericType::scalar(kind))
    }

    pub fn vector(kind: ScalarKind, size: usize) -> ShaderType {
        ShaderType::Numeric(NumericType::vector(kind, size))
    }

    pub fn matrix(kind: ScalarKind, rows: usize, columns: usize) -> ShaderType {
        ShaderType::Numeric(NumericType::matrix(kind, rows, columns))
    }

    pub fn scalar_or_vector(kind: ScalarKind, size: usize) -> ShaderType {
        ShaderType::Numeric(NumericType::scalar_or_vector(kind, size))
    }

    pub fn bool() -> ShaderType {
        ShaderType::scalar(ScalarKind::Bool)
    }

    pub fn int() -> ShaderType {
        ShaderType::scalar(ScalarKind::Int)
    }

    pub fn uint() -> ShaderType {
        ShaderType::scalar(ScalarKind::UInt)
    }

    pub fn float() -> ShaderType {
        ShaderType::scalar(ScalarKind::Float)
    }

    pub fn array(element: ShaderType, length: usize) -> ShaderType {
        let mut component_kinds: Vec<ScalarKind> = Vec::with_capacity(element.component_count() * length);
        for _ in 0..length {
            component_kinds.extend(element.component_kinds());
        }
        ShaderType::Array(Arc::new(ArrayType { element, length, component_kinds }))
    }

    pub fn structure(name: &str, fields: Vec<(String, ShaderType)>) -> ShaderType {
        let mut laid_out: Vec<StructField> = Vec::new();
        let mut component_kinds: Vec<ScalarKind> = Vec::new();
        let mut offset: usize = 0;
        for (field_name, field_type) in fields {
            component_kinds.extend(field_type.component_kinds());
            let count: usize = field_type.component_count();
            laid_out.push(StructField { name: field_name, ty: field_type, component_offset: offset });
            offset += count;
        }
        ShaderType::Struct(Arc::new(StructType {
            name: name.to_string(),
            fields: laid_out,
            component_count: offset,
            component_kinds,
        }))
    }

    /// Number of scalar components once flattened (structs and arrays included).
    pub fn component_count(&self) -> usize {
        match self {
            ShaderType::Numeric(numeric) => numeric.size(),
            ShaderType::Array(array) => array.component_kinds.len(),
            ShaderType::Struct(structure) => structure.component_count,
            ShaderType::Void => 0,
        }
    }

    /// Kind of each flattened component.
    pub fn component_kinds(&self) -> Vec<ScalarKind> {
        match self {
            ShaderType::Numeric(numeric) => vec![numeric.kind; numeric.size()],
            ShaderType::Array(array) => array.component_kinds.clone(),
            ShaderType::Struct(structure) => structure.component_kinds.clone(),
            ShaderType::Void => Vec::new(),
        }
    }

    pub fn kind_at(&self, index: usize) -> ScalarKind {
        match self {
            ShaderType::Numeric(numeric) => numeric.kind,
            ShaderType::Array(array) => array.component_kinds[index],
            ShaderType::Struct(structure) => structure.component_kinds[index],
            ShaderType::Void => panic!("void has no components"),
        }
    }

    pub fn as_numeric(&self) -> Option<&NumericType> {
        match self {
            ShaderType::Numeric(numeric) => Some(numeric),
            _ => None,
        }
    }

    pub fn as_array(&self) -> Option<&Arc<ArrayType>> {
        match self {
            ShaderType::Array(array) => Some(array),
            _ => None,
        }
    }

    pub fn as_struct(&self) -> Option<&Arc<StructType>> {
        match self {
            ShaderType::Struct(structure) => Some(structure),
            _ => None,
        }
    }

    pub fn is_void(&self) -> bool {
        matches!(self, ShaderType::Void)
    }

    pub fn is_scalar(&self) -> bool {
        matches!(self, ShaderType::Numeric(numeric) if numeric.is_scalar())
    }
}

impl From<NumericType> for ShaderType {
    fn from(numeric: NumericType) -> ShaderType {
        ShaderType::Numeric(numeric)
    }
}

impl PartialEq for ShaderType {
    fn eq(&self, other: &ShaderType) -> bool {
        match (self, other) {
            (ShaderType::Numeric(left), ShaderType::Numeric(right)) => left == right,
            (ShaderType::Array(left), ShaderType::Array(right)) => {
                left.length == right.length && left.element == right.element
            }
            (ShaderType::Struct(left), ShaderType::Struct(right)) => Arc::ptr_eq(left, right),
            (ShaderType::Void, ShaderType::Void) => true,
            _ => false,
        }
    }
}

impl Eq for ShaderType {}

impl Hash for ShaderType {
    fn hash<H: Hasher>(&self, state: &mut H) {
        match self {
            ShaderType::Numeric(numeric) => numeric.hash(state),
            ShaderType::Array(array) => {
                array.length.hash(state);
                array.element.hash(state);
            }
            ShaderType::Struct(structure) => std::ptr::hash(Arc::as_ptr(structure), state),
            ShaderType::Void => 0.hash(state),
        }
    }
}

impl fmt::Display for ShaderType {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ShaderType::Numeric(numeric) => write!(formatter, "{numeric}"),
            ShaderType::Array(array) => write!(formatter, "{}{}", array.innermost_element(), array.dimensions()),
            ShaderType::Struct(structure) => write!(formatter, "{}", structure.name),
            ShaderType::Void => write!(formatter, "void"),
        }
    }
}
