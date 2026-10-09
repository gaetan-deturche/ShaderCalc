mod common;

use common::*;
use shadercalc_core::diagnostics::{Diagnostic, DiagnosticSeverity};
use shadercalc_core::session::{LineResult, ShaderSession};

#[test]
fn engine_helpers_evaluate() {
    check_cases(
        &[
            ("PackRGBA8(float4(0.25, 0.5, 0.75, 1.0))", "4290740288"),
            ("KineticEnergy(2 kg, 3 m/s)", "9 kg·m²/s² (J)"),
            ("Illuminance(100 cd, 2 m)", "25 cd/m² (lx or nit)"),
            ("Offset(2 m)", "3 m"),
            ("Pythagoras(0.7)", "1"),
            ("Hash(42u)", "388445122"),
            ("Hash(0xDEADBEEFu)", "3861431939"),
            ("UnitVectorToOctahedron(normalize(float3(0.3, -0.5, -0.8)))", "float2(0.6875, -0.8125)"),
        ],
        |line| eval(&mut session(ENGINE_HELPERS), line),
    );
}

#[test]
fn units_error_points_inside_the_function() {
    let result: LineResult = session(ENGINE_HELPERS).evaluate("Bad(3 m, 2 s)");
    assert_eq!("5 m", result.text());
    assert_eq!(1, result.diagnostics.len(), "{}", joined(&result.diagnostics, " | "));
    let error: &Diagnostic = &result.diagnostics[0];
    assert_eq!(Some("Bad".to_string()), error.function);
    assert!(error.message.contains("mixes units: m and s"), "{}", error.message);
}

#[test]
fn units_strict_mode_reports_adoption() {
    let mut session: ShaderSession = session(ENGINE_HELPERS);
    session.options.strict_units = true;
    let result: LineResult = session.evaluate("Offset(2 m)");
    assert_eq!("3 m", result.text());
    assert!(
        result.diagnostics.iter().any(|diagnostic| diagnostic.severity == DiagnosticSeverity::Warning
            && diagnostic.message.contains("taken as m"))
    );
}

#[test]
fn cpp_forms_evaluate() {
    check_cases(
        &[
            ("SmoothStep01(0.25)", "0.15625"),
            ("SmoothStep01(1.7)", "1"),
            ("RoundToInt(2.5)", "3"),
            ("AccumulateTwice(1.0, 0.1)", "1.2"),
            ("AccumulateTwice(2 m, 30 cm)", "2.6 m"),
        ],
        |line| eval(&mut session(CPP_HELPERS), line),
    );
}

#[test]
fn calculator_evaluates() {
    check_cases(
        &[
            // HLSL semantics kalk gets wrong
            ("firstbithigh(11)", "3"),
            ("firstbithigh(-12)", "3"),
            ("uint(5) - uint(7)", "4294967294"),
            ("int(3.7)", "3"),
            ("int(-3.7)", "-3"),
            ("-2 ^ 2", "-4"),
            ("~0u", "4294967295"),
            ("-7 / 2", "-3"),
            ("-7 % 3", "-1"),
            ("7.5 % 2", "1.5"),
            ("int(2147483647) + 1", "-2147483648"),
            // Literals are 64-bit until they meet a type (DXC)
            ("2147483647 + 1", "2147483648"),
            ("int(1 << 33)", "0"),
            ("int x = 1; x << 33", "2"),
            // Intrinsics
            ("asuint(sqrt(2.0))", "1068827891"),
            ("0.1f + 0.2f", "0.3"),
            ("1.0f / 3.0f", "0.33333334"),
            ("f16tof32(f32tof16(0.1))", "0.099975586"),
            ("round(2.5)", "2"),
            ("round(-0.5)", "-0"),
            ("countbits(0xF0F0u)", "8"),
            ("reversebits(1u)", "2147483648"),
            ("length(float3(3, 4, 12))", "13"),
            ("smoothstep(0.0, 1.0, 0.25)", "0.15625"),
            ("frac(-1.25)", "0.75"),
            ("mad(0.1, 0.2, 0.3)", "0.32000002"),
            ("max(3, 2.5)", "3"),
            ("saturate(1.5)", "1"),
            ("asfloat(0x7F800000u)", "+INF"),
            ("float3(1, 2, 3).zyx * 2", "float3(6, 4, 2)"),
            ("dot(float3(1, 2, 3), float3(4, 5, 6))", "32"),
            ("cross(float3(1, 0, 0), float3(0, 1, 0))", "float3(0, 0, 1)"),
            ("pow(-2.0, 2.0)", "4"),
            ("pow(-2.0, 3.0)", "NaN"),
            // Units
            ("sqrt(4 m^2)", "2 m"),
            ("length(float3(3 m, 4 m, 0 m))", "5 m"),
            ("2 km / 30 min", "1.1111112 m/s"),
            ("3 km + 200 m", "3200 m"),
            ("pow(3 m, 2)", "9 m²"),
            ("60Hz * 2 s", "120"),
            ("100 km / 2 h", "13.888889 m/s"),
            // The full table: prefixes, names, plurals, bytes, rendering units
            ("2 kPa", "2000 kg/m·s² (Pa)"),
            ("1 µs + 1 us", "2E-06 s"),
            ("3 hours", "10800 s"),
            ("5 feet", "1.524 m"),
            ("16 MiB", "16777216 B"),
            ("8 b", "1 B"),
            ("100 nit", "100 cd/m² (lx or nit)"),
            ("1 Bq", "1 1/s (Hz)"),
            ("1 Tbsp / 1 tsp", "3"),
            ("float t = 2; 3 m / t", "1.5 m"),
        ],
        |line| eval(&mut session(""), line),
    );
}

#[test]
fn calculator_reports_unit_errors() {
    for (line, message) in [("3 km + 2 s", "mixes units"), ("sin(2 m)", "dimensionless")] {
        let result: LineResult = session("").evaluate(line);
        assert!(
            result.diagnostics.iter().any(|diagnostic| diagnostic.is_error() && diagnostic.message.contains(message)),
            "{line}: {}",
            joined(&result.diagnostics, " | ")
        );
    }
}

#[test]
fn matrices_multiply_transpose_determinant() {
    let mut session: ShaderSession = session("");
    eval(&mut session, "float2x2 m = float2x2(1, 2, 3, 4)");
    assert_eq!("float2(5, 11)", eval(&mut session, "mul(m, float2(1, 2))"));
    assert_eq!("float2(7, 10)", eval(&mut session, "mul(float2(1, 2), m)"));
    assert_eq!("float2x2(1, 3, 2, 4)", eval(&mut session, "transpose(m)"));
    assert_eq!("-2", eval(&mut session, "determinant(m)"));
    assert_eq!("float2(3, 4)", eval(&mut session, "m[1]"));
    assert_eq!("2", eval(&mut session, "m._m01"));
    assert_eq!("float2(1, 4)", eval(&mut session, "m._11_22"));
    assert_eq!("float2x2(7, 10, 15, 22)", eval(&mut session, "mul(m, m)"));
}

#[test]
fn structs_arrays_and_initializers() {
    let mut session: ShaderSession = session(
        r#"
        struct Light { float3 Color; float Intensity; };
        static const float Weights[3] = { 0.25, 0.5, 0.25 };
        float Sum(float values[3]) { float total = 0; for (int i = 0; i < 3; i++) total += values[i]; return total; }
        Light MakeLight() { Light light = { 1, 0.5, 0.25, 2 }; return light; }
        Light Zero() { return (Light)0; }
        "#,
    );
    assert_eq!("1", eval(&mut session, "Sum(Weights)"));
    assert_eq!("{ Color = float3(1, 0.5, 0.25), Intensity = 2 }", eval(&mut session, "MakeLight()"));
    assert_eq!("2", eval(&mut session, "MakeLight().Intensity"));
    assert_eq!("{ Color = float3(0, 0, 0), Intensity = 0 }", eval(&mut session, "Zero()"));
    assert_eq!("{1, 2, 3}", eval(&mut session, "int a[] = { 1, 2, 3 }"));
}

#[test]
fn macros_expand_like_the_preprocessor() {
    let mut session: ShaderSession = session(
        r#"
        #define SQUARE(x) ((x) * (x))
        #define PI 3.14159265
        #if defined(PI) && 1
        float Area(float r) { return PI * SQUARE(r); }
        #else
        float Area(float r) { return 0; }
        #endif
        "#,
    );
    assert_eq!("12.566371", eval(&mut session, "Area(2)"));
}

#[test]
fn overloads_pick_the_best_match() {
    let mut session: ShaderSession = session(
        r#"
        int Kind(int x) { return 1; }
        int Kind(float x) { return 2; }
        int Kind(float3 x) { return 3; }
        "#,
    );
    assert_eq!("1", eval(&mut session, "Kind(5)"));
    assert_eq!("2", eval(&mut session, "Kind(5.0)"));
    assert_eq!("3", eval(&mut session, "Kind(float3(1, 2, 3))"));
}

#[test]
fn control_flow_switch_and_loops() {
    let mut session: ShaderSession = session(
        r#"
        int Classify(int x)
        {
            switch (x)
            {
                case 0: return 10;
                case 1:
                case 2: return 20;
                default: break;
            }
            int n = 0;
            do { n++; } while (n < x);
            return n;
        }
        "#,
    );
    assert_eq!("10", eval(&mut session, "Classify(0)"));
    assert_eq!("20", eval(&mut session, "Classify(2)"));
    assert_eq!("7", eval(&mut session, "Classify(7)"));
}

#[test]
fn session_variables_and_uniforms() {
    let mut session: ShaderSession = session(
        r#"
        float Scale;
        float Scaled(float x) { return x * Scale; }
        "#,
    );
    assert_eq!("0", eval(&mut session, "Scaled(3)"));
    eval(&mut session, "Scale = 2");
    assert_eq!("6", eval(&mut session, "Scaled(3)"));
    eval(&mut session, "v = float3(1, 2, 3)");
    eval(&mut session, "v.y = 5");
    assert_eq!("float3(1, 5, 3)", eval(&mut session, "v"));
    assert_eq!("defined Twice", eval(&mut session, "float Twice(float x) { return 2 * x; }"));
    assert_eq!("10", eval(&mut session, "Twice(v.y)"));
    let truncated: LineResult = session.evaluate("Twice(v)");
    assert_eq!("2", truncated.text());
    assert!(truncated.diagnostics.iter().any(|diagnostic| diagnostic.message.contains("truncation")));
}

#[test]
fn binder_reports_errors() {
    assert_program_error("float Bad(float x) { return x + Missing; }", "unknown name 'Missing'");
    assert_program_error("int F(int x) { return F(x - 1); }", "recursive");
    assert_program_error("float3 F(float3 v) { return v && v; }", "and()");
    assert_program_error("float F(float x) { x.w = 1; return x; }", "reads past");
}

#[test]
fn binder_warns_on_implicit_truncation() {
    let mut session: ShaderSession = ShaderSession::default();
    let diagnostics: Vec<Diagnostic> = session.set_program("float3 F(float4 v) { return v; }");
    assert!(
        diagnostics.iter().any(|diagnostic| diagnostic.severity == DiagnosticSeverity::Warning
            && diagnostic.message.contains("truncation"))
    );
}
