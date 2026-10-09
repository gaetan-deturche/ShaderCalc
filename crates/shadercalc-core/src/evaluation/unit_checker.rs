use std::cell::RefCell;

use crate::diagnostics::DiagnosticSeverity;
use crate::units::{Dimension, UnitTag};

/// Unit rules for one operation: sums and comparisons need one unit (a bare literal adopts the other's), products
/// combine them, transcendental functions need dimensionless input. Violations are reports (diagnostics the
/// evaluator adds at the operation), never failures.
pub struct UnitChecker {
    reports: RefCell<Vec<(DiagnosticSeverity, String)>>,
    is_strict: bool,
}

impl UnitChecker {
    pub fn new(is_strict: bool) -> UnitChecker {
        UnitChecker { reports: RefCell::new(Vec::new()), is_strict }
    }

    pub fn report(&self, severity: DiagnosticSeverity, message: String) {
        self.reports.borrow_mut().push((severity, message));
    }

    /// The problems reported so far, in order.
    pub fn take_reports(&self) -> Vec<(DiagnosticSeverity, String)> {
        std::mem::take(&mut self.reports.borrow_mut())
    }

    pub fn same(&self, left: UnitTag, right: UnitTag, context: &str) -> UnitTag {
        if left.dimension == right.dimension {
            return UnitTag { dimension: left.dimension, is_adoptable: left.is_adoptable && right.is_adoptable };
        }
        if right.is_adoptable && !left.is_adoptable {
            self.note_adoption(left.dimension, context);
            return UnitTag::of(left.dimension);
        }
        if left.is_adoptable && !right.is_adoptable {
            self.note_adoption(right.dimension, context);
            return UnitTag::of(right.dimension);
        }
        self.report(
            DiagnosticSeverity::Error,
            format!("{} mixes units: {} and {}", context, left.dimension, right.dimension),
        );
        UnitTag::of(left.dimension)
    }

    pub fn multiply(left: UnitTag, right: UnitTag) -> UnitTag {
        UnitTag {
            dimension: left.dimension.multiply(&right.dimension),
            is_adoptable: left.is_adoptable && right.is_adoptable,
        }
    }

    pub fn divide(left: UnitTag, right: UnitTag) -> UnitTag {
        UnitTag {
            dimension: left.dimension.divide(&right.dimension),
            is_adoptable: left.is_adoptable && right.is_adoptable,
        }
    }

    /// Input of sin, exp, saturate...: must be a pure number.
    pub fn dimensionless(&self, value: UnitTag, context: &str) -> UnitTag {
        if !value.dimension.is_none() && !value.is_adoptable {
            self.report(
                DiagnosticSeverity::Error,
                format!("{} needs a dimensionless value, got {}", context, value.dimension),
            );
        }
        UnitTag { dimension: Dimension::NONE, is_adoptable: value.is_adoptable }
    }

    pub fn power(&self, value: UnitTag, exponent: f64, context: &str) -> UnitTag {
        match value.dimension.power(exponent) {
            Some(raised) => UnitTag { dimension: raised, is_adoptable: value.is_adoptable },
            None => {
                self.report(
                    DiagnosticSeverity::Error,
                    format!("{} of {} has no unit (exponent {})", context, value.dimension, format_exponent(exponent)),
                );
                UnitTag { dimension: Dimension::NONE, is_adoptable: value.is_adoptable }
            }
        }
    }

    /// Bit operations and reinterpretations: the result is raw bits.
    pub fn drop(&self, value: UnitTag, context: &str) -> UnitTag {
        if !value.dimension.is_none() {
            self.report(DiagnosticSeverity::Warning, format!("{} drops the unit {}", context, value.dimension));
        }
        UnitTag { dimension: Dimension::NONE, is_adoptable: value.is_adoptable }
    }

    fn note_adoption(&self, adopted: Dimension, context: &str) {
        if self.is_strict && !adopted.is_none() {
            self.report(DiagnosticSeverity::Warning, format!("number literal in {context} taken as {adopted}"));
        }
    }
}

/// An exponent as the messages show it (0.5, -1, 0.3333333333333333).
fn format_exponent(exponent: f64) -> String {
    crate::values::format_double(exponent)
}
