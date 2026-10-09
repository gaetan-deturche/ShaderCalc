use std::collections::HashMap;
use std::sync::Arc;

use crate::binding::binder::{bind_interactive, bind_program};
use crate::binding::program::{BoundInteractive, BoundProgram};
use crate::binding::symbols::{VariableKind, VariableRef};
use crate::diagnostics::{Diagnostic, DiagnosticBag, DiagnosticSeverity, SourceSpan};
use crate::evaluation::evaluator::{EvaluationOptions, Evaluator, Interrupt, Storage};
use crate::semantics::SemanticsProfile;
use crate::syntax::parser::{parse_interactive, parse_program};
use crate::syntax::tree::{CompilationUnitSyntax, DeclarationSyntax, ItemSyntax, StatementSyntax};
use crate::types::{NumericType, ScalarKind, ShaderType};
use crate::units::UNITS;
use crate::values::{Value, scalars};
use crate::worksheet::declared_names;

/// The outcome of one calculator or worksheet line.
#[derive(Clone, Debug, Default)]
pub struct LineResult {
    pub value: Option<Value>,
    pub diagnostics: Vec<Diagnostic>,
    pub line: Option<BoundInteractive>,
    pub message: Option<String>,
    /// The program the line ran against.
    pub program: Option<Arc<BoundProgram>>,
    /// Calculator variables and uniforms as the line found them (what a reference run must start from).
    pub inputs: Storage,
    /// Why WARP's result can't be trusted for this line (sinh, sin of huge angles...); empty when it can.
    pub reference_limits: Vec<String>,
}

impl LineResult {
    pub fn new(value: Option<Value>, diagnostics: Vec<Diagnostic>, line: Option<BoundInteractive>) -> LineResult {
        LineResult { value, diagnostics, line, ..LineResult::default() }
    }

    pub fn has_errors(&self) -> bool {
        self.diagnostics.iter().any(Diagnostic::is_error)
    }

    pub fn text(&self) -> String {
        match (&self.value, &self.message) {
            (Some(value), _) => value.to_string(),
            (None, Some(message)) => message.clone(),
            (None, None) => String::new(),
        }
    }
}

pub const PROGRAM_SOURCE_NAME: &str = "program";
pub const LINE_SOURCE_NAME: &str = "input";

/// A calculator session: pasted HLSL / C++ code (the program) plus calculator lines evaluated against it. Lines can
/// declare variables (kept across lines), set uniforms, and define functions or structs.
pub struct ShaderSession {
    pub profile: SemanticsProfile,
    pub options: EvaluationOptions,
    pub program_source: String,
    pub program: Arc<BoundProgram>,
    variables: HashMap<String, VariableRef>,
    variable_values: Storage,
    uniform_values: HashMap<String, Value>,
    line_declarations: Vec<DeclarationSyntax>,
    program_declarations: Vec<DeclarationSyntax>,
    parse_diagnostics: Vec<Diagnostic>,
}

impl Default for ShaderSession {
    fn default() -> ShaderSession {
        ShaderSession::new(SemanticsProfile::HLSL)
    }
}

impl ShaderSession {
    pub fn new(profile: SemanticsProfile) -> ShaderSession {
        ShaderSession {
            profile,
            options: EvaluationOptions::default(),
            program_source: String::new(),
            program: BoundProgram::empty(),
            variables: HashMap::new(),
            variable_values: Storage::new(),
            uniform_values: HashMap::new(),
            line_declarations: Vec::new(),
            program_declarations: Vec::new(),
            parse_diagnostics: Vec::new(),
        }
    }

    /// Calculator variables by name.
    pub fn variables(&self) -> &HashMap<String, VariableRef> {
        &self.variables
    }

    pub fn get_variable_value(&self, name: &str) -> Option<&Value> {
        self.variables.get(name).and_then(|variable| self.variable_values.get(variable))
    }

    /// Replaces the pasted code. Returns its diagnostics (the session keeps working with what bound).
    pub fn set_program(&mut self, source: &str) -> Vec<Diagnostic> {
        self.program_source = source.to_string();
        let mut diagnostics: DiagnosticBag = DiagnosticBag::new();
        let unit: CompilationUnitSyntax = parse_program(source, PROGRAM_SOURCE_NAME, &mut diagnostics, &[]);
        self.program_declarations = unit.declarations;
        self.parse_diagnostics = diagnostics.into_items();
        self.rebind();
        self.program.diagnostics.clone()
    }

    pub fn reset_variables(&mut self) {
        self.variables.clear();
        self.variable_values.clear();
        self.uniform_values.clear();
        self.line_declarations.clear();
        self.rebind();
    }

    fn rebind(&mut self) {
        let mut declarations: Vec<DeclarationSyntax> = self.program_declarations.clone();
        declarations.extend(self.line_declarations.iter().cloned());
        let unit: CompilationUnitSyntax = CompilationUnitSyntax { declarations };
        self.program = bind_program(&unit, PROGRAM_SOURCE_NAME, &self.profile, &self.parse_diagnostics);
    }

    /// Names a calculator literal must not read as a unit (`2 h` is hours unless h is a variable).
    fn is_unit_name(&self, name: &str) -> bool {
        UNITS.contains_key(name)
            && !self.variables.contains_key(name)
            && self.program.globals.iter().all(|global| global.name != name)
            && self.program.functions.iter().all(|function| function.name != name)
            && !self.program.type_names.contains_key(name)
    }

    pub fn evaluate(&mut self, line: &str) -> LineResult {
        let mut diagnostics: DiagnosticBag = DiagnosticBag::new();
        let type_names: Vec<String> = self.program.type_names.keys().cloned().collect();
        let items: Vec<ItemSyntax> = {
            let parse = |diagnostics: &mut DiagnosticBag, declared: &[String]| {
                let is_unit_name = |name: &str| self.is_unit_name(name) && !declared.iter().any(|other| other == name);
                parse_interactive(line, LINE_SOURCE_NAME, diagnostics, &type_names, &is_unit_name)
            };
            let items: Vec<ItemSyntax> = parse(&mut diagnostics, &[]);
            // Unless the line itself names something h (`float t = 2; 3 m / t`)
            let declared: Vec<String> = declared_names(&items);
            if declared.iter().any(|name| UNITS.contains_key(name.as_str())) {
                diagnostics = DiagnosticBag::new();
                parse(&mut diagnostics, &declared)
            } else {
                items
            }
        };
        if diagnostics.has_errors() {
            return LineResult::new(None, diagnostics.into_items(), None);
        }

        // Functions, structs and typedefs typed in a line join the program
        let declarations: Vec<DeclarationSyntax> = items
            .iter()
            .filter_map(|item| match item {
                ItemSyntax::Declaration(declaration) => Some(declaration.clone()),
                ItemSyntax::Statement(_) => None,
            })
            .collect();
        if !declarations.is_empty() {
            let previous: Vec<DeclarationSyntax> = self.line_declarations.clone();
            self.line_declarations.extend(declarations.iter().cloned());
            self.rebind();
            let new_problems: Vec<Diagnostic> =
                self.program.diagnostics.iter().filter(|diagnostic| diagnostic.is_error()).cloned().collect();
            if !new_problems.is_empty()
                && declarations.iter().any(|declaration| {
                    new_problems.iter().any(|problem| problem.span.offset >= declaration_span(declaration).offset)
                })
            {
                self.line_declarations = previous;
                self.rebind();
                return LineResult::new(None, new_problems, None);
            }
        }

        let statements: Vec<&StatementSyntax> = items
            .iter()
            .filter_map(|item| match item {
                ItemSyntax::Statement(statement) => Some(statement),
                ItemSyntax::Declaration(_) => None,
            })
            .collect();
        if statements.is_empty() {
            let names: Vec<String> = declarations
                .iter()
                .map(|declaration| match declaration {
                    DeclarationSyntax::Function(function) => function.name.clone(),
                    DeclarationSyntax::Struct(structure) => structure.name.clone(),
                    DeclarationSyntax::Typedef(typedef) => typedef.name.clone(),
                    DeclarationSyntax::GlobalVariable(_) => "declaration".to_string(),
                })
                .collect();
            let mut result: LineResult = LineResult::new(None, diagnostics.into_items(), None);
            result.message = Some(format!("defined {}", names.join(", ")));
            return result;
        }

        let (bound, bind_diagnostics) =
            bind_interactive(&statements, &self.program, &self.variables, LINE_SOURCE_NAME, &self.profile);
        diagnostics.add_range(&bind_diagnostics);
        if diagnostics.has_errors() {
            return LineResult::new(None, diagnostics.into_items(), Some(bound));
        }

        // Run on copies, so a failing line leaves the session as it was
        let mut storage: Storage = self.variable_values.clone();
        for uniform in self.program.globals.iter().filter(|global| global.kind == VariableKind::Uniform) {
            if let Some(value) = self.uniform_values.get(&uniform.name)
                && value.ty == uniform.ty
            {
                storage.insert(uniform.clone(), value.clone());
            }
        }
        let program: Arc<BoundProgram> = self.program.clone();
        let mut inputs: Storage = Storage::new();
        let (run, reference_limits): (Result<Option<Value>, Interrupt>, Vec<String>) = {
            let mut evaluator: Evaluator =
                Evaluator::new(&self.profile, &self.options, &mut storage, &mut diagnostics, LINE_SOURCE_NAME);
            let outcome: Result<Option<Value>, Interrupt> = evaluator.initialize_globals(&program).and_then(|_| {
                // What the line starts from, once the globals are initialised
                inputs = evaluator
                    .storage()
                    .iter()
                    .filter(|(variable, _)| matches!(variable.kind, VariableKind::Session | VariableKind::Uniform))
                    .map(|(variable, value)| (variable.clone(), value.clone()))
                    .collect();
                evaluator.run(&bound)
            });
            (outcome, evaluator.take_reference_limits())
        };
        let mut result: Option<Value> = match run {
            Ok(value) => value,
            Err(Interrupt::Error(error)) => {
                diagnostics.add(Diagnostic::new(
                    DiagnosticSeverity::Error,
                    error.message,
                    LINE_SOURCE_NAME,
                    error.span,
                ));
                return LineResult::new(None, diagnostics.into_items(), Some(bound));
            }
            Err(Interrupt::Cancelled) | Err(Interrupt::NotConstant) => {
                diagnostics.add(Diagnostic::new(
                    DiagnosticSeverity::Error,
                    "evaluation cancelled",
                    LINE_SOURCE_NAME,
                    SourceSpan::NONE,
                ));
                return LineResult::new(None, diagnostics.into_items(), Some(bound));
            }
        };

        if let Some(value) = result.take() {
            let (materialized, note) = materialize(value, &self.profile);
            if let Some(note) = note {
                diagnostics.add(Diagnostic::new(DiagnosticSeverity::Info, note, LINE_SOURCE_NAME, SourceSpan::NONE));
            }
            result = Some(materialized);
        }

        // Commit: variables (new and updated) and uniforms
        for variable in &bound.declared_variables {
            if let Some(replaced) = self.variables.get(&variable.name) {
                self.variable_values.remove(replaced);
            }
            self.variables.insert(variable.name.clone(), variable.clone());
        }
        for (variable, value) in storage {
            if variable.kind == VariableKind::Session
                && self.variables.get(&variable.name).is_some_and(|current| *current == variable)
            {
                self.variable_values.insert(variable, value);
            } else if variable.kind == VariableKind::Uniform {
                self.uniform_values.insert(variable.name.clone(), value);
            }
        }
        LineResult {
            value: result,
            diagnostics: diagnostics.into_items(),
            line: Some(bound),
            message: None,
            program: Some(program),
            inputs,
            reference_limits,
        }
    }
}

fn declaration_span(declaration: &DeclarationSyntax) -> SourceSpan {
    match declaration {
        DeclarationSyntax::Function(function) => function.span,
        DeclarationSyntax::Struct(structure) => structure.span,
        DeclarationSyntax::Typedef(typedef) => typedef.span,
        DeclarationSyntax::GlobalVariable(global) => global.span,
    }
}

/// A literal-typed value as a line shows it (float / int, int64 when it doesn't fit), and why if widened.
pub fn materialize(value: Value, profile: &SemanticsProfile) -> (Value, Option<String>) {
    let numeric: NumericType = match &value.ty {
        ShaderType::Numeric(numeric) if numeric.kind.is_literal() => *numeric,
        _ => return (value, None),
    };
    let mut kind: ScalarKind = numeric.kind.materialized();
    let mut note: Option<String> = None;
    if numeric.kind == ScalarKind::LiteralInt
        && value.bits.iter().any(|bits| (*bits as i64) < i32::MIN as i64 || (*bits as i64) > i32::MAX as i64)
    {
        kind = ScalarKind::Int64;
        note = Some("doesn't fit in an int: shown as int64 (an int would wrap)".to_string());
    }
    let ty: ShaderType = numeric.with_kind(kind).into();
    let bits: Vec<u64> = value.bits.iter().map(|bits| scalars::convert(numeric.kind, kind, *bits, profile)).collect();
    (Value::new(ty, bits, value.units), note)
}
