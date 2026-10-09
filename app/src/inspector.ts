import { Component, Diagnostic, Line, Reference, ValueInfo } from "./types";

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

function buildProblem(diagnostic: Diagnostic): HTMLElement {
  const paragraph: HTMLElement = element("p", "problem");
  paragraph.append(element("span", diagnostic.severity === "info" ? "dim" : diagnostic.severity, diagnostic.message));
  const place: string =
    diagnostic.function === null ? `${diagnostic.source}:${diagnostic.line}` : `${diagnostic.source}:${diagnostic.line} in ${diagnostic.function}`;
  paragraph.append(element("span", "dim small", `  (${place})`));
  return paragraph;
}

function buildComponents(value: ValueInfo, reference: Reference | undefined): HTMLElement {
  const table: HTMLTableElement = element("table", "components");
  const referenceValue: ValueInfo | null =
    reference?.verdict === "mismatch" || reference?.verdict === "withinTolerance" ? reference.referenceValue : null;
  value.components.forEach((component: Component, index: number) => {
    const theirs: Component | undefined = referenceValue?.components[index];
    const differs: boolean = theirs !== undefined && theirs.raw !== component.raw;
    addComponent(table, component.name, component, differs ? "error" : "");
    if (differs && theirs !== undefined) {
      addComponent(table, "WARP", theirs, "dim");
    }
  });
  return table;
}

/** name · value · hex, and the bit pattern on its own line underneath. */
function addComponent(table: HTMLTableElement, name: string, component: Component, className: string): void {
  const row: HTMLTableRowElement = table.insertRow();
  row.className = className;
  for (const text of [name, component.text, component.hex]) {
    row.insertCell().textContent = text;
  }
  const bitsRow: HTMLTableRowElement = table.insertRow();
  bitsRow.className = "bits";
  const cell: HTMLTableCellElement = bitsRow.insertCell();
  cell.colSpan = 3;
  cell.textContent = component.bits;
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
