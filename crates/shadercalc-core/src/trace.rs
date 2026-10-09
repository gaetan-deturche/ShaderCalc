//! What a worksheet line's loops, ifs and blocks computed: every statement's value there, at every iteration.

use std::collections::HashMap;

use crate::binding::bound_tree::{BoundExpressionKind, BoundStatement, BoundStatementKind};
use crate::binding::symbols::VariableRef;
use crate::diagnostics::SourceSpan;
use crate::types::ShaderType;
use crate::values::Value;

/// Entries kept per line (each goes to the UI on every evaluation): a longer run keeps its first ones.
pub const MAX_TRACE_ENTRIES: usize = 4_096;

#[derive(Clone, Debug)]
pub enum TracePointKind {
    /// A declaration or an expression statement: the value it produces.
    Value { ty: ShaderType },
    /// A loop: each iteration, the variables its initializer declares (or assigns).
    Loop { variables: Vec<VariableRef> },
}

/// A statement whose value shows on its line, or a loop.
#[derive(Clone, Debug)]
pub struct TracePoint {
    pub kind: TracePointKind,
    pub span: SourceSpan,
    /// The loops it is in (point ids), outermost first.
    pub loops: Vec<usize>,
    /// 1-based lines where it starts and ends (set by the worksheet, which knows the text).
    pub first_line: usize,
    pub last_line: usize,
}

/// One execution of a point: its iteration in each enclosing loop, and its values (a loop's: its variables').
#[derive(Clone, Debug, PartialEq)]
pub struct TraceEntry {
    pub point: usize,
    pub iterations: Vec<u32>,
    pub values: Vec<Value>,
}

#[derive(Clone, Debug, Default)]
pub struct LineTrace {
    pub points: Vec<TracePoint>,
    pub entries: Vec<TraceEntry>,
    /// More than `MAX_TRACE_ENTRIES` ran: only the first are kept.
    pub is_truncated: bool,
}

impl TracePoint {
    /// The types of the values its entries hold.
    pub fn value_types(&self) -> Vec<ShaderType> {
        match &self.kind {
            TracePointKind::Value { ty } => vec![ty.clone()],
            TracePointKind::Loop { variables } => variables.iter().map(|variable| variable.ty.clone()).collect(),
        }
    }
}

/// A line statement's trace points in pre-order, and the statement node of each. The evaluator and the reference
/// emitter each walk their own copy of the tree; the numbering is the same.
pub fn trace_points(statement: &BoundStatement) -> (Vec<TracePoint>, HashMap<*const BoundStatement, usize>) {
    let mut points: Vec<TracePoint> = Vec::new();
    let mut ids: HashMap<*const BoundStatement, usize> = HashMap::new();
    walk(statement, false, &mut Vec::new(), &mut points, &mut ids);
    (points, ids)
}

/// `is_nested`: inside a loop body, an if, a switch or braces, where a statement's value isn't the line's.
fn walk(
    statement: &BoundStatement,
    is_nested: bool,
    loops: &mut Vec<usize>,
    points: &mut Vec<TracePoint>,
    ids: &mut HashMap<*const BoundStatement, usize>,
) {
    let mut add = |kind: TracePointKind, loops: &[usize]| -> usize {
        let id: usize = points.len();
        points.push(TracePoint { kind, span: statement.span, loops: loops.to_vec(), first_line: 0, last_line: 0 });
        ids.insert(statement as *const BoundStatement, id);
        id
    };
    match &statement.kind {
        BoundStatementKind::Block { statements, is_scope } => {
            for inner in statements {
                walk(inner, is_nested || *is_scope, loops, points, ids);
            }
        }
        BoundStatementKind::VariableDeclaration { variable, .. } if is_nested => {
            add(TracePointKind::Value { ty: variable.ty.clone() }, loops);
        }
        BoundStatementKind::Expression(expression)
            if is_nested
                && !expression.ty.is_void()
                && !expression.ty.component_kinds().iter().any(|kind| kind.is_literal()) =>
        {
            add(TracePointKind::Value { ty: expression.ty.clone() }, loops);
        }
        BoundStatementKind::If { then, otherwise, .. } => {
            walk(then, true, loops, points, ids);
            if let Some(otherwise) = otherwise {
                walk(otherwise, true, loops, points, ids);
            }
        }
        BoundStatementKind::Loop { initializer, body, .. } => {
            let variables: Vec<VariableRef> = initializer.as_deref().map(loop_variables).unwrap_or_default();
            let id: usize = add(TracePointKind::Loop { variables }, loops);
            loops.push(id);
            walk(body, true, loops, points, ids);
            loops.pop();
        }
        BoundStatementKind::Switch { sections, .. } => {
            for section in sections {
                for inner in &section.statements {
                    walk(inner, true, loops, points, ids);
                }
            }
        }
        _ => {}
    }
}

/// What a loop's initializer declares (`int i = 0, j = 0`), else the variable it assigns (`i = 0`).
fn loop_variables(initializer: &BoundStatement) -> Vec<VariableRef> {
    match &initializer.kind {
        BoundStatementKind::VariableDeclaration { variable, .. } => vec![variable.clone()],
        BoundStatementKind::Block { statements, .. } => statements.iter().flat_map(loop_variables).collect(),
        BoundStatementKind::Expression(expression) => match &expression.kind {
            BoundExpressionKind::Assignment { target, .. } => match &target.kind {
                BoundExpressionKind::Variable(variable) => vec![variable.clone()],
                _ => Vec::new(),
            },
            _ => Vec::new(),
        },
        _ => Vec::new(),
    }
}
