import { autocompletion, closeBrackets, closeBracketsKeymap, CompletionContext, CompletionResult, completionKeymap, snippet } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { bracketMatching, indentOnInput, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { Diagnostic as LintDiagnostic, lintKeymap, setDiagnostics } from "@codemirror/lint";
import { highlightSelectionMatches, searchKeymap } from "@codemirror/search";
import { EditorState, Extension } from "@codemirror/state";
import {
  drawSelection,
  EditorView,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
  ViewUpdate,
} from "@codemirror/view";
import { hlslHighlight, hlslLanguage } from "./hlsl";
import { IterationChoices, TraceIndex } from "./trace";
import { Diagnostic, Line, Reference, Severity, TracePoint, Verdict } from "./types";

/** A name the editor can complete: what it is (function, type...) and a description for the tooltip. */
export interface CompletionEntry {
  name: string;
  kind: string;
  detail: string;
}

/** What one line shows on the right: its value (or problem) and the reference mark. */
interface Cell {
  text: string;
  isProblemText: boolean;
  problem: Severity | null;
  details: string | null;
  line: Line | null;
  /** Inside a traced line: the entry shown (null: didn't run in the chosen iteration), or a loop's stepper. */
  trace?: { index: TraceIndex; entry: number | null; loop: number | null };
  isDim?: boolean;
}

/** The trace point under the caret: what the Inspector lists. */
export interface TraceFocus {
  index: TraceIndex;
  point: number;
  /** The entry the chosen iterations select. */
  entry: number | null;
  source: string;
}

interface CellPosition {
  number: number;
  top: number;
  height: number;
}

export interface WorksheetHooks {
  onEdit(view: WorksheetView): void;
  onSelect(view: WorksheetView): void;
  completions(): CompletionEntry[];
  intrinsics: string[];
  resultWidth: number;
}

/** The mark in front of a result: problems first, then the reference verdict. */
export function markOf(problem: Severity | null, reference: { verdict: Verdict } | undefined, hasValue: boolean): { mark: string; kind: string } {
  if (problem === "error") {
    return { mark: "✗", kind: "error" };
  }
  if (reference?.verdict === "mismatch") {
    return { mark: "≠", kind: "error" };
  }
  if (problem === "warning") {
    return { mark: "⚠", kind: "warning" };
  }
  switch (reference?.verdict) {
    case "match":
      return { mark: "✓", kind: "match" };
    case "withinTolerance":
      return { mark: "≈", kind: "approximate" };
    case "warpLimit":
      return { mark: "⊘", kind: "warning" };
    case "notChecked":
      return { mark: "–", kind: "dim" };
  }
  return hasValue ? { mark: "…", kind: "dim" } : { mark: "", kind: "dim" };
}

function worstProblem(diagnostics: Diagnostic[]): Diagnostic | null {
  const rank = (severity: Severity): number => (severity === "error" ? 2 : severity === "warning" ? 1 : 0);
  let worst: Diagnostic | null = null;
  for (const diagnostic of diagnostics) {
    if (diagnostic.severity !== "info" && (worst === null || rank(diagnostic.severity) > rank(worst.severity))) {
      worst = diagnostic;
    }
  }
  return worst;
}

function isWordCharacter(character: string): boolean {
  return /[\p{L}\p{N}_]/u.test(character);
}

/**
 * One worksheet tab: the code editor and, on its right, the result column aligned with the editor's lines.
 * Problems are underlined (hover for the message); the line under the caret, or a clicked result, is the
 * selected line.
 */
export class WorksheetView {
  name: string;
  readonly element: HTMLElement;
  readonly view: EditorView;
  /** The worksheet line under the caret (or clicked), if it has a result. */
  selectedLine: Line | null = null;

  private readonly hooks: WorksheetHooks;
  private readonly results: HTMLElement;
  private readonly splitter: HTMLElement;
  private cells: Map<number, Cell> = new Map();
  /** The cells of the lines themselves, before the traces' are added. */
  private lineCells: Map<number, Cell> = new Map();
  private traces: TraceIndex[] = [];
  private readonly choices: IterationChoices = new Map();
  private lines: Line[] = [];
  private references: Map<number, Reference> = new Map();
  private positions: CellPosition[] = [];
  private selectedNumber: number = 0;
  private caretLineShown: number = 0;
  private isReplacingText: boolean = false;
  private isLayoutPending: boolean = false;

  constructor(name: string, text: string, hooks: WorksheetHooks) {
    this.name = name;
    this.hooks = hooks;
    this.element = document.createElement("div");
    this.element.className = "worksheet";
    this.element.style.setProperty("--result-width", `${hooks.resultWidth}px`);
    const editorHost: HTMLElement = document.createElement("div");
    editorHost.className = "editor-host";
    this.splitter = document.createElement("div");
    this.splitter.className = "splitter vertical";
    this.results = document.createElement("div");
    this.results.className = "results";
    this.results.setAttribute("aria-label", "Results");
    this.element.append(editorHost, this.splitter, this.results);

    this.view = new EditorView({
      parent: editorHost,
      state: EditorState.create({ doc: text, extensions: this.extensions() }),
    });
    this.view.contentDOM.setAttribute("aria-label", name);
    this.view.scrollDOM.addEventListener("scroll", () => this.scheduleLayout());
    this.results.addEventListener(
      "wheel",
      (event: WheelEvent) => {
        this.view.scrollDOM.scrollTop += event.deltaY;
        event.preventDefault();
      },
      { passive: false },
    );
    this.results.addEventListener("mousedown", (event: MouseEvent) => this.onResultsClick(event));
    this.makeSplitter();
  }

  private extensions(): Extension[] {
    return [
      lineNumbers(),
      highlightActiveLineGutter(),
      history(),
      drawSelection(),
      indentOnInput(),
      bracketMatching(),
      closeBrackets(),
      highlightActiveLine(),
      highlightSelectionMatches(),
      EditorState.tabSize.of(4),
      indentUnit.of("    "),
      hlslLanguage(this.hooks.intrinsics),
      syntaxHighlighting(hlslHighlight),
      autocompletion({ override: [(context: CompletionContext) => this.complete(context)], activateOnTyping: true }),
      keymap.of([
        { key: "Alt-ArrowLeft", run: () => this.stepAtCaret(-1) },
        { key: "Alt-ArrowRight", run: () => this.stepAtCaret(1) },
        ...closeBracketsKeymap,
        ...defaultKeymap,
        ...historyKeymap,
        ...searchKeymap,
        ...completionKeymap,
        ...lintKeymap,
        indentWithTab,
      ]),
      EditorView.updateListener.of((update: ViewUpdate) => this.onUpdate(update)),
    ];
  }

  get text(): string {
    return this.view.state.doc.toString();
  }

  get resultWidth(): number {
    return this.results.getBoundingClientRect().width;
  }

  focus(): void {
    this.view.focus();
  }

  referenceFor(line: Line): Reference | undefined {
    return this.references.get(line.index);
  }

  /** Text of a line's statement, for the inspector. */
  sourceOf(line: Line): string {
    const text: string = this.text;
    return text.slice(Math.min(line.from, text.length), Math.min(line.to, text.length));
  }

  /** Replaces the text after an outside edit, keeping the caret where it was. */
  replaceText(text: string): void {
    if (text === this.text) {
      return;
    }
    const caret: number = Math.min(this.view.state.selection.main.head, text.length);
    this.isReplacingText = true;
    this.view.dispatch({ changes: { from: 0, to: this.view.state.doc.length, insert: text }, selection: { anchor: caret } });
    this.isReplacingText = false;
  }

  /**
   * Types a CodeMirror snippet (Tab moves between its `${}` fields) for `name` where it reads as code: over the
   * selection or a partly typed `name`, at the caret inside an expression or on a blank line, else on a new line
   * after the caret's.
   */
  insertSnippet(template: string, name: string): void {
    let { from, to } = this.view.state.selection.main;
    const line = this.view.state.doc.lineAt(from);
    const before: string = line.text.slice(0, from - line.from);
    const word: string = /[\p{L}\p{N}_]*$/u.exec(before)?.[0] ?? "";
    const isInExpression: boolean = /[(\[{,=+\-*/%<>&|^!?:~]\s*$/.test(before);
    if (from === to && word !== "" && name.toLowerCase().startsWith(word.toLowerCase())) {
      from -= word.length;
    } else if (from === to && line.text.trim() !== "" && !isInExpression) {
      from = to = line.to;
      template = `\n${template}`;
    }
    snippet(template)(this.view, null, from, to);
    this.view.focus();
  }

  /** Puts the caret at an offset, scrolled to the middle. */
  goTo(offset: number): void {
    const position: number = Math.min(Math.max(offset, 0), this.view.state.doc.length);
    this.view.dispatch({ selection: { anchor: position }, effects: EditorView.scrollIntoView(position, { y: "center" }) });
    this.view.focus();
  }

  /** The identifier under the caret (for F1). */
  wordAtCaret(): string | null {
    const text: string = this.text;
    const offset: number = this.view.state.selection.main.head;
    let start: number = offset;
    while (start > 0 && isWordCharacter(text[start - 1])) {
      start--;
    }
    let end: number = offset;
    while (end < text.length && isWordCharacter(text[end])) {
      end++;
    }
    return end > start ? text.slice(start, end) : null;
  }

  applyEvaluation(lines: Line[], diagnostics: Diagnostic[]): void {
    this.lines = lines;
    this.references.clear();
    const cells: Map<number, Cell> = new Map();
    for (const line of lines) {
      const problem: Diagnostic | null = worstProblem(line.diagnostics);
      const details: string = line.diagnostics.map((diagnostic: Diagnostic) => diagnostic.text).join("\n");
      if (line.value !== null) {
        cells.set(line.line, { text: line.value.text, isProblemText: false, problem: problem?.severity ?? null, details: details || null, line });
      } else if (problem !== null) {
        cells.set(line.line, { text: problem.message, isProblemText: true, problem: problem.severity, details, line });
      }
    }
    // Problems outside any line (in a function, a struct...) still get a cell on their line
    for (const diagnostic of diagnostics) {
      if (diagnostic.severity !== "info" && diagnostic.line > 0 && !cells.has(diagnostic.line)) {
        cells.set(diagnostic.line, { text: diagnostic.message, isProblemText: true, problem: diagnostic.severity, details: diagnostic.text, line: null });
      }
    }
    this.lineCells = cells;
    this.traces = lines.flatMap((line: Line) => (line.trace !== null && line.trace.points.length > 0 ? [new TraceIndex(line, line.trace)] : []));
    this.buildTraceCells();

    const length: number = this.view.state.doc.length;
    const marks: LintDiagnostic[] = diagnostics
      .filter((diagnostic: Diagnostic) => diagnostic.severity !== "info")
      .map((diagnostic: Diagnostic) => {
        let from: number = Math.min(Math.max(diagnostic.from, 0), length);
        let to: number = Math.min(Math.max(diagnostic.to, from + 1), length);
        if (to <= from && from > 0) {
          from--;
          to = from + 1;
        }
        return {
          from,
          to,
          severity: diagnostic.severity === "error" ? "error" : "warning",
          message: diagnostic.function === null ? diagnostic.message : `${diagnostic.message} (in ${diagnostic.function})`,
        } as LintDiagnostic;
      });
    this.view.dispatch(setDiagnostics(this.view.state, marks));
    this.selectLine(this.caretLine(), true);
    this.scheduleLayout();
  }

  applyReference(index: number, reference: Reference): void {
    const line: Line | undefined = this.lines.find((candidate: Line) => candidate.index === index);
    if (line === undefined) {
      return;
    }
    this.references.set(index, reference);
    this.scheduleLayout();
    if (this.selectedLine?.index === index) {
      this.hooks.onSelect(this);
    }
  }

  /** "12 ✓ 9 J" for every cell, in line order: what the result column shows, as text (tests read it). */
  resultsText(): string {
    return [...this.cells.entries()]
      .sort((left: [number, Cell], right: [number, Cell]) => left[0] - right[0])
      .map(([number, cell]: [number, Cell]) => `${number} ${this.markFor(cell).mark} ${cell.text}`)
      .join("\n");
  }

  private markFor(cell: Cell): { mark: string; kind: string } {
    const reference: Reference | undefined = cell.line === null ? undefined : this.references.get(cell.line.index);
    if (cell.trace !== undefined) {
      // A loop's stepper carries the whole line's verdict; a value its own iteration's
      if (cell.trace.loop !== null) {
        return markOf(cell.problem, reference, true);
      }
      const verdict: Verdict | undefined = cell.trace.entry === null ? undefined : reference?.trace[cell.trace.entry]?.verdict;
      return markOf(cell.problem, verdict === undefined ? undefined : { verdict }, cell.trace.entry !== null);
    }
    return markOf(cell.problem, reference, cell.line?.value != null);
  }

  // ---- Traces (values inside a line's loops, ifs and blocks) ----

  /** The line cells plus each traced statement's value at the chosen iterations, and each loop's stepper. */
  private buildTraceCells(): void {
    const cells: Map<number, Cell> = new Map(this.lineCells);
    for (const index of this.traces) {
      index.trace.points.forEach((point: TracePoint, id: number) => {
        const cell: Cell = this.traceCell(index, point, id);
        const number: number = point.kind === "loop" ? point.firstLine : point.lastLine;
        const existing: Cell | undefined = cells.get(number);
        if (existing === undefined || existing.isProblemText) {
          cells.set(number, cell);
        } else if (cell.trace?.loop !== null && existing.trace?.loop === null) {
          // `for (...) x += i;` on one line: the stepper, then the value
          cells.set(number, { ...cell, text: `${cell.text}  ·  ${existing.text}` });
        } else {
          existing.text += `  ·  ${cell.text}`;
        }
      });
    }
    this.cells = cells;
  }

  private traceCell(index: TraceIndex, point: TracePoint, id: number): Cell {
    const entry: number | null = index.entryAt(id, this.choices);
    const path: number[] | null = index.path(id, this.choices);
    const truncated: string = index.trace.isTruncated ? "\n(only the first values the loops computed are kept)" : "";
    if (point.kind === "loop") {
      if (path === null) {
        return { text: "no iteration", isProblemText: false, problem: null, details: `The loop didn't run${truncated}`, line: index.line, trace: { index, entry, loop: id }, isDim: true };
      }
      const count: number = index.iterationCount(id, path.slice(0, -1));
      const own: string =
        entry === null ? "" : point.variables.map((name: string, at: number) => `${name} = ${index.trace.entries[entry].values[at]?.text ?? "?"}`).join(", ");
      const all: string = index.loopVariables([...point.loops, id], path);
      return {
        text: `${own}${own ? " · " : ""}${path[path.length - 1] + 1}/${count}`,
        isProblemText: false,
        problem: null,
        details: `Iteration ${path[path.length - 1] + 1} of ${count}${all ? `: ${all}` : ""}\n◀ ▶, or Alt+← / Alt+→ with the caret in the loop, pick another${truncated}`,
        line: index.line,
        trace: { index, entry, loop: id },
      };
    }
    if (entry === null) {
      const where: string = path === null ? "" : ` (${index.loopVariables(point.loops, path)})`;
      return { text: "–", isProblemText: false, problem: null, details: `Not run in this iteration${where}${truncated}`, line: index.line, trace: { index, entry, loop: null }, isDim: true };
    }
    const value: string = index.trace.entries[entry].values[0]?.text ?? "";
    const where: string = path === null || path.length === 0 ? "" : `\n${index.loopVariables(point.loops, path)}`;
    return { text: value, isProblemText: false, problem: null, details: `${value}${where}${truncated}`, line: index.line, trace: { index, entry, loop: null } };
  }

  /** After the chosen iterations changed: the cells and the Inspector follow. */
  private refreshTrace(): void {
    this.buildTraceCells();
    this.scheduleLayout();
    this.hooks.onSelect(this);
  }

  /** Alt+← / Alt+→: another iteration of the innermost loop around the caret. */
  private stepAtCaret(delta: number): boolean {
    const lineNumber: number = this.caretLine();
    for (const index of this.traces) {
      const loop: number | null = index.loopAt(lineNumber);
      if (loop !== null) {
        if (index.step(loop, delta, this.choices)) {
          this.refreshTrace();
        }
        return true;
      }
    }
    return false;
  }

  private stepLoopAtLine(lineNumber: number, delta: number): void {
    const cell: Cell | undefined = this.cells.get(lineNumber);
    if (cell?.trace?.loop != null && cell.trace.index.step(cell.trace.loop, delta, this.choices)) {
      this.refreshTrace();
    }
  }

  /** The traced statement on the caret's line, else the innermost loop around it. */
  traceFocus(): TraceFocus | null {
    const lineNumber: number = this.caretLine();
    for (const index of this.traces) {
      if (lineNumber < index.line.firstLine || lineNumber > index.line.line) {
        continue;
      }
      let point: number = index.trace.points.findIndex(
        (candidate: TracePoint) => candidate.kind === "value" && candidate.firstLine <= lineNumber && lineNumber <= candidate.lastLine,
      );
      if (point < 0) {
        point = index.loopAt(lineNumber) ?? -1;
      }
      if (point < 0) {
        return null;
      }
      const target: TracePoint = index.trace.points[point];
      const doc = this.view.state.doc;
      const last: number = Math.min(target.kind === "loop" ? target.firstLine : target.lastLine, doc.lines);
      const source: string = doc.sliceString(doc.line(Math.min(target.firstLine, doc.lines)).from, doc.line(last).to);
      return { index, point, entry: index.entryAt(point, this.choices), source };
    }
    return null;
  }

  /** The Inspector picked an entry: show its iterations everywhere. */
  chooseEntry(index: TraceIndex, entry: number): void {
    index.choose(entry, this.choices);
    this.refreshTrace();
  }

  private caretLine(): number {
    return this.view.state.doc.lineAt(this.view.state.selection.main.head).number;
  }

  private selectLine(number: number, force: boolean = false): void {
    const line: Line | null =
      this.lines.find((candidate: Line) => candidate.line === number) ??
      this.lines.find((candidate: Line) => candidate.firstLine <= number && number <= candidate.line) ??
      null;
    // In a traced line, the highlight follows the caret onto its statements' own results
    const selectedNumber: number = line?.trace != null && this.cells.get(number)?.trace !== undefined ? number : (line?.line ?? 0);
    if (selectedNumber !== this.selectedNumber) {
      this.selectedNumber = selectedNumber;
      this.scheduleLayout();
    }
    // Inside a traced line, each statement has its own Inspector page
    const movedInTrace: boolean = line?.trace != null && number !== this.caretLineShown;
    this.caretLineShown = number;
    if (force || line?.index !== this.selectedLine?.index || movedInTrace) {
      this.selectedLine = line;
      this.hooks.onSelect(this);
    }
  }

  private onUpdate(update: ViewUpdate): void {
    if (update.docChanged && !this.isReplacingText) {
      this.hooks.onEdit(this);
    }
    if (update.selectionSet || update.docChanged) {
      this.selectLine(this.caretLine());
    }
    if (update.docChanged || update.viewportChanged || update.geometryChanged || update.heightChanged) {
      this.scheduleLayout();
    }
  }

  // ---- Result column ----

  private scheduleLayout(): void {
    if (this.isLayoutPending) {
      return;
    }
    this.isLayoutPending = true;
    this.view.requestMeasure({
      read: (view: EditorView) => {
        const documentTop: number = view.documentTop - this.results.getBoundingClientRect().top;
        const lineHeight: number = view.defaultLineHeight;
        const lineCount: number = view.state.doc.lines;
        const first: number = view.state.doc.lineAt(view.viewport.from).number;
        const last: number = view.state.doc.lineAt(view.viewport.to).number;
        const positions: CellPosition[] = [];
        const numbers: Set<number> = new Set(this.cells.keys());
        if (this.selectedNumber > 0) {
          numbers.add(this.selectedNumber);
        }
        for (const number of numbers) {
          if (number < first || number > last || number > lineCount) {
            continue;
          }
          const block = view.lineBlockAt(view.state.doc.line(number).from);
          positions.push({ number, top: documentTop + block.top, height: Math.min(block.height, lineHeight) });
        }
        return positions;
      },
      write: (positions: CellPosition[]) => {
        this.isLayoutPending = false;
        this.positions = positions;
        this.renderResults();
      },
    });
  }

  private renderResults(): void {
    const fragment: DocumentFragment = document.createDocumentFragment();
    for (const position of this.positions) {
      const row: HTMLElement = document.createElement("div");
      row.className = "result-row";
      row.style.top = `${position.top}px`;
      row.style.height = `${position.height}px`;
      row.style.lineHeight = `${position.height}px`;
      if (position.number === this.selectedNumber) {
        row.classList.add("selected");
      }
      const cell: Cell | undefined = this.cells.get(position.number);
      if (cell !== undefined) {
        const { mark, kind } = this.markFor(cell);
        const markElement: HTMLElement = document.createElement("span");
        markElement.className = `mark ${kind}`;
        markElement.textContent = mark;
        const textElement: HTMLElement = document.createElement("span");
        textElement.className = cell.isProblemText ? `text ${cell.problem === "error" ? "error" : "warning"}` : cell.isDim ? "text dim" : "text value";
        textElement.textContent = cell.text;
        if (cell.trace?.loop != null) {
          const step = (symbol: string, delta: number, title: string): HTMLElement => {
            const button: HTMLElement = document.createElement("span");
            button.className = "step";
            button.dataset.step = String(delta);
            button.textContent = symbol;
            button.title = title;
            return button;
          };
          row.append(markElement, step("◀", -1, "Previous iteration (Alt+←)"), textElement, step("▶", 1, "Next iteration (Alt+→)"));
        } else {
          row.append(markElement, textElement);
        }
        row.title = this.describe(cell);
        row.dataset.line = String(position.number);
      }
      fragment.append(row);
    }
    this.results.replaceChildren(fragment);
  }

  private describe(cell: Cell): string {
    const parts: string[] = [cell.text];
    if (cell.details !== null) {
      parts.push(cell.details);
    }
    const reference: Reference | undefined = cell.line === null ? undefined : this.references.get(cell.line.index);
    if (reference !== undefined) {
      parts.push(reference.summary);
    }
    return parts.join("\n");
  }

  private onResultsClick(event: MouseEvent): void {
    const step: HTMLElement | null = (event.target as HTMLElement).closest<HTMLElement>(".step");
    const stepRow: HTMLElement | null | undefined = step?.closest<HTMLElement>(".result-row");
    if (step !== null && stepRow?.dataset.line !== undefined) {
      event.preventDefault();
      this.stepLoopAtLine(Number(stepRow.dataset.line), Number(step.dataset.step));
      return;
    }
    const top: number = this.results.getBoundingClientRect().top;
    const y: number = event.clientY - top;
    const position: CellPosition | undefined = this.positions.find(
      (candidate: CellPosition) => candidate.top <= y && y < candidate.top + candidate.height,
    );
    if (position === undefined) {
      return;
    }
    event.preventDefault();
    const line = this.view.state.doc.line(position.number);
    this.view.dispatch({ selection: { anchor: line.to }, scrollIntoView: true });
    this.view.focus();
  }

  private makeSplitter(): void {
    this.splitter.addEventListener("pointerdown", (event: PointerEvent) => {
      const startX: number = event.clientX;
      const startWidth: number = this.results.getBoundingClientRect().width;
      this.splitter.setPointerCapture(event.pointerId);
      const move = (moveEvent: PointerEvent): void => {
        const width: number = Math.max(80, startWidth - (moveEvent.clientX - startX));
        this.element.style.setProperty("--result-width", `${width}px`);
        this.scheduleLayout();
      };
      const up = (): void => {
        this.splitter.removeEventListener("pointermove", move);
        this.splitter.removeEventListener("pointerup", up);
      };
      this.splitter.addEventListener("pointermove", move);
      this.splitter.addEventListener("pointerup", up);
    });
  }

  // ---- Completion ----

  private complete(context: CompletionContext): CompletionResult | null {
    const word = context.matchBefore(/[\p{L}\p{N}_]+/u);
    const prefix: string = word?.text ?? "";
    const from: number = word?.from ?? context.pos;
    if ((!context.explicit && prefix.length < 2) || /^\p{N}/u.test(prefix)) {
      return null;
    }
    const seen: Set<string> = new Set();
    const options = [];
    for (const entry of this.hooks.completions()) {
      if (seen.has(entry.name) || !entry.name.toLowerCase().startsWith(prefix.toLowerCase())) {
        continue;
      }
      seen.add(entry.name);
      options.push({ label: entry.name, detail: entry.kind, info: entry.detail, type: completionType(entry.kind) });
    }
    if (options.length === 0 || (options.length === 1 && options[0].label === prefix)) {
      return null;
    }
    return { from, options, validFor: /^[\p{L}\p{N}_]*$/u };
  }
}

function completionType(kind: string): string {
  switch (kind) {
    case "intrinsic":
    case "function":
      return "function";
    case "keyword":
      return "keyword";
    case "type":
      return "type";
    case "unit":
      return "constant";
    default:
      return "variable";
  }
}
