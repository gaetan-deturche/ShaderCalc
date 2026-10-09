//! A shader that removes the WARP device must not kill the reference for the lines after it. Its own test binary:
//! the removal would fail the other tests' runs on the shared device.

use shadercalc_core::reference::checker::{self, ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::session::ShaderSession;

#[test]
fn reference_recovers_from_a_removed_device() {
    // WARP removes the device on a double fma
    let crashed: ReferenceOutcome =
        checker::check(&ShaderSession::default().evaluate("mad(1.0L, 2.0L, 0.5L)"), ReferenceMode::Strict);
    assert_eq!(ReferenceVerdict::NotChecked, crashed.verdict, "{crashed}");
    assert!(crashed.to_string().contains("WARP stopped"), "{crashed}");
    let after: ReferenceOutcome =
        checker::check(&ShaderSession::default().evaluate("1.5f + 2.0f"), ReferenceMode::Strict);
    assert_eq!(ReferenceVerdict::Match, after.verdict, "{after}");
}
