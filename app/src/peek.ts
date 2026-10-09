import { Language } from "@codemirror/language";
import { highlightTree } from "@lezer/highlight";
import { hlslHighlight } from "./hlsl";
import { worstVerdict } from "./marks";
import { IterationChoices, SiteRun, TraceFocus, TraceIndex } from "./trace";
import { CallTrace, Reference, Trace, TraceCheck, TraceEntry, TracePoint, Verdict } from "./types";

export type CallPath = [number, number][];

export interface PeekHooks {
  /** Looks inside a call: the path from the worksheet line (site, run), level by level. */
  traceCall(path: CallPath): Promise<CallTrace | null>;
  /** The reference check of that call's body. */
  checkCall(path: CallPath): Promise<Reference | null>;
  /** Every run of the last call of `path` (its run doesn't matter), keeping some of its function's lines. */
  traceCallRuns(path: CallPath, points: number[]): Promise<CallTrace[] | null>;
  checkCallRuns(path: CallPath, points: number[]): Promise<Reference | null>;
  documentText(name: string): string | null;
  language: Language;
  /** A row was picked: the Inspector shows it. */
  focus(focus: TraceFocus): void;
  /** The peek was drawn again (its height or its values changed). */
  changed(): void;
}

/** The runs of a call site in the code making the call, and how to show one of them there. */
export interface SiteRuns {
  runs: SiteRun[];
  choose(run: number): void;
}

/** A call site on the line a peek hangs under. */
export interface PeekSite {
  site: number;
  name: string;
}

/** What the result column shows beside one line of a peek. */
export interface PeekCell {
  peek: Peek;
  /** The peek's row it lines up with. */
  row: HTMLElement;
  line: number;
  text: string;
  isDim: boolean;
  /** The worst verdict of its values (undefined: not checked yet, or nothing to check). */
  verdict: Verdict | undefined;
  hasValue: boolean;
  /** A loop's header: its stepper. */
  loop: number | null;
  hasCalls: boolean;
  isOpen: boolean;
  title: string;
}

/**
 * A look inside one call, shown under the line that makes it: the call's arguments and the function's code; the
 * result column shows each line's values for that call, its loops' steppers and ⤵ on the calls it makes, which
 * open their own peek underneath.
 */
export class Peek {
  readonly element: HTMLElement;
  private readonly box: HTMLElement;
  /** The line it hangs under, in the code that makes the call (an editor line, or a line of the parent's function). */
  lineNumber: number;
  /** Bumped at each drawing, so the editor measures it again. */
  drawing: number = 0;
  private readonly hooks: PeekHooks;
  private readonly parentPath: CallPath;
  private sites: PeekSite[];
  private site: number;
  /** Which run of the site the parent's chosen iterations select. */
  private readonly locate: (site: number) => number | null;
  /** The site's runs in the parent. */
  private readonly runsOf: (site: number) => SiteRuns;
  private focusToken: number = 0;
  private readonly onClose: () => void;
  private occurrence: number | null = null;
  private call: CallTrace | null = null;
  private index: TraceIndex | null = null;
  private reference: Reference | undefined;
  private message: string = "Looking inside…";
  private readonly choices: IterationChoices = new Map();
  private readonly children: Map<number, Peek> = new Map();
  private rowCells: PeekCell[] = [];
  private version: number = 0;

  constructor(
    hooks: PeekHooks,
    parentPath: CallPath,
    lineNumber: number,
    sites: PeekSite[],
    locate: (site: number) => number | null,
    runsOf: (site: number) => SiteRuns,
    onClose: () => void,
  ) {
    this.hooks = hooks;
    this.parentPath = parentPath;
    this.lineNumber = lineNumber;
    this.sites = sites;
    this.site = sites[0]?.site ?? 0;
    this.locate = locate;
    this.runsOf = runsOf;
    this.onClose = onClose;
    // The frame's padding spaces it out: margins would escape the editor's height measurement
    this.element = document.createElement("div");
    this.element.className = "peek-frame";
    this.box = document.createElement("div");
    this.box.className = "peek";
    this.element.append(this.box);
    this.box.addEventListener("mousedown", (event: MouseEvent) => this.onMouseDown(event));
    this.draw(false);
  }

  private get path(): CallPath {
    return this.occurrence === null ? [] : [...this.parentPath, [this.site, this.occurrence]];
  }

  /** The parent's iterations or code changed: the run to show may be another one. */
  async refresh(sites: PeekSite[] | null, isForced: boolean): Promise<void> {
    if (sites !== null) {
      this.sites = sites;
      if (!sites.some((candidate: PeekSite) => candidate.site === this.site)) {
        this.site = sites[0]?.site ?? 0;
      }
    }
    const occurrence: number | null = this.locate(this.site);
    if (!isForced && occurrence === this.occurrence && this.call !== null) {
      return;
    }
    this.occurrence = occurrence;
    const version: number = ++this.version;
    if (occurrence === null) {
      this.call = null;
      this.index = null;
      this.message = "Not called in the chosen iteration.";
      this.draw();
      return;
    }
    let call: CallTrace | null = null;
    this.message = "The worksheet changed: looking again…";
    try {
      call = await this.hooks.traceCall(this.path);
    } catch (error) {
      this.message = String((error as Error).message ?? error);
    }
    if (version !== this.version) {
      return;
    }
    this.call = call;
    this.index = call === null ? null : new TraceIndex(call.trace, `peek:${call.document}:${call.firstLine}`, null);
    this.reference = undefined;
    this.draw();
    if (call !== null) {
      for (const child of this.children.values()) {
        void child.refresh(this.sitesOn(child.lineNumber), true);
      }
      const reference: Reference | null = await this.hooks.checkCall(this.path).catch(() => null);
      if (version === this.version && reference !== null) {
        this.reference = reference;
        this.draw();
      }
    }
  }

  /** The result column's cells for this peek and the peeks opened inside it, in order down the page. */
  cells(): PeekCell[] {
    const cells: PeekCell[] = [];
    for (const cell of this.rowCells) {
      cells.push(cell);
      const child: Peek | undefined = this.children.get(cell.line);
      if (child !== undefined) {
        cells.push(...child.cells());
      }
    }
    return cells;
  }

  /** ◀ ▶ in the result column. */
  step(loop: number, delta: number): void {
    if (this.index?.step(loop, delta, this.choices)) {
      this.draw();
      for (const child of this.children.values()) {
        void child.refresh(null, false);
      }
    }
  }

  /** ⤵ in the result column: opens (or closes) a look inside the calls of one of its lines. */
  toggle(lineNumber: number): void {
    const existing: Peek | undefined = this.children.get(lineNumber);
    if (existing !== undefined) {
      this.children.delete(lineNumber);
      this.draw();
      return;
    }
    const sites: PeekSite[] = this.sitesOn(lineNumber);
    if (sites.length === 0 || this.index === null) {
      return;
    }
    const child: Peek = new Peek(
      { ...this.hooks, changed: () => this.draw() },
      this.path,
      lineNumber,
      sites,
      (site: number) => this.index?.occurrence(site, this.choices) ?? null,
      (site: number) => ({
        runs: this.index?.siteRuns(site) ?? [],
        choose: (run: number) => {
          this.index?.chooseRun(site, run, this.choices);
          this.draw();
          for (const other of this.children.values()) {
            void other.refresh(null, false);
          }
        },
      }),
      () => {
        this.children.delete(lineNumber);
        this.draw();
      },
    );
    this.children.set(lineNumber, child);
    this.draw();
    void child.refresh(null, true);
  }

  private sitesOn(lineNumber: number): PeekSite[] {
    const index: TraceIndex | null = this.index;
    return index === null ? [] : index.callSitesEndingOn(lineNumber).map((site: number) => ({ site, name: index.trace.calls[site].function }));
  }

  // ---- Drawing ----

  /** The header and the code (its rows' cells go to the result column); the editor is told unless `isTold` is false. */
  private draw(isTold: boolean = true): void {
    this.drawing++;
    const header: HTMLElement = element("div", "peek-header");
    const close: HTMLElement = element("span", "peek-close", "×");
    close.title = "Close (F11 on the call)";
    close.dataset.action = "close";
    header.append(close);
    if (this.sites.length > 1) {
      for (const candidate of this.sites) {
        const tab: HTMLElement = element("span", candidate.site === this.site ? "peek-site active" : "peek-site", candidate.name);
        tab.dataset.action = "site";
        tab.dataset.site = String(candidate.site);
        header.append(tab);
      }
    }
    this.rowCells = [];
    const call: CallTrace | null = this.call;
    if (call === null) {
      header.append(element("span", "peek-title", this.sites.find((candidate: PeekSite) => candidate.site === this.site)?.name ?? ""));
      this.box.replaceChildren(header, element("div", "peek-message", this.message));
    } else {
      const signature: HTMLElement = element("span", "peek-title");
      signature.append(element("b", "", call.function), "(");
      call.parameters.forEach((name: string, at: number) => {
        signature.append(`${at > 0 ? ", " : ""}${name} = `, element("span", "value", call.arguments[at]?.text ?? "?"));
      });
      signature.append(")");
      header.append(signature, element("span", "peek-where", `${call.document}:${call.firstLine}`));

      const body: HTMLElement = element("div", "peek-body");
      const lines: HTMLElement[] = highlightLines(this.hooks.documentText(call.document) ?? "", this.hooks.language);
      for (let lineNumber = call.firstLine; lineNumber <= call.lastLine; lineNumber++) {
        const row: HTMLElement = element("div", "peek-row");
        row.dataset.line = String(lineNumber);
        row.append(element("span", "peek-number", String(lineNumber)), lines[lineNumber - 1] ?? element("span"));
        const cell: PeekCell | null = this.cellFor(row, lineNumber);
        if (cell !== null) {
          this.rowCells.push(cell);
          if (cell.hasValue) {
            row.dataset.focus = "1";
          }
        }
        body.append(row);
        const child: Peek | undefined = this.children.get(lineNumber);
        if (child !== undefined) {
          body.append(child.element);
        }
      }
      if (call.trace.isTruncated) {
        body.append(element("div", "peek-message", "The call ran longer: only its first values are kept."));
      }
      this.box.replaceChildren(header, body);
    }
    if (isTold) {
      this.hooks.changed();
    }
  }

  /** What the result column shows beside a line of the function: its loop's stepper, its values, its calls' ⤵. */
  private cellFor(row: HTMLElement, lineNumber: number): PeekCell | null {
    const index: TraceIndex = this.index!;
    const loopIndex: number = index.trace.points.findIndex((point: TracePoint) => point.kind === "loop" && point.firstLine === lineNumber);
    const loop: number | null = loopIndex < 0 ? null : loopIndex;
    const points: number[] = index.valuePointsEndingOn(lineNumber);
    const hasCalls: boolean = index.callSitesEndingOn(lineNumber).length > 0;
    if (loop === null && points.length === 0 && !hasCalls) {
      return null;
    }
    const texts: string[] = [];
    let isDim: boolean = false;
    if (loop !== null) {
      const path: number[] | null = index.path(loop, this.choices);
      if (path === null) {
        texts.push("no iteration");
        isDim = points.length === 0;
      } else {
        const entry: number | null = index.entryAtPath(loop, path);
        const own: string = entry === null ? "" : index.parts(entry).map((part) => `${part.name} = ${part.value.text}`).join(", ");
        const count: number = index.iterationCount(loop, path.slice(0, -1));
        texts.push(`${own}${own ? " · " : ""}${path[path.length - 1] + 1}/${count}`);
      }
    }
    const entries: (number | null)[] = points.map((point: number) => index.entryAt(point, this.choices));
    if (points.length > 0) {
      texts.push(index.describe(points, entries));
      isDim = isDim || entries.every((entry: number | null) => entry === null);
    }
    const verdict: Verdict | undefined = worstVerdict(entries.map((entry: number | null) => (entry === null ? undefined : this.reference?.trace[entry]?.verdict)));
    return {
      peek: this,
      row,
      line: lineNumber,
      text: texts.join("  ·  "),
      isDim,
      verdict,
      hasValue: points.length > 0 && entries.some((entry: number | null) => entry !== null),
      loop,
      hasCalls,
      isOpen: this.children.has(lineNumber),
      title: `${this.call?.function ?? ""}, line ${lineNumber}: click the line for the Inspector`,
    };
  }

  // ---- Interaction ----

  private onMouseDown(event: MouseEvent): void {
    const target: HTMLElement = event.target as HTMLElement;
    // A nested peek handles its own clicks
    if (target.closest(".peek") !== this.box) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const action: HTMLElement | null = target.closest<HTMLElement>("[data-action]");
    const row: HTMLElement | null = target.closest<HTMLElement>(".peek-row");
    switch (action?.dataset.action) {
      case "close":
        this.onClose();
        return;
      case "site":
        this.site = Number(action.dataset.site);
        void this.refresh(null, true);
        return;
    }
    if (row?.dataset.focus === "1") {
      this.showInInspector(Number(row.dataset.line));
    }
  }

  /** A line of the function in the Inspector: its variables, every iteration; every call too when it runs in a loop. */
  showInInspector(lineNumber: number): void {
    const index: TraceIndex | null = this.index;
    const call: CallTrace | null = this.call;
    if (index === null || call === null) {
      return;
    }
    const points: number[] = index.valuePointsEndingOn(lineNumber);
    const first: number = Math.min(...points.map((point: number) => index.trace.points[point].firstLine));
    const lines: string[] = (this.hooks.documentText(call.document) ?? "").split("\n");
    const source: string = lines.slice(first - 1, lineNumber).join("\n");
    const token: number = ++this.focusToken;
    const runs: SiteRun[] = this.runsOf(this.site).runs;
    if (runs.length > 1) {
      void this.showRuns(points, source, runs, token);
    }
    this.hooks.focus({
      index,
      points,
      isLoop: false,
      entries: points.map((point: number) => index.entryAt(point, this.choices)),
      source,
      reference: this.reference,
      choose: (entry: number) => {
        index.choose(entry, this.choices);
        this.draw();
        for (const child of this.children.values()) {
          void child.refresh(null, false);
        }
        this.showInInspector(lineNumber);
      },
    });
  }

  /** The call runs in a loop: the line's values in every run, in the Inspector (once they're traced). */
  private async showRuns(points: number[], source: string, runs: SiteRun[], token: number): Promise<void> {
    const path: CallPath = [...this.parentPath, [this.site, 0]];
    const [traced, reference] = await Promise.all([
      this.hooks.traceCallRuns(path, points).catch(() => null),
      this.hooks.checkCallRuns(path, points).catch(() => null),
    ]);
    if (token !== this.focusToken || traced === null || traced.length === 0 || this.index === null) {
      return;
    }
    const merged: Trace = mergeRuns(traced, runs);
    const index: TraceIndex = new TraceIndex(merged, `runs:${this.call?.document}:${this.call?.firstLine}`, null);
    // The runs' own loop entries come first: no verdict of their own
    const placeholders: TraceCheck[] = traced.map(() => ({ verdict: "notChecked", values: [] }));
    const aligned: Reference | undefined = reference === null ? undefined : { ...reference, trace: [...placeholders, ...reference.trace] };
    const shown = (): (number | null)[] => {
      const run: number = this.occurrence ?? 0;
      return points.map((point: number) => index.entryAtPath(point + 1, [run, ...(this.index?.path(point, this.choices) ?? [])]));
    };
    const focus = (): void => {
      this.hooks.focus({
        index,
        points: points.map((point: number) => point + 1),
        isLoop: false,
        entries: shown(),
        source,
        reference: aligned,
        choose: (entry: number) => {
          const target: TraceEntry = merged.entries[entry];
          const own: TraceIndex | null = this.index;
          if (own !== null) {
            own.setPath(own.loopsOf(target.point - 1), target.iterations.slice(1), this.choices);
          }
          this.runsOf(this.site).choose(target.iterations[0]);
          this.occurrence = target.iterations[0];
          this.draw();
          focus();
        },
      });
    };
    focus();
  }
}

/**
 * Several runs of a call as one trace: a first loop (point 0) whose iterations are the runs, labelled with the
 * caller's loops ("i = 2"), around the function's points (shifted by one).
 */
function mergeRuns(traced: CallTrace[], runs: SiteRun[]): Trace {
  const base: Trace = traced[0].trace;
  const points: TracePoint[] = [
    { kind: "loop", firstLine: 0, lastLine: 0, loops: [], variables: [] },
    ...base.points.map((point: TracePoint) => ({ ...point, loops: [0, ...point.loops.map((loop: number) => loop + 1)] })),
  ];
  const entries: TraceEntry[] = traced.map((_call: CallTrace, run: number) => ({
    point: 0,
    iterations: [run],
    values: [{ text: runs[run]?.label ?? `call ${run + 1}`, ty: "", units: "", components: [] }],
  }));
  traced.forEach((call: CallTrace, run: number) => {
    for (const entry of call.trace.entries) {
      entries.push({ point: entry.point + 1, iterations: [run, ...entry.iterations], values: entry.values });
    }
  });
  return { points, entries, isTruncated: traced.some((call: CallTrace) => call.trace.isTruncated), calls: [], callEntries: [] };
}

function element(tag: string, className: string = "", text: string = ""): HTMLElement {
  const node: HTMLElement = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text) {
    node.textContent = text;
  }
  return node;
}

/** A document's lines as syntax-highlighted spans (the editor's colours). */
function highlightLines(text: string, language: Language): HTMLElement[] {
  const ranges: { from: number; to: number; classes: string }[] = [];
  try {
    highlightTree(language.parser.parse(text), hlslHighlight, (from: number, to: number, classes: string) => ranges.push({ from, to, classes }));
  } catch {
    // Plain text then
  }
  const lines: HTMLElement[] = [];
  let line: HTMLElement = element("span", "peek-code");
  let position: number = 0;
  let next: number = 0;
  const append = (end: number, classes: string): void => {
    const pieces: string[] = text.slice(position, end).split("\n");
    pieces.forEach((piece: string, at: number) => {
      if (at > 0) {
        lines.push(line);
        line = element("span", "peek-code");
      }
      if (piece) {
        line.append(classes ? element("span", classes, piece) : piece);
      }
    });
    position = end;
  };
  for (const range of ranges) {
    if (range.from < position) {
      continue;
    }
    append(range.from, "");
    append(range.to, range.classes);
    next = range.to;
  }
  append(Math.max(next, text.length), "");
  lines.push(line);
  return lines;
}
