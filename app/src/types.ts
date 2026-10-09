// The backend's JSON (crates/shadercalc-backend/src/dto.rs). Offsets are UTF-16, like JavaScript strings.

export interface DocumentText {
  name: string;
  text: string;
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

export interface Component {
  name: string;
  text: string;
  hex: string;
  bits: string;
  raw: string;
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
}

export interface Symbol {
  name: string;
  kind: string;
  detail: string;
}

export interface Evaluation {
  generation: number;
  durationMs: number;
  lines: Line[];
  diagnostics: Diagnostic[];
  symbols: Symbol[];
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
