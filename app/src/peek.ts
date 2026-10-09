import { Language } from "@codemirror/language";
import { highlightTree } from "@lezer/highlight";
import { hlslHighlight } from "./hlsl";
import { markOf, worstVerdict } from "./marks";
import { IterationChoices, TraceFocus, TraceIndex } from "./trace";
import { CallTrace, Reference, TracePoint, Verdict } from "./types";

export type CallPath = [number, number][];

export interface PeekHooks {
  /** Looks inside a call: the path from the worksheet line (site, run), level by level. */
  traceCall(path: CallPath): Promise<CallTrace | null>;
  /** The reference check of that call's body. */
  checkCall(path: CallPath): Promise<Reference | null>;
  documentText(name: string): string | null;
  language: Language;
  /** A row was picked: the Inspector shows it. */
  focus(focus: TraceFocus): void;
  /** The peek changed height. */
  resized(): void;
}

/** A call site on the line a peek hangs under. */
export interface PeekSite {
  site: number;
  name: string;
}

/**
 * A look inside one call, shown under the line that makes it: the function's code with that call's values beside
 * each line, its loops' steppers, and calls it makes opening their own peek underneath.
 */
export class Peek {
  readonly element: HTMLElement;
  /** The line it hangs under, in the code that makes the call (an editor line, or a line of the parent's function). */
  lineNumber: number;
  private readonly hooks: PeekHooks;
  private readonly parentPath: CallPath;
  private sites: PeekSite[];
  private site: number;
  /** Which run of the site the parent's chosen iterations select. */
  private readonly locate: (site: number) => number | null;
  private readonly onClose: () => void;
  private occurrence: number | null = null;
  private call: CallTrace | null = null;
  private index: TraceIndex | null = null;
  private reference: Reference | undefined;
  private message: string = "Looking inside…";
  private readonly choices: IterationChoices = new Map();
  private readonly children: Map<number, Peek> = new Map();
  private version: number = 0;

  constructor(hooks: PeekHooks, parentPath: CallPath, lineNumber: number, sites: PeekSite[], locate: (site: number) => number | null, onClose: () => void) {
    this.hooks = hooks;
    this.parentPath = parentPath;
    this.lineNumber = lineNumber;
    this.sites = sites;
    this.site = sites[0]?.site ?? 0;
    this.locate = locate;
    this.onClose = onClose;
    this.element = document.createElement("div");
    this.element.className = "peek";
    this.element.addEventListener("mousedown", (event: MouseEvent) => this.onMouseDown(event));
    this.render();
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
      this.render();
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
    this.render();
    if (call !== null) {
      for (const child of this.children.values()) {
        void child.refresh(this.sitesOn(child.lineNumber), true);
      }
      const reference: Reference | null = await this.hooks.checkCall(this.path).catch(() => null);
      if (version === this.version && reference !== null) {
        this.reference = reference;
        this.render();
      }
    }
  }

  private sitesOn(lineNumber: number): PeekSite[] {
    if (this.index === null) {
      return [];
    }
    return this.index.callSitesEndingOn(lineNumber).map((site: number) => ({ site, name: this.index!.trace.calls[site].function }));
  }

  // ---- Rendering ----

  private render(): void {
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
    const call: CallTrace | null = this.call;
    if (call === null) {
      header.append(element("span", "peek-title", this.sites.find((candidate: PeekSite) => candidate.site === this.site)?.name ?? ""));
      this.element.replaceChildren(header, element("div", "peek-message", this.message));
      this.hooks.resized();
      return;
    }
    const signature: HTMLElement = element("span", "peek-title");
    signature.append(element("b", "", call.function), "(");
    call.parameters.forEach((name: string, at: number) => {
      signature.append(`${at > 0 ? ", " : ""}${name} = `, element("span", "value", call.arguments[at]?.text ?? "?"));
    });
    signature.append(")");
    if (call.result !== null) {
      signature.append(" → ", element("span", "value", call.result.text));
    }
    header.append(signature, element("span", "peek-where", `${call.document}:${call.firstLine}`));

    const body: HTMLElement = element("div", "peek-body");
    const text: string = this.hooks.documentText(call.document) ?? "";
    const lines: HTMLElement[] = highlightLines(text, this.hooks.language);
    for (let lineNumber = call.firstLine; lineNumber <= call.lastLine; lineNumber++) {
      body.append(this.row(lineNumber, lines[lineNumber - 1] ?? element("span")));
      const child: Peek | undefined = this.children.get(lineNumber);
      if (child !== undefined) {
        body.append(child.element);
      }
    }
    if (call.trace.isTruncated) {
      body.append(element("div", "peek-message", "The call ran longer: only its first values are kept."));
    }
    this.element.replaceChildren(header, body);
    this.hooks.resized();
  }

  private row(lineNumber: number, code: HTMLElement): HTMLElement {
    const index: TraceIndex = this.index!;
    const row: HTMLElement = element("div", "peek-row");
    row.dataset.line = String(lineNumber);
    const result: HTMLElement = element("span", "peek-result");
    const loop: number | null = this.loopHeaderOn(lineNumber);
    const points: number[] = index.valuePointsEndingOn(lineNumber);
    const verdicts: (Verdict | undefined)[] = [];
    if (loop !== null) {
      const path: number[] | null = index.path(loop, this.choices);
      result.append(stepButton("◀", -1, loop));
      if (path === null) {
        result.append(element("span", "dim", "no iteration"));
      } else {
        const entry: number | null = index.entryAtPath(loop, path);
        const own: string = entry === null ? "" : index.parts(entry).map((part) => `${part.name} = ${part.value.text}`).join(", ");
        const count: number = index.iterationCount(loop, path.slice(0, -1));
        result.append(element("span", "value", `${own}${own ? " · " : ""}${path[path.length - 1] + 1}/${count}`));
      }
      result.append(stepButton("▶", 1, loop));
    }
    if (points.length > 0) {
      const entries: (number | null)[] = points.map((point: number) => index.entryAt(point, this.choices));
      const isRun: boolean = entries.some((entry: number | null) => entry !== null);
      result.append(element("span", isRun ? "value" : "dim", index.describe(points, entries)));
      entries.forEach((entry: number | null) => verdicts.push(entry === null ? undefined : this.reference?.trace[entry]?.verdict));
      row.dataset.focus = "1";
    }
    if (index.callSitesEndingOn(lineNumber).length > 0) {
      const toggle: HTMLElement = element("span", "peek-toggle", this.children.has(lineNumber) ? "⤴" : "⤵");
      toggle.title = this.children.has(lineNumber) ? "Close the look inside" : "Look inside this call";
      toggle.dataset.action = "toggle";
      result.append(toggle);
    }
    const worst: Verdict | undefined = worstVerdict(verdicts);
    const { mark, kind } = markOf(null, worst === undefined ? undefined : { verdict: worst }, points.length > 0 && this.reference !== undefined);
    row.append(element("span", "peek-number", String(lineNumber)), code, element("span", `mark ${kind}`, mark), result);
    return row;
  }

  /** The loop whose header is this line, if any. */
  private loopHeaderOn(lineNumber: number): number | null {
    const found: number = this.index!.trace.points.findIndex((point: TracePoint) => point.kind === "loop" && point.firstLine === lineNumber);
    return found < 0 ? null : found;
  }

  // ---- Interaction ----

  private onMouseDown(event: MouseEvent): void {
    const target: HTMLElement = event.target as HTMLElement;
    // A nested peek handles its own clicks
    if (target.closest(".peek") !== this.element) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const action: HTMLElement | null = target.closest<HTMLElement>("[data-action]");
    const row: HTMLElement | null = target.closest<HTMLElement>(".peek-row");
    const lineNumber: number = Number(row?.dataset.line ?? 0);
    switch (action?.dataset.action) {
      case "close":
        this.onClose();
        return;
      case "site":
        this.site = Number(action.dataset.site);
        void this.refresh(null, true);
        return;
      case "step":
        if (this.index?.step(Number(action.dataset.loop), Number(action.dataset.step), this.choices)) {
          this.changedIterations();
        }
        return;
      case "toggle":
        this.toggleChild(lineNumber);
        return;
    }
    if (row?.dataset.focus === "1") {
      this.focusLine(lineNumber);
    }
  }

  private changedIterations(): void {
    this.render();
    for (const child of this.children.values()) {
      void child.refresh(null, false);
    }
  }

  private toggleChild(lineNumber: number): void {
    const existing: Peek | undefined = this.children.get(lineNumber);
    if (existing !== undefined) {
      this.children.delete(lineNumber);
      this.render();
      return;
    }
    const sites: PeekSite[] = this.sitesOn(lineNumber);
    if (sites.length === 0 || this.index === null) {
      return;
    }
    const child: Peek = new Peek(
      this.hooks,
      this.path,
      lineNumber,
      sites,
      (site: number) => this.index?.occurrence(site, this.choices) ?? null,
      () => {
        this.children.delete(lineNumber);
        this.render();
      },
    );
    this.children.set(lineNumber, child);
    this.render();
    void child.refresh(null, true);
  }

  private focusLine(lineNumber: number): void {
    const index: TraceIndex | null = this.index;
    const call: CallTrace | null = this.call;
    if (index === null || call === null) {
      return;
    }
    const points: number[] = index.valuePointsEndingOn(lineNumber);
    const first: number = Math.min(...points.map((point: number) => index.trace.points[point].firstLine));
    const lines: string[] = (this.hooks.documentText(call.document) ?? "").split("\n");
    this.hooks.focus({
      index,
      points,
      isLoop: false,
      entries: points.map((point: number) => index.entryAt(point, this.choices)),
      source: lines.slice(first - 1, lineNumber).join("\n"),
      reference: this.reference,
      choose: (entry: number) => {
        index.choose(entry, this.choices);
        this.changedIterations();
        this.focusLine(lineNumber);
      },
    });
  }
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

function stepButton(symbol: string, delta: number, loop: number): HTMLElement {
  const button: HTMLElement = element("span", "step", symbol);
  button.dataset.action = "step";
  button.dataset.step = String(delta);
  button.dataset.loop = String(loop);
  button.title = delta < 0 ? "Previous iteration" : "Next iteration";
  return button;
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
