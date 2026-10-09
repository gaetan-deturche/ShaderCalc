import "./styles.css";
import { appCommand, call, isTauri } from "./api";
import { DocsPanel } from "./docs";
import { COMMON_TYPES, KEYWORDS } from "./hlsl";
import { buildInspector, buildTraceInspector, emptyInspector } from "./inspector";
import { insertionOf, LibraryPanel } from "./library";
import { CallTrace, DocEntry, DocumentText, Evaluation, Export, Line, LoadResult, OutsideChanges, Profile, Reference, UpdateInfo } from "./types";
import { CallPath } from "./peek";
import { TraceFocus } from "./trace";
import { CompletionEntry, WorksheetView } from "./worksheet-view";

const EXTENSION: string = ".hlsl";
/** The scratch pad: always the first tab; the other tabs are its libraries. */
const SCRATCH: string = "scratch.hlsl";

const tabs: HTMLElement = document.getElementById("tabs") as HTMLElement;
const editors: HTMLElement = document.getElementById("editors") as HTMLElement;
const inspector: HTMLElement = document.getElementById("inspector") as HTMLElement;
const statusText: HTMLElement = document.getElementById("status") as HTMLElement;
const referenceText: HTMLElement = document.getElementById("reference-status") as HTMLElement;
const folderText: HTMLElement = document.getElementById("folder") as HTMLElement;
const versionText: HTMLElement = document.getElementById("app-version") as HTMLElement;
const app: HTMLElement = document.getElementById("app") as HTMLElement;
const profileSelect: HTMLSelectElement = document.getElementById("profile-select") as HTMLSelectElement;

const docs: DocsPanel = new DocsPanel();
const library: LibraryPanel = new LibraryPanel({ insert: insertExport, reveal: revealExport });
const views: WorksheetView[] = [];
const unsaved: Set<WorksheetView> = new Set();
let active: WorksheetView | null = null;
let intrinsics: string[] = [];
let units: { name: string; detail: string }[] = [];
let lastEvaluation: Evaluation | null = null;
let latestGeneration: number = 0;
let resultWidth: number = 340;
let saveTimer: number | undefined;
let evaluateTimer: number | undefined;
let counts = { matches: 0, approximations: 0, mismatches: 0, limits: 0 };
let profiles: Profile[] = [];
let profile: string = "hlsl";

function displayName(name: string): string {
  return name.toLowerCase().endsWith(EXTENSION) ? name.slice(0, -EXTENSION.length) : name;
}

function isScratch(name: string): boolean {
  return name.toLowerCase() === SCRATCH;
}

// ---------------------------------------------------------------- Dialogs

const dialog: HTMLDialogElement = document.getElementById("dialog") as HTMLDialogElement;
const dialogMessage: HTMLElement = document.getElementById("dialog-message") as HTMLElement;
const dialogInput: HTMLInputElement = document.getElementById("dialog-input") as HTMLInputElement;

function ask(message: string, initial: string | null): Promise<string | null> {
  dialogMessage.textContent = message;
  dialogInput.hidden = initial === null;
  dialogInput.value = initial ?? "";
  dialog.returnValue = "";
  dialog.showModal();
  if (initial !== null) {
    dialogInput.focus();
    dialogInput.select();
  } else {
    (document.getElementById("dialog-ok") as HTMLElement).focus();
  }
  return new Promise((resolve: (value: string | null) => void) => {
    dialog.addEventListener("close", () => resolve(dialog.returnValue === "ok" ? dialogInput.value : null), { once: true });
  });
}

async function confirmAction(message: string): Promise<boolean> {
  return (await ask(message, null)) !== null;
}

// ---------------------------------------------------------------- Tabs

function completionEntries(): CompletionEntry[] {
  const entries: CompletionEntry[] = [];
  for (const entry of docs.entries) {
    if (entry.signature !== null) {
      entries.push({ name: entry.name, kind: "intrinsic", detail: `${entry.signature.replace(/`/g, "")}\n${entry.summary}` });
    }
  }
  for (const keyword of KEYWORDS) {
    entries.push({ name: keyword, kind: "keyword", detail: keyword });
  }
  for (const type of COMMON_TYPES) {
    entries.push({ name: type, kind: "type", detail: type });
  }
  for (const symbol of lastEvaluation?.symbols ?? []) {
    entries.push({ name: symbol.name, kind: symbol.kind, detail: symbol.detail });
  }
  for (const unit of units) {
    entries.push({ name: unit.name, kind: "unit", detail: unit.detail });
  }
  return entries.sort((left: CompletionEntry, right: CompletionEntry) => left.name.toLowerCase().localeCompare(right.name.toLowerCase()));
}

function addTab(name: string, text: string, index: number = -1): WorksheetView {
  const view: WorksheetView = new WorksheetView(name, text, {
    onEdit: (edited: WorksheetView) => {
      unsaved.add(edited);
      window.clearTimeout(saveTimer);
      saveTimer = window.setTimeout(() => void saveUnsaved(), 400);
      requestEvaluation();
    },
    onSelect: onLineSelected,
    completions: completionEntries,
    traceCall: (line: Line, path: CallPath) => call<CallTrace | null>("traceCall", { generation: latestGeneration, index: line.index, path }),
    checkCall: (line: Line, path: CallPath) => call<Reference | null>("checkCall", { generation: latestGeneration, index: line.index, path }),
    traceCallRuns: (line: Line, path: CallPath, points: number[]) =>
      call<CallTrace[] | null>("traceCallRuns", { generation: latestGeneration, index: line.index, path, points }),
    checkCallRuns: (line: Line, path: CallPath, points: number[]) =>
      call<Reference | null>("checkCallRuns", { generation: latestGeneration, index: line.index, path, points }),
    documentText: (name: string) => views.find((candidate: WorksheetView) => candidate.name === name)?.text ?? null,
    intrinsics,
    resultWidth,
  });
  view.element.classList.add("hidden");
  if (index < 0 || index >= views.length) {
    views.push(view);
  } else {
    views.splice(index, 0, view);
  }
  editors.append(view.element);
  renderTabs();
  return view;
}

function removeTab(view: WorksheetView): void {
  const index: number = views.indexOf(view);
  if (index < 0) {
    return;
  }
  views.splice(index, 1);
  unsaved.delete(view);
  view.view.destroy();
  view.element.remove();
  if (active === view) {
    active = null;
    activate(views[Math.min(index, views.length - 1)] ?? null);
  }
  renderTabs();
}

function renderTabs(): void {
  const fragment: DocumentFragment = document.createDocumentFragment();
  for (const view of views) {
    const tab: HTMLButtonElement = document.createElement("button");
    tab.className = (view === active ? "tab active" : "tab") + (isScratch(view.name) ? " scratch" : "");
    tab.setAttribute("role", "tab");
    tab.setAttribute("aria-selected", String(view === active));
    tab.textContent = displayName(view.name);
    tab.title = isScratch(view.name)
      ? "Scratch pad: its lines run with every library"
      : `${view.name}: a library for the scratch pad (its own lines run on their own)`;
    tab.addEventListener("click", () => activate(view));
    tab.addEventListener("dblclick", () => void renameActive());
    fragment.append(tab);
  }
  tabs.replaceChildren(fragment);
}

function activate(view: WorksheetView | null): void {
  if (active === view) {
    return;
  }
  active?.element.classList.add("hidden");
  active = view;
  const isPinned: boolean = view !== null && isScratch(view.name);
  (document.getElementById("rename-button") as HTMLButtonElement).disabled = isPinned;
  (document.getElementById("delete-button") as HTMLButtonElement).disabled = isPinned;
  if (view !== null) {
    view.element.classList.remove("hidden");
    view.view.requestMeasure();
    view.focus();
    onLineSelected(view);
  }
  renderTabs();
  void saveState();
}

async function newWorksheet(): Promise<void> {
  const { name } = await call<{ name: string }>("create");
  activate(addTab(name, ""));
  requestEvaluation();
}

async function renameActive(): Promise<void> {
  const view: WorksheetView | null = active;
  if (view === null || isScratch(view.name)) {
    return;
  }
  const title: string | null = await ask("Rename library", displayName(view.name));
  if (title === null) {
    view.focus();
    return;
  }
  await saveUnsaved();
  try {
    const { name } = await call<{ name: string }>("rename", { name: view.name, title });
    view.name = name;
    renderTabs();
    void saveState();
    requestEvaluation();
  } catch (error) {
    await ask(String((error as Error).message ?? error), null);
  }
  view.focus();
}

async function deleteActive(): Promise<void> {
  const view: WorksheetView | null = active;
  if (view === null || isScratch(view.name) || !(await confirmAction(`Send ${view.name} to the Recycle Bin?`))) {
    view?.focus();
    return;
  }
  unsaved.delete(view);
  await call("delete", { name: view.name });
  removeTab(view);
  if (views.length === 0) {
    await newWorksheet();
  }
  requestEvaluation();
}

async function saveUnsaved(): Promise<void> {
  window.clearTimeout(saveTimer);
  const pending: WorksheetView[] = [...unsaved];
  unsaved.clear();
  for (const view of pending) {
    await call("save", { name: view.name, text: view.text });
  }
}

async function saveState(): Promise<void> {
  const side: number = (document.getElementById("side") as HTMLElement).getBoundingClientRect().width;
  await call("saveState", {
    state: {
      tabOrder: views.map((view: WorksheetView) => view.name),
      activeTab: active?.name ?? null,
      sidePanelWidth: side,
      resultColumnWidth: active?.resultWidth ?? resultWidth,
      profile,
    },
  });
}

/** Files edited, added or removed outside the app: reload them live. */
async function pollChanges(): Promise<void> {
  const changes: OutsideChanges = await call<OutsideChanges>("pollChanges");
  if (changes.changed.length === 0 && changes.added.length === 0 && changes.removed.length === 0) {
    return;
  }
  const find = (name: string): WorksheetView | undefined =>
    views.find((view: WorksheetView) => view.name.toLowerCase() === name.toLowerCase());
  for (const document of changes.changed) {
    const view: WorksheetView | undefined = find(document.name);
    if (view !== undefined && !unsaved.has(view)) {
      view.replaceText(document.text);
    }
  }
  for (const document of changes.added) {
    if (find(document.name) === undefined) {
      addTab(document.name, document.text);
    }
  }
  for (const name of changes.removed) {
    const view: WorksheetView | undefined = find(name);
    if (view !== undefined) {
      removeTab(view);
    }
  }
  if (views.length === 0) {
    await newWorksheet();
  }
  if (active === null) {
    activate(views[0]);
  }
  requestEvaluation();
}

// ---------------------------------------------------------------- Evaluation

function requestEvaluation(): void {
  window.clearTimeout(evaluateTimer);
  evaluateTimer = window.setTimeout(() => void runEvaluation(), 200);
}

async function runEvaluation(): Promise<void> {
  window.clearTimeout(evaluateTimer);
  const documents: DocumentText[] = views.map((view: WorksheetView) => ({ name: view.name, text: view.text }));
  let result: Evaluation | { cancelled: true };
  try {
    result = await call<Evaluation | { cancelled: true }>("evaluate", { documents, profile });
  } catch (error) {
    statusText.textContent = `Evaluation failed: ${(error as Error).message ?? error}`;
    return;
  }
  if ("cancelled" in result || result.generation < latestGeneration) {
    return;
  }
  latestGeneration = result.generation;
  lastEvaluation = result;
  counts = { matches: 0, approximations: 0, mismatches: 0, limits: 0 };
  for (const view of views) {
    view.applyEvaluation(
      result.lines.filter((line: Line) => line.document === view.name),
      result.diagnostics.filter((diagnostic) => diagnostic.source === view.name),
    );
  }
  library.update(
    result.exports,
    views.filter((view: WorksheetView) => !isScratch(view.name)).map((view: WorksheetView) => ({ name: view.name, title: displayName(view.name) })),
  );
  const errors: number = result.diagnostics.filter((diagnostic) => diagnostic.severity === "error").length;
  const shown: number = result.lines.filter((line: Line) => line.value !== null).length;
  statusText.textContent =
    `${shown} result${shown === 1 ? "" : "s"}` + (errors > 0 ? ` · ${errors} problem${errors === 1 ? "" : "s"}` : "") + ` · ${result.durationMs.toFixed(0)} ms`;
  void runReferences(result);
}

/** Checks every result against DXC + WARP, the active tab first; a newer evaluation stops it. */
async function runReferences(evaluation: Evaluation): Promise<void> {
  const order: string[] = views.map((view: WorksheetView) => view.name);
  const activeName: string | undefined = active?.name;
  const lines: Line[] = evaluation.lines
    .filter((line: Line) => line.value !== null || (line.trace?.entries.length ?? 0) > 0)
    .sort((left: Line, right: Line) => {
      const rank = (line: Line): number => (line.document === activeName ? -1 : order.indexOf(line.document));
      return rank(left) - rank(right) || left.line - right.line;
    });
  showReferenceProgress(0, lines.length);
  for (let index = 0; index < lines.length; index++) {
    if (evaluation.generation !== latestGeneration) {
      return;
    }
    const line: Line = lines[index];
    let reference: Reference | null;
    try {
      reference = await call<Reference | null>("checkReference", { generation: evaluation.generation, index: line.index });
    } catch (error) {
      reference = {
        verdict: "notChecked",
        summary: `not checked: ${(error as Error).message ?? error}`,
        maxUlps: 0,
        usesApproximations: false,
        message: String((error as Error).message ?? error),
        hlsl: "",
        referenceValue: null,
        timings: null,
        trace: [],
      };
    }
    if (reference === null || evaluation.generation !== latestGeneration) {
      return;
    }
    if (reference.verdict === "match") {
      counts.matches++;
    } else if (reference.verdict === "withinTolerance") {
      counts.approximations++;
    } else if (reference.verdict === "mismatch") {
      counts.mismatches++;
    } else if (reference.verdict === "warpLimit") {
      counts.limits++;
    }
    views.find((view: WorksheetView) => view.name === line.document)?.applyReference(line.index, reference);
    showReferenceProgress(index + 1, lines.length);
  }
}

function showReferenceProgress(done: number, total: number): void {
  const current: Profile | undefined = profiles.find((candidate: Profile) => candidate.id === profile);
  if (current !== undefined && !current.hasReference) {
    referenceText.textContent = total === 0 ? "" : `No reference: DXC + WARP check HLSL, not ${current.label}`;
    return;
  }
  const parts: string =
    `✓ ${counts.matches}` +
    (counts.approximations > 0 ? `  ≈ ${counts.approximations}` : "") +
    (counts.mismatches > 0 ? `  ≠ ${counts.mismatches}` : "") +
    (counts.limits > 0 ? `  ⊘ ${counts.limits}` : "");
  referenceText.textContent = total === 0 ? "" : done < total ? `Reference ${done}/${total} · ${parts}` : `Reference (DXC + WARP): ${parts}`;
}

function onLineSelected(view: WorksheetView): void {
  if (view !== active) {
    return;
  }
  const line: Line | null = view.selectedLine;
  const focus: TraceFocus | null = view.traceFocus();
  inspector.replaceChildren(
    focus !== null ? buildTraceInspector(focus) : line === null ? emptyInspector() : buildInspector(line, view.sourceOf(line), view.referenceFor(line)),
  );
}

// ---------------------------------------------------------------- Side panel

type Panel = "inspector" | "docs" | "library";

function showPanel(panel: Panel): void {
  for (const tab of document.querySelectorAll<HTMLElement>(".side-tab")) {
    tab.classList.toggle("active", tab.dataset.panel === panel);
  }
  inspector.classList.toggle("hidden", panel !== "inspector");
  (document.getElementById("docs") as HTMLElement).classList.toggle("hidden", panel !== "docs");
  (document.getElementById("library") as HTMLElement).classList.toggle("hidden", panel !== "library");
}

/** A Library entry clicked: typed at the scratch pad's caret. */
function insertExport(entry: Export): void {
  const scratch: WorksheetView | undefined = views.find((view: WorksheetView) => isScratch(view.name));
  if (scratch === undefined) {
    return;
  }
  activate(scratch);
  scratch.insertSnippet(insertionOf(entry), entry.name);
}

/** A Library entry Ctrl+clicked: its declaration in its library. */
function revealExport(entry: Export): void {
  const view: WorksheetView | undefined = views.find((candidate: WorksheetView) => candidate.name === entry.document);
  if (view !== undefined) {
    activate(view);
    view.goTo(entry.offset);
  }
}

function makeSplitter(splitter: HTMLElement, onMove: (dx: number, dy: number) => void): void {
  splitter.addEventListener("pointerdown", (event: PointerEvent) => {
    const startX: number = event.clientX;
    const startY: number = event.clientY;
    splitter.setPointerCapture(event.pointerId);
    const move = (moveEvent: PointerEvent): void => onMove(moveEvent.clientX - startX, moveEvent.clientY - startY);
    const up = (): void => {
      splitter.removeEventListener("pointermove", move);
      splitter.removeEventListener("pointerup", up);
      void saveState();
    };
    onMove(0, 0);
    splitter.addEventListener("pointermove", move);
    splitter.addEventListener("pointerup", up);
  });
}

function setupLayout(sideWidth: number): void {
  app.style.setProperty("--side-width", `${sideWidth}px`);
  const side: HTMLElement = document.getElementById("side") as HTMLElement;
  let startWidth: number = sideWidth;
  makeSplitter(document.getElementById("side-splitter") as HTMLElement, (dx: number) => {
    if (dx === 0) {
      startWidth = side.getBoundingClientRect().width;
    }
    app.style.setProperty("--side-width", `${Math.max(220, startWidth - dx)}px`);
  });
  const list: HTMLElement = document.getElementById("docs-list") as HTMLElement;
  let startHeight: number = 0;
  makeSplitter(document.getElementById("docs-splitter") as HTMLElement, (_dx: number, dy: number) => {
    if (dy === 0) {
      startHeight = list.getBoundingClientRect().height;
    }
    list.style.flex = `0 0 ${Math.max(80, startHeight + dy)}px`;
  });
}

// ---------------------------------------------------------------- Updates (the app only)

// Hourly, like Auger: releases come in bursts
const UPDATE_CHECK_MS: number = 60 * 60 * 1000;

const updateButton: HTMLButtonElement = document.getElementById("update-status") as HTMLButtonElement;
const checkButton: HTMLButtonElement = document.getElementById("check-updates") as HTMLButtonElement;
let updateStep: "available" | "installing" | "installed" | "failed" = "available";
let offeredUpdate: UpdateInfo | null = null;
let appVersion: string = "";
let checkTimer: number | undefined;

function showUpdate(text: string, title: string, step: typeof updateStep): void {
  updateButton.textContent = text;
  updateButton.title = title;
  updateButton.disabled = step === "installing";
  updateButton.classList.remove("hidden");
  checkButton.classList.add("hidden");
  updateStep = step;
}

/** The Check for updates link; `forMs` shows an outcome that long, then the link again. */
function showCheck(text: string, title: string, forMs: number = 0, isBusy: boolean = false): void {
  window.clearTimeout(checkTimer);
  checkButton.textContent = text;
  checkButton.title = title;
  checkButton.disabled = isBusy;
  if (forMs > 0) {
    checkTimer = window.setTimeout(() => showCheck("Check for updates", checkTitle()), forMs);
  }
}

function checkTitle(): string {
  return `Look for a newer release now (ShaderCalc ${appVersion} also checks at start-up and every hour)`;
}

/** Asks GitHub for a newer release; a manual check says how it went where it was asked. */
async function checkForUpdate(isManual: boolean = false): Promise<void> {
  if (updateStep === "installing" || updateStep === "installed") {
    return;
  }
  if (isManual) {
    showCheck("Checking…", "Asking GitHub for the latest release", 0, true);
  }
  try {
    offeredUpdate = await appCommand<UpdateInfo | null>("check_update");
  } catch (error) {
    // Offline or GitHub unreachable: the next check may work
    console.warn(`update check: ${error}`);
    if (isManual) {
      showCheck("Update check failed", String(error), 8000);
    }
    return;
  }
  if (offeredUpdate === null) {
    updateButton.classList.add("hidden");
    checkButton.classList.remove("hidden");
    if (isManual) {
      showCheck("Up to date", `ShaderCalc ${appVersion} is the latest release`, 5000);
    }
    return;
  }
  if (isManual) {
    showCheck("Check for updates", checkTitle());
  }
  showUpdate(
    `Update to ${offeredUpdate.version}`,
    `ShaderCalc ${offeredUpdate.version} is available (this is ${offeredUpdate.current}). Click to download and install it.

${offeredUpdate.notes}`,
    "available",
  );
}

async function onUpdateClick(): Promise<void> {
  if (updateStep === "installed") {
    await saveUnsaved();
    await saveState();
    await appCommand("restart_app");
    return;
  }
  if (updateStep === "failed") {
    updateStep = "available";
    await checkForUpdate();
    return;
  }
  showUpdate(`Downloading ${offeredUpdate?.version ?? ""}…`, "Downloading and checking the update", "installing");
  try {
    await appCommand("install_update");
    showUpdate("Restart to update", `ShaderCalc ${offeredUpdate?.version ?? ""} is installed: click to restart into it (the worksheets are saved first)`, "installed");
  } catch (error) {
    showUpdate("Update failed", `${error}

Click to check again.`, "failed");
  }
}

// ---------------------------------------------------------------- Keys and startup

function onKeyDown(event: KeyboardEvent): void {
  if (dialog.open) {
    return;
  }
  const isControl: boolean = event.ctrlKey && !event.altKey && !event.shiftKey;
  if (event.key === "F1") {
    showPanel("docs");
    void docs.showFor(active?.wordAtCaret() ?? null);
  } else if (event.key === "F2") {
    void renameActive();
  } else if (isControl && event.key.toLowerCase() === "t") {
    void newWorksheet();
  } else if (isControl && event.key.toLowerCase() === "s") {
    void saveUnsaved();
  } else if (event.key === "Escape" && active !== null && !active.view.dom.contains(document.activeElement)) {
    // Esc outside the editor (docs search, inspector...) goes back to the code; inside it (the find panel) it's
    // CodeMirror's
    active.focus();
  } else {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
}

async function start(): Promise<void> {
  const [loaded] = await Promise.all([call<LoadResult>("load"), docs.load()]);
  units = await call<{ name: string; detail: string }[]>("units");
  intrinsics = docs.entries.filter((entry: DocEntry) => entry.signature !== null).map((entry: DocEntry) => entry.name);
  resultWidth = loaded.resultColumnWidth;
  profiles = loaded.profiles;
  profile = loaded.profile;
  profileSelect.replaceChildren(
    ...profiles.map((candidate: Profile) => {
      const option: HTMLOptionElement = document.createElement("option");
      option.value = candidate.id;
      option.textContent = candidate.label;
      return option;
    }),
  );
  profileSelect.value = profile;
  folderText.textContent = loaded.folder;
  setupLayout(loaded.sidePanelWidth);
  for (const document of loaded.documents) {
    addTab(document.name, document.text);
  }
  activate(views.find((view: WorksheetView) => view.name === loaded.activeTab) ?? views[0] ?? null);
  inspector.replaceChildren(emptyInspector());
  await runEvaluation();
  window.setInterval(() => void pollChanges(), 1000);
  if (isTauri) {
    appVersion = await appCommand<string>("app_version");
    versionText.textContent = `ShaderCalc ${appVersion}`;
    showCheck("Check for updates", checkTitle());
    checkButton.classList.remove("hidden");
    window.setTimeout(() => void checkForUpdate(), 3000);
    window.setInterval(() => void checkForUpdate(), UPDATE_CHECK_MS);
  } else {
    versionText.textContent = "ShaderCalc (browser dev build)";
  }
}

document.getElementById("new-button")?.addEventListener("click", () => void newWorksheet());
document.getElementById("rename-button")?.addEventListener("click", () => void renameActive());
document.getElementById("delete-button")?.addEventListener("click", () => void deleteActive());
document.getElementById("folder-button")?.addEventListener("click", () => void call("openFolder"));
updateButton.addEventListener("click", () => void onUpdateClick());
checkButton.addEventListener("click", () => void checkForUpdate(true));
profileSelect.addEventListener("change", () => {
  profile = profileSelect.value;
  void saveState();
  requestEvaluation();
  active?.focus();
});
for (const tab of document.querySelectorAll<HTMLElement>(".side-tab")) {
  tab.addEventListener("click", () => showPanel((tab.dataset.panel ?? "inspector") as Panel));
}
window.addEventListener("keydown", onKeyDown, true);

if (isTauri) {
  const { getCurrentWindow } = await import("@tauri-apps/api/window");
  await getCurrentWindow().onCloseRequested(async () => {
    await saveUnsaved();
    await saveState();
  });
} else {
  window.addEventListener("beforeunload", () => void saveUnsaved());
}

/** Test hook: what the active result column shows, one "line mark text" row per result. */
(window as unknown as { shaderCalc: object }).shaderCalc = {
  results: (): string => active?.resultsText() ?? "",
  activeName: (): string | null => active?.name ?? null,
  profile: (): string => profile,
  tabNames: (): string[] => views.map((view: WorksheetView) => view.name),
  library: (): string[] => library.rows(),
};

start().catch((error: unknown) => {
  statusText.textContent = `Startup failed: ${(error as Error).message ?? error}`;
});
