use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::binding::binder::bind_worksheet;
use crate::binding::program::{BoundInteractive, BoundProgram, BoundWorksheet, WorksheetSource};
use crate::binding::symbols::VariableRef;
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};
use crate::evaluation::evaluator::{EvaluationOptions, Evaluator, Interrupt, Storage};
use crate::exports::{Export, exports_of};
use crate::semantics::SemanticsProfile;
use crate::session::{LineResult, materialize};
use crate::syntax::lexer::{Token, tokenize};
use crate::syntax::parser::parse_worksheet;
use crate::syntax::preprocessor::{MacroDefinition, Preprocessor};
use crate::syntax::tree::{DeclarationSyntax, ExpressionKind, ItemSyntax, StatementKind};
use crate::trace::LineTrace;
use crate::units::UNITS;
use crate::values::Value;

/// One worksheet file (a tab): its name, used in diagnostics, and its text.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WorksheetDocument {
    pub name: String,
    pub text: String,
}

impl WorksheetDocument {
    pub fn new(name: impl Into<String>, text: impl Into<String>) -> WorksheetDocument {
        WorksheetDocument { name: name.into(), text: text.into() }
    }
}

/// The outcome of one top-level line, to show at the end of its statement (1-based `line`).
#[derive(Clone, Debug)]
pub struct WorksheetLine {
    pub document: String,
    pub line: usize,
    pub span: SourceSpan,
    pub result: LineResult,
}

impl WorksheetLine {
    pub fn value(&self) -> Option<&Value> {
        self.result.value.as_ref()
    }
}

#[derive(Debug)]
pub struct WorksheetResult {
    pub program: Arc<BoundProgram>,
    pub lines: Vec<WorksheetLine>,
    pub diagnostics: Vec<Diagnostic>,
    /// What the documents declare (with libraries: what each library declares on its own).
    pub exports: Vec<Export>,
    pub duration: Duration,
}

impl WorksheetResult {
    pub fn lines_of<'a>(&'a self, document: &'a str) -> impl Iterator<Item = &'a WorksheetLine> + 'a {
        self.lines.iter().filter(move |line| line.document == document)
    }

    pub fn diagnostics_of<'a>(&'a self, document: &'a str) -> impl Iterator<Item = &'a Diagnostic> + 'a {
        self.diagnostics.iter().filter(move |diagnostic| diagnostic.source == document)
    }
}

/// The evaluation was cancelled through the options' cancellation flag.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Cancelled;

/// Evaluates worksheet files as one program, like headers included in tab order: declarations (functions, structs,
/// macros, globals) are shared by every file, top-level lines run in file then line order, and their variables are
/// globals later lines and functions can read. A line break ends a top-level line.
pub fn evaluate(
    documents: &[WorksheetDocument],
    profile: &SemanticsProfile,
    options: &EvaluationOptions,
) -> Result<WorksheetResult, Cancelled> {
    let clock: Instant = Instant::now();

    // `2 h` is hours, unless the worksheet names something h
    let (mut sources, mut parse_diagnostics, mut macros) = parse(documents, &|name: &str| UNITS.contains_key(name));
    let declared_names: HashSet<String> = sources.iter().flat_map(|source| declared_names(&source.items)).collect();
    if declared_names.iter().any(|name| UNITS.contains_key(name.as_str())) {
        (sources, parse_diagnostics, macros) =
            parse(documents, &|name: &str| UNITS.contains_key(name) && !declared_names.contains(name));
    }

    let bound: BoundWorksheet = bind_worksheet(&sources, profile, &parse_diagnostics);
    let mut all_diagnostics: DiagnosticBag = DiagnosticBag::new();
    all_diagnostics.add_range(&bound.program.diagnostics);

    let mut storage: Storage = Storage::new();
    {
        let mut scratch: DiagnosticBag = DiagnosticBag::new();
        let mut evaluator: Evaluator = Evaluator::new(profile, options, &mut storage, &mut scratch, "");
        evaluator.zero_globals(&bound.program);
    }
    let line_starts: HashMap<&str, Vec<usize>> =
        documents.iter().map(|document| (document.name.as_str(), line_starts(&document.text))).collect();

    let mut lines: Vec<WorksheetLine> = Vec::new();
    for line in &bound.lines {
        if options.is_cancelled() {
            return Err(Cancelled);
        }
        // What the line starts from, for its reference check
        let inputs: Storage = bound
            .program
            .globals
            .iter()
            .filter(|global| !global.has_constant_value() && storage.contains_key(*global))
            .map(|global| (global.clone(), storage[global].clone()))
            .collect();

        let mut line_diagnostics: DiagnosticBag = DiagnosticBag::new();
        // The line's own compile problems belong to it; a line that doesn't compile doesn't run
        line_diagnostics.add_range(bound.program.diagnostics.iter().filter(|diagnostic| {
            diagnostic.source == line.source
                && diagnostic.function.is_none()
                && diagnostic.span.offset >= line.span.offset
                && diagnostic.span.offset < line.span.end() + 1
        }));
        let mut value: Option<Value> = None;
        let mut reference_limits: Vec<String> = Vec::new();
        let mut trace: Option<LineTrace> = None;
        if !line_diagnostics.has_errors() {
            let outcome: Result<Option<Value>, Interrupt> = {
                let mut evaluator: Evaluator =
                    Evaluator::new(profile, options, &mut storage, &mut line_diagnostics, &line.source);
                evaluator.set_current_source(&line.source);
                evaluator.trace_line(&line.statement);
                let outcome: Result<Option<Value>, Interrupt> = evaluator.run_line(line);
                reference_limits = evaluator.take_reference_limits();
                trace = evaluator.take_trace();
                outcome
            };
            match outcome {
                Ok(Some(result)) => {
                    let (materialized, note) = materialize(result, profile);
                    if let Some(note) = note {
                        line_diagnostics.add(Diagnostic::new(
                            DiagnosticSeverity::Info,
                            note,
                            line.source.clone(),
                            line.span,
                        ));
                    }
                    value = Some(materialized);
                }
                Ok(None) => {}
                Err(Interrupt::Error(error)) => {
                    let source: String = error.source_name.clone().unwrap_or_else(|| line.source.clone());
                    line_diagnostics.add(
                        Diagnostic::new(DiagnosticSeverity::Error, error.message, source, error.span)
                            .in_function(error.function_name),
                    );
                }
                Err(Interrupt::Cancelled) | Err(Interrupt::NotConstant) => return Err(Cancelled),
            }
        }
        if value.as_ref().is_some_and(|value| value.ty.is_void()) {
            value = None;
        }
        all_diagnostics.add_range(line_diagnostics.items());

        let shown_variable: Option<VariableRef> =
            if !line.shows_result && !line.declared.is_empty() { line.declared.last().cloned() } else { None };
        let interactive: BoundInteractive = BoundInteractive {
            statements: vec![line.statement.clone()],
            result_statement: if line.shows_result { Some(0) } else { None },
            result_variable: shown_variable,
            declared_variables: line.declared.clone(),
        };
        let last_line_of = |span: SourceSpan| -> usize {
            match line_starts.get(line.source.as_str()) {
                Some(starts) => line_of(starts, span.end().saturating_sub(1).max(span.offset)),
                None => span.line,
            }
        };
        let last_line: usize = last_line_of(line.span);
        if let Some(trace) = trace.as_mut() {
            for point in &mut trace.points {
                point.first_line = point.span.line;
                point.last_line = last_line_of(point.span);
            }
        }
        let result: LineResult = LineResult {
            value,
            diagnostics: line_diagnostics.into_items(),
            line: Some(interactive),
            message: None,
            program: Some(bound.program.clone()),
            inputs,
            reference_limits,
            trace,
        };
        lines.push(WorksheetLine { document: line.source.clone(), line: last_line, span: line.span, result });
    }
    let exports: Vec<Export> = documents
        .iter()
        .zip(&sources)
        .flat_map(|(document, source)| exports_of(document, source, &macros, &lines))
        .collect();
    Ok(WorksheetResult {
        program: bound.program,
        lines,
        diagnostics: all_diagnostics.into_items(),
        exports,
        duration: clock.elapsed(),
    })
}

/// A scratch pad and its libraries: the scratch pad runs with every other document in front of it, as if included
/// in order, and only its own lines and problems come from that run. Each library also runs on its own, so its lines
/// are local tests and anything it takes from another library is an error there; problems only the combination
/// has (two libraries defining the same name) are added to it. Without the scratch pad, this is `evaluate`.
pub fn evaluate_with_libraries(
    scratch: &str,
    documents: &[WorksheetDocument],
    profile: &SemanticsProfile,
    options: &EvaluationOptions,
) -> Result<WorksheetResult, Cancelled> {
    let is_scratch = |document: &WorksheetDocument| document.name.eq_ignore_ascii_case(scratch);
    let Some(scratch_document) = documents.iter().find(|document| is_scratch(document)) else {
        return evaluate(documents, profile, options);
    };
    let libraries: Vec<&WorksheetDocument> = documents.iter().filter(|document| !is_scratch(document)).collect();
    let mut combined_documents: Vec<WorksheetDocument> = libraries.iter().map(|library| (*library).clone()).collect();
    combined_documents.push(scratch_document.clone());
    let combined: WorksheetResult = evaluate(&combined_documents, profile, options)?;

    let mut duration: Duration = combined.duration;
    let mut lines: Vec<WorksheetLine> = Vec::new();
    let mut diagnostics: Vec<Diagnostic> = Vec::new();
    let mut exports: Vec<Export> = Vec::new();
    lines.extend(combined.lines.iter().filter(|line| line.document == scratch_document.name).cloned());
    diagnostics.extend(combined.diagnostics_of(&scratch_document.name).cloned());
    for library in libraries {
        let alone: WorksheetResult = evaluate(std::slice::from_ref(library), profile, options)?;
        duration += alone.duration;
        lines.extend(alone.lines);
        exports.extend(alone.exports);
        for diagnostic in alone.diagnostics.iter().chain(combined.diagnostics_of(&library.name)) {
            if !diagnostics.contains(diagnostic) {
                diagnostics.push(diagnostic.clone());
            }
        }
    }
    Ok(WorksheetResult { program: combined.program, lines, diagnostics, exports, duration })
}

fn parse(
    documents: &[WorksheetDocument],
    is_unit_name: &dyn Fn(&str) -> bool,
) -> (Vec<WorksheetSource>, Vec<Diagnostic>, Vec<MacroDefinition>) {
    let mut diagnostics: DiagnosticBag = DiagnosticBag::new();
    let mut preprocessor: Preprocessor = Preprocessor::create_shared();
    let mut type_names: Vec<String> = Vec::new();
    let mut sources: Vec<WorksheetSource> = Vec::new();
    for document in documents {
        let lexed: Vec<Token> = tokenize(&document.text, &document.name, &mut diagnostics);
        let tokens: Vec<Token> = preprocessor.process_file(lexed, &document.name, &mut diagnostics);
        let items: Vec<ItemSyntax> =
            parse_worksheet(tokens, &document.name, &mut diagnostics, &type_names, is_unit_name);
        for item in &items {
            match item {
                ItemSyntax::Declaration(DeclarationSyntax::Struct(structure)) => {
                    type_names.push(structure.name.clone())
                }
                ItemSyntax::Declaration(DeclarationSyntax::Typedef(typedef)) => type_names.push(typedef.name.clone()),
                _ => {}
            }
        }
        sources.push(WorksheetSource { name: document.name.clone(), items });
    }
    (sources, diagnostics.into_items(), preprocessor.definitions)
}

pub(crate) fn declared_names(items: &[ItemSyntax]) -> Vec<String> {
    let mut names: Vec<String> = Vec::new();
    for item in items {
        match item {
            ItemSyntax::Declaration(DeclarationSyntax::Function(function)) => names.push(function.name.clone()),
            ItemSyntax::Declaration(DeclarationSyntax::Struct(structure)) => names.push(structure.name.clone()),
            ItemSyntax::Declaration(DeclarationSyntax::Typedef(typedef)) => names.push(typedef.name.clone()),
            ItemSyntax::Declaration(DeclarationSyntax::GlobalVariable(global)) => {
                names.extend(global.declaration.declarators.iter().map(|declarator| declarator.name.clone()));
            }
            ItemSyntax::Statement(statement) => match &statement.kind {
                StatementKind::VariableDeclaration(declaration) => {
                    names.extend(declaration.declarators.iter().map(|declarator| declarator.name.clone()));
                }
                StatementKind::Expression(expression) => {
                    if let ExpressionKind::Assignment { target, .. } = &expression.kind
                        && let ExpressionKind::Name(name) = &target.kind
                    {
                        names.push(name.clone());
                    }
                }
                _ => {}
            },
        }
    }
    names
}

/// UTF-16 offsets where each line starts.
fn line_starts(text: &str) -> Vec<usize> {
    let mut starts: Vec<usize> = vec![0];
    let mut offset: usize = 0;
    for character in text.chars() {
        offset += character.len_utf16();
        if character == '\n' {
            starts.push(offset);
        }
    }
    starts
}

/// 1-based line of a UTF-16 offset.
fn line_of(line_starts: &[usize], offset: usize) -> usize {
    match line_starts.binary_search(&offset) {
        Ok(index) => index + 1,
        Err(index) => index,
    }
}
