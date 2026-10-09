use shadercalc_core::evaluation::evaluator::EvaluationOptions;
use shadercalc_core::reference::checker::{self, ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::semantics::SemanticsProfile;
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
