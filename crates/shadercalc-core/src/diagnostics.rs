use std::collections::HashSet;
use std::fmt;

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, PartialOrd, Ord)]
pub enum DiagnosticSeverity {
    Info,
    Warning,
    Error,
}

impl DiagnosticSeverity {
    pub fn name(self) -> &'static str {
        match self {
            DiagnosticSeverity::Info => "info",
            DiagnosticSeverity::Warning => "warning",
            DiagnosticSeverity::Error => "error",
        }
    }
}

/// A position in a source text: 1-based line and column, plus the length of the marked span.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct SourceSpan {
    pub offset: usize,
    pub length: usize,
    pub line: usize,
    pub column: usize,
}

impl SourceSpan {
    pub const NONE: SourceSpan = SourceSpan { offset: 0, length: 0, line: 0, column: 0 };

    pub fn new(offset: usize, length: usize, line: usize, column: usize) -> SourceSpan {
        SourceSpan { offset, length, line, column }
    }

    pub fn end(&self) -> usize {
        self.offset + self.length
    }

    /// From the start of this span to the end of another one (same source).
    pub fn to(&self, end: SourceSpan) -> SourceSpan {
        if end.end() <= self.offset { *self } else { SourceSpan { length: end.end() - self.offset, ..*self } }
    }
}

/// A problem found while compiling or evaluating. `source` is the text it refers to ("program" for the pasted
/// code, "input" for a calculator line); `function` is the function being evaluated, if any.
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct Diagnostic {
    pub severity: DiagnosticSeverity,
    pub message: String,
    pub source: String,
    pub span: SourceSpan,
    pub function: Option<String>,
}

impl Diagnostic {
    pub fn new(
        severity: DiagnosticSeverity,
        message: impl Into<String>,
        source: impl Into<String>,
        span: SourceSpan,
    ) -> Diagnostic {
        Diagnostic { severity, message: message.into(), source: source.into(), span, function: None }
    }

    pub fn in_function(mut self, function: Option<String>) -> Diagnostic {
        self.function = function;
        self
    }

    pub fn is_error(&self) -> bool {
        self.severity == DiagnosticSeverity::Error
    }
}

impl fmt::Display for Diagnostic {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let place: String = match &self.function {
            None => self.source.clone(),
            Some(function) => format!("{}:{}", self.source, function),
        };
        write!(
            formatter,
            "{} {}({},{}): {}",
            self.severity.name(),
            place,
            self.span.line,
            self.span.column,
            self.message
        )
    }
}

/// Collects diagnostics, dropping exact duplicates (a loop reports the same unit error once).
#[derive(Clone, Debug, Default)]
pub struct DiagnosticBag {
    diagnostics: Vec<Diagnostic>,
    seen: HashSet<Diagnostic>,
}

impl DiagnosticBag {
    pub fn new() -> DiagnosticBag {
        DiagnosticBag::default()
    }

    pub fn items(&self) -> &[Diagnostic] {
        &self.diagnostics
    }

    pub fn into_items(self) -> Vec<Diagnostic> {
        self.diagnostics
    }

    pub fn has_errors(&self) -> bool {
        self.diagnostics.iter().any(Diagnostic::is_error)
    }

    pub fn add(&mut self, diagnostic: Diagnostic) {
        if self.seen.insert(diagnostic.clone()) {
            self.diagnostics.push(diagnostic);
        }
    }

    pub fn add_range<'a>(&mut self, diagnostics: impl IntoIterator<Item = &'a Diagnostic>) {
        for diagnostic in diagnostics {
            self.add(diagnostic.clone());
        }
    }

    pub fn error(&mut self, message: impl Into<String>, source: &str, span: SourceSpan) {
        self.add(Diagnostic::new(DiagnosticSeverity::Error, message, source, span));
    }

    pub fn warning(&mut self, message: impl Into<String>, source: &str, span: SourceSpan) {
        self.add(Diagnostic::new(DiagnosticSeverity::Warning, message, source, span));
    }
}
