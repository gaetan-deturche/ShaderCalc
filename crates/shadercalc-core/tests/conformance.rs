//! Each operation and intrinsic over hundreds of inputs, interpreter vs DXC + WARP. `T F(params) { return
//! expression; }` runs in the interpreter and in one WARP dispatch (inputs read from a buffer in a loop, so nothing is
//! constant-folded), and every sample is compared.

// Symbols hash by their id only (see the crate root)
#![allow(clippy::mutable_key_type)]

use std::io::Write;
use std::sync::Arc;

use shadercalc_core::binding::bound_tree::ConversionKind;
use shadercalc_core::binding::symbols::FunctionRef;
use shadercalc_core::diagnostics::{Diagnostic, DiagnosticBag};
use shadercalc_core::evaluation::evaluator::{EvaluationOptions, Evaluator, Storage};
use shadercalc_core::reference::checker::{from_words, max_ulps};
use shadercalc_core::reference::warp_device::{ENTRY_POINT, ROOT_SIGNATURE, ReferenceMode, WarpDevice};
use shadercalc_core::session::ShaderSession;
use shadercalc_core::types::{NumericType, ScalarKind, ShaderType};
use shadercalc_core::units::UnitTag;
use shadercalc_core::values::Value;

struct ConformanceSample {
    inputs: Vec<Value>,
    ours: Value,
    reference: Vec<u32>,
    ulps: i64,
}

struct ConformanceReport {
    expression: String,
    samples: usize,
    exact: usize,
    max_ulps: i64,
    mismatches: Vec<ConformanceSample>,
}

impl std::fmt::Display for ConformanceReport {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let max: String = if self.max_ulps == i64::MAX { "∞".to_string() } else { self.max_ulps.to_string() };
        write!(formatter, "{:<40} {:>5}/{:<5} exact, max {} ulp", self.expression, self.exact, self.samples, max)?;
        for sample in self.mismatches.iter().take(4) {
            let reference: Value = from_words(&sample.ours.ty, &sample.reference);
            let inputs: Vec<String> = sample.inputs.iter().map(Value::to_string).collect();
            write!(formatter, "\n    ({}) → ours {}, WARP {}", inputs.join(", "), sample.ours, reference)?;
        }
        Ok(())
    }
}

/// splitmix64: deterministic samples.
struct Random(u64);

impl Random {
    fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut mixed: u64 = self.0;
        mixed = (mixed ^ (mixed >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        mixed = (mixed ^ (mixed >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        mixed ^ (mixed >> 31)
    }

    /// In [0, 1).
    fn next_double(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64
    }

    /// In [minimum, maximum).
    fn next_range(&mut self, minimum: i64, maximum: i64) -> i64 {
        minimum + (self.next_u64() % (maximum - minimum) as u64) as i64
    }
}

const SPECIAL_FLOATS: [f32; 38] = [
    0.0,
    -0.0,
    1.0,
    -1.0,
    0.5,
    -0.5,
    1.5,
    2.5,
    -2.5,
    3.5,
    2.0,
    3.0,
    0.1,
    0.25,
    0.75,
    0.999999,
    1.000001,
    100.0,
    -100.0,
    std::f32::consts::PI,
    -std::f32::consts::PI / 2.0,
    1e-7,
    1e-20,
    1e-38,
    1e-40,
    -1e-40,
    1e10,
    1e30,
    -1e30,
    3.4e38,
    255.5,
    16777217.0,
    2147483648.0,
    -2147483904.0,
    4294967296.0,
    f32::INFINITY,
    f32::NEG_INFINITY,
    f32::NAN,
];

fn floats(random_count: usize, seed: u64) -> Vec<f32> {
    let mut random: Random = Random(seed);
    let mut values: Vec<f32> = SPECIAL_FLOATS.to_vec();
    for _ in 0..random_count {
        let mantissa: f32 = (random.next_double() * 2.0 - 1.0) as f32;
        values.push(mantissa * 2.0f32.powi(random.next_range(-24, 25) as i32));
    }
    values
}

fn ints(random_count: usize, seed: u64) -> Vec<i32> {
    let mut random: Random = Random(seed);
    let mut values: Vec<i32> = vec![0, 1, -1, 2, -2, 7, -7, 31, 32, 33, 63, i32::MAX, i32::MIN, 255, -256, 65536];
    for _ in 0..random_count {
        values.push(random.next_range(i32::MIN as i64, i32::MAX as i64) as i32);
    }
    values
}

/// Cartesian product of the special values, then random tuples.
fn float_tuples(arity: usize, random_count: usize, seed: u64) -> Vec<Vec<Value>> {
    let values: Vec<f32> = floats(random_count, seed);
    if arity == 1 {
        return values.iter().map(|value| vec![Value::from_float(*value)]).collect();
    }
    let specials: usize = if arity == 2 { SPECIAL_FLOATS.len() } else { 12 };
    let mut product: Vec<Vec<f32>> = vec![Vec::new()];
    for _ in 0..arity {
        product = product
            .into_iter()
            .flat_map(|prefix| {
                SPECIAL_FLOATS[..specials].iter().map(move |value| {
                    let mut tuple: Vec<f32> = prefix.clone();
                    tuple.push(*value);
                    tuple
                })
            })
            .collect();
    }
    let mut tuples: Vec<Vec<Value>> =
        product.into_iter().map(|tuple| tuple.into_iter().map(Value::from_float).collect()).collect();
    let mut random: Random = Random(seed + 1);
    for _ in 0..random_count {
        tuples.push(
            (0..arity).map(|_| Value::from_float(values[random.next_range(0, values.len() as i64) as usize])).collect(),
        );
    }
    tuples
}

fn load(kind: ScalarKind, word: &str, high: &str) -> String {
    match kind {
        ScalarKind::Int => format!("asint({word})"),
        ScalarKind::UInt => word.to_string(),
        ScalarKind::Bool => format!("({word} != 0u)"),
        ScalarKind::Double => format!("asdouble({word}, {high})"),
        ScalarKind::Int64 => format!("((int64_t)(((uint64_t){high} << 32) | (uint64_t){word}))"),
        ScalarKind::UInt64 => format!("(((uint64_t){high} << 32) | (uint64_t){word})"),
        _ => format!("asfloat({word})"),
    }
}

fn run(parameters: &str, return_type: &str, expression: &str, samples: &[Vec<Value>]) -> ConformanceReport {
    let program: String = format!("{return_type} F({parameters}) {{ return {expression}; }}");
    let mut session: ShaderSession = ShaderSession::default();
    let diagnostics: Vec<Diagnostic> = session.set_program(&program);
    let errors: Vec<String> =
        diagnostics.iter().filter(|diagnostic| diagnostic.is_error()).map(ToString::to_string).collect();
    assert!(errors.is_empty(), "{}", errors.join("\n"));
    let function: FunctionRef = session.program.functions[0].clone();

    // Interpreter
    let options: EvaluationOptions = EvaluationOptions::default();
    let mut storage: Storage = Storage::new();
    let mut bag: DiagnosticBag = DiagnosticBag::new();
    let mut evaluator: Evaluator = Evaluator::new(&session.profile, &options, &mut storage, &mut bag, "input");
    let ours: Vec<Value> = samples
        .iter()
        .map(|sample| {
            let arguments: Vec<Value> = sample
                .iter()
                .enumerate()
                .map(|(index, input)| {
                    evaluator.convert_value(input.clone(), &function.parameters[index].ty, ConversionKind::Numeric)
                })
                .collect();
            evaluator.call(&function, &arguments).expect("the interpreter evaluates every sample")
        })
        .collect();

    // WARP: one loop over the input buffer
    let input_words: usize = samples[0].iter().map(|input| input.to_words().len()).sum();
    let output_words: usize = ours[0].to_words().len();
    let mut hlsl: String = String::new();
    hlsl.push_str(
        "RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n",
    );
    hlsl.push_str(&program);
    hlsl.push('\n');
    hlsl.push_str(&format!("[RootSignature(\"{ROOT_SIGNATURE}\")]\n[numthreads(1, 1, 1)]\nvoid {ENTRY_POINT}()\n{{\n"));
    hlsl.push_str(&format!(
        "    for (uint sample = 0; sample < {}u; sample++)\n    {{\n        uint input = sample * {}u;\n",
        samples.len(),
        input_words
    ));
    let mut arguments: Vec<String> = Vec::new();
    let mut word: usize = 0;
    for parameter in &function.parameters {
        let ty: NumericType = *parameter.ty.as_numeric().expect("numeric parameters");
        let mut components: Vec<String> = Vec::new();
        for _ in 0..ty.size() {
            let low: String = format!("RefInput[input + {word}u]");
            word += 1;
            let high: String = if ty.kind.is_64bit() {
                word += 1;
                format!("RefInput[input + {}u]", word - 1)
            } else {
                String::new()
            };
            components.push(load(ty.kind, &low, &high));
        }
        arguments.push(if ty.is_scalar() {
            components[0].clone()
        } else {
            format!("{}({})", ty, components.join(", "))
        });
    }
    hlsl.push_str(&format!("        {} result = F({});\n", return_type, arguments.join(", ")));
    let result_type: NumericType = *function.return_type.as_numeric().expect("numeric result");
    let mut output_word: usize = 0;
    for component in 0..result_type.size() {
        let access: String = if result_type.is_scalar() {
            "result".to_string()
        } else if result_type.is_matrix() {
            format!("result[{}][{}]", component / result_type.columns, component % result_type.columns)
        } else {
            format!("result[{component}]")
        };
        let target: String = format!("RefOutput[sample * {output_words}u + {output_word}u]");
        output_word += 1;
        let line: String = match result_type.kind {
            ScalarKind::Bool => format!("        {target} = {access} ? 1u : 0u;\n"),
            ScalarKind::UInt => format!("        {target} = {access};\n"),
            ScalarKind::Double => {
                output_word += 1;
                format!(
                    "        {{ uint low, high; asuint({access}, low, high); {target} = low; RefOutput[sample * {output_words}u + {}u] = high; }}\n",
                    output_word - 1
                )
            }
            ScalarKind::Int64 | ScalarKind::UInt64 => {
                output_word += 1;
                format!(
                    "        {target} = (uint)({access}); RefOutput[sample * {output_words}u + {}u] = (uint)((uint64_t)({access}) >> 32);\n",
                    output_word - 1
                )
            }
            _ => format!("        {target} = asuint({access});\n"),
        };
        hlsl.push_str(&line);
    }
    hlsl.push_str("    }\n}\n");

    let inputs: Vec<u32> = samples.iter().flat_map(|sample| sample.iter().flat_map(Value::to_words)).collect();
    let device: Arc<WarpDevice> = WarpDevice::shared().expect("WARP is available");
    let (output, _) =
        device.run(&hlsl, &inputs, output_words * samples.len(), ReferenceMode::Strict).expect("the shader runs");

    let mut exact: usize = 0;
    let mut worst: i64 = 0;
    let mut mismatches: Vec<ConformanceSample> = Vec::new();
    for (index, mine) in ours.into_iter().enumerate() {
        let reference: Vec<u32> = output[index * output_words..(index + 1) * output_words].to_vec();
        let reference_value: Value = from_words(&mine.ty, &reference);
        let is_same: bool = (0..mine.bits.len()).all(|component| {
            mine.bits[component] == reference_value.bits[component]
                || (mine.kind_at(component).is_float()
                    && mine.get_number(component).is_nan()
                    && reference_value.get_number(component).is_nan())
        });
        if is_same {
            exact += 1;
            continue;
        }
        let ulps: i64 = max_ulps(&mine, &reference_value);
        worst = worst.max(ulps);
        mismatches.push(ConformanceSample { inputs: samples[index].clone(), ours: mine, reference, ulps });
    }
    mismatches.sort_by_key(|sample| std::cmp::Reverse(sample.ulps));

    // Investigation aid: every mismatch with exact bits, one line each
    if let Some(dump_path) = std::env::var_os("SHADERCALC_CONFORMANCE_DUMP") {
        let mut file = std::fs::OpenOptions::new().create(true).append(true).open(dump_path).expect("dump file");
        for sample in &mismatches {
            let hex =
                |words: Vec<u32>| words.iter().map(|bits| format!("{bits:08X}")).collect::<Vec<String>>().join(",");
            let inputs: Vec<u32> = sample.inputs.iter().flat_map(Value::to_words).collect();
            writeln!(
                file,
                "{}\t{}\t{}\t{}",
                expression,
                hex(inputs),
                hex(sample.ours.to_words()),
                hex(sample.reference.clone())
            )
            .expect("dump line");
        }
    }
    ConformanceReport { expression: expression.to_string(), samples: samples.len(), exact, max_ulps: worst, mismatches }
}

fn parameters(arity: usize) -> String {
    ["a", "b", "c"][..arity].iter().map(|name| format!("float {name}")).collect::<Vec<String>>().join(", ")
}

fn vector(x: &Value, y: &Value, z: &Value) -> Value {
    Value::new(ShaderType::vector(ScalarKind::Float, 3), vec![x.bits[0], y.bits[0], z.bits[0]], vec![UnitTag::BARE; 3])
}

/// Runs every case, prints every report, and fails listing the inexact ones.
fn check_exact(reports: Vec<ConformanceReport>) {
    let mut failures: Vec<String> = Vec::new();
    for report in reports {
        println!("{report}");
        if report.exact != report.samples {
            failures.push(report.to_string());
        }
    }
    assert!(failures.is_empty(), "{} inexact operation(s):\n{}", failures.len(), failures.join("\n"));
}

/// Operations whose result must be bit-identical (IEEE-exact DXIL ops and DXC's exact formulas).
#[test]
fn float_operations_are_bit_exact() {
    let cases: [(usize, &str); 32] = [
        (1, "-a"),
        (1, "abs(a)"),
        (1, "sqrt(a)"),
        (1, "rcp(a)"),
        (1, "frac(a)"),
        (1, "floor(a)"),
        (1, "ceil(a)"),
        (1, "round(a)"),
        (1, "trunc(a)"),
        (1, "saturate(a)"),
        (1, "degrees(a)"),
        (1, "radians(a)"),
        (1, "(float)sign(a)"),
        (1, "(float)(int)a"),
        (1, "(float)(uint)a"),
        (1, "f16tof32(f32tof16(a))"),
        (1, "(float)f32tof16(a)"),
        (2, "a + b"),
        (2, "a - b"),
        (2, "a * b"),
        (2, "a / b"),
        (2, "a % b"),
        (2, "fmod(a, b)"),
        (2, "min(a, b)"),
        (2, "max(a, b)"),
        (2, "step(a, b)"),
        (2, "(float)(a < b)"),
        (2, "(float)(a != b)"),
        (3, "lerp(a, b, c)"),
        (3, "smoothstep(a, b, c)"),
        (3, "clamp(a, b, c)"),
        (3, "mad(a, b, c)"),
    ];
    check_exact(
        cases
            .iter()
            .map(|(arity, expression)| run(&parameters(*arity), "float", expression, &float_tuples(*arity, 300, 7)))
            .collect(),
    );
}

/// Functions WARP approximates: reported, not required to match.
#[test]
fn float_approximations_are_reported() {
    let cases: [(usize, &str); 18] = [
        (1, "rsqrt(a)"),
        (1, "exp(a)"),
        (1, "exp2(a)"),
        (1, "log(a)"),
        (1, "log2(a)"),
        (1, "log10(a)"),
        (1, "sin(a)"),
        (1, "cos(a)"),
        (1, "tan(a)"),
        (1, "asin(a)"),
        (1, "acos(a)"),
        (1, "atan(a)"),
        (1, "sinh(a)"),
        (1, "cosh(a)"),
        (1, "tanh(a)"),
        (2, "pow(a, b)"),
        (2, "atan2(a, b)"),
        (2, "ldexp(a, b)"),
    ];
    for (arity, expression) in cases {
        println!("{}", run(&parameters(arity), "float", expression, &float_tuples(arity, 300, 7)));
    }
}

#[test]
fn vector_operations_match_warp() {
    let cases: [(&str, &str, bool); 19] = [
        ("float", "dot(a, b)", true),
        ("float", "length(a)", true),
        ("float", "distance(a, b)", true),
        ("float3", "cross(a, b)", true),
        ("float3", "reflect(a, b)", true),
        ("float3", "faceforward(a, b, a.zxy)", true),
        ("float3", "refract(a, b, a.x)", true),
        ("float3", "a * b - b.zxy", true),
        ("float3", "mul(float3x3(a, b, a.yzx), b)", true),
        ("float3", "mul(a, float3x3(a, b, b.yzx))", true),
        ("float3x3", "mul(float3x3(a, b, a.yzx), float3x3(b, a, b.zxy))", true),
        ("float", "determinant(float3x3(a, b, a.yzx * b))", true),
        ("float", "determinant(float4x4(a, b.x, b, a.y, a.zxy, b.z, b.yzx, a.x))", true),
        ("float", "determinant(float2x2(a.xy, b.yz))", true),
        ("float3x3", "transpose(float3x3(a, b, a.yzx))", true),
        ("float4", "lit(a.x, a.y, b.x)", false),
        ("float4", "dst(float4(a, b.x), float4(b, a.x))", true),
        ("int4", "D3DCOLORtoUBYTE4(float4(a, b.x))", true),
        ("float3", "normalize(a)", false),
    ];
    let left: Vec<Vec<Value>> = float_tuples(3, 300, 11);
    let mut right: Vec<Vec<Value>> = float_tuples(3, 300, 12);
    right.reverse();
    let samples: Vec<Vec<Value>> =
        left.iter().zip(&right).map(|(a, b)| vec![vector(&a[0], &a[1], &a[2]), vector(&b[0], &b[1], &b[2])]).collect();
    let mut exact_reports: Vec<ConformanceReport> = Vec::new();
    for (return_type, expression, is_exact) in cases {
        let report: ConformanceReport = run("float3 a, float3 b", return_type, expression, &samples);
        if is_exact {
            exact_reports.push(report);
        } else {
            println!("{report}");
        }
    }
    check_exact(exact_reports);
}

#[test]
fn bit_operations_match_warp() {
    let cases: [(&str, &str); 15] = [
        ("uint", "countbits(a)"),
        ("uint", "reversebits(a)"),
        ("uint", "firstbitlow(a)"),
        ("uint", "firstbithigh(a)"),
        ("uint", "a * b + (a ^ b) - (b >> 3)"),
        ("uint", "a / (b | 1u) + a % (b | 1u)"),
        ("uint", "min(a, b) + clamp(a, b, a ^ b)"),
        ("float", "asfloat(a)"),
        ("float", "f16tof32(a)"),
        ("uint", "f32tof16(asfloat(a))"),
        ("bool", "isnan(asfloat(a)) || isinf(asfloat(b))"),
        ("int", "sign(asint(a)) + abs(asint(b))"),
        ("float", "(float)a + (float)asint(b)"),
        ("uint", "(uint)asfloat(a)"),
        ("int", "(int)asfloat(a)"),
    ];
    let mut values: Vec<i32> = ints(60, 5);
    values.extend([0x7F80_0000, 0xFF80_0000u32 as i32, 0x7FC0_0000, 0x0000_0001, 0x3F80_0000, 0x477F_E000, 0x7BFF]);
    let samples: Vec<Vec<Value>> = values
        .iter()
        .flat_map(|left| {
            values
                .iter()
                .take(25)
                .map(move |right| vec![Value::from_uint(*left as u32), Value::from_uint(*right as u32)])
        })
        .collect();
    check_exact(
        cases
            .iter()
            .map(|(return_type, expression)| run("uint a, uint b", return_type, expression, &samples))
            .collect(),
    );
}

#[test]
fn double_operations_match_warp() {
    let cases: [&str; 8] = [
        "a + b",
        "a * b - a",
        "a / b",
        "min(a, b)",
        "abs(a) + saturate(b)",
        "rcp(a)",
        "fma(a, b, a)",
        "(double)(float)a",
    ];
    let values: [f64; 18] = [
        0.0,
        -0.0,
        1.0,
        -1.0,
        0.5,
        0.1,
        1e-310,
        -1e-310,
        3.5,
        1e300,
        -1e300,
        std::f64::consts::PI,
        2.5,
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::NAN,
        123456.789,
        1e-8,
    ];
    let samples: Vec<Vec<Value>> = values
        .iter()
        .flat_map(|left| values.iter().map(move |right| vec![Value::from_double(*left), Value::from_double(*right)]))
        .collect();
    check_exact(cases.iter().map(|expression| run("double a, double b", "double", expression, &samples)).collect());
}

#[test]
fn int64_operations_match_warp() {
    let cases: [&str; 4] = ["a + b * 3", "a / (b | 1)", "(a >> 7) ^ (b << 9)", "min(a, b)"];
    let values: [i64; 13] =
        [0, 1, -1, 2, 1 << 40, -(1 << 40), i64::MAX, i64::MIN, 123456789012345, -987654321, 63, 64, 65];
    let int64 = |value: i64| Value::scalar(ScalarKind::Int64, value as u64, UnitTag::BARE);
    let samples: Vec<Vec<Value>> =
        values.iter().flat_map(|left| values.iter().map(move |right| vec![int64(*left), int64(*right)])).collect();
    check_exact(cases.iter().map(|expression| run("int64_t a, int64_t b", "int64_t", expression, &samples)).collect());
}

#[test]
fn int_operations_are_bit_exact() {
    let cases: [&str; 8] = [
        "a / b",
        "a % b",
        "a >> b",
        "a << b",
        "abs(a)",
        "firstbithigh(a)",
        "(int)(uint(a) / uint(b))",
        "(int)(uint(a) % uint(b))",
    ];
    let values: Vec<i32> = ints(40, 3);
    let samples: Vec<Vec<Value>> = values
        .iter()
        .flat_map(|left| values.iter().take(30).map(move |right| vec![Value::from_int(*left), Value::from_int(*right)]))
        .collect();
    check_exact(cases.iter().map(|expression| run("int a, int b", "int", expression, &samples)).collect());
}
