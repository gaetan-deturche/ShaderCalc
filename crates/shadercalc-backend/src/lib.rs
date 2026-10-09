//! ShaderCalc's app backend: one `handle(command, arguments)` entry point taking and returning JSON, served the same way
//! by the Tauri app (IPC) and the development server (HTTP), so the UI can be exercised in a plain browser.

pub mod dto;
pub mod store;

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde::Deserialize;
use serde_json::{Value as Json, json};
use shadercalc_core::docs;
use shadercalc_core::evaluation::evaluator::EvaluationOptions;
use shadercalc_core::reference::checker;
use shadercalc_core::reference::warp_device::ReferenceMode;
use shadercalc_core::semantics::SemanticsProfile;
use shadercalc_core::trace::CallTrace;
use shadercalc_core::units::UNITS;
use shadercalc_core::worksheet::{self, WorksheetDocument, WorksheetLine, WorksheetResult};

use dto::{CallTraceDto, DiagnosticDto, DocDto, EvaluationDto, ExportDto, LineDto, ReferenceDto, SymbolDto};
use store::{OutsideChanges, WorksheetStore};

#[derive(Deserialize)]
struct DocumentArgument {
    name: String,
    text: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StateArgument {
    tab_order: Vec<String>,
    active_tab: Option<String>,
    side_panel_width: f64,
    result_column_width: f64,
    profile: String,
}

/// An evaluation's generation, result, documents and profile.
type Evaluated = (u64, Arc<WorksheetResult>, Arc<Vec<WorksheetDocument>>, &'static SemanticsProfile);

pub struct Backend {
    store: Mutex<WorksheetStore>,
    generation: AtomicU64,
    cancellation: Mutex<Arc<AtomicBool>>,
    /// The latest evaluation, for its reference checks and call traces.
    last: Mutex<Option<Evaluated>>,
}

fn argument<'a, T: Deserialize<'a>>(arguments: &'a Json, name: &str) -> Result<T, String> {
    T::deserialize(&arguments[name]).map_err(|error| format!("argument '{name}': {error}"))
}

fn io_error(error: std::io::Error) -> String {
    error.to_string()
}

impl Backend {
    pub fn open() -> Result<Backend, String> {
        Backend::open_folder(WorksheetStore::default_folder())
    }

    pub fn open_folder(folder: std::path::PathBuf) -> Result<Backend, String> {
        let store: WorksheetStore = WorksheetStore::open(folder).map_err(io_error)?;
        Ok(Backend {
            store: Mutex::new(store),
            generation: AtomicU64::new(0),
            cancellation: Mutex::new(Arc::new(AtomicBool::new(false))),
            last: Mutex::new(None),
        })
    }

    /// Runs one command. Arguments and result are JSON; errors are messages for the user.
    pub fn handle(&self, command: &str, arguments: &Json) -> Result<Json, String> {
        match command {
            "load" => self.load(),
            "save" => {
                let name: String = argument(arguments, "name")?;
                let text: String = argument(arguments, "text")?;
                self.store.lock().expect("store lock").write(&name, &text).map_err(io_error)?;
                Ok(Json::Null)
            }
            "create" => Ok(json!({ "name": self.store.lock().expect("store lock").create().map_err(io_error)? })),
            "rename" => {
                let name: String = argument(arguments, "name")?;
                let title: String = argument(arguments, "title")?;
                Ok(json!({ "name": self.store.lock().expect("store lock").rename(&name, &title)? }))
            }
            "delete" => {
                let name: String = argument(arguments, "name")?;
                self.store.lock().expect("store lock").delete(&name)?;
                Ok(Json::Null)
            }
            "saveState" => {
                let state: StateArgument = argument(arguments, "state")?;
                let mut store = self.store.lock().expect("store lock");
                store.state.tab_order = state.tab_order;
                store.state.active_tab = state.active_tab;
                store.state.side_panel_width = state.side_panel_width;
                store.state.result_column_width = state.result_column_width;
                store.state.profile = state.profile;
                store.save_state().map_err(io_error)?;
                Ok(Json::Null)
            }
            "pollChanges" => {
                let changes: OutsideChanges =
                    self.store.lock().expect("store lock").outside_changes().map_err(io_error)?;
                let documents = |pairs: Vec<(String, String)>| -> Json {
                    pairs.into_iter().map(|(name, text)| json!({ "name": name, "text": text })).collect()
                };
                Ok(json!({
                    "changed": documents(changes.changed),
                    "added": documents(changes.added),
                    "removed": changes.removed,
                }))
            }
            "openFolder" => {
                let folder = self.store.lock().expect("store lock").folder.clone();
                std::process::Command::new("explorer.exe").arg(folder).spawn().map_err(io_error)?;
                Ok(Json::Null)
            }
            "evaluate" => {
                let documents: Vec<DocumentArgument> = argument(arguments, "documents")?;
                let profile_id: String = argument(arguments, "profile")?;
                let profile: &'static SemanticsProfile =
                    SemanticsProfile::by_id(&profile_id).ok_or_else(|| format!("unknown profile '{profile_id}'"))?;
                self.evaluate(documents, profile)
            }
            "checkReference" => {
                let generation: u64 = argument(arguments, "generation")?;
                let index: usize = argument(arguments, "index")?;
                self.check_reference(generation, index)
            }
            "traceCall" => {
                let generation: u64 = argument(arguments, "generation")?;
                let index: usize = argument(arguments, "index")?;
                let path: Vec<(usize, usize)> = argument(arguments, "path")?;
                self.trace_call(generation, index, &path)
            }
            "traceCallRuns" | "checkCallRuns" => {
                let generation: u64 = argument(arguments, "generation")?;
                let index: usize = argument(arguments, "index")?;
                let path: Vec<(usize, usize)> = argument(arguments, "path")?;
                let points: Vec<usize> = argument(arguments, "points")?;
                self.call_runs(generation, index, &path, &points, command == "checkCallRuns")
            }
            "checkCall" => {
                let generation: u64 = argument(arguments, "generation")?;
                let index: usize = argument(arguments, "index")?;
                let path: Vec<(usize, usize)> = argument(arguments, "path")?;
                self.check_call(generation, index, &path)
            }
            "docs" => {
                let entries: Vec<DocDto> = docs::entries().iter().map(DocDto::from).collect();
                serde_json::to_value(entries).map_err(|error| error.to_string())
            }
            "searchDocs" => {
                let query: String = argument(arguments, "query")?;
                Ok(docs::search(&query).into_iter().map(|entry| Json::String(entry.name.clone())).collect())
            }
            "units" => {
                let mut units: Vec<Json> = UNITS
                    .values()
                    .filter(|unit| unit.is_listed)
                    .map(|unit| {
                        let detail: String =
                            format!("{}\n1 {} = {} {}", unit.description, unit.name, unit.to_si, unit.dimension);
                        json!({ "name": unit.name, "detail": detail })
                    })
                    .collect();
                units.sort_by(|left, right| left["name"].as_str().cmp(&right["name"].as_str()));
                Ok(Json::Array(units))
            }
            other => Err(format!("unknown command '{other}'")),
        }
    }

    fn load(&self) -> Result<Json, String> {
        let mut store = self.store.lock().expect("store lock");
        let names: Vec<String> = store.ordered_names().map_err(io_error)?;
        let mut documents: Vec<Json> = Vec::new();
        for name in names {
            let text: String = store.read(&name).map_err(io_error)?;
            documents.push(json!({ "name": name, "text": text }));
        }
        Ok(json!({
            "folder": store.folder.display().to_string(),
            "documents": documents,
            "activeTab": store.state.active_tab,
            "sidePanelWidth": store.state.side_panel_width,
            "resultColumnWidth": store.state.result_column_width,
            "profile": SemanticsProfile::by_id(&store.state.profile).unwrap_or(&SemanticsProfile::HLSL).id,
            "profiles": SemanticsProfile::ALL
                .iter()
                .map(|profile| json!({ "id": profile.id, "label": profile.label, "hasReference": profile.has_reference() }))
                .collect::<Vec<Json>>(),
        }))
    }

    /// Evaluates the worksheets; a newer evaluation cancels this one (the result is then `{ "cancelled": true }`).
    fn evaluate(&self, documents: Vec<DocumentArgument>, profile: &'static SemanticsProfile) -> Result<Json, String> {
        let generation: u64 = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        let cancellation: Arc<AtomicBool> = Arc::new(AtomicBool::new(false));
        {
            let mut current = self.cancellation.lock().expect("cancellation lock");
            current.store(true, Ordering::SeqCst);
            *current = cancellation.clone();
        }
        let documents: Vec<WorksheetDocument> =
            documents.into_iter().map(|document| WorksheetDocument::new(document.name, document.text)).collect();
        let options: EvaluationOptions =
            EvaluationOptions { cancellation: Some(cancellation), ..EvaluationOptions::default() };
        let Ok(result) = worksheet::evaluate_with_libraries(store::SCRATCH, &documents, profile, &options) else {
            return Ok(json!({ "cancelled": true }));
        };
        if self.generation.load(Ordering::SeqCst) != generation {
            return Ok(json!({ "cancelled": true }));
        }

        let mut symbols: Vec<SymbolDto> = Vec::new();
        for function in &result.program.functions {
            symbols.push(SymbolDto {
                name: function.name.clone(),
                kind: "function",
                detail: format!("{}\n{}", function.signature(), function.source_name()),
            });
        }
        for global in &result.program.globals {
            symbols.push(SymbolDto { name: global.name.clone(), kind: "variable", detail: global.to_string() });
        }
        for name in result.program.type_names.keys() {
            symbols.push(SymbolDto { name: name.clone(), kind: "type", detail: name.clone() });
        }
        let evaluation: EvaluationDto = EvaluationDto {
            generation,
            duration_ms: result.duration.as_secs_f64() * 1000.0,
            lines: result.lines.iter().enumerate().map(|(index, line)| LineDto::new(index, line)).collect(),
            diagnostics: result.diagnostics.iter().map(DiagnosticDto::from).collect(),
            symbols,
            exports: result.exports.iter().map(ExportDto::from).collect(),
        };
        *self.last.lock().expect("evaluation lock") =
            Some((generation, Arc::new(result), Arc::new(documents), profile));
        serde_json::to_value(evaluation).map_err(|error| error.to_string())
    }

    /// What one call made by a line of an evaluation computed (`path`: see `worksheet::trace_call`); null when a
    /// newer evaluation replaced it.
    fn trace_call(&self, generation: u64, index: usize, path: &[(usize, usize)]) -> Result<Json, String> {
        let (result, documents, profile) = match &*self.last.lock().expect("evaluation lock") {
            Some((last, result, documents, profile)) if *last == generation => {
                (result.clone(), documents.clone(), *profile)
            }
            _ => return Ok(Json::Null),
        };
        let line: &WorksheetLine = result.lines.get(index).ok_or_else(|| format!("no line {index}"))?;
        let call: CallTrace = worksheet::trace_call(line, &documents, path, profile, &EvaluationOptions::default())?;
        serde_json::to_value(CallTraceDto::from(&call)).map_err(|error| error.to_string())
    }

    /// Every run of the call `path` leads to (its last run doesn't matter), keeping `points`: the runs, or with
    /// `is_check` their reference check; null when a newer evaluation replaced it.
    fn call_runs(
        &self,
        generation: u64,
        index: usize,
        path: &[(usize, usize)],
        points: &[usize],
        is_check: bool,
    ) -> Result<Json, String> {
        let (result, documents, profile) = match &*self.last.lock().expect("evaluation lock") {
            Some((last, result, documents, profile)) if *last == generation => {
                (result.clone(), documents.clone(), *profile)
            }
            _ => return Ok(Json::Null),
        };
        let line: &WorksheetLine = result.lines.get(index).ok_or_else(|| format!("no line {index}"))?;
        let options: EvaluationOptions = EvaluationOptions::default();
        let runs: Vec<CallTrace> = worksheet::trace_call_runs(line, &documents, path, points, profile, &options)?;
        if !is_check {
            let runs: Vec<CallTraceDto> = runs.iter().map(CallTraceDto::from).collect();
            return serde_json::to_value(runs).map_err(|error| error.to_string());
        }
        let outcome = if profile.has_reference() {
            checker::check_call_runs(&line.result, path, &runs, points, ReferenceMode::Strict)
        } else {
            checker::not_checked(
                format!("DXC + WARP check the HLSL profile only, not {}", profile.label),
                String::new(),
            )
        };
        serde_json::to_value(ReferenceDto::from(&outcome)).map_err(|error| error.to_string())
    }

    /// The reference check of one call made by a line of an evaluation; null when a newer evaluation replaced it.
    fn check_call(&self, generation: u64, index: usize, path: &[(usize, usize)]) -> Result<Json, String> {
        let (result, documents, profile) = match &*self.last.lock().expect("evaluation lock") {
            Some((last, result, documents, profile)) if *last == generation => {
                (result.clone(), documents.clone(), *profile)
            }
            _ => return Ok(Json::Null),
        };
        let line: &WorksheetLine = result.lines.get(index).ok_or_else(|| format!("no line {index}"))?;
        let outcome = if profile.has_reference() {
            let call: CallTrace =
                worksheet::trace_call(line, &documents, path, profile, &EvaluationOptions::default())?;
            checker::check_call(&line.result, path, &call, ReferenceMode::Strict)
        } else {
            checker::not_checked(
                format!("DXC + WARP check the HLSL profile only, not {}", profile.label),
                String::new(),
            )
        };
        serde_json::to_value(ReferenceDto::from(&outcome)).map_err(|error| error.to_string())
    }

    /// The reference check of one line of an evaluation; null when a newer evaluation replaced it.
    fn check_reference(&self, generation: u64, index: usize) -> Result<Json, String> {
        let (result, profile): (Arc<WorksheetResult>, &'static SemanticsProfile) =
            match &*self.last.lock().expect("evaluation lock") {
                Some((last, result, _, profile)) if *last == generation => (result.clone(), *profile),
                _ => return Ok(Json::Null),
            };
        let Some(line) = result.lines.get(index) else {
            return Err(format!("no line {index}"));
        };
        let outcome = if profile.has_reference() {
            checker::check(&line.result, ReferenceMode::Strict)
        } else {
            checker::not_checked(
                format!("DXC + WARP check the HLSL profile only, not {}", profile.label),
                String::new(),
            )
        };
        serde_json::to_value(ReferenceDto::from(&outcome)).map_err(|error| error.to_string())
    }
}
