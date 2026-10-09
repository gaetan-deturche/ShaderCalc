use std::collections::HashMap;
use std::sync::Arc;

use super::bound_tree::{BoundExpression, BoundStatement};
use super::symbols::{FunctionRef, VariableRef};
use crate::diagnostics::{Diagnostic, SourceSpan};
use crate::syntax::tree::ItemSyntax;
use crate::types::{ShaderType, StructType};

/// Names in insertion order, with lookup; replacing a value keeps its position.
#[derive(Clone, Debug)]
pub struct NameMap<V> {
    entries: Vec<(String, V)>,
    index: HashMap<String, usize>,
}

impl<V> Default for NameMap<V> {
    fn default() -> NameMap<V> {
        NameMap { entries: Vec::new(), index: HashMap::new() }
    }
}

impl<V> NameMap<V> {
    pub fn get(&self, name: &str) -> Option<&V> {
        self.index.get(name).map(|position| &self.entries[*position].1)
    }

    pub fn contains_key(&self, name: &str) -> bool {
        self.index.contains_key(name)
    }

    pub fn insert(&mut self, name: String, value: V) {
        match self.index.get(&name) {
            Some(position) => self.entries[*position].1 = value,
            None => {
                self.index.insert(name.clone(), self.entries.len());
                self.entries.push((name, value));
            }
        }
    }

    pub fn iter(&self) -> impl Iterator<Item = (&String, &V)> {
        self.entries.iter().map(|(name, value)| (name, value))
    }

    pub fn keys(&self) -> impl Iterator<Item = &String> {
        self.entries.iter().map(|(name, _)| name)
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

/// The bound result of pasted code: types, globals and functions, all type-checked.
#[derive(Debug, Default)]
pub struct BoundProgram {
    /// Structs in declaration order.
    pub structs: Vec<Arc<StructType>>,
    /// Struct and typedef names.
    pub type_names: NameMap<ShaderType>,
    /// Static and uniform globals in declaration order.
    pub globals: Vec<VariableRef>,
    pub functions: Vec<FunctionRef>,
    pub diagnostics: Vec<Diagnostic>,
}

impl BoundProgram {
    pub fn empty() -> Arc<BoundProgram> {
        Arc::new(BoundProgram::default())
    }

    pub fn has_errors(&self) -> bool {
        self.diagnostics.iter().any(Diagnostic::is_error)
    }

    pub fn find_functions<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a FunctionRef> + 'a {
        self.functions.iter().filter(move |function| function.name == name)
    }
}

impl Drop for BoundProgram {
    /// Recursive functions reference themselves through their bodies: drop the bodies to free them.
    fn drop(&mut self) {
        for function in &self.functions {
            function.set_body(None);
        }
    }
}

/// One worksheet file to bind: its name (for diagnostics) and its parsed items.
#[derive(Clone, Debug)]
pub struct WorksheetSource {
    pub name: String,
    pub items: Vec<ItemSyntax>,
}

/// A top-level worksheet line: the statement, the expression whose value it shows (if any), and the globals it
/// declares (a declaration line shows its variable).
#[derive(Clone, Debug)]
pub struct ScriptLine {
    pub source: String,
    pub span: SourceSpan,
    pub statement: BoundStatement,
    /// Whether the statement is an expression statement whose value the line shows.
    pub shows_result: bool,
    pub declared: Vec<VariableRef>,
}

impl ScriptLine {
    /// The expression whose value the line shows.
    pub fn result(&self) -> Option<&BoundExpression> {
        match &self.statement.kind {
            super::bound_tree::BoundStatementKind::Expression(expression) if self.shows_result => Some(expression),
            _ => None,
        }
    }
}

/// Worksheet files bound together: the shared program and the lines to run in order.
#[derive(Debug)]
pub struct BoundWorksheet {
    pub program: Arc<BoundProgram>,
    pub lines: Vec<ScriptLine>,
}

/// A bound calculator line.
#[derive(Clone, Debug)]
pub struct BoundInteractive {
    pub statements: Vec<BoundStatement>,
    /// Index of the expression statement whose value the line shows (the last one), if any.
    pub result_statement: Option<usize>,
    /// When the line ends with a declaration, the variable to show.
    pub result_variable: Option<VariableRef>,
    /// Session variables this line declares (explicitly or by assigning a new name).
    pub declared_variables: Vec<VariableRef>,
}

impl BoundInteractive {
    /// The expression whose value the line shows (the last expression statement), if any.
    pub fn result(&self) -> Option<&BoundExpression> {
        let index: usize = self.result_statement?;
        match &self.statements[index].kind {
            super::bound_tree::BoundStatementKind::Expression(expression) => Some(expression),
            _ => None,
        }
    }
}
