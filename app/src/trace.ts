import { Line, Trace, TraceEntry, TracePoint } from "./types";

/** The iteration chosen for each loop, keyed by its line's first line and point: kept across evaluations. */
export type IterationChoices = Map<string, number>;

/** A traced line's entries, indexed by iteration path and loop. */
export class TraceIndex {
  readonly line: Line;
  readonly trace: Trace;
  /** `point|iterations` → entry index. */
  private readonly byPath: Map<string, number> = new Map();
  /** `loop|enclosing iterations` → how many iterations ran. */
  private readonly counts: Map<string, number> = new Map();

  constructor(line: Line, trace: Trace) {
    this.line = line;
    this.trace = trace;
    trace.entries.forEach((entry: TraceEntry, index: number) => {
      this.byPath.set(`${entry.point}|${entry.iterations.join(",")}`, index);
      if (trace.points[entry.point].kind === "loop") {
        const key: string = `${entry.point}|${entry.iterations.slice(0, -1).join(",")}`;
        this.counts.set(key, Math.max(this.counts.get(key) ?? 0, entry.iterations[entry.iterations.length - 1] + 1));
      }
    });
  }

  private key(loop: number): string {
    return `${this.line.document}:${this.line.firstLine}:${loop}`;
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
    const path: number[] = [];
    for (const loop of this.loopsOf(point)) {
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
    return path === null ? null : (this.byPath.get(`${point}|${path.join(",")}`) ?? null);
  }

  entryAtPath(point: number, path: number[]): number | null {
    return this.byPath.get(`${point}|${path.join(",")}`) ?? null;
  }

  entriesOf(point: number): number[] {
    const found: number[] = [];
    this.trace.entries.forEach((entry: TraceEntry, index: number) => {
      if (entry.point === point) {
        found.push(index);
      }
    });
    return found;
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

  /** "i = 4, k = 0": the variables of a path's loops as they stood in its iterations. */
  loopVariables(loops: number[], path: number[]): string {
    const parts: string[] = [];
    loops.forEach((loop: number, depth: number) => {
      const entry: number | null = this.entryAtPath(loop, path.slice(0, depth + 1));
      const names: string[] = this.trace.points[loop].variables;
      if (entry !== null) {
        names.forEach((name: string, index: number) => parts.push(`${name} = ${this.trace.entries[entry].values[index]?.text ?? "?"}`));
      }
    });
    return parts.join(", ");
  }
}
