use std::sync::LazyLock;

/// One documentation page: an intrinsic or a topic (worksheet, units...). `markdown` is its body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocEntry {
    pub name: String,
    pub group: String,
    pub signature: Option<String>,
    pub markdown: String,
}

impl DocEntry {
    pub fn is_topic(&self) -> bool {
        self.signature.is_none()
    }

    /// First sentence of the body, for lists.
    pub fn summary(&self) -> String {
        let first_line: &str = self
            .markdown
            .split('\n')
            .map(str::trim)
            .find(|line| !line.is_empty() && !line.starts_with('`'))
            .unwrap_or("");
        let sentence: &str = match first_line.find(". ") {
            Some(end) if end > 0 => &first_line[..=end],
            _ => first_line,
        };
        sentence.replace('`', "").replace("**", "")
    }
}

const REFERENCE: &str = include_str!("reference.md");

static ENTRIES: LazyLock<Vec<DocEntry>> = LazyLock::new(|| load(REFERENCE));

/// The embedded reference (docs/reference.md): `<!-- group: X -->` starts a group, `## Name` a page.
pub fn entries() -> &'static [DocEntry] {
    &ENTRIES
}

pub fn find(name: &str) -> Option<&'static DocEntry> {
    entries()
        .iter()
        .find(|entry| entry.name == name)
        .or_else(|| entries().iter().find(|entry| entry.name.eq_ignore_ascii_case(name)))
}

/// Pages matching every term, best first: exact name, name prefix, name contains, signature, body.
pub fn search(query: &str) -> Vec<&'static DocEntry> {
    let terms: Vec<String> = query.split(' ').filter(|term| !term.is_empty()).map(str::to_lowercase).collect();
    if terms.is_empty() {
        return entries().iter().collect();
    }
    let mut candidates: Vec<(&'static DocEntry, i32)> = entries()
        .iter()
        .filter_map(|entry| {
            let scores: Vec<i32> = terms.iter().map(|term| score(entry, term)).collect();
            if scores.iter().all(|score| *score < i32::MAX) {
                Some((entry, *scores.iter().max().expect("terms")))
            } else {
                None
            }
        })
        .collect();
    // Name matches: functions first; text matches: topic pages explain more
    candidates.sort_by_key(|(entry, score)| (*score, if *score >= 3 { !entry.is_topic() } else { entry.is_topic() }));
    candidates.into_iter().map(|(entry, _)| entry).collect()
}

/// `term` is lower case.
fn score(entry: &DocEntry, term: &str) -> i32 {
    let name: String = entry.name.to_lowercase();
    if name == term {
        return 0;
    }
    if name.starts_with(term) {
        return 1;
    }
    if name.contains(term) {
        return 2;
    }
    if entry.signature.as_ref().is_some_and(|signature| signature.to_lowercase().contains(term)) {
        return 3;
    }
    if entry.markdown.to_lowercase().contains(term) { 4 } else { i32::MAX }
}

/// `<!-- group: Name -->`.
fn group_marker(line: &str) -> Option<&str> {
    let inner: &str = line.strip_prefix("<!--")?.strip_suffix("-->")?.trim();
    let group: &str = inner.strip_prefix("group:")?.trim();
    if group.is_empty() { None } else { Some(group) }
}

fn load(text: &str) -> Vec<DocEntry> {
    let mut entries: Vec<DocEntry> = Vec::new();
    let mut group: String = String::new();
    let mut name: Option<String> = None;
    let mut body: Vec<&str> = Vec::new();

    let mut flush = |name: &mut Option<String>, body: &mut Vec<&str>, group: &str| {
        let Some(page) = name.take() else {
            return;
        };
        // A page whose first line is `code` is an intrinsic: that line is its signature
        let signature: Option<String> = body
            .iter()
            .find(|line| !line.trim().is_empty())
            .filter(|line| line.starts_with('`'))
            .map(|line| line.trim().to_string());
        entries.push(DocEntry {
            name: page,
            group: group.to_string(),
            signature,
            markdown: body.join("\n").trim().to_string(),
        });
        body.clear();
    };

    for line in text.split('\n').map(|line| line.trim_end_matches('\r')) {
        if let Some(marker) = group_marker(line.trim()) {
            flush(&mut name, &mut body, &group);
            group = marker.to_string();
        } else if let Some(page) = line.strip_prefix("## ") {
            flush(&mut name, &mut body, &group);
            name = Some(page.trim().to_string());
        } else if name.is_some() {
            body.push(line);
        }
    }
    flush(&mut name, &mut body, &group);
    entries
}
