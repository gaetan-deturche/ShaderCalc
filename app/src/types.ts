// The backend's JSON (crates/shadercalc-backend/src/dto.rs). Offsets are UTF-16, like JavaScript strings.

export interface DocumentText {
  name: string;
  text: string;
}

/** A newer release, from the updater. */
export interface UpdateInfo {
  version: string;
  current: string;
  notes: string;
}

export interface Profile {
  id: string;
  label: string;
  hasReference: boolean;
}

export interface LoadResult {
  folder: string;
  documents: DocumentText[];
  activeTab: string | null;
  sidePanelWidth: number;
  resultColumnWidth: number;
  profile: string;
  profiles: Profile[];
}

export type Severity = "error" | "warning" | "info";

export interface Diagnostic {
  severity: Severity;
  message: string;
  source: string;
  function: string | null;
  line: number;
  column: number;
  from: number;
  to: number;
  text: string;
}

/** A float's sign, exponent or mantissa: its top and bottom bit. */
export interface BitField {
  name: "sign" | "exponent" | "mantissa";
  high: number;
  low: number;
}

export interface Component {
  name: string;
  text: string;
  hex: string;
  bits: string;
  /** The bits as an unsigned decimal (up to 64 bits: read with BigInt). */
  raw: string;
  /** The bits the inspector draws (0 for bool). */
  width: number;
  fields: BitField[];
  exponentMeaning: string | null;
}

export interface ValueInfo {
  text: string;
  ty: string;
  units: string;
  components: Component[];
}

export interface Line {
  index: number;
  document: string;
  line: number;
  firstLine: number;
  from: number;
  to: number;
  value: ValueInfo | null;
  diagnostics: Diagnostic[];
  trace: Trace | null;
}

/**
 * A statement inside a line's loops/ifs/blocks ("value": the variables it writes, or its value when `variables` is
 * empty), or a loop with its variables.
 */
export interface TracePoint {
  kind: "value" | "loop";
  firstLine: number;
  lastLine: number;
  /** Enclosing loops (point indices), outermost first. */
  loops: number[];
  variables: string[];
}

/** A call to a worksheet function in the traced code. */
export interface CallSite {
  function: string;
  firstLine: number;
  lastLine: number;
}

/** A run of a call site; a site's n-th entry is its n-th run. */
export interface CallEntry {
  site: number;
  iterations: number[];
}

/** One call's look inside: its function (document and lines), arguments, result and body trace. */
export interface CallTrace {
  function: string;
  document: string;
  firstLine: number;
  lastLine: number;
  parameters: string[];
  arguments: ValueInfo[];
  result: ValueInfo | null;
  trace: Trace;
}

/** One execution of a point: its iteration in each enclosing loop (a loop's own entry: its iteration last). */
export interface TraceEntry {
  point: number;
  iterations: number[];
  values: ValueInfo[];
}

export interface Trace {
  points: TracePoint[];
  entries: TraceEntry[];
  isTruncated: boolean;
  calls: CallSite[];
  callEntries: CallEntry[];
}

/** The reference's verdict on a trace entry; its values only when they differ. */
export interface TraceCheck {
  verdict: Verdict;
  values: ValueInfo[];
}

export interface Symbol {
  name: string;
  kind: string;
  detail: string;
}

/** A library's declaration (the Library panel). `offset` is where its name is. */
export interface Export {
  document: string;
  kind: "function" | "struct" | "type" | "macro" | "variable";
  name: string;
  declaration: string;
  /** What a call takes (functions, function-like macros). */
  parameters: string[] | null;
  comment: string;
  line: number;
  offset: number;
}

export interface Evaluation {
  generation: number;
  durationMs: number;
  lines: Line[];
  diagnostics: Diagnostic[];
  symbols: Symbol[];
  exports: Export[];
}

export type Verdict = "match" | "withinTolerance" | "mismatch" | "warpLimit" | "notChecked";

export interface Reference {
  verdict: Verdict;
  summary: string;
  maxUlps: number;
  usesApproximations: boolean;
  message: string | null;
  hlsl: string;
  referenceValue: ValueInfo | null;
  timings: { compileMs: number; pipelineMs: number; runMs: number } | null;
  trace: TraceCheck[];
}

export interface DocEntry {
  name: string;
  group: string;
  signature: string | null;
  summary: string;
  markdown: string;
}

export interface OutsideChanges {
  changed: DocumentText[];
  added: DocumentText[];
  removed: string[];
}
