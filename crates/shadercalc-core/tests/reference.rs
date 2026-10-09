//! Every line also runs on DXC + WARP: the interpreter must give the same bits (or ulps for approximate functions).

mod common;

use common::*;
use shadercalc_core::reference::checker::{self, ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::session::{LineResult, ShaderSession};

const LINES: &[(&str, &str)] = &[
    ("engine", "PackRGBA8(float4(0.25, 0.5, 0.75, 1.0))"),
    ("engine", "KineticEnergy(2 kg, 3 m/s)"),
    ("engine", "Illuminance(100 cd, 2 m)"),
    ("engine", "Bad(3 m, 2 s)"),
    ("engine", "Offset(2 m)"),
    ("engine", "Pythagoras(0.7)"),
    ("engine", "SumSeries(100)"),
    ("engine", "Hash(42u)"),
    ("engine", "Hash(0xDEADBEEFu)"),
    ("engine", "UnitVectorToOctahedron(normalize(float3(0.3, -0.5, -0.8)))"),
    ("cpp", "SmoothStep01(0.25)"),
    ("cpp", "SmoothStep01(1.7)"),
    ("cpp", "RoundToInt(2.5)"),
    ("cpp", "AccumulateTwice(1.0, 0.1)"),
    ("cpp", "AccumulateTwice(2 m, 30 cm)"),
    ("", "firstbithigh(11)"),
    ("", "firstbithigh(-12)"),
    ("", "uint(5) - uint(7)"),
    ("", "int(3.7)"),
    ("", "int(-3.7)"),
    ("", "-2 ^ 2"),
    ("", "~0u"),
    ("", "-7 / 2"),
    ("", "-7 % 3"),
    ("", "7.5 % 2"),
    ("", "int(2147483647) + 1"),
    ("", "2147483647 + 1"),
    ("", "int(1 << 33)"),
    ("", "int x = 1; x << 33"),
    ("", "asuint(sqrt(2.0))"),
    ("", "0.1f + 0.2f"),
    ("", "0.1 + 0.2"),
    ("", "1.0f / 3.0f"),
    ("", "f16tof32(f32tof16(0.1))"),
    ("", "round(2.5)"),
    ("", "round(-0.5)"),
    ("", "countbits(0xF0F0u)"),
    ("", "reversebits(1u)"),
    ("", "sin(1.0)"),
    ("", "cos(100.0)"),
    ("", "pow(2.0, 0.5)"),
    ("", "pow(-2.0, 2.0)"),
    ("", "exp(1.0)"),
    ("", "log(10.0)"),
    ("", "length(float3(3, 4, 12))"),
    ("", "normalize(float3(1, 2, 3))"),
    ("", "smoothstep(0.0, 1.0, 0.25)"),
    ("", "lerp(1.0, 3.0, 0.3)"),
    ("", "frac(-1.25)"),
    ("", "mad(0.1, 0.2, 0.3)"),
    ("", "max(3, 2.5)"),
    ("", "saturate(1.5)"),
    ("", "asfloat(0x7F800000u)"),
    ("", "float3(1, 2, 3).zyx * 2"),
    ("", "dot(float3(1, 2, 3), float3(4, 5, 6))"),
    ("", "cross(float3(1, 0, 0), float3(0, 1, 0))"),
    ("", "fmod(7.5, -2)"),
    ("", "atan2(-1.0, -1.0)"),
    ("", "determinant(float3x3(2, 1, 3, 0.5, 4, 1, 7, 2, 9))"),
    ("", "mul(float3x3(2, 1, 3, 0.5, 4, 1, 7, 2, 9), float3(0.1, 0.2, 0.3))"),
    ("", "reflect(float3(1, -1, 0), normalize(float3(0, 1, 0.1)))"),
    ("", "refract(float3(0.6, -0.8, 0), float3(0, 1, 0), 0.75)"),
    ("", "D3DCOLORtoUBYTE4(float4(0.1, 0.5, 0.9, 1))"),
    ("", "sqrt(4 m^2)"),
    ("", "length(float3(3 m, 4 m, 0 m))"),
    ("", "2 km / 30 min"),
    ("", "100 km / 2 h"),
    ("", "pow(3 m, 2)"),
    ("", "int a[] = { 1, 2, 3 }"),
    ("", "float2x2 m = float2x2(1, 2, 3, 4)"),
];

#[test]
fn lines_match_the_reference() {
    let mut failures: Vec<String> = Vec::new();
    for (program, line) in LINES {
        let mut session: ShaderSession = ShaderSession::default();
        session.set_program(match *program {
            "engine" => ENGINE_HELPERS,
            "cpp" => CPP_HELPERS,
            _ => "",
        });
        let result: LineResult = session.evaluate(line);
        let outcome: ReferenceOutcome = checker::check(&result, ReferenceMode::Strict);
        if !matches!(outcome.verdict, ReferenceVerdict::Match | ReferenceVerdict::WithinTolerance) {
            failures.push(format!("{} = {}: {}\n{}", line, result.text(), outcome, outcome.hlsl));
        }
    }
    assert!(failures.is_empty(), "{} line(s) differ:\n{}", failures.len(), failures.join("\n"));
}
