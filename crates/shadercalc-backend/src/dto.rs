use serde::Serialize;
use shadercalc_core::diagnostics::{Diagnostic, DiagnosticSeverity};
use shadercalc_core::docs::DocEntry;
use shadercalc_core::exports::Export;
use shadercalc_core::reference::checker::{ReferenceOutcome, ReferenceVerdict};
use shadercalc_core::trace::{LineTrace, TracePointKind};
use shadercalc_core::types::{ScalarKind, ShaderType};
use shadercalc_core::units::Dimension;
use shadercalc_core::values::{Value, describe_dimension, format_component};
use shadercalc_core::worksheet::WorksheetLine;

/// Spans are UTF-16 offsets, the same unit as JavaScript strings.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticDto {
    pub severity: &'static str,
    pub message: String,
    pub source: String,
    pub function: Option<String>,
    pub line: usize,
    pub column: usize,
    pub from: usize,
    pub to: usize,
    pub text: String,
}

impl From<&Diagnostic> for DiagnosticDto {
    fn from(diagnostic: &Diagnostic) -> DiagnosticDto {
        DiagnosticDto {
            severity: match diagnostic.severity {
                DiagnosticSeverity::Error => "error",
                DiagnosticSeverity::Warning => "warning",
                DiagnosticSeverity::Info => "info",
            },
            message: diagnostic.message.clone(),
            source: diagnostic.source.clone(),
            function: diagnostic.function.clone(),
            line: diagnostic.span.line,
            column: diagnostic.span.column,
            from: diagnostic.span.offset,
            to: diagnostic.span.end(),
            text: diagnostic.to_string(),
        }
    }
}

/// A float's sign, exponent or mantissa: its top and bottom bit.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BitFieldDto {
    pub name: &'static str,
    pub high: u32,
    pub low: u32,
}

/// One flattened component: its name (.x, [1][0], .Field), value, hex and bit pattern.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ComponentDto {
    pub name: String,
    pub text: String,
    pub hex: String,
    pub bits: String,
    /// The raw bits, to compare with the reference's.
    pub raw: String,
    /// The bits the inspector draws (0 for bool), and a float's fields.
    pub width: u32,
    pub fields: Vec<BitFieldDto>,
    /// What a float's exponent means: zero, denormal, infinity, NaN or 2^e.
    pub exponent_meaning: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ValueDto {
    pub text: String,
    pub ty: String,
    pub units: String,
    pub components: Vec<ComponentDto>,
}

/// x/y/z/w, [row][column], [index], .field names for every flattened component.
fn component_names(ty: &ShaderType, prefix: &str, names: &mut Vec<String>) {
    match ty {
        ShaderType::Numeric(numeric) if numeric.is_scalar() => {
            names.push(if prefix.is_empty() { "value".to_string() } else { prefix.to_string() });
        }
        ShaderType::Numeric(vector) if vector.is_vector() => {
            for index in 0..vector.size() {
                names.push(if vector.size() <= 4 {
                    format!("{prefix}.{}", ['x', 'y', 'z', 'w'][index])
                } else {
                    format!("{prefix}[{index}]")
                });
            }
        }
        ShaderType::Numeric(matrix) => {
            for row in 0..matrix.rows {
                for column in 0..matrix.columns {
                    names.push(format!("{prefix}._m{row}{column}"));
                }
            }
        }
        ShaderType::Array(array) => {
            for index in 0..array.length {
                component_names(&array.element, &format!("{prefix}[{index}]"), names);
            }
        }
        ShaderType::Struct(structure) => {
            for field in &structure.fields {
                component_names(&field.ty, &format!("{prefix}.{}", field.name), names);
            }
        }
        ShaderType::Void => {}
    }
}

pub fn hex(kind: ScalarKind, bits: u64) -> String {
    match kind {
        ScalarKind::Bool => (if bits != 0 { "1" } else { "0" }).to_string(),
        ScalarKind::Half => format!("0x{:04X}", bits as u16),
        _ if kind.is_64bit() => format!("0x{bits:016X}"),
        _ => format!("0x{:08X}", bits as u32),
    }
}

/// A float kind's width and exponent width (a 16-bit half is Slang's; HLSL's half is a float).
fn float_layout(kind: ScalarKind) -> Option<(u32, u32)> {
    match kind {
        ScalarKind::Half => Some((16, 5)),
        ScalarKind::Float => Some((32, 8)),
        ScalarKind::Double | ScalarKind::LiteralFloat => Some((64, 11)),
        _ => None,
    }
}

fn bit_width(kind: ScalarKind) -> u32 {
    match kind {
        ScalarKind::Bool => 0,
        _ if kind.is_64bit() => 64,
        _ => float_layout(kind).map_or(32, |(width, _)| width),
    }
}

fn float_fields(kind: ScalarKind) -> Vec<BitFieldDto> {
    let Some((width, exponent_width)) = float_layout(kind) else {
        return Vec::new();
    };
    let mantissa_width: u32 = width - 1 - exponent_width;
    vec![
        BitFieldDto { name: "sign", high: width - 1, low: width - 1 },
        BitFieldDto { name: "exponent", high: width - 2, low: mantissa_width },
        BitFieldDto { name: "mantissa", high: mantissa_width - 1, low: 0 },
    ]
}

/// The exponent and mantissa fields of a float's bits.
fn float_parts(kind: ScalarKind, bits: u64) -> Option<(u64, u64)> {
    let (width, exponent_width) = float_layout(kind)?;
    let mantissa_width: u32 = width - 1 - exponent_width;
    Some(((bits >> mantissa_width) & ((1 << exponent_width) - 1), bits & ((1 << mantissa_width) - 1)))
}

fn exponent_meaning(kind: ScalarKind, bits: u64) -> Option<String> {
    let (_, exponent_width) = float_layout(kind)?;
    let (exponent, mantissa) = float_parts(kind, bits)?;
    let maximum: u64 = (1 << exponent_width) - 1;
    Some(match exponent {
        0 => (if mantissa == 0 { "zero" } else { "denormal" }).to_string(),
        _ if exponent == maximum => (if mantissa == 0 { "infinity" } else { "NaN" }).to_string(),
        _ => format!("2^{}", exponent as i64 - (maximum >> 1) as i64),
    })
}

/// Floats as sign | exponent | mantissa with the decoded exponent; integers in groups of 8 bits.
pub fn bit_pattern(kind: ScalarKind, bits: u64) -> String {
    if let (Some((width, exponent_width)), Some((exponent, mantissa))) = (float_layout(kind), float_parts(kind, bits)) {
        let mantissa_width: usize = (width - 1 - exponent_width) as usize;
        let meaning: String = exponent_meaning(kind, bits).unwrap_or_default();
        let sign: u64 = (bits >> (width - 1)) & 1;
        return format!(
            "{sign} {exponent:0exponent_width$b} {mantissa:0mantissa_width$b}  ({meaning})",
            exponent_width = exponent_width as usize
        );
    }
    match kind {
        ScalarKind::Bool => (if bits != 0 { "true" } else { "false" }).to_string(),
        _ => {
            let binary: String = if kind.is_64bit() { format!("{bits:064b}") } else { format!("{:032b}", bits as u32) };
            let groups: Vec<&str> = (0..binary.len()).step_by(8).map(|start| &binary[start..start + 8]).collect();
            groups.join(" ")
        }
    }
}

fn describe_units(value: &Value) -> String {
    let mut dimensions: Vec<Dimension> = Vec::new();
    for unit in &value.units {
        if !dimensions.contains(&unit.dimension) {
            dimensions.push(unit.dimension);
        }
    }
    match dimensions.as_slice() {
        [single] if single.is_none() => String::new(),
        [single] => describe_dimension(single),
        _ => "units per component".to_string(),
    }
}

impl From<&Value> for ValueDto {
    fn from(value: &Value) -> ValueDto {
        let mut names: Vec<String> = Vec::new();
        component_names(&value.ty, "", &mut names);
        let components: Vec<ComponentDto> = (0..value.bits.len())
            .map(|index| {
                let kind: ScalarKind = value.kind_at(index);
                let bits: u64 = value.bits[index];
                let mut text: String = format_component(kind, bits);
                if !value.units[index].dimension.is_none() {
                    text.push_str(&format!(" {}", value.units[index].dimension));
                }
                ComponentDto {
                    name: names.get(index).cloned().unwrap_or_default(),
                    text,
                    hex: hex(kind, bits),
                    bits: bit_pattern(kind, bits),
                    raw: bits.to_string(),
                    width: bit_width(kind),
                    fields: float_fields(kind),
                    exponent_meaning: exponent_meaning(kind, bits),
                }
            })
            .collect();
        ValueDto { text: value.to_string(), ty: value.ty.to_string(), units: describe_units(value), components }
    }
}

/// One top-level line: where it is, what it shows and its problems. `index` identifies it for reference checks.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LineDto {
    pub index: usize,
    pub document: String,
    /// 1-based line where the statement ends (where its result shows) and where it starts.
    pub line: usize,
    pub first_line: usize,
    pub from: usize,
    pub to: usize,
    pub value: Option<ValueDto>,
    pub diagnostics: Vec<DiagnosticDto>,
    pub trace: Option<TraceDto>,
}

/// A statement of a line's loops/ifs/blocks (`kind` "value") or a loop ("loop", with its variables' names).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TracePointDto {
    pub kind: &'static str,
    pub first_line: usize,
    pub last_line: usize,
    /// Enclosing loops (point indices), outermost first.
    pub loops: Vec<usize>,
    pub variables: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TraceEntryDto {
    pub point: usize,
    pub iterations: Vec<u32>,
    pub values: Vec<ValueDto>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TraceDto {
    pub points: Vec<TracePointDto>,
    pub entries: Vec<TraceEntryDto>,
    pub is_truncated: bool,
}

impl From<&LineTrace> for TraceDto {
    fn from(trace: &LineTrace) -> TraceDto {
        TraceDto {
            points: trace
                .points
                .iter()
                .map(|point| {
                    let (kind, variables) = match &point.kind {
                        TracePointKind::Value { .. } => ("value", Vec::new()),
                        TracePointKind::Loop { variables } => {
                            ("loop", variables.iter().map(|variable| variable.name.clone()).collect())
                        }
                    };
                    TracePointDto {
                        kind,
                        first_line: point.first_line,
                        last_line: point.last_line,
                        loops: point.loops.clone(),
                        variables,
                    }
                })
                .collect(),
            entries: trace
                .entries
                .iter()
                .map(|entry| TraceEntryDto {
                    point: entry.point,
                    iterations: entry.iterations.clone(),
                    values: entry.values.iter().map(ValueDto::from).collect(),
                })
                .collect(),
            is_truncated: trace.is_truncated,
        }
    }
}

impl LineDto {
    pub fn new(index: usize, line: &WorksheetLine) -> LineDto {
        LineDto {
            index,
            document: line.document.clone(),
            line: line.line,
            first_line: line.span.line,
            from: line.span.offset,
            to: line.span.end(),
            value: line.value().map(ValueDto::from),
            diagnostics: line.result.diagnostics.iter().map(DiagnosticDto::from).collect(),
            trace: line.result.trace.as_ref().map(TraceDto::from),
        }
    }
}

/// A name the editor can complete, with what it is and a description.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SymbolDto {
    pub name: String,
    pub kind: &'static str,
    pub detail: String,
}

/// A library's declaration, for the Library panel.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportDto {
    pub document: String,
    pub kind: &'static str,
    pub name: String,
    pub declaration: String,
    pub parameters: Option<Vec<String>>,
    pub comment: String,
    pub line: usize,
    pub offset: usize,
}

impl From<&Export> for ExportDto {
    fn from(export: &Export) -> ExportDto {
        ExportDto {
            document: export.document.clone(),
            kind: export.kind.name(),
            name: export.name.clone(),
            declaration: export.declaration.clone(),
            parameters: export.parameters.clone(),
            comment: export.comment.clone(),
            line: export.line,
            offset: export.offset,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EvaluationDto {
    pub generation: u64,
    pub duration_ms: f64,
    pub lines: Vec<LineDto>,
    pub diagnostics: Vec<DiagnosticDto>,
    pub symbols: Vec<SymbolDto>,
    pub exports: Vec<ExportDto>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TimingsDto {
    pub compile_ms: f64,
    pub pipeline_ms: f64,
    pub run_ms: f64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReferenceDto {
    pub verdict: &'static str,
    pub summary: String,
    pub max_ulps: i64,
    pub uses_approximations: bool,
    pub message: Option<String>,
    pub hlsl: String,
    pub reference_value: Option<ValueDto>,
    pub timings: Option<TimingsDto>,
    /// A traced line: each entry's verdict, with the reference's values when they differ.
    pub trace: Vec<TraceCheckDto>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TraceCheckDto {
    pub verdict: &'static str,
    pub values: Vec<ValueDto>,
}

fn verdict_name(verdict: ReferenceVerdict) -> &'static str {
    match verdict {
        ReferenceVerdict::Match => "match",
        ReferenceVerdict::WithinTolerance => "withinTolerance",
        ReferenceVerdict::Mismatch => "mismatch",
        ReferenceVerdict::WarpLimit => "warpLimit",
        ReferenceVerdict::NotChecked => "notChecked",
    }
}

impl From<&ReferenceOutcome> for ReferenceDto {
    fn from(outcome: &ReferenceOutcome) -> ReferenceDto {
        ReferenceDto {
            verdict: verdict_name(outcome.verdict),
            summary: outcome.to_string(),
            max_ulps: outcome.max_ulps,
            uses_approximations: outcome.uses_approximations,
            message: outcome.message.clone(),
            hlsl: outcome.hlsl.clone(),
            reference_value: outcome.reference_value.as_ref().map(ValueDto::from),
            timings: outcome.timings.map(|timings| TimingsDto {
                compile_ms: timings.compile_milliseconds,
                pipeline_ms: timings.pipeline_milliseconds,
                run_ms: timings.run_milliseconds,
            }),
            trace: outcome
                .trace
                .iter()
                .map(|check| TraceCheckDto {
                    verdict: verdict_name(check.verdict),
                    values: match check.verdict {
                        ReferenceVerdict::Match => Vec::new(),
                        _ => check.values.iter().map(ValueDto::from).collect(),
                    },
                })
                .collect(),
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DocDto {
    pub name: String,
    pub group: String,
    pub signature: Option<String>,
    pub summary: String,
    pub markdown: String,
}

impl From<&DocEntry> for DocDto {
    fn from(entry: &DocEntry) -> DocDto {
        DocDto {
            name: entry.name.clone(),
            group: entry.group.clone(),
            signature: entry.signature.clone(),
            summary: entry.summary(),
            markdown: entry.markdown.clone(),
        }
    }
}
