//! The Slang CPU profile: C rules from Slang's C++ prelude and core-module default bodies. No reference runs it, so
//! the expected values come from those sources (checked with float32 arithmetic outside the interpreter).

mod common;

use common::*;
use shadercalc_core::diagnostics::Diagnostic;
use shadercalc_core::semantics::SemanticsProfile;
use shadercalc_core::session::{LineResult, ShaderSession};

fn slang_session() -> ShaderSession {
    ShaderSession::new(SemanticsProfile::SLANG_CPU)
}

#[test]
fn slang_cpu_follows_c_rules() {
    check_cases(
        &[
            // powf, roundf (ties away), fmaf, fmodf
            ("pow(-2.0, 3.0)", "-8"),
            ("round(2.5)", "3"),
            ("round(-2.5)", "-3"),
            ("mad(0.1f, 10.0f, -1.0f)", "1.4901161E-08"),
            ("fmod(5.0, 1.5)", "0.5"),
            // Literals: float and int (int64_t when it doesn't fit)
            ("16777217.0 + 1.0", "16777216"),
            ("3000000000 + 1", "3000000001"),
            // No denormal flush; f32tof16 rounds to nearest even
            ("1e-40f * 1.0f", "1E-40"),
            ("f32tof16(65520.0)", "31744"),
            // C++ casts on x86-64: the integer indefinite, uint through int64
            ("int(3e9)", "-2147483648"),
            ("int(asfloat(0x7FC00000u))", "-2147483648"),
            ("uint(-1.0f)", "4294967295"),
            // Core-module default bodies
            ("frac(-1e-8)", "1"),
            ("sign(asfloat(0x7FC00000u))", "1"),
            ("degrees(1.0)", "57.295776"),
            ("normalize(float3(1, 2, 3))", "float3(0.26726124, 0.5345225, 0.8017837)"),
            ("dot(float2(-0.0, -0.0), float2(1, 1))", "0"),
            ("lit(0.5, 0.5, 2.0)", "float4(1, 0.5, 0.25, 1)"),
            ("min(asfloat(0x7FC00000u), 1.0)", "1"),
            // 16-bit half, computed in float and stored rounded to nearest even
            ("half(1.0) / half(3.0)", "0.3333"),
            ("half(65520.0)", "+INF"),
            ("1.5h + 1.0h", "2.5"),
            // The prelude has double versions of every float function
            ("sin(2.5L)", "0.5984721441039564"),
            ("determinant(double2x2(1, 2, 3, 4))", "-2"),
        ],
        |line| {
            let result: LineResult = slang_session().evaluate(line);
            assert!(!result.has_errors(), "{line}: {}", joined(&result.diagnostics, " | "));
            result.text()
        },
    );
}

#[test]
fn slang_cpu_reports_cpp_undefined_behaviour() {
    for (line, message) in [("1 << 33", "undefined in C++"), ("-7 / 0", "x86 traps"), ("2147483647 + 1", "")] {
        let result: LineResult = slang_session().evaluate(line);
        assert!(
            message.is_empty() || result.diagnostics.iter().any(|diagnostic| diagnostic.message.contains(message)),
            "{line}: {}",
            joined(&result.diagnostics, " | ")
        );
    }
    // int overflow wraps (two's complement on x86)
    assert_eq!("-2147483648", slang_session().evaluate("2147483647 + 1").text());
}

#[test]
fn hlsl_profile_is_unchanged() {
    let mut session: ShaderSession = ShaderSession::default();
    let problems: Vec<Diagnostic> = session.set_program("");
    assert!(problems.is_empty());
    for (line, expected) in [("pow(-2.0, 3.0)", "NaN"), ("round(2.5)", "2"), ("degrees(1.0)", "57.29578")] {
        assert_eq!(expected, session.evaluate(line).text(), "{line}");
    }
}
