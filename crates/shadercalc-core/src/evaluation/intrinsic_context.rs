use std::cell::RefCell;

use super::unit_checker::UnitChecker;
use crate::binding::intrinsic::IntrinsicSignature;
use crate::diagnostics::DiagnosticSeverity;
use crate::semantics::SemanticsProfile;
use crate::types::ShaderType;
use crate::values::Value;

/// What an intrinsic implementation can see while it runs.
pub struct IntrinsicContext<'a> {
    pub signature: &'a IntrinsicSignature,
    /// The call's result type.
    pub result_type: &'a ShaderType,
    /// Compile-time values of the call's constant arguments.
    pub constant_arguments: &'a [Option<Value>],
    pub profile: &'a SemanticsProfile,
    pub units: UnitChecker,
    /// Values for out arguments, by argument index.
    pub outputs: RefCell<Vec<Option<Value>>>,
    /// Why WARP can't be trusted for this call (see `limit_reference`).
    pub reference_limits: RefCell<Vec<String>>,
}

impl<'a> IntrinsicContext<'a> {
    pub fn new(
        signature: &'a IntrinsicSignature,
        result_type: &'a ShaderType,
        constant_arguments: &'a [Option<Value>],
        profile: &'a SemanticsProfile,
        is_strict_units: bool,
        argument_count: usize,
    ) -> IntrinsicContext<'a> {
        IntrinsicContext {
            signature,
            result_type,
            constant_arguments,
            profile,
            units: UnitChecker::new(is_strict_units),
            outputs: RefCell::new(vec![None; argument_count]),
            reference_limits: RefCell::new(Vec::new()),
        }
    }

    /// Notes that WARP is known to be wrong for this call, so a reference mismatch is WARP's limit, not ours.
    pub fn limit_reference(&self, reason: String) {
        let mut limits = self.reference_limits.borrow_mut();
        if !limits.contains(&reason) {
            limits.push(reason);
        }
    }

    pub fn report(&self, severity: DiagnosticSeverity, message: String) {
        self.units.report(severity, message);
    }

    pub fn set_output(&self, index: usize, value: Value) {
        self.outputs.borrow_mut()[index] = Some(value);
    }

    /// True when argument `index` is a compile-time constant whose every component equals `number`.
    pub fn is_constant(&self, index: usize, number: f64) -> bool {
        match &self.constant_arguments[index] {
            Some(constant) => (0..constant.bits.len()).all(|component| constant.get_number(component) == number),
            None => false,
        }
    }
}
