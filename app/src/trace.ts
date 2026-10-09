import { CallEntry, CallSite, Line, Reference, Trace, TraceEntry, TracePoint, ValueInfo } from "./types";

/** The iteration chosen for each loop, keyed by the trace's key and the loop's point: kept across evaluations. */
export type IterationChoices = Map<string, number>;

/** What the Inspector shows for traced code: a line's statements, or a loop. */
export interface TraceFocus {
  index: TraceIndex;
  /** The statements of the line (value points), or the loop alone. */
  points: number[];
  isLoop: boolean;
  /** The entry each point shows for the chosen iterations (null: didn't run). */
  entries: (number | null)[];
  source: string;
  /** The reference check its entries' verdicts come from. */
  reference: Reference | undefined;
  /** Picks the iterations an entry ran in, everywhere. */
  choose(entry: number): void;
}

/** One value an entry holds: the variable it belongs to (none for a plain value). */
export interface ValuePart {
  name: string | null;
  value: ValueInfo;
}

/** The iterations where some of a line's statements ran, with each one's entry there (null: didn't run). */
export interface IterationRow {
  iterations: number[];
  entries: (number | null)[];
}

/** A trace (a line's, or a call's body) indexed by iteration path, loop and line. */
export class TraceIndex {
  readonly trace: Trace;
  /** The worksheet line it belongs to (null: a call's body). */
  readonly line: Line | null;
  private readonly keyPrefix: string;
  /** `point|iterations` → entry index. */
  private readonly byPath: Map<string, number> = new Map();
  /** `loop|enclosing iterations` → how many iterations ran. */
  private readonly counts: Map<string, number> = new Map();

  constructor(trace: Trace, keyPrefix: string, line: Line | null) {
    this.trace = trace;
    this.keyPrefix = keyPrefix;
    this.line = line;
    trace.entries.forEach((entry: TraceEntry, index: number) => {
      this.byPath.set(`${entry.point}|${entry.iterations.join(",")}`, index);
      if (trace.points[entry.point].kind === "loop") {
        const key: string = `${entry.point}|${entry.iterations.slice(0, -1).join(",")}`;
        this.counts.set(key, Math.max(this.counts.get(key) ?? 0, entry.iterations[entry.iterations.length - 1] + 1));
      }
    });
  }

  private key(loop: number): string {
    return `${this.keyPrefix}:${loop}`;
  }

  /** A point's loops, outermost first; a loop's own last. */
  loopsOf(point: number): number[] {
    const target: TracePoint = this.trace.points[point];
    return target.kind === "loop" ? [...target.loops, point] : target.loops;
  }

  iterationCount(loop: number, enclosing: number[]): number {
    return this.counts.get(`${loop}|${enclosing.join(",")}`) ?? 0;
  }

  /** The iterations the choices select for a point (the last one where none is chosen); null if a loop never ran. */
  path(point: number, choices: IterationChoices): number[] | null {
    return this.pathThrough(this.loopsOf(point), choices);
  }

  private pathThrough(loops: number[], choices: IterationChoices): number[] | null {
    const path: number[] = [];
    for (const loop of loops) {
      const count: number = this.iterationCount(loop, path);
      if (count === 0) {
        return null;
      }
      path.push(Math.min(choices.get(this.key(loop)) ?? count - 1, count - 1));
    }
    return path;
  }

  /** The entry a point shows under the choices; null when it didn't run there. */
  entryAt(point: number, choices: IterationChoices): number | null {
    const path: number[] | null = this.path(point, choices);
    return path === null ? null : this.entryAtPath(point, path);
  }

  entryAtPath(point: number, path: number[]): number | null {
    return this.byPath.get(`${point}|${path.join(",")}`) ?? null;
  }

  /** Chooses the iterations an entry ran in. */
  choose(entry: number, choices: IterationChoices): void {
    const iterations: number[] = this.trace.entries[entry].iterations;
    this.loopsOf(this.trace.entries[entry].point).forEach((loop: number, depth: number) => choices.set(this.key(loop), iterations[depth]));
  }

  /** Moves a loop's chosen iteration by `delta`; false when it can't move. */
  step(loop: number, delta: number, choices: IterationChoices): boolean {
    const path: number[] | null = this.path(loop, choices);
    if (path === null) {
      return false;
    }
    const count: number = this.iterationCount(loop, path.slice(0, -1));
    const next: number = Math.min(Math.max(path[path.length - 1] + delta, 0), count - 1);
    if (next === path[path.length - 1]) {
      return false;
    }
    choices.set(this.key(loop), next);
    return true;
  }

  /** The innermost loop whose lines hold a line, if any. */
  loopAt(lineNumber: number): number | null {
    let found: number | null = null;
    this.trace.points.forEach((point: TracePoint, index: number) => {
      if (point.kind === "loop" && point.firstLine <= lineNumber && lineNumber <= point.lastLine) {
        if (found === null || point.loops.length > this.trace.points[found].loops.length) {
          found = index;
        }
      }
    });
    return found;
  }

  /** The statements (value points) that end on a line. */
  valuePointsEndingOn(lineNumber: number): number[] {
    const found: number[] = [];
    this.trace.points.forEach((point: TracePoint, index: number) => {
      if (point.kind === "value" && point.lastLine === lineNumber) {
        found.push(index);
      }
    });
    return found;
  }

  /** The statements of the line a line belongs to: those ending where the first one around it ends. */
  valuePointsAround(lineNumber: number): number[] {
    const around: TracePoint | undefined = this.trace.points.find(
      (point: TracePoint) => point.kind === "value" && point.firstLine <= lineNumber && lineNumber <= point.lastLine,
    );
    return around === undefined ? [] : this.valuePointsEndingOn(around.lastLine);
  }

  /** What an entry holds: each written variable's value, or the statement's value. */
  parts(entry: number): ValuePart[] {
    const target: TraceEntry = this.trace.entries[entry];
    const names: string[] = this.trace.points[target.point].variables;
    return target.values.map((value: ValueInfo, index: number) => ({ name: names[index] ?? null, value }));
  }

  /** "a = 1, b = 2" for several values, else the value alone; "–" for a statement that didn't run. */
  describe(points: number[], entries: (number | null)[]): string {
    const parts: string[] = [];
    let count: number = 0;
    points.forEach((point: number, at: number) => {
      const entry: number | null = entries[at];
      const names: string[] = this.trace.points[point].variables;
      if (entry === null) {
        parts.push(names.length > 0 ? names.map((name: string) => `${name} = –`).join(", ") : "–");
        count += Math.max(names.length, 1);
        return;
      }
      for (const part of this.parts(entry)) {
        parts.push(part.name === null ? part.value.text : `${part.name} = ${part.value.text}`);
        count++;
      }
    });
    if (count === 1 && points.length === 1) {
      const entry: number | null = entries[0];
      return entry === null ? "–" : (this.trace.entries[entry].values[0]?.text ?? "");
    }
    return parts.join(", ");
  }

  /** "i = 4, k = 0": the variables of a path's loops as they stood in its iterations. */
  loopVariables(loops: number[], path: number[]): string {
    const parts: string[] = [];
    loops.forEach((loop: number, depth: number) => {
      const entry: number | null = this.entryAtPath(loop, path.slice(0, depth + 1));
      if (entry !== null) {
        parts.push(...this.parts(entry).map((part: ValuePart) => `${part.name} = ${part.value.text}`));
      }
    });
    return parts.join(", ");
  }

  /** Every iteration where some of these statements ran, in the order they ran. */
  rows(points: number[]): IterationRow[] {
    const rows: Map<string, IterationRow> = new Map();
    this.trace.entries.forEach((entry: TraceEntry, index: number) => {
      const at: number = points.indexOf(entry.point);
      if (at < 0) {
        return;
      }
      const key: string = entry.iterations.join(",");
      let row: IterationRow | undefined = rows.get(key);
      if (row === undefined) {
        row = { iterations: entry.iterations, entries: points.map(() => null) };
        rows.set(key, row);
      }
      row.entries[at] = index;
    });
    return [...rows.values()];
  }

  // ---- Calls ----

  /** The call sites that end on a line. */
  callSitesEndingOn(lineNumber: number): number[] {
    const found: number[] = [];
    this.trace.calls.forEach((site: CallSite, index: number) => {
      if (site.lastLine === lineNumber) {
        found.push(index);
      }
    });
    return found;
  }

  /** Which run of a call site the chosen iterations select (its first in them); null when it didn't run there. */
  occurrence(site: number, choices: IterationChoices): number | null {
    const loop: number | null = this.loopAt(this.trace.calls[site].firstLine);
    const path: number[] | null = loop === null ? [] : this.path(loop, choices);
    if (path === null) {
      return null;
    }
    const key: string = path.join(",");
    let occurrence: number = 0;
    for (const entry of this.trace.callEntries) {
      if (entry.site !== site) {
        continue;
      }
      if (sameRun(entry, key)) {
        return occurrence;
      }
      occurrence++;
    }
    return null;
  }
}

function sameRun(entry: CallEntry, path: string): boolean {
  return entry.iterations.join(",") === path;
}
