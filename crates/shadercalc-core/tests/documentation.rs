use shadercalc_core::docs;
use shadercalc_core::evaluation::intrinsics;

#[test]
fn every_intrinsic_is_documented() {
    let mut missing: Vec<&str> = intrinsics::names()
        .filter(|name| docs::find(name).and_then(|entry| entry.signature.as_ref()).is_none())
        .collect();
    missing.sort();
    assert!(missing.is_empty(), "undocumented: {}", missing.join(", "));
}

#[test]
fn every_intrinsic_page_exists_in_the_table() {
    let unknown: Vec<&str> = docs::entries()
        .iter()
        .filter(|entry| !entry.is_topic() && intrinsics::try_get(&entry.name).is_none())
        .map(|entry| entry.name.as_str())
        .collect();
    assert!(unknown.is_empty(), "documented but unknown: {}", unknown.join(", "));
}

#[test]
fn search_ranks_the_best_match_first() {
    for (query, expected) in [("pow", "pow"), ("units", "Units"), ("denormal", "HLSL semantics"), ("fmod", "fmod")] {
        assert_eq!(expected, docs::search(query)[0].name, "{query}");
    }
}
