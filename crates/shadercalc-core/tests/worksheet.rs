use shadercalc_core::evaluation::evaluator::EvaluationOptions;
use shadercalc_core::reference::checker::{self, ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::semantics::SemanticsProfile;
use shadercalc_core::trace::{CallTrace, LineTrace};
use shadercalc_core::worksheet::{self, WorksheetDocument, WorksheetLine, WorksheetResult};

pub fn run(documents: &[(&str, &str)]) -> WorksheetResult {
    let documents: Vec<WorksheetDocument> =
        documents.iter().map(|(name, text)| WorksheetDocument::new(*name, *text)).collect();
    worksheet::evaluate(&documents, &SemanticsProfile::HLSL, &EvaluationOptions::default()).expect("not cancelled")
}

/// "line: value" for every line that shows one.
pub fn shown(result: &WorksheetResult, document: &str) -> Vec<String> {
    result.lines_of(document).filter_map(|line| line.value().map(|value| format!("{}: {}", line.line, value))).collect()
}

fn assert_no_errors(result: &WorksheetResult) {
    let errors: Vec<String> =
        result.diagnostics.iter().filter(|diagnostic| diagnostic.is_error()).map(ToString::to_string).collect();
    assert!(errors.is_empty(), "{}", errors.join("\n"));
}

#[test]
fn lines_show_their_value_at_their_last_line() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float KineticEnergy(float m, float v)\n{\n    return 0.5 * m * v * v;\n}\n\nKineticEnergy(2 kg, 3 m/s)\nfloat3 n = normalize(float3(1, 2, 3))\nasuint(n.x)\ntotal = KineticEnergy(1, 2) +\n        KineticEnergy(3,\n                      4)\nx = 1\n-2",
    )]);
    assert_no_errors(&result);
    assert_eq!(
        vec![
            "6: 9 kg·m²/s² (J)",
            "7: float3(0.26726124, 0.5345225, 0.8017837)",
            "8: 1049155191",
            "11: 26",
            "12: 1",
            "13: -2",
        ],
        shown(&result, "main.hlsl")
    );
}

#[test]
fn top_level_variables_are_globals_functions_can_read() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float Scaled(float x) { return x * Scale; }\nScale = 3\nScaled(2)\nScale = 10\nScaled(2)",
    )]);
    assert_no_errors(&result);
    assert_eq!(vec!["2: 3", "3: 6", "4: 10", "5: 20"], shown(&result, "main.hlsl"));
}

#[test]
fn tabs_share_declarations_and_macros_in_tab_order() {
    let result: WorksheetResult = run(&[
        (
            "engine.hlsl",
            "#define SQUARE(x) ((x) * (x))\nstruct Light { float3 Color; float Intensity; };\nTexture2D SceneColor;\nfloat Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }",
        ),
        ("scratch.hlsl", "Light l = { 1, 1, 1, 2 }\nSQUARE(l.Intensity) * Luminance(l.Color)"),
    ]);
    assert_no_errors(&result);
    assert_eq!(vec!["1: { Color = float3(1, 1, 1), Intensity = 2 }", "2: 4"], shown(&result, "scratch.hlsl"));
    assert!(
        result
            .diagnostics_of("engine.hlsl")
            .any(|diagnostic| diagnostic.message.contains("resources aren't supported"))
    );
}

#[test]
fn errors_stay_on_their_line_and_name_their_file() {
    let result: WorksheetResult = run(&[
        ("lib.hlsl", "float Bad(float d, float t) { return d + t; }\nint Div(int a, int b) { return a / b; }"),
        ("main.hlsl", "a = 1\nb = Missing + 1\nBad(3 m, 2 s)\nint arr[2] = { 1, 2 }\narr[a + 5]\nc = a + 1"),
    ]);
    assert!(
        result
            .diagnostics_of("main.hlsl")
            .any(|diagnostic| diagnostic.is_error() && diagnostic.message.contains("unknown name 'Missing'"))
    );
    // The unit error points into lib.hlsl, inside Bad
    assert!(result.diagnostics_of("lib.hlsl").any(|diagnostic| diagnostic.is_error()
        && diagnostic.function.as_deref() == Some("Bad")
        && diagnostic.message.contains("mixes units")));
    let out_of_range: Vec<&WorksheetLine> = result.lines_of("main.hlsl").filter(|line| line.line == 5).collect();
    assert_eq!(1, out_of_range.len());
    assert!(out_of_range[0].result.diagnostics.iter().any(|diagnostic| diagnostic.message.contains("out of range")));
    // Later lines still run
    assert!(shown(&result, "main.hlsl").contains(&"6: 2".to_string()));
}

#[test]
fn lines_match_the_reference_from_their_inputs() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float Scaled(float x) { return x * Scale; }\nScale = 0.1\nfloat3 n = normalize(float3(1, 2, 3)) * Scale\nasuint(n.y)\nScaled(n.z)\nScale = Scale * 3",
    )]);
    assert_no_errors(&result);
    for line in &result.lines {
        let outcome: ReferenceOutcome = checker::check(&line.result, ReferenceMode::Strict);
        assert!(
            outcome.verdict == ReferenceVerdict::Match,
            "line {} = {:?}: {}\n{}",
            line.line,
            line.value().map(ToString::to_string),
            outcome,
            outcome.hlsl
        );
    }
}

fn run_with_libraries(documents: &[(&str, &str)]) -> WorksheetResult {
    let documents: Vec<WorksheetDocument> =
        documents.iter().map(|(name, text)| WorksheetDocument::new(*name, *text)).collect();
    worksheet::evaluate_with_libraries(
        "scratch.hlsl",
        &documents,
        &SemanticsProfile::HLSL,
        &EvaluationOptions::default(),
    )
    .expect("not cancelled")
}

fn errors_of(result: &WorksheetResult, document: &str) -> Vec<String> {
    result
        .diagnostics_of(document)
        .filter(|diagnostic| diagnostic.is_error())
        .map(|diagnostic| diagnostic.message.clone())
        .collect()
}

#[test]
fn the_scratch_pad_sees_every_library() {
    let result: WorksheetResult = run_with_libraries(&[
        ("scratch.hlsl", "Twice(Square(3))\nSCALE * 2"),
        ("square.hlsl", "#define SCALE 5\nfloat Square(float x) { return x * x; }\nSquare(4)"),
        ("twice.hlsl", "float Twice(float x) { return 2 * x; }"),
    ]);
    assert_eq!(vec!["1: 18", "2: 10"], shown(&result, "scratch.hlsl"));
    // A library's own lines run on their own, and once only
    assert_eq!(vec!["3: 16"], shown(&result, "square.hlsl"));
    assert!(result.diagnostics.iter().all(|diagnostic| !diagnostic.is_error()), "{:?}", result.diagnostics);
}

#[test]
fn a_library_stands_alone() {
    let result: WorksheetResult = run_with_libraries(&[
        ("scratch.hlsl", "Quad(2)"),
        ("square.hlsl", "float Square(float x) { return x * x; }"),
        ("quad.hlsl", "float Quad(float x) { return Square(Square(x)); }"),
        ("other.hlsl", "float Square(float y) { return y; }"),
    ]);
    // It runs in the scratch pad, where every library is included...
    assert_eq!(1, result.lines_of("scratch.hlsl").count());
    // ...but quad.hlsl needs square.hlsl, and two libraries define Square
    assert!(
        errors_of(&result, "quad.hlsl").iter().any(|message| message.contains("Square")),
        "{:?}",
        result.diagnostics
    );
    assert!(
        errors_of(&result, "other.hlsl").iter().any(|message| message.contains("already")),
        "{:?}",
        result.diagnostics
    );
}

#[test]
fn loops_trace_every_iteration() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float total = 0\nfor (int i = 0; i < 3; ++i)\n{\n    int j = 2 * i;\n    for (int k = 0; k < i; ++k)\n        total += k;\n}\nwhile (total < 0) { total = 1; }",
    )]);
    assert_no_errors(&result);
    let trace: &LineTrace = result.lines[1].result.trace.as_ref().expect("the loop line is traced");
    let points: Vec<String> = trace
        .points
        .iter()
        .map(|point| format!("{}-{} {:?}", point.first_line, point.last_line, point.loops))
        .collect();
    assert_eq!(vec!["2-7 []", "4-4 [0]", "5-6 [0]", "6-6 [0, 2]"], points);
    let entries: Vec<String> = trace
        .entries
        .iter()
        .map(|entry| {
            let values: Vec<String> = entry.values.iter().map(ToString::to_string).collect();
            format!("{} {:?} {}", entry.point, entry.iterations, values.join(","))
        })
        .collect();
    let expected: [&str; 12] = [
        "0 [0] 0",
        "1 [0] 0",
        "0 [1] 1",
        "1 [1] 2",
        "2 [1, 0] 0",
        "3 [1, 0] 0",
        "0 [2] 2",
        "1 [2] 4",
        "2 [2, 0] 0",
        "3 [2, 0] 0",
        "2 [2, 1] 1",
        "3 [2, 1] 1",
    ];
    assert_eq!(expected[..], entries[..], "{entries:#?}");
    // A loop that never runs is traced without entries; a line outside any loop isn't traced
    assert!(result.lines[2].result.trace.as_ref().is_some_and(|trace| trace.entries.is_empty()));
    assert!(result.lines[0].result.trace.is_none());
}

#[test]
fn loop_values_are_checked_on_warp() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float total = 0.1f\nfor (int i = 0; i < 4; ++i)\n{\n    float3 v = float3(i, total, 2.5f) * 0.3f;\n    total += sqrt(v.y + 1.0f);\n    double d = i * 0.1L;\n    if (i == 2) { uint64_t big = 0x100000000ull * i; }\n    if (i == 1)\n        total *= 0.5f;\n}\nfor (int k = 0; k < 2; ++k) { tanh(7.0f + k); }",
    )]);
    assert_no_errors(&result);
    let verdicts = |line: &WorksheetLine| -> (ReferenceVerdict, Vec<ReferenceVerdict>, usize) {
        let outcome: ReferenceOutcome = checker::check(&line.result, ReferenceMode::Strict);
        let entries: usize = line.result.trace.as_ref().map_or(0, |trace| trace.entries.len());
        (outcome.verdict, outcome.trace.iter().map(|check| check.verdict).collect(), entries)
    };
    // 4 iterations of (i, v, total, d), big once, the unbraced if's total once
    let (verdict, checks, entries) = verdicts(&result.lines[1]);
    assert_eq!((ReferenceVerdict::Match, 18, 18), (verdict, checks.len(), entries), "{checks:?}");
    assert!(checks.iter().all(|check| *check == ReferenceVerdict::Match));
    // WARP's tanh is off: those entries are WARP limits, the loop variable's still match
    let (verdict, checks, _) = verdicts(&result.lines[2]);
    assert_eq!(ReferenceVerdict::WarpLimit, verdict);
    assert_eq!(
        vec![
            ReferenceVerdict::Match,
            ReferenceVerdict::WarpLimit,
            ReferenceVerdict::Match,
            ReferenceVerdict::WarpLimit
        ],
        checks
    );
}

#[test]
fn a_line_shows_every_variable_it_writes() {
    let result: WorksheetResult = run(&[(
        "main.hlsl",
        "float a = 1, b = 2\na = b = 3\nvoid Split(float v, out float whole, inout float count) { whole = floor(v); count += 1; }\nfloat s = 0, c = 0, w = 0, n = 0\nfor (int i = 0; i < 2; ++i)\n{\n    sincos(i * 0.5f, s, c);\n    Split(i + 1.25f, w, n);\n}",
    )]);
    assert_no_errors(&result);
    let written = |line: &WorksheetLine| -> Vec<String> {
        let trace: &LineTrace = line.result.trace.as_ref().expect("traced");
        trace
            .entries
            .iter()
            .map(|entry| {
                let names: &[shadercalc_core::binding::symbols::VariableRef] =
                    trace.points[entry.point].kind.variables();
                let pairs: Vec<String> = entry
                    .values
                    .iter()
                    .enumerate()
                    .map(|(index, value)| match names.get(index) {
                        Some(variable) => format!("{} = {value}", variable.name),
                        None => value.to_string(),
                    })
                    .collect();
                format!("{:?} {}", entry.iterations, pairs.join(", "))
            })
            .collect()
    };
    assert_eq!(vec!["[] a = 1", "[] b = 2"], written(&result.lines[0]));
    assert_eq!(vec!["[] a = 3, b = 3"], written(&result.lines[1]));
    let looped: Vec<String> = written(&result.lines[3]);
    assert_eq!(6, looped.len(), "{looped:#?}");
    assert_eq!(["[0] i = 0", "[0] s = 0, c = 1", "[0] w = 1, n = 1"], looped[..3]);
    assert!(looped[5] == "[1] w = 2, n = 2" && looped[4].starts_with("[1] s = 0.47"), "{looped:#?}");
    // WARP agrees on every variable (sincos may be approximate)
    for line in [&result.lines[0], &result.lines[1], &result.lines[3]] {
        let outcome: ReferenceOutcome = checker::check(&line.result, ReferenceMode::Strict);
        assert!(
            matches!(outcome.verdict, ReferenceVerdict::Match | ReferenceVerdict::WithinTolerance),
            "{outcome}\n{}",
            outcome.hlsl
        );
        assert_eq!(line.result.trace.as_ref().map_or(0, |trace| trace.entries.len()), outcome.trace.len());
    }
}

#[test]
fn a_call_is_traced_on_demand() {
    let text: &str = "float Sum(float x, int count)\n{\n    float total = x;\n    for (int i = 0; i < count; ++i)\n        total += Twice(i);\n    return total;\n}\nfloat Twice(float y) { return 2 * y; }\nfor (int k = 1; k < 3; ++k)\n{\n    Sum(0.5, k);\n}";
    let documents: Vec<WorksheetDocument> = vec![WorksheetDocument::new("main.hlsl", text)];
    let result: WorksheetResult =
        worksheet::evaluate(&documents, &SemanticsProfile::HLSL, &EvaluationOptions::default()).expect("ran");
    assert_no_errors(&result);
    let line: &WorksheetLine = &result.lines[0];
    let trace: &LineTrace = line.result.trace.as_ref().expect("traced");
    let sites: Vec<String> =
        trace.calls.iter().map(|site| format!("{} {}", site.function.name, site.last_line)).collect();
    assert_eq!(vec!["Sum 11"], sites);
    let runs: Vec<Vec<u32>> = trace.call_entries.iter().map(|entry| entry.iterations.clone()).collect();
    assert_eq!(vec![vec![0], vec![1]], runs);

    let follow = |path: &[(usize, usize)]| -> CallTrace {
        worksheet::trace_call(line, &documents, path, &SemanticsProfile::HLSL, &EvaluationOptions::default())
            .expect("traced")
    };
    let describe = |call: &CallTrace| -> Vec<String> {
        call.trace
            .entries
            .iter()
            .map(|entry| {
                let point: usize = call.trace.points[entry.point].last_line;
                let values: Vec<String> = entry.values.iter().map(ToString::to_string).collect();
                format!("{point} {:?} {}", entry.iterations, values.join(","))
            })
            .collect()
    };
    // The second run of Sum (k = 2)
    let sum: CallTrace = follow(&[(0, 1)]);
    assert_eq!((1, 7), (sum.first_line, sum.last_line));
    let arguments: Vec<String> = sum.arguments.iter().map(ToString::to_string).collect();
    assert_eq!(
        (vec!["0.5", "2"], Some("2.5".to_string())),
        (arguments.iter().map(String::as_str).collect(), sum.result.as_ref().map(ToString::to_string))
    );
    assert_eq!(vec!["3 [] 0.5", "5 [0] 0", "5 [0] 0.5", "5 [1] 1", "5 [1] 2.5", "6 [] 2.5"], describe(&sum));
    // Inside it, the second run of Twice
    let twice: CallTrace = follow(&[(0, 1), (0, 1)]);
    assert_eq!(("Twice", vec!["8 [] 2".to_string()]), (twice.function.name.as_str(), describe(&twice)));
    // Both checked on WARP, value by value
    for (path, call) in [(vec![(0, 1)], &sum), (vec![(0, 1), (0, 1)], &twice)] {
        let outcome: ReferenceOutcome = checker::check_call(&line.result, &path, call, ReferenceMode::Strict);
        assert_eq!(ReferenceVerdict::Match, outcome.verdict, "{outcome}\n{}", outcome.hlsl);
        assert_eq!(call.trace.entries.len(), outcome.trace.len());
    }
    // A run that doesn't exist
    assert!(
        worksheet::trace_call(line, &documents, &[(0, 2)], &SemanticsProfile::HLSL, &EvaluationOptions::default())
            .is_err()
    );
}

#[test]
fn libraries_export_their_declarations() {
    let library: &str = "// GGX normal distribution\n//\n// (Trowbridge-Reitz)\n\
        float D_GGX(float NoH, float roughness, float scale = 1.0)\n{\n    return roughness;\n}\n\
        float Proto(float x);\nfloat Proto(float x) { return x; }\n\
        struct Surface\n{\n    float3 normal; // unit length\n    float roughness;\n};\n\
        typedef float3 Color;\n\
        #define PI 3.14159265 // pi\n#define SQUARE(x) ((x) * (x))\n#define GONE 1\n#undef GONE\n\
        static const float kA = 1, kB[2] = { 2, 3 };\n\
        cbuffer Settings { float exposure; }\n\
        albedo = float3(0.5, 0.5, 0.5)\n\
        float é = 2 // UTF-16 offsets\n\
        float gain = 3";
    let result: WorksheetResult = run_with_libraries(&[
        ("scratch.hlsl", "float Mine(float x) { return x; }\nD_GGX(1, 2)"),
        ("lib.hlsl", library),
    ]);
    let rows: Vec<String> = result
        .exports
        .iter()
        .map(|export| {
            assert_eq!("lib.hlsl", export.document);
            let parameters: String =
                export.parameters.as_ref().map_or(String::new(), |names| format!(" ({})", names.join(", ")));
            format!("{} {}: {}{parameters} // {}", export.line, export.kind.name(), export.declaration, export.comment)
        })
        .collect();
    let expected: [&str; 12] = [
        "4 function: float D_GGX(float NoH, float roughness, float scale = 1.0) (NoH, roughness) // GGX normal distribution (Trowbridge-Reitz)",
        "9 function: float Proto(float x) (x) // ",
        "10 struct: struct Surface { float3 normal; float roughness; } // ",
        "15 type: typedef float3 Color // ",
        "16 macro: #define PI 3.14159265 // pi",
        "17 macro: #define SQUARE(x) ((x) * (x)) (x) // ",
        "20 variable: static const float kA = 1 // ",
        "20 variable: static const float kB[2] = { 2, 3 } // ",
        "21 variable: float exposure // ",
        "22 variable: float3 albedo = float3(0.5, 0.5, 0.5) // ",
        "23 variable: float é = 2 // UTF-16 offsets",
        "24 variable: float gain = 3 // ",
    ];
    assert_eq!(expected[..], rows[..], "{rows:#?}");
    // Offsets are UTF-16 units of the name, like the editor's
    let gain: usize = library.encode_utf16().count() - "gain = 3".len();
    assert_eq!(gain, result.exports[11].offset);
}
