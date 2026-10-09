use std::fmt;
use std::hash::{Hash, Hasher};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, RwLock};

use super::bound_tree::{BoundExpression, BoundStatement};
use crate::diagnostics::SourceSpan;
use crate::syntax::tree::{FunctionSyntax, ParameterMode};
use crate::types::ShaderType;
use crate::values::Value;

static NEXT_SYMBOL_ID: AtomicUsize = AtomicUsize::new(1);

fn next_id() -> usize {
    NEXT_SYMBOL_ID.fetch_add(1, Ordering::Relaxed)
}

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum VariableKind {
    Local,
    Parameter,
    /// static global: re-initialised at the start of every evaluation, like a shader invocation.
    Static,
    /// Non-static global (uniform, cbuffer member): read-only in functions, set from calculator lines.
    Uniform,
    /// A calculator variable: lives across lines.
    Session,
}

pub type VariableRef = Arc<VariableSymbol>;

/// A variable or a parameter. Symbols compare by identity: two declarations of the same name are two variables.
#[derive(Debug)]
pub struct VariableSymbol {
    pub id: usize,
    pub name: String,
    pub ty: ShaderType,
    pub kind: VariableKind,
    pub is_const: bool,
    pub span: SourceSpan,
    /// Parameters only.
    pub mode: Option<ParameterMode>,
    initializer: RwLock<Option<Arc<BoundExpression>>>,
    constant_value: RwLock<Option<Value>>,
    default_value: RwLock<Option<Arc<BoundExpression>>>,
}

impl VariableSymbol {
    pub fn new(name: &str, ty: ShaderType, kind: VariableKind, is_const: bool, span: SourceSpan) -> VariableRef {
        Arc::new(VariableSymbol {
            id: next_id(),
            name: name.to_string(),
            ty,
            kind,
            is_const,
            span,
            mode: None,
            initializer: RwLock::new(None),
            constant_value: RwLock::new(None),
            default_value: RwLock::new(None),
        })
    }

    pub fn parameter(name: &str, ty: ShaderType, mode: ParameterMode, is_const: bool, span: SourceSpan) -> VariableRef {
        Arc::new(VariableSymbol {
            id: next_id(),
            name: name.to_string(),
            ty,
            kind: VariableKind::Parameter,
            is_const,
            span,
            mode: Some(mode),
            initializer: RwLock::new(None),
            constant_value: RwLock::new(None),
            default_value: RwLock::new(None),
        })
    }

    /// A parameter's mode (In for ordinary variables).
    pub fn parameter_mode(&self) -> ParameterMode {
        self.mode.unwrap_or(ParameterMode::In)
    }

    /// Initializer of a global (None: zero).
    pub fn initializer(&self) -> Option<Arc<BoundExpression>> {
        self.initializer.read().expect("symbol lock").clone()
    }

    pub fn set_initializer(&self, initializer: Option<BoundExpression>) {
        *self.initializer.write().expect("symbol lock") = initializer.map(Arc::new);
    }

    /// Compile-time value of a const with a constant initializer (array lengths, case labels).
    pub fn constant_value(&self) -> Option<Value> {
        self.constant_value.read().expect("symbol lock").clone()
    }

    pub fn has_constant_value(&self) -> bool {
        self.constant_value.read().expect("symbol lock").is_some()
    }

    pub fn set_constant_value(&self, value: Option<Value>) {
        *self.constant_value.write().expect("symbol lock") = value;
    }

    pub fn default_value(&self) -> Option<Arc<BoundExpression>> {
        self.default_value.read().expect("symbol lock").clone()
    }

    pub fn set_default_value(&self, value: BoundExpression) {
        *self.default_value.write().expect("symbol lock") = Some(Arc::new(value));
    }
}

impl PartialEq for VariableSymbol {
    fn eq(&self, other: &VariableSymbol) -> bool {
        self.id == other.id
    }
}

impl Eq for VariableSymbol {}

impl Hash for VariableSymbol {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.id.hash(state);
    }
}

impl fmt::Display for VariableSymbol {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{} {}", self.ty, self.name)
    }
}

pub type FunctionRef = Arc<FunctionSymbol>;

#[derive(Debug)]
pub struct FunctionSymbol {
    pub id: usize,
    pub name: String,
    pub return_type: ShaderType,
    pub parameters: Vec<VariableRef>,
    syntax: RwLock<Arc<FunctionSyntax>>,
    body: RwLock<Option<Arc<BoundStatement>>>,
    source_name: RwLock<String>,
}

impl FunctionSymbol {
    pub fn new(
        name: &str,
        return_type: ShaderType,
        parameters: Vec<VariableRef>,
        syntax: Arc<FunctionSyntax>,
    ) -> FunctionRef {
        Arc::new(FunctionSymbol {
            id: next_id(),
            name: name.to_string(),
            return_type,
            parameters,
            syntax: RwLock::new(syntax),
            body: RwLock::new(None),
            source_name: RwLock::new("program".to_string()),
        })
    }

    pub fn syntax(&self) -> Arc<FunctionSyntax> {
        self.syntax.read().expect("symbol lock").clone()
    }

    /// A prototype gets its body from a later definition.
    pub fn attach_definition(&self, definition: Arc<FunctionSyntax>) {
        *self.syntax.write().expect("symbol lock") = definition;
    }

    /// The bound body: a block statement.
    pub fn body(&self) -> Option<Arc<BoundStatement>> {
        self.body.read().expect("symbol lock").clone()
    }

    pub fn set_body(&self, body: Option<BoundStatement>) {
        *self.body.write().expect("symbol lock") = body.map(Arc::new);
    }

    /// The file (or "program") the definition comes from, for diagnostics.
    pub fn source_name(&self) -> String {
        self.source_name.read().expect("symbol lock").clone()
    }

    pub fn set_source_name(&self, source_name: &str) {
        *self.source_name.write().expect("symbol lock") = source_name.to_string();
    }

    pub fn required_parameter_count(&self) -> usize {
        self.parameters.iter().filter(|parameter| parameter.default_value().is_none()).count()
    }

    pub fn signature(&self) -> String {
        let parameters: Vec<String> = self
            .parameters
            .iter()
            .map(|parameter| {
                let mode: &str = match parameter.parameter_mode() {
                    ParameterMode::In => "",
                    ParameterMode::Out => "out ",
                    ParameterMode::InOut => "inout ",
                };
                format!("{mode}{}", parameter.ty)
            })
            .collect();
        format!("{} {}({})", self.return_type, self.name, parameters.join(", "))
    }
}

impl PartialEq for FunctionSymbol {
    fn eq(&self, other: &FunctionSymbol) -> bool {
        self.id == other.id
    }
}

impl Eq for FunctionSymbol {}

impl Hash for FunctionSymbol {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.id.hash(state);
    }
}

impl fmt::Display for FunctionSymbol {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.signature())
    }
}
