use std::fmt;

use crate::evaluation::intrinsic_context::IntrinsicContext;
use crate::syntax::tree::ParameterMode;
use crate::types::ShaderType;
use crate::values::Value;

/// How well a backend's result can be expected to match: exact IEEE operations, or functions each GPU approximates
/// (sin, exp2, log2...), which references are compared against with a tolerance.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum IntrinsicPrecision {
    Exact,
    Approximate,
}

/// Resolved overload: parameter types (arguments are converted to them), modes, return type.
#[derive(Clone, Debug, PartialEq)]
pub struct IntrinsicSignature {
    pub parameter_types: Vec<ShaderType>,
    pub modes: Vec<ParameterMode>,
    pub return_type: ShaderType,
}

impl IntrinsicSignature {
    pub fn all_in(parameter_types: Vec<ShaderType>, return_type: ShaderType) -> IntrinsicSignature {
        let modes: Vec<ParameterMode> = vec![ParameterMode::In; parameter_types.len()];
        IntrinsicSignature { parameter_types, modes, return_type }
    }
}

/// A resolved signature, or why the arguments don't fit.
pub type IntrinsicResolution = Result<IntrinsicSignature, String>;

pub type IntrinsicResolver = Box<dyn Fn(&[ShaderType]) -> IntrinsicResolution + Send + Sync>;

/// Computes an intrinsic. Out arguments are written to the context's outputs.
pub type IntrinsicImplementation = Box<dyn Fn(&IntrinsicContext, &[Value]) -> Value + Send + Sync>;

pub struct Intrinsic {
    pub name: &'static str,
    pub precision: IntrinsicPrecision,
    pub resolve: IntrinsicResolver,
    pub implementation: IntrinsicImplementation,
    /// Arguments whose being a compile-time constant changes DXC's lowering (pow's exponent 2 → x * x).
    pub constant_sensitive_arguments: Vec<usize>,
}

impl fmt::Debug for Intrinsic {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.name)
    }
}

impl fmt::Display for Intrinsic {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.name)
    }
}
