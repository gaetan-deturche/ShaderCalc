#![allow(dead_code)]

use shadercalc_core::diagnostics::{Diagnostic, DiagnosticSeverity};
use shadercalc_core::session::{LineResult, ShaderSession};

pub const ENGINE_HELPERS: &str = r#"
// Pasted as found in engine shaders
float2 UnitVectorToOctahedron(float3 N)
{
    N.xy /= dot(1, abs(N));
    if (N.z <= 0)
    {
        N.xy = (1 - abs(N.yx)) * select(N.xy >= 0, float2(1, 1), float2(-1, -1));
    }
    return N.xy;
}

uint PackRGBA8(float4 Color)
{
    uint4 Bytes = uint4(saturate(Color) * 255.0 + 0.5);
    return Bytes.x | (Bytes.y << 8) | (Bytes.z << 16) | (Bytes.w << 24);
}

float KineticEnergy(float Mass, float Speed) { return 0.5 * Mass * Speed * Speed; }
float Illuminance(float Intensity, float Distance) { return Intensity / (Distance * Distance); }
float Bad(float Distance, float Time) { return Distance + Time; }
float Offset(float Distance) { return Distance + 1.0; }

void SinCos(float Angle, out float S, out float C) { S = sin(Angle); C = cos(Angle); }
float Pythagoras(float Angle) { float s, c; SinCos(Angle, s, c); return s * s + c * c; }

float SumSeries(int Count)
{
    float Sum = 0;
    for (int i = 1; i <= Count; ++i)
    {
        Sum += 1.0 / (i * i);
    }
    return Sum;
}

uint Hash(uint Seed)
{
    Seed ^= Seed >> 16;
    Seed *= 0x7feb352du;
    Seed ^= Seed >> 15;
    Seed *= 0x846ca68bu;
    return Seed ^ (Seed >> 16);
}
"#;

pub const CPP_HELPERS: &str = r#"
#include <algorithm>

inline float SmoothStep01(const float& x)
{
    const float t = std::clamp(x, 0.0f, 1.0f);
    return t * t * (3.0f - 2.0f * t);
}

inline int RoundToInt(float value) { return static_cast<int>(value + 0.5f); }
inline void Accumulate(float& total, const float amount) { total += amount; }
inline float AccumulateTwice(float start, float amount)
{
    auto total = start;
    Accumulate(total, amount);
    Accumulate(total, amount);
    return total;
}
"#;

pub fn joined(diagnostics: &[Diagnostic], separator: &str) -> String {
    diagnostics.iter().map(Diagnostic::to_string).collect::<Vec<String>>().join(separator)
}

/// A session with the program bound without errors.
pub fn session(program: &str) -> ShaderSession {
    let mut session: ShaderSession = ShaderSession::default();
    let diagnostics: Vec<Diagnostic> = session.set_program(program);
    assert!(!diagnostics.iter().any(Diagnostic::is_error), "{}", joined(&diagnostics, "\n"));
    session
}

/// Evaluates a line that must have no warnings or errors; returns what it shows.
pub fn eval(session: &mut ShaderSession, line: &str) -> String {
    let result: LineResult = session.evaluate(line);
    assert!(
        !result.diagnostics.iter().any(|diagnostic| diagnostic.severity != DiagnosticSeverity::Info),
        "{}: {}",
        line,
        joined(&result.diagnostics, " | ")
    );
    result.text()
}

/// Runs every case and reports all the mismatches at once.
pub fn check_cases(cases: &[(&str, &str)], mut evaluate: impl FnMut(&str) -> String) {
    let mut failures: Vec<String> = Vec::new();
    for (line, expected) in cases {
        let actual: String = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| evaluate(line)))
            .unwrap_or_else(|panic| panic.downcast_ref::<String>().cloned().unwrap_or_else(|| "panicked".to_string()));
        if actual != *expected {
            failures.push(format!("{line}: expected {expected}, got {actual}"));
        }
    }
    assert!(failures.is_empty(), "{} failing case(s):\n{}", failures.len(), failures.join("\n"));
}

/// Binds a program that must report an error containing `message`.
pub fn assert_program_error(program: &str, message: &str) {
    let mut session: ShaderSession = ShaderSession::default();
    let diagnostics: Vec<Diagnostic> = session.set_program(program);
    assert!(
        diagnostics.iter().any(|diagnostic| diagnostic.is_error() && diagnostic.message.contains(message)),
        "{program}: no error containing '{message}' in {}",
        joined(&diagnostics, " | ")
    );
}
