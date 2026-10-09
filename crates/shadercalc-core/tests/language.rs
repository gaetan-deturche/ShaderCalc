//! Language coverage: each case is evaluated, then (when it has a value) checked against DXC + WARP.

mod common;

use common::*;
use shadercalc_core::diagnostics::Diagnostic;
use shadercalc_core::reference::checker::{self, ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::session::{LineResult, ShaderSession};

const PROGRAM: &str = r#"
#define CONCAT(a, b) a##b
#define STRINGIFY(x) #x
#define CHANNELS 3
#ifndef CHANNELS
#error CHANNELS must be defined
#endif

namespace Lighting
{
    typedef float3 Color;
    using Scalar = float;

    struct Surface
    {
        Color Albedo;
        Scalar Roughness;
        float2 Uv[2];
    };
}

cbuffer View : register(b0)
{
    float Exposure;
    float4 Tint;
};

static const int Lut[4] = { 1, 3, 5, 7 };
static float Accumulator = 0.25;

float Weight(float x, float scale = 2.0) { return x * scale; }
float Forward(float x);
float Forward(float x) { return x + 1; }

void Order(inout float a, out float b, in float c)
{
    b = a + c;
    a = b * 2;
}

float CallOrder() { float a = 1, b = 0; Order(a, b, 10); return a * 100 + b; }

Surface MakeSurface(float r)
{
    Surface s = (Surface)0;
    s.Albedo = float3(0.5, 0.25, 1);
    s.Roughness = r;
    s.Uv[1] = float2(r, 1 - r);
    return s;
}

float Sum(int count)
{
    float total = 0;
    for (int i = 0, j = 10; i < count; ++i, --j)
    {
        if (i == 2) continue;
        total += Lut[i % 4] * j;
    }
    return total;
}

int Fallthrough(int x)
{
    int result = 0;
    switch (x)
    {
        case 0: result += 1;
        case 1: result += 10; break;
        default: result = -1;
    }
    return result;
}

float Statics() { Accumulator += 1; return Accumulator; }

float3 Swizzles(float4 v)
{
    float3 r = v.wzy;
    r.xz *= 2;
    r.y += v[3];
    return r;
}

int CONCAT(Pas, ted)() { return CHANNELS; }
"#;

const CASES: &[(&str, &str)] = &[
    ("Weight(3)", "6"),
    ("Weight(3, 0.5)", "1.5"),
    ("Forward(1)", "2"),
    ("CallOrder()", "2211"),
    ("MakeSurface(0.25).Uv[1]", "float2(0.25, 0.75)"),
    ("MakeSurface(0.75).Roughness", "0.75"),
    ("Sum(4)", "86"),
    ("Fallthrough(0)", "11"),
    ("Fallthrough(1)", "10"),
    ("Fallthrough(5)", "-1"),
    ("Statics() + Statics()", "3.5"),
    ("Swizzles(float4(1, 2, 3, 4))", "float3(8, 7, 4)"),
    ("Pasted()", "3"),
    ("Lut[2] * Exposure", "0"),
    ("float3(1, 2, 3) > 2", "bool3(false, false, true)"),
    ("any(float3(0, 0, 1)) && !all(int2(1, 0))", "true"),
    ("select(bool2(true, false), float2(1, 2), float2(3, 4))", "float2(1, 4)"),
    ("and(bool2(true, true), bool2(false, true))", "bool2(false, true)"),
    ("010 + 0x10 + 1.f + .5 + 1e1", "35.5"),
    ("min16float3(1, 2, 3).y", "2"),
    ("half2(0.1, 0.2) * 2", "float2(0.2, 0.4)"),
    ("uint64_t(1) << 40", "1099511627776"),
    ("2.5L * 2", "5"),
    ("(int2)float2(1.9, -1.9)", "int2(1, -1)"),
    ("vector<float, 2>(1, 2) + matrix<float, 1, 2>(3, 4)", "float2(4, 6)"),
    ("float2x2 m = { 1, 2, 3, 4 }; m._m10_m01", "float2(3, 2)"),
    ("int x = 5; x += 3; x <<= 2; x", "32"),
    ("float3 v = float3(1, 2, 3); v.zx = v.xz; v", "float3(3, 2, 1)"),
];

fn program_session() -> ShaderSession {
    let mut session: ShaderSession = ShaderSession::default();
    let problems: Vec<Diagnostic> = session.set_program(PROGRAM);
    assert!(!problems.iter().any(Diagnostic::is_error), "{}", joined(&problems, "\n"));
    session
}

#[test]
fn evaluates_and_matches_reference() {
    let mut failures: Vec<String> = Vec::new();
    for (line, expected) in CASES {
        let result: LineResult = program_session().evaluate(line);
        if result.has_errors() || result.text() != *expected {
            failures.push(format!(
                "{}: expected {}, got {} ({})",
                line,
                expected,
                result.text(),
                joined(&result.diagnostics, " | ")
            ));
            continue;
        }
        let outcome: ReferenceOutcome = checker::check(&result, ReferenceMode::Strict);
        if outcome.verdict != ReferenceVerdict::Match {
            failures.push(format!("{} = {}: {}\n{}", line, result.text(), outcome, outcome.hlsl));
        }
    }
    assert!(failures.is_empty(), "{} failing case(s):\n{}", failures.len(), failures.join("\n"));
}

#[test]
fn uniforms_persist_and_reach_the_reference() {
    let mut session: ShaderSession = program_session();
    session.evaluate("Exposure = 2");
    session.evaluate("Tint = float4(1, 0.5, 0.25, 1)");
    let result: LineResult = session.evaluate("Tint.rgb * Exposure");
    assert_eq!("float3(2, 1, 0.5)", result.text());
    let outcome: ReferenceOutcome = checker::check(&result, ReferenceMode::Strict);
    assert_eq!(ReferenceVerdict::Match, outcome.verdict, "{}\n{}", outcome, outcome.hlsl);
}

#[test]
fn reports_errors() {
    assert_program_error("float F() { return ddx(1.0); }", "only exists inside a GPU pipeline");
    assert_program_error("float16_t F() { return 1; }", "16-bit types");
    assert_program_error("float F(float3 v) { return v ? 1 : 0; }", "select()");
    assert_program_error("struct S { float a; }; S F() { return S(1); }", "no constructors");
    assert_program_error("float F() { int a[2] = { 1, 2, 3 }; return a[0]; }", "needs 2 values");
    assert_program_error("float F() { float2 v = 1; return v[2]; }", "out of range");
    assert_program_error("float F(float x) { return x; } float F(float y) { return y; }", "already defined");
    assert_program_error(
        "float F(int a) { return 1; } float F(uint a) { return 2; } float G() { return F(1.5); }",
        "ambiguous",
    );
    assert_program_error("float F() { const float k = 1; k = 2; return k; }", "is const");
    assert_program_error("float Exposure; void F() { Exposure = 1; }", "read-only");
}

#[test]
fn approximations_are_flagged_not_hidden() {
    let result: LineResult = ShaderSession::default().evaluate("tanh(100.0)");
    assert_eq!("1", result.text());
    let outcome: ReferenceOutcome = checker::check(&result, ReferenceMode::Strict);
    // WARP's tanh overflows to NaN here; the difference is reported, attributed to approximate functions
    assert_eq!(ReferenceVerdict::Mismatch, outcome.verdict, "{outcome}");
    assert!(outcome.uses_approximations);
}
