import { autocompletion, closeBrackets, closeBracketsKeymap, CompletionContext, CompletionResult, completionKeymap, snippet } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { bracketMatching, indentOnInput, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { Diagnostic as LintDiagnostic, lintKeymap, setDiagnostics } from "@codemirror/lint";
import { highlightSelectionMatches, searchKeymap } from "@codemirror/search";
import { Range, EditorState, Extension, StateEffect, StateField } from "@codemirror/state";
import {
  Decoration,
  DecorationSet,
  drawSelection,
  EditorView,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
  ViewUpdate,
  WidgetType,
} from "@codemirror/view";
import { StreamLanguage } from "@codemirror/language";
import { hlslHighlight, hlslLanguage } from "./hlsl";
import { markOf, worstVerdict } from "./marks";
import { CallPath, Peek, PeekCell, PeekHooks, PeekSite } from "./peek";
import { IterationChoices, TraceFocus, TraceIndex } from "./trace";
import { CallTrace, Diagnostic, Line, Reference, Severity, TracePoint, Verdict } from "./types";

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
  /** A traced line's cell: its statements' entries at the chosen iterations, and a loop's stepper. */
  trace?: { index: TraceIndex; points: number[]; entries: (number | null)[]; loop: number | null };
  /** The line calls worksheet functions: it can open a peek. */
  hasCalls?: boolean;
  isDim?: boolean;
}

/** Peeks under their lines, as block widgets. */
const setPeeks = StateEffect.define<DecorationSet>();
const peekField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update: (peeks: DecorationSet, transaction) => {
    let updated: DecorationSet = peeks.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (effect.is(setPeeks)) {
        updated = effect.value;
      }
    }
    return updated;
  },
  provide: (field) => EditorView.decorations.from(field),
});

/** A peek's element; a new drawing makes it unequal, so the editor measures its height again. */
class PeekWidget extends WidgetType {
  readonly peek: Peek;
  readonly drawing: number;

  constructor(peek: Peek) {
    super();
    this.peek = peek;
    this.drawing = peek.drawing;
  }

  eq(other: PeekWidget): boolean {
    return other.peek === this.peek && other.drawing === this.drawing;
  }

  updateDOM(dom: HTMLElement): boolean {
    return dom === this.peek.element;
  }

  toDOM(): HTMLElement {
    return this.peek.element;
  }

  ignoreEvent(): boolean {
    return true;
  }
}

interface CellPosition {
  number: number;
  top: number;
  height: number;
  /** A line of a peek (its cell, by index in `peekCells`). */
  peekCell?: number;
}

export interface WorksheetHooks {
  onEdit(view: WorksheetView): void;
  onSelect(view: WorksheetView): void;
  completions(): CompletionEntry[];
  /** Looks inside a call a line makes (see `Peek`). */
  traceCall(line: Line, path: CallPath): Promise<CallTrace | null>;
  checkCall(line: Line, path: CallPath): Promise<Reference | null>;
  traceCallRuns(line: Line, path: CallPath, points: number[]): Promise<CallTrace[] | null>;
  checkCallRuns(line: Line, path: CallPath, points: number[]): Promise<Reference | null>;
  documentText(name: string): string | null;
  intrinsics: string[];
  resultWidth: number;
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
  /** Open looks inside calls, under their lines. */
  private peeks: Peek[] = [];
  /** A peek row picked for the Inspector (until the caret moves). */
  private peekFocus: TraceFocus | null = null;
  /** The peeks' cells the result column shows. */
  private peekCells: PeekCell[] = [];
  private isPeekSyncPending: boolean = false;
  private language: StreamLanguage<unknown> | null = null;
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
    this.language = hlslLanguage(this.hooks.intrinsics);
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
      this.language,
      syntaxHighlighting(hlslHighlight),
      peekField,
      autocompletion({ override: [(context: CompletionContext) => this.complete(context)], activateOnTyping: true }),
      keymap.of([
        { key: "Alt-ArrowLeft", run: () => this.stepAtCaret(-1) },
        { key: "Alt-ArrowRight", run: () => this.stepAtCaret(1) },
        { key: "F11", run: () => this.togglePeek(this.caretLine()) },
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
    this.traces = lines.flatMap((line: Line) => (line.trace !== null ? [new TraceIndex(line.trace, `${line.document}:${line.firstLine}`, line)] : []));
    this.buildTraceCells();
    this.refreshPeeks(true);

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
      // Statements: the worst of their entries' verdicts; a loop alone: the whole line's
      if (cell.trace.points.length === 0) {
        return cell.trace.loop === null ? { mark: "", kind: "dim" } : markOf(cell.problem, reference, true);
      }
      const verdict: Verdict | undefined = worstVerdict(
        cell.trace.entries.map((entry: number | null) => (entry === null ? undefined : reference?.trace[entry]?.verdict)),
      );
      return markOf(cell.problem, verdict === undefined ? undefined : { verdict }, cell.trace.entries.some((entry: number | null) => entry !== null));
    }
    return markOf(cell.problem, reference, cell.line?.value != null);
  }

  // ---- Traces (values inside a line's loops, ifs and blocks; calls to look inside) ----

  /** The line cells plus, per traced line, its statements' values at the chosen iterations and its loops' steppers. */
  private buildTraceCells(): void {
    const cells: Map<number, Cell> = new Map(this.lineCells);
    for (const index of this.traces) {
      const numbers: Set<number> = new Set();
      index.trace.points.forEach((point: TracePoint) => numbers.add(point.kind === "loop" ? point.firstLine : point.lastLine));
      index.trace.calls.forEach((site) => numbers.add(site.lastLine));
      for (const number of numbers) {
        const existing: Cell | undefined = cells.get(number);
        const cell: Cell = this.traceCell(index, number);
        if (existing?.isProblemText) {
          continue;
        }
        if (existing !== undefined && cell.trace!.points.length === 0 && cell.trace!.loop === null) {
          // Only calls on a line that shows its own value: keep it, with the peek toggle
          existing.hasCalls = true;
          continue;
        }
        // `a = b = 3`: the line's statements replace its single value
        cells.set(number, cell);
      }
    }
    this.cells = cells;
  }

  /** A traced line's cell: its loop's stepper, its statements' values, its calls' toggle. */
  private traceCell(index: TraceIndex, number: number): Cell {
    const loopIndex: number = index.trace.points.findIndex((point: TracePoint) => point.kind === "loop" && point.firstLine === number);
    const loop: number | null = loopIndex < 0 ? null : loopIndex;
    const points: number[] = index.valuePointsEndingOn(number);
    const entries: (number | null)[] = points.map((point: number) => index.entryAt(point, this.choices));
    const texts: string[] = [];
    const details: string[] = [];
    let isDim: boolean = false;
    if (loop !== null) {
      const path: number[] | null = index.path(loop, this.choices);
      if (path === null) {
        texts.push("no iteration");
        details.push("The loop didn't run");
        isDim = points.length === 0;
      } else {
        const entry: number | null = index.entryAtPath(loop, path);
        const own: string = entry === null ? "" : index.parts(entry).map((part) => `${part.name} = ${part.value.text}`).join(", ");
        const count: number = index.iterationCount(loop, path.slice(0, -1));
        const all: string = index.loopVariables(index.loopsOf(loop), path);
        texts.push(`${own}${own ? " · " : ""}${path[path.length - 1] + 1}/${count}`);
        details.push(`Iteration ${path[path.length - 1] + 1} of ${count}${all ? `: ${all}` : ""}\n◀ ▶, or Alt+← / Alt+→ with the caret in the loop, pick another`);
      }
    }
    if (points.length > 0) {
      texts.push(index.describe(points, entries));
      const path: number[] | null = index.path(points[0], this.choices);
      if (entries.every((entry: number | null) => entry === null)) {
        isDim = loop === null;
        details.push("Not run in this iteration");
      } else if (path !== null && path.length > 0) {
        details.push(index.loopVariables(index.trace.points[points[0]].loops, path));
      }
    }
    const hasCalls: boolean = index.callSitesEndingOn(number).length > 0;
    if (hasCalls) {
      details.push("⤵ or F11: look inside the call");
    }
    if (index.trace.isTruncated) {
      details.push("(only the first values the loops computed are kept)");
    }
    return {
      text: texts.join("  ·  "),
      isProblemText: false,
      problem: null,
      details: details.join("\n") || null,
      line: index.line,
      trace: { index, points, entries, loop },
      hasCalls,
      isDim,
    };
  }

  /** After the chosen iterations changed: the cells, the peeks and the Inspector follow. */
  private refreshTrace(): void {
    this.buildTraceCells();
    this.scheduleLayout();
    this.refreshPeeks(false);
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

  /** The Inspector's subject in traced code: a peek row picked, else the statements of the caret's line, else the loop around it. */
  traceFocus(): TraceFocus | null {
    if (this.peekFocus !== null) {
      return this.peekFocus;
    }
    const lineNumber: number = this.caretLine();
    for (const index of this.traces) {
      const line: Line = index.line!;
      if (lineNumber < line.firstLine || lineNumber > line.line) {
        continue;
      }
      const reference: Reference | undefined = this.references.get(line.index);
      const choose = (entry: number): void => {
        index.choose(entry, this.choices);
        this.refreshTrace();
      };
      const doc = this.view.state.doc;
      const source = (first: number, last: number): string =>
        doc.sliceString(doc.line(Math.min(first, doc.lines)).from, doc.line(Math.min(last, doc.lines)).to);
      const points: number[] = index.valuePointsAround(lineNumber);
      if (points.length > 0) {
        const first: number = Math.min(...points.map((point: number) => index.trace.points[point].firstLine));
        const last: number = index.trace.points[points[0]].lastLine;
        const entries: (number | null)[] = points.map((point: number) => index.entryAt(point, this.choices));
        return { index, points, isLoop: false, entries, source: source(first, last), reference, choose };
      }
      const loop: number | null = index.loopAt(lineNumber);
      if (loop !== null) {
        const header: number = index.trace.points[loop].firstLine;
        return { index, points: [loop], isLoop: true, entries: [index.entryAt(loop, this.choices)], source: source(header, header), reference, choose };
      }
      return null;
    }
    return null;
  }

  // ---- Peeks (a look inside a call, under its line) ----

  /** The trace of the line a call line belongs to, when it calls a worksheet function there. */
  private traceWithCallsOn(lineNumber: number): TraceIndex | null {
    return (
      this.traces.find(
        (index: TraceIndex) => index.line !== null && index.line.firstLine <= lineNumber && lineNumber <= index.line.line && index.callSitesEndingOn(lineNumber).length > 0,
      ) ?? null
    );
  }

  private sitesOn(lineNumber: number): PeekSite[] {
    const index: TraceIndex | null = this.traceWithCallsOn(lineNumber);
    return index === null ? [] : index.callSitesEndingOn(lineNumber).map((site: number) => ({ site, name: index.trace.calls[site].function }));
  }

  /** F11 or ⤵: opens (or closes) a look inside the calls of a line. */
  togglePeek(lineNumber: number): boolean {
    const existing: Peek | undefined = this.peeks.find((peek: Peek) => peek.lineNumber === lineNumber);
    if (existing !== undefined) {
      this.closePeek(existing);
      return true;
    }
    const sites: PeekSite[] = this.sitesOn(lineNumber);
    if (sites.length === 0) {
      return false;
    }
    let peek: Peek | null = null;
    const line = (): Line | null => (peek === null ? null : (this.traceWithCallsOn(peek.lineNumber)?.line ?? null));
    const hooks: PeekHooks = {
      traceCall: (path: CallPath) => {
        const owner: Line | null = line();
        return owner === null ? Promise.resolve(null) : this.hooks.traceCall(owner, path);
      },
      checkCall: (path: CallPath) => {
        const owner: Line | null = line();
        return owner === null ? Promise.resolve(null) : this.hooks.checkCall(owner, path);
      },
      traceCallRuns: (path: CallPath, points: number[]) => {
        const owner: Line | null = line();
        return owner === null ? Promise.resolve(null) : this.hooks.traceCallRuns(owner, path, points);
      },
      checkCallRuns: (path: CallPath, points: number[]) => {
        const owner: Line | null = line();
        return owner === null ? Promise.resolve(null) : this.hooks.checkCallRuns(owner, path, points);
      },
      documentText: (name: string) => (name === this.name ? this.text : this.hooks.documentText(name)),
      language: this.language!,
      focus: (focus: TraceFocus) => {
        this.peekFocus = focus;
        this.hooks.onSelect(this);
      },
      changed: () => this.peekChanged(),
    };
    peek = new Peek(
      hooks,
      [],
      lineNumber,
      sites,
      (site: number) => (peek === null ? null : (this.traceWithCallsOn(peek.lineNumber)?.occurrence(site, this.choices) ?? null)),
      (site: number) => {
        const index: TraceIndex | null = peek === null ? null : this.traceWithCallsOn(peek.lineNumber);
        return {
          runs: index?.siteRuns(site) ?? [],
          choose: (run: number) => {
            index?.chooseRun(site, run, this.choices);
            this.refreshTrace();
          },
        };
      },
      () => this.closePeek(peek!),
    );
    this.peeks.push(peek);
    this.syncPeeks();
    this.buildTraceCells();
    this.scheduleLayout();
    void peek.refresh(null, true);
    return true;
  }

  private closePeek(peek: Peek): void {
    this.peeks = this.peeks.filter((candidate: Peek) => candidate !== peek);
    if (this.peekFocus !== null) {
      this.peekFocus = null;
      this.hooks.onSelect(this);
    }
    this.syncPeeks();
    this.scheduleLayout();
  }

  /** A peek was drawn again: its widget is replaced (outside any editor update), so its height is measured. */
  private peekChanged(): void {
    if (this.isPeekSyncPending) {
      return;
    }
    this.isPeekSyncPending = true;
    queueMicrotask(() => {
      this.isPeekSyncPending = false;
      this.syncPeeks();
      this.scheduleLayout();
    });
  }

  /** The peeks as block widgets under their lines. */
  private syncPeeks(): void {
    const doc = this.view.state.doc;
    const ranges: Range<Decoration>[] = this.peeks
      .filter((peek: Peek) => peek.lineNumber <= doc.lines)
      .sort((left: Peek, right: Peek) => left.lineNumber - right.lineNumber)
      .map((peek: Peek) => Decoration.widget({ widget: new PeekWidget(peek), block: true, side: 1 }).range(doc.line(peek.lineNumber).to));
    this.view.dispatch({ effects: setPeeks.of(Decoration.set(ranges)) });
  }

  /** A new evaluation (`isForced`), or other iterations chosen: each peek follows its call, or closes when the line no longer calls. */
  private refreshPeeks(isForced: boolean): void {
    let isClosing: boolean = false;
    for (const peek of [...this.peeks]) {
      const sites: PeekSite[] = this.sitesOn(peek.lineNumber);
      if (sites.length === 0) {
        this.peeks = this.peeks.filter((candidate: Peek) => candidate !== peek);
        isClosing = true;
        continue;
      }
      void peek.refresh(sites, isForced);
    }
    if (isClosing) {
      this.syncPeeks();
    }
  }

  /** After an edit: the lines the peeks hang under, as the widgets moved. */
  private followPeekLines(): void {
    const doc = this.view.state.doc;
    this.view.state.field(peekField).between(0, doc.length, (from: number, _to: number, decoration: Decoration) => {
      const widget: PeekWidget | undefined = decoration.spec.widget as PeekWidget | undefined;
      if (widget !== undefined) {
        widget.peek.lineNumber = doc.lineAt(from).number;
      }
    });
  }

  /** "lighting.hlsl:5 D_GGX(…) → …" for every open peek (tests read it). */
  peeksText(): string[] {
    return this.peeks.map((peek: Peek) => peek.element.innerText);
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
    if (update.docChanged) {
      this.followPeekLines();
    }
    // The caret moved: the Inspector leaves the peek row it showed
    const leavesPeek: boolean = update.selectionSet && this.peekFocus !== null;
    if (leavesPeek) {
      this.peekFocus = null;
    }
    if (update.selectionSet || update.docChanged) {
      this.selectLine(this.caretLine(), leavesPeek);
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
        // The peeks' lines, where their rows are drawn
        const resultsTop: number = this.results.getBoundingClientRect().top;
        this.peekCells = this.peeks.flatMap((peek: Peek) => (peek.element.isConnected ? peek.cells() : []));
        this.peekCells.forEach((cell: PeekCell, index: number) => {
          const box: DOMRect = cell.row.getBoundingClientRect();
          if (box.height > 0) {
            positions.push({ number: cell.line, top: box.top - resultsTop, height: box.height, peekCell: index });
          }
        });
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
      if (position.peekCell !== undefined) {
        this.renderPeekCell(row, position.peekCell);
        fragment.append(row);
        continue;
      }
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
        if (cell.hasCalls) {
          const isOpen: boolean = this.peeks.some((peek: Peek) => peek.lineNumber === position.number);
          const toggle: HTMLElement = document.createElement("span");
          toggle.className = "peek-toggle";
          toggle.textContent = isOpen ? "⤴" : "⤵";
          toggle.title = isOpen ? "Close the look inside (F11)" : "Look inside the call (F11)";
          row.append(toggle);
        }
        row.title = this.describe(cell);
        row.dataset.line = String(position.number);
      }
      fragment.append(row);
    }
    this.results.replaceChildren(fragment);
  }

  /** A peek line's values in the result column: mark, its loop's stepper, values, its calls' ⤵. */
  private renderPeekCell(row: HTMLElement, index: number): void {
    const cell: PeekCell = this.peekCells[index];
    row.classList.add("peek-cell");
    row.dataset.peekCell = String(index);
    row.title = cell.title;
    const { mark, kind } = markOf(null, cell.verdict === undefined ? undefined : { verdict: cell.verdict }, cell.hasValue);
    const markElement: HTMLElement = document.createElement("span");
    markElement.className = `mark ${kind}`;
    markElement.textContent = mark;
    const text: HTMLElement = document.createElement("span");
    text.className = cell.isDim ? "text dim" : "text value";
    text.textContent = cell.text;
    const button = (className: string, symbol: string, title: string, step: number = 0): HTMLElement => {
      const element: HTMLElement = document.createElement("span");
      element.className = className;
      element.textContent = symbol;
      element.title = title;
      element.dataset.step = String(step);
      return element;
    };
    if (cell.loop !== null) {
      row.append(markElement, button("step", "◀", "Previous iteration", -1), text, button("step", "▶", "Next iteration", 1));
    } else {
      row.append(markElement, text);
    }
    if (cell.hasCalls) {
      row.append(button("peek-toggle", cell.isOpen ? "⤴" : "⤵", cell.isOpen ? "Close the look inside" : "Look inside the call"));
    }
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
    // A peek line's row: its stepper, its ⤵, else its Inspector page
    const peekRow: HTMLElement | null = (event.target as HTMLElement).closest<HTMLElement>(".result-row.peek-cell");
    if (peekRow !== null) {
      event.preventDefault();
      const cell: PeekCell | undefined = this.peekCells[Number(peekRow.dataset.peekCell)];
      const control: HTMLElement | null = (event.target as HTMLElement).closest<HTMLElement>(".step, .peek-toggle");
      if (cell === undefined) {
        return;
      }
      if (control?.classList.contains("step") && cell.loop !== null) {
        cell.peek.step(cell.loop, Number(control.dataset.step));
      } else if (control?.classList.contains("peek-toggle")) {
        cell.peek.toggle(cell.line);
      } else if (cell.hasValue) {
        cell.peek.showInInspector(cell.line);
      }
      return;
    }
    const step: HTMLElement | null = (event.target as HTMLElement).closest<HTMLElement>(".step");
    const stepRow: HTMLElement | null | undefined = step?.closest<HTMLElement>(".result-row");
    if (step !== null && stepRow?.dataset.line !== undefined) {
      event.preventDefault();
      this.stepLoopAtLine(Number(stepRow.dataset.line), Number(step.dataset.step));
      return;
    }
    const toggleRow: HTMLElement | null | undefined = (event.target as HTMLElement).closest(".peek-toggle")?.closest<HTMLElement>(".result-row");
    if (toggleRow?.dataset.line !== undefined) {
      event.preventDefault();
      this.togglePeek(Number(toggleRow.dataset.line));
      this.scheduleLayout();
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
