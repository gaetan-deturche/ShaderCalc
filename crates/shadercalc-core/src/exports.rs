//! What each worksheet file declares for the files after it: the scratch pad's view of its libraries.

use crate::binding::program::WorksheetSource;
use crate::diagnostics::SourceSpan;
use crate::syntax::preprocessor::MacroDefinition;
use crate::syntax::tree::{DeclarationSyntax, FunctionSyntax, ItemSyntax, VariableDeclarationSyntax};
use crate::worksheet::{WorksheetDocument, WorksheetLine};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExportKind {
    Function,
    Struct,
    /// A typedef or a `using` alias.
    Type,
    Macro,
    /// A global, or the variable a top-level line declares.
    Variable,
}

impl ExportKind {
    pub fn name(self) -> &'static str {
        match self {
            ExportKind::Function => "function",
            ExportKind::Struct => "struct",
            ExportKind::Type => "type",
            ExportKind::Macro => "macro",
            ExportKind::Variable => "variable",
        }
    }
}

/// A name a worksheet file declares for the files after it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Export {
    pub document: String,
    pub kind: ExportKind,
    pub name: String,
    /// The declaration on one line as written (a function without its body, a struct with its fields); a line's
    /// variable shows its type and value.
    pub declaration: String,
    /// What a call takes (functions, function-like macros): the parameters without a default value.
    pub parameters: Option<Vec<String>>,
    /// The `//` lines right above the declaration, else the `//` comment ending its line.
    pub comment: String,
    /// 1-based line of the declaration, and the UTF-16 offset of its name.
    pub line: usize,
    pub offset: usize,
}

/// A document's text addressed like spans are, in UTF-16 units.
struct Text<'a> {
    text: &'a str,
    /// Byte index of every UTF-16 unit, then the end.
    bytes: Vec<usize>,
    lines: Vec<&'a str>,
}

impl<'a> Text<'a> {
    fn new(text: &'a str) -> Text<'a> {
        let mut bytes: Vec<usize> = Vec::with_capacity(text.len() + 1);
        for (index, character) in text.char_indices() {
            bytes.extend(std::iter::repeat_n(index, character.len_utf16()));
        }
        bytes.push(text.len());
        Text { text, bytes, lines: text.lines().collect() }
    }

    fn slice(&self, from: usize, to: usize) -> &'a str {
        let last: usize = self.bytes.len() - 1;
        let start: usize = self.bytes[from.min(last)];
        &self.text[start..self.bytes[to.min(last)].max(start)]
    }

    fn span(&self, span: SourceSpan) -> &'a str {
        self.slice(span.offset, span.end())
    }

    /// The comment documenting what starts on a 1-based line.
    fn comment_for(&self, line: usize) -> String {
        let comment_text = |text: &str| text.trim_start_matches('/').trim().to_string();
        if line == 0 || line > self.lines.len() {
            return String::new();
        }
        let mut above: Vec<String> = Vec::new();
        for text in self.lines[..line - 1].iter().rev() {
            match text.trim_start().strip_prefix("//") {
                Some(comment) => above.push(comment_text(comment)),
                None => break,
            }
        }
        if !above.is_empty() {
            above.reverse();
            return above.into_iter().filter(|part| !part.is_empty()).collect::<Vec<String>>().join(" ");
        }
        let own: &str = self.lines[line - 1];
        own.find("//").map(|start| comment_text(&own[start + 2..])).unwrap_or_default()
    }
}

/// Code on one line: comments dropped, line continuations and runs of white space made one space.
fn one_line(code: &str) -> String {
    let mut kept: String = String::with_capacity(code.len());
    let mut rest: &str = code;
    while let Some(character) = rest.chars().next() {
        if let Some(after) = rest.strip_prefix("//") {
            rest = after.find('\n').map_or("", |end| &after[end..]);
        } else if let Some(after) = rest.strip_prefix("/*") {
            rest = after.find("*/").map_or("", |end| &after[end + 2..]);
            kept.push(' ');
        } else if rest.starts_with("\\\n") || rest.starts_with("\\\r\n") {
            rest = &rest[1..];
        } else {
            kept.push(character);
            rest = &rest[character.len_utf8()..];
        }
    }
    let joined: String = kept.split_whitespace().collect::<Vec<&str>>().join(" ");
    joined.trim_end_matches(';').trim_end().to_string()
}

/// Each declarator of a declaration on its own: `static const float A = 1, B[2]` gives `static const float A = 1`
/// and `static const float B[2]`.
fn declarator_texts(text: &Text, declaration: &VariableDeclarationSyntax) -> Vec<String> {
    let Some(first) = declaration.declarators.first() else {
        return Vec::new();
    };
    let prefix: String = one_line(text.slice(declaration.span.offset, first.name_span.offset));
    let starts: Vec<usize> = declaration.declarators.iter().map(|declarator| declarator.name_span.offset).collect();
    starts
        .iter()
        .enumerate()
        .map(|(index, start)| {
            let end: usize = starts.get(index + 1).copied().unwrap_or(declaration.span.end());
            let own: String = one_line(text.slice(*start, end));
            format!("{prefix} {}", own.trim_end_matches(',').trim_end())
        })
        .collect()
}

/// What one document of a run declares, in file order: its functions (a prototype only when nothing defines it),
/// structs, typedefs, macros, globals, and the variables its top-level lines declare.
pub(crate) fn exports_of(
    document: &WorksheetDocument,
    source: &WorksheetSource,
    macros: &[MacroDefinition],
    lines: &[WorksheetLine],
) -> Vec<Export> {
    let text: Text = Text::new(&document.text);
    let mut exports: Vec<Export> = Vec::new();
    let mut add = |kind: ExportKind,
                   name: &str,
                   declaration: String,
                   parameters: Option<Vec<String>>,
                   span: SourceSpan,
                   offset: usize| {
        exports.push(Export {
            document: document.name.clone(),
            kind,
            name: name.to_string(),
            declaration,
            parameters,
            comment: text.comment_for(span.line),
            line: span.line,
            offset,
        });
    };

    let functions: Vec<&FunctionSyntax> = source
        .items
        .iter()
        .filter_map(|item| match item {
            ItemSyntax::Declaration(DeclarationSyntax::Function(function)) => Some(function),
            _ => None,
        })
        .collect();
    for item in &source.items {
        let ItemSyntax::Declaration(declaration) = item else {
            continue;
        };
        match declaration {
            DeclarationSyntax::Function(function) => {
                let is_defined: bool = functions.iter().any(|other| {
                    other.body.is_some()
                        && other.name == function.name
                        && other.parameters.len() == function.parameters.len()
                });
                if function.body.is_none() && is_defined {
                    continue;
                }
                let end: usize = function.body.as_ref().map_or(function.span.end(), |body| body.span.offset);
                let parameters: Vec<String> = function
                    .parameters
                    .iter()
                    .filter(|parameter| parameter.default_value.is_none())
                    .map(|parameter| parameter.name.clone())
                    .collect();
                let signature: String = one_line(text.slice(function.span.offset, end));
                add(
                    ExportKind::Function,
                    &function.name,
                    signature,
                    Some(parameters),
                    function.span,
                    function.name_span.offset,
                );
            }
            DeclarationSyntax::Struct(structure) => {
                let declaration: String = one_line(text.span(structure.span));
                add(ExportKind::Struct, &structure.name, declaration, None, structure.span, structure.name_span.offset);
            }
            DeclarationSyntax::Typedef(typedef) => {
                let declaration: String = one_line(text.span(typedef.span));
                add(ExportKind::Type, &typedef.name, declaration, None, typedef.span, typedef.name_span.offset);
            }
            DeclarationSyntax::GlobalVariable(global) => {
                let texts: Vec<String> = declarator_texts(&text, &global.declaration);
                for (declarator, declaration) in global.declaration.declarators.iter().zip(texts) {
                    add(
                        ExportKind::Variable,
                        &declarator.name,
                        declaration,
                        None,
                        global.span,
                        declarator.name_span.offset,
                    );
                }
            }
        }
    }
    for definition in macros.iter().filter(|definition| definition.source == document.name) {
        let declaration: String = one_line(text.span(definition.span));
        let parameters: Option<Vec<String>> = definition.parameters.clone();
        add(ExportKind::Macro, &definition.name, declaration, parameters, definition.span, definition.span.offset);
    }
    for line in lines.iter().filter(|line| line.document == document.name) {
        let Some(interactive) = &line.result.line else {
            continue;
        };
        let count: usize = interactive.declared_variables.len();
        for (position, variable) in interactive.declared_variables.iter().enumerate() {
            let mut declaration: String = format!("{} {}", variable.ty, variable.name);
            // The line shows its last variable's value
            if position + 1 == count
                && let Some(value) = &line.result.value
            {
                declaration.push_str(&format!(" = {value}"));
            }
            add(ExportKind::Variable, &variable.name, declaration, None, line.span, variable.span.offset);
        }
    }
    exports.sort_by_key(|export| export.offset);
    exports
}
