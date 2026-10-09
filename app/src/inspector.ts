import { TraceIndex } from "./trace";
import { BitField, Component, Diagnostic, Line, Reference, TraceCheck, TraceEntry, TracePoint, ValueInfo, Verdict } from "./types";
import { markOf, TraceFocus } from "./worksheet-view";

function element<K extends keyof HTMLElementTagNameMap>(tag: K, className: string = "", text: string = ""): HTMLElementTagNameMap[K] {
  const node: HTMLElementTagNameMap[K] = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text) {
    node.textContent = text;
  }
  return node;
}

export function emptyInspector(): HTMLElement {
  return element("p", "dim", "Put the caret on a line with a result, or click a result, to see its bits, units and the reference check.");
}

/**
 * The selected line in detail: its code, value, type, units, every component's bits (sign / exponent / mantissa for
 * floats), its problems, and the reference check with the HLSL that ran.
 */
export function buildInspector(line: Line, source: string, reference: Reference | undefined): HTMLElement {
  const root: HTMLElement = element("div", "inspector");
  root.append(element("pre", "code-block", source.trim()));

  const value: ValueInfo | null = line.value;
  if (value !== null) {
    root.append(element("div", "inspector-value", value.text));
    const typeLine: HTMLElement = element("div", "inspector-type");
    typeLine.append(element("code", "", value.ty));
    if (value.units) {
      typeLine.append(element("span", "", `   ${value.units}`));
    }
    root.append(typeLine, buildComponents(value, reference));
  }

  if (line.diagnostics.length > 0) {
    root.append(element("h3", "", "Problems"));
    for (const diagnostic of line.diagnostics) {
      root.append(buildProblem(diagnostic));
    }
  }

  if (value !== null) {
    root.append(element("h3", "", "Reference (DXC + WARP)"));
    root.append(buildReference(reference));
  }
  return root;
}

/** "2·1": 1-based iterations, outermost loop first. */
function iterationLabel(iterations: number[]): string {
  return iterations.map((iteration: number) => String(iteration + 1)).join("·");
}

/**
 * A statement inside a traced line, or a loop: its value at the chosen iterations in detail, then every iteration's
 * in a table (a click picks those iterations everywhere), then the line's reference check.
 */
export function buildTraceInspector(focus: TraceFocus, reference: Reference | undefined, onChoose: (entry: number) => void): HTMLElement {
  const index: TraceIndex = focus.index;
  const target: TracePoint = index.trace.points[focus.point];
  const loops: number[] = index.loopsOf(focus.point);
  const root: HTMLElement = element("div", "inspector");
  root.append(element("pre", "code-block", focus.source.trim()));

  if (focus.entry === null) {
    root.append(element("p", "dim", target.kind === "loop" ? "The loop didn't run." : "Not run in the chosen iteration."));
  } else {
    const entry: TraceEntry = index.trace.entries[focus.entry];
    const where: string = index.loopVariables(loops, entry.iterations);
    root.append(element("div", "dim small", `Iteration ${iterationLabel(entry.iterations)}${where ? ` · ${where}` : ""}`));
    if (target.kind === "value" && entry.values.length > 0) {
      const value: ValueInfo = entry.values[0];
      const check: TraceCheck | undefined = reference?.trace[focus.entry];
      root.append(element("div", "inspector-value", value.text));
      const typeLine: HTMLElement = element("div", "inspector-type");
      typeLine.append(element("code", "", value.ty));
      if (value.units) {
        typeLine.append(element("span", "", `   ${value.units}`));
      }
      root.append(typeLine, buildComponents(value, check === undefined ? undefined : { verdict: check.verdict, referenceValue: check.values[0] ?? null }));
    }
  }

  const entries: number[] = index.entriesOf(focus.point);
  root.append(element("h3", "", `${target.kind === "loop" ? "Iterations" : "Every iteration"} (${entries.length}${index.trace.isTruncated ? "+" : ""})`));
  const table: HTMLTableElement = element("table", "iterations");
  const header: HTMLTableRowElement = table.createTHead().insertRow();
  for (const title of ["#", "loop", target.kind === "value" ? "value" : "", ""]) {
    header.append(element("th", "", title));
  }
  const body: HTMLTableSectionElement = table.createTBody();
  let selected: HTMLTableRowElement | null = null;
  for (const entryIndex of entries) {
    const entry: TraceEntry = index.trace.entries[entryIndex];
    const verdict: Verdict | undefined = reference?.trace[entryIndex]?.verdict;
    const { mark, kind } = markOf(null, verdict === undefined ? undefined : { verdict }, true);
    const row: HTMLTableRowElement = body.insertRow();
    row.insertCell().textContent = iterationLabel(entry.iterations);
    row.insertCell().textContent = index.loopVariables(loops, entry.iterations);
    row.insertCell().textContent = target.kind === "value" ? (entry.values[0]?.text ?? "") : "";
    row.insertCell().append(element("span", `mark ${kind}`, mark));
    if (entryIndex === focus.entry) {
      row.className = "selected";
      selected = row;
    }
    row.addEventListener("click", () => onChoose(entryIndex));
  }
  root.append(table);
  if (index.trace.isTruncated) {
    root.append(element("p", "dim small", "The loops ran longer: only their first values are kept."));
  }
  if (selected !== null) {
    const row: HTMLTableRowElement = selected;
    requestAnimationFrame(() => row.scrollIntoView({ block: "nearest" }));
  }

  root.append(element("h3", "", "Reference (DXC + WARP)"));
  root.append(buildReference(reference));
  return root;
}

function buildProblem(diagnostic: Diagnostic): HTMLElement {
  const paragraph: HTMLElement = element("p", "problem");
  paragraph.append(element("span", diagnostic.severity === "info" ? "dim" : diagnostic.severity, diagnostic.message));
  const place: string =
    diagnostic.function === null ? `${diagnostic.source}:${diagnostic.line}` : `${diagnostic.source}:${diagnostic.line} in ${diagnostic.function}`;
  paragraph.append(element("span", "dim small", `  (${place})`));
  return paragraph;
}

function buildComponents(value: ValueInfo, reference: { verdict: Verdict; referenceValue: ValueInfo | null } | undefined): HTMLElement {
  const table: HTMLTableElement = element("table", "components");
  const referenceValue: ValueInfo | null =
    reference?.verdict === "mismatch" || reference?.verdict === "withinTolerance" || reference?.verdict === "warpLimit"
      ? reference.referenceValue
      : null;
  value.components.forEach((component: Component, index: number) => {
    const theirs: Component | undefined = referenceValue?.components[index];
    const differs: boolean = theirs !== undefined && theirs.raw !== component.raw;
    addComponent(table, component.name, component, differs ? "error" : "", differs ? theirs : undefined);
    if (differs && theirs !== undefined) {
      addComponent(table, "WARP", theirs, "dim", component);
    }
  });
  return table;
}

/** name · value · hex, and the bits underneath (those that differ from `other` marked). */
function addComponent(table: HTMLTableElement, name: string, component: Component, className: string, other: Component | undefined): void {
  const row: HTMLTableRowElement = table.insertRow();
  row.className = className;
  for (const text of [name, component.text, component.hex]) {
    row.insertCell().textContent = text;
  }
  const bitsRow: HTMLTableRowElement = table.insertRow();
  bitsRow.className = "bits";
  const cell: HTMLTableCellElement = bitsRow.insertCell();
  cell.colSpan = 3;
  if (component.width === 0) {
    cell.textContent = component.bits;
  } else {
    cell.append(buildBits(component, other));
  }
}

function bitAt(value: bigint, index: number): number {
  return Number((value >> BigInt(index)) & 1n);
}

/**
 * The bits in groups of 4, each labelled with its top bit's index, a wider gap between bytes and 32 bits a row;
 * a float's sign / exponent / mantissa coloured, with their values underneath. Hover a bit for its index.
 */
function buildBits(component: Component, other: Component | undefined): HTMLElement {
  const value: bigint = BigInt(component.raw);
  const otherValue: bigint | null = other === undefined ? null : BigInt(other.raw);
  const fieldOf = (index: number): BitField | undefined => component.fields.find((field: BitField) => field.low <= index && index <= field.high);
  const grid: HTMLElement = element("div", "bit-grid");
  for (let rowTop = component.width - 1; rowTop >= 0; rowTop -= 32) {
    const row: HTMLElement = element("div", "bit-row");
    for (let groupTop = rowTop; groupTop > rowTop - 32 && groupTop >= 0; groupTop -= 4) {
      const group: HTMLElement = element("span", groupTop !== rowTop && groupTop % 8 === 7 ? "bit-group byte" : "bit-group");
      const bits: HTMLElement = element("span", "bit-values");
      for (let index = groupTop; index > groupTop - 4 && index >= 0; index--) {
        const bit: number = bitAt(value, index);
        const field: BitField | undefined = fieldOf(index);
        const differs: boolean = otherValue !== null && bitAt(otherValue, index) !== bit;
        const span: HTMLElement = element("span", `bit ${bit === 1 ? "one" : "zero"} ${field?.name ?? ""}${differs ? " differs" : ""}`, String(bit));
        span.title = `bit ${index}` + (field === undefined ? "" : ` · ${field.name} bit ${index - field.low}`) + ` = ${bit}` + (differs ? " (differs)" : "");
        bits.append(span);
      }
      group.append(element("span", "bit-index", String(groupTop)), bits);
      row.append(group);
    }
    grid.append(row);
  }
  if (component.fields.length > 0) {
    const legend: HTMLElement = element("div", "bit-legend");
    for (const field of component.fields) {
      const bits: bigint = (value >> BigInt(field.low)) & ((1n << BigInt(field.high - field.low + 1)) - 1n);
      const text: string =
        field.name === "sign"
          ? `sign ${bits}`
          : field.name === "exponent"
            ? `exponent ${bits}${component.exponentMeaning === null ? "" : ` → ${component.exponentMeaning}`}`
            : `mantissa 0x${bits.toString(16).toUpperCase()}`;
      legend.append(element("span", field.name, text));
    }
    grid.append(legend);
  }
  return grid;
}

function buildReference(reference: Reference | undefined): HTMLElement {
  const section: HTMLElement = element("div", "reference");
  if (reference === undefined) {
    section.append(element("p", "dim", "Running…"));
    return section;
  }
  let text: string;
  let className: string;
  switch (reference.verdict) {
    case "match":
      [text, className] = ["✓ Bit-identical to WARP.", "match"];
      break;
    case "withinTolerance":
      [text, className] = [`≈ Within ${reference.maxUlps} ulp: approximate functions (sin, exp2, log2...) differ between GPUs.`, "approximate"];
      break;
    case "mismatch":
      text = reference.usesApproximations
        ? `≠ WARP gives ${reference.referenceValue?.text}. The line uses approximate functions, which GPUs implement differently.`
        : `≠ WARP gives ${reference.referenceValue?.text}.`;
      className = "error";
      break;
    case "warpLimit":
      [text, className] = [`⊘ WARP gives ${reference.referenceValue?.text}, but WARP is wrong here: ${reference.message ?? ""}.`, "warning"];
      break;
    default:
      [text, className] = [`Not checked: ${reference.message ?? ""}`, "dim"];
  }
  section.append(element("p", className, text));
  if (reference.timings !== null) {
    const timings = reference.timings;
    section.append(
      element(
        "p",
        "dim small",
        `DXC ${timings.compileMs.toFixed(0)} ms, WARP pipeline ${timings.pipelineMs.toFixed(0)} ms, run ${timings.runMs.toFixed(1)} ms`,
      ),
    );
  }
  if (reference.hlsl) {
    const details: HTMLDetailsElement = element("details");
    details.append(element("summary", "", "The HLSL that ran"), element("pre", "code-block hlsl", reference.hlsl));
    section.append(details);
  }
  return section;
}
