//! What a worksheet line's loops, ifs and blocks computed: every statement's value there, at every iteration.

use std::collections::HashMap;

use crate::binding::bound_tree::{BoundExpression, BoundExpressionKind, BoundStatement, BoundStatementKind};
use crate::binding::symbols::{FunctionRef, VariableRef};
use crate::diagnostics::SourceSpan;
use crate::syntax::tree::ParameterMode;
use crate::types::ShaderType;
use crate::values::Value;

/// Entries kept per line (each goes to the UI on every evaluation): a longer run keeps its first ones.
pub const MAX_TRACE_ENTRIES: usize = 4_096;

#[derive(Clone, Debug)]
pub enum TracePointKind {
    /// A statement that writes variables (declares, assigns, increments, passes as out/inout): their values after it.
    Writes { variables: Vec<VariableRef> },
    /// An expression statement writing nothing, or a return: its value.
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
    /// The worksheet functions the traced code calls, and each time it did.
    pub calls: Vec<CallSite>,
    pub call_entries: Vec<CallEntry>,
}

/// A call to a worksheet function in the traced code (numbered in pre-order, like the points).
#[derive(Clone, Debug)]
pub struct CallSite {
    pub function: FunctionRef,
    pub span: SourceSpan,
    pub first_line: usize,
    pub last_line: usize,
}

/// One run of a call site: its iteration in each enclosing loop. A site's n-th entry is its n-th run.
#[derive(Clone, Debug, PartialEq)]
pub struct CallEntry {
    pub site: usize,
    pub iterations: Vec<u32>,
}

/// What one call of a worksheet function computed: its arguments, its body's trace, its result.
#[derive(Clone, Debug)]
pub struct CallTrace {
    pub function: FunctionRef,
    /// Its parameters as the call set them.
    pub arguments: Vec<Value>,
    /// None for a void function, or a call that failed.
    pub result: Option<Value>,
    pub trace: LineTrace,
    /// The lines of the function's definition in its document.
    pub first_line: usize,
    pub last_line: usize,
}

/// The calls to worksheet functions in a statement's expressions, in pre-order, with the node of each.
pub fn call_sites(statement: &BoundStatement) -> (Vec<CallSite>, HashMap<*const BoundExpression, usize>) {
    fn visit(
        expression: &BoundExpression,
        sites: &mut Vec<CallSite>,
        ids: &mut HashMap<*const BoundExpression, usize>,
    ) {
        if let BoundExpressionKind::Call { function, .. } = &expression.kind {
            ids.insert(expression as *const BoundExpression, sites.len());
            sites.push(CallSite { function: function.clone(), span: expression.span, first_line: 0, last_line: 0 });
        }
        for child in expression.children() {
            visit(child, sites, ids);
        }
    }
    let mut sites: Vec<CallSite> = Vec::new();
    let mut ids: HashMap<*const BoundExpression, usize> = HashMap::new();
    for expression in statement.expressions() {
        visit(expression, &mut sites, &mut ids);
    }
    (sites, ids)
}

impl TracePointKind {
    /// The variables its entries hold, in order (none for a plain value).
    pub fn variables(&self) -> &[VariableRef] {
        match self {
            TracePointKind::Writes { variables } | TracePointKind::Loop { variables } => variables,
            TracePointKind::Value { .. } => &[],
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

/// `is_nested`: inside a loop body, an if, a switch or braces, where a statement's value isn't the line's. A line
/// writing several variables (`float a = 1, b = 2`, `a = b = 3`) is traced too: its value alone shows only one.
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
            // `float a, b;` is a block of declarations, without braces
            let is_several: bool = statements.len() > 1;
            for inner in statements {
                walk(inner, is_nested || *is_scope || is_several, loops, points, ids);
            }
        }
        BoundStatementKind::VariableDeclaration { variable, .. } if is_nested => {
            add(TracePointKind::Writes { variables: vec![variable.clone()] }, loops);
        }
        BoundStatementKind::Expression(expression) => {
            let mut variables: Vec<VariableRef> = Vec::new();
            written_variables(expression, &mut variables);
            let is_value: bool =
                !expression.ty.is_void() && !expression.ty.component_kinds().iter().any(|kind| kind.is_literal());
            if (is_nested && !variables.is_empty()) || variables.len() > 1 {
                add(TracePointKind::Writes { variables }, loops);
            } else if is_nested && is_value {
                add(TracePointKind::Value { ty: expression.ty.clone() }, loops);
            }
        }
        BoundStatementKind::Return(Some(value)) if is_nested => {
            add(TracePointKind::Value { ty: value.ty.clone() }, loops);
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

/// The variables an expression writes, in the order met: assignment and increment targets, out/inout arguments
/// (the whole variable for `v.x = ...`, `a[i] = ...`).
fn written_variables(expression: &BoundExpression, found: &mut Vec<VariableRef>) {
    fn root(target: &BoundExpression) -> Option<&VariableRef> {
        match &target.kind {
            BoundExpressionKind::Variable(variable) => Some(variable),
            BoundExpressionKind::Swizzle { operand, .. }
            | BoundExpressionKind::Index { operand, .. }
            | BoundExpressionKind::Field { operand, .. }
            | BoundExpressionKind::Conversion { operand, .. } => root(operand),
            _ => None,
        }
    }
    let mut add = |target: &BoundExpression| {
        if let Some(variable) = root(target)
            && !found.contains(variable)
        {
            found.push(variable.clone());
        }
    };
    match &expression.kind {
        BoundExpressionKind::Assignment { target, .. }
        | BoundExpressionKind::CompoundAssignment { target, .. }
        | BoundExpressionKind::Increment { target, .. } => add(target),
        BoundExpressionKind::Call { function, arguments, .. } => {
            for (parameter, argument) in function.parameters.iter().zip(arguments) {
                if parameter.parameter_mode() != ParameterMode::In {
                    add(argument);
                }
            }
        }
        BoundExpressionKind::IntrinsicCall { signature, arguments, .. } => {
            for (mode, argument) in signature.modes.iter().zip(arguments) {
                if *mode != ParameterMode::In {
                    add(argument);
                }
            }
        }
        _ => {}
    }
    for child in expression.children() {
        written_variables(child, found);
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
