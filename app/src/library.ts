import { Export } from "./types";

const KIND_MARKS: Record<Export["kind"], string> = { function: "ƒ", struct: "S", type: "T", macro: "#", variable: "v" };

export interface LibraryActions {
  /** Click / Enter: type it at the scratch pad's caret. */
  insert(entry: Export): void;
  /** Ctrl+click / Ctrl+Enter: show its declaration. */
  reveal(entry: Export): void;
}

/** A library tab: its file name and the name its tab shows. */
export interface LibraryName {
  name: string;
  title: string;
}

/** The text a click types: a call with its parameters as fields (Tab moves between them), else the name. */
export function insertionOf(entry: Export): string {
  if (entry.parameters === null) {
    return entry.name;
  }
  if (entry.parameters.length === 0) {
    return `${entry.name}()`;
  }
  const fields: string[] = entry.parameters.map((parameter: string, index: number) => `\${${index + 1}:${parameter}}`);
  return `${entry.name}(${fields.join(", ")})\${0}`;
}

/** The Library panel: what every library declares, grouped by library and filtered by the search box. */
export class LibraryPanel {
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly actions: LibraryActions;
  private exports: Export[] = [];
  private libraries: LibraryName[] = [];
  private readonly collapsed: Set<string> = new Set();
  private shown: Export[] = [];
  private selected: number = -1;
  private renderedKey: string = "";

  constructor(actions: LibraryActions) {
    this.actions = actions;
    this.search = document.getElementById("library-search") as HTMLInputElement;
    this.list = document.getElementById("library-list") as HTMLElement;
    this.search.addEventListener("input", () => {
      this.selected = 0;
      this.render();
    });
    this.search.addEventListener("keydown", (event: KeyboardEvent) => this.onSearchKey(event));
  }

  /** A new evaluation: the libraries in tab order and what they declare. */
  update(exports: Export[], libraries: LibraryName[]): void {
    const key: string = JSON.stringify([exports, libraries]);
    if (key === this.renderedKey) {
      return;
    }
    this.renderedKey = key;
    this.exports = exports;
    this.libraries = libraries;
    this.render();
  }

  focusSearch(): void {
    this.search.focus();
    this.search.select();
  }

  /** "file: declaration" for every entry shown (tests read it). */
  rows(): string[] {
    return this.shown.map((entry: Export) => `${entry.document}: ${entry.declaration}`);
  }

  private render(): void {
    const query: string = this.search.value.trim().toLowerCase();
    const matches = (entry: Export): boolean =>
      query === "" || [entry.name, entry.declaration, entry.comment].some((text: string) => text.toLowerCase().includes(query));
    const fragment: DocumentFragment = document.createDocumentFragment();
    const scrollTop: number = this.list.scrollTop;
    this.shown = [];
    for (const library of this.libraries) {
      const entries: Export[] = this.exports.filter((entry: Export) => entry.document === library.name && matches(entry));
      if (query !== "" && entries.length === 0) {
        continue;
      }
      const isCollapsed: boolean = query === "" && this.collapsed.has(library.name);
      fragment.append(this.groupHeader(library, entries.length, isCollapsed));
      if (isCollapsed) {
        continue;
      }
      if (entries.length === 0) {
        fragment.append(message("Declares nothing yet", "library-empty"));
      }
      for (const entry of entries) {
        fragment.append(this.item(entry, this.shown.length));
        this.shown.push(entry);
      }
    }
    if (this.libraries.length === 0) {
      fragment.append(
        message("No library yet. Every tab after the scratch pad is a library the scratch pad includes: New (Ctrl+T) adds one, and what it declares shows here.", "library-hint"),
      );
    } else if (query !== "" && this.shown.length === 0) {
      fragment.append(message(`Nothing matches “${this.search.value.trim()}”`, "library-hint"));
    }
    this.list.replaceChildren(fragment);
    this.list.scrollTop = scrollTop;
    this.selected = Math.min(this.selected, this.shown.length - 1);
    this.markSelected(false);
  }

  private groupHeader(library: LibraryName, count: number, isCollapsed: boolean): HTMLElement {
    const header: HTMLButtonElement = document.createElement("button");
    header.className = "library-group";
    header.title = isCollapsed ? `Show what ${library.name} declares` : `Hide what ${library.name} declares`;
    const arrow: HTMLElement = document.createElement("span");
    arrow.className = "arrow";
    arrow.textContent = isCollapsed ? "▸" : "▾";
    const title: HTMLElement = document.createElement("span");
    title.textContent = library.title;
    const total: HTMLElement = document.createElement("span");
    total.className = "count";
    total.textContent = String(count);
    header.append(arrow, title, total);
    header.addEventListener("click", () => {
      if (this.search.value.trim() !== "") {
        return;
      }
      if (!this.collapsed.delete(library.name)) {
        this.collapsed.add(library.name);
      }
      this.render();
    });
    return header;
  }

  private item(entry: Export, index: number): HTMLElement {
    const item: HTMLElement = document.createElement("div");
    item.className = "library-item";
    item.setAttribute("role", "option");
    item.dataset.index = String(index);
    item.title =
      [entry.declaration, entry.comment].filter((text: string) => text !== "").join("\n") +
      `\n\nClick: insert into the scratch pad · Ctrl+click: go to line ${entry.line} of ${entry.document}`;
    const kind: HTMLElement = document.createElement("span");
    kind.className = `library-kind ${entry.kind}`;
    kind.textContent = KIND_MARKS[entry.kind];
    const text: HTMLElement = document.createElement("div");
    text.className = "library-text";
    const declaration: HTMLElement = document.createElement("div");
    declaration.className = "library-declaration";
    // The name in bold
    const match: RegExpExecArray | null = new RegExp(`(?<![\\p{L}\\p{N}_])${escapeRegExp(entry.name)}(?![\\p{L}\\p{N}_])`, "u").exec(entry.declaration);
    if (match === null) {
      declaration.textContent = entry.declaration;
    } else {
      const name: HTMLElement = document.createElement("b");
      name.textContent = entry.name;
      declaration.append(entry.declaration.slice(0, match.index), name, entry.declaration.slice(match.index + entry.name.length));
    }
    text.append(declaration);
    if (entry.comment !== "") {
      const comment: HTMLElement = document.createElement("div");
      comment.className = "library-comment";
      comment.textContent = entry.comment;
      text.append(comment);
    }
    item.append(kind, text);
    item.addEventListener("mousedown", (event: MouseEvent) => event.preventDefault());
    item.addEventListener("click", (event: MouseEvent) => {
      this.selected = index;
      this.markSelected(false);
      if (event.ctrlKey || event.metaKey) {
        this.actions.reveal(entry);
      } else {
        this.actions.insert(entry);
      }
    });
    return item;
  }

  private markSelected(scroll: boolean): void {
    for (const item of this.list.querySelectorAll<HTMLElement>(".library-item")) {
      const isSelected: boolean = Number(item.dataset.index) === this.selected;
      item.classList.toggle("selected", isSelected);
      item.setAttribute("aria-selected", String(isSelected));
      if (isSelected && scroll) {
        item.scrollIntoView({ block: "nearest" });
      }
    }
  }

  private onSearchKey(event: KeyboardEvent): void {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      this.selected = Math.min(Math.max(this.selected + (event.key === "ArrowDown" ? 1 : -1), 0), this.shown.length - 1);
      this.markSelected(true);
    } else if (event.key === "Enter" && this.shown[Math.max(this.selected, 0)] !== undefined) {
      const entry: Export = this.shown[Math.max(this.selected, 0)];
      if (event.ctrlKey) {
        this.actions.reveal(entry);
      } else {
        this.actions.insert(entry);
      }
    } else {
      return;
    }
    event.preventDefault();
  }
}

function message(text: string, className: string): HTMLElement {
  const element: HTMLElement = document.createElement("div");
  element.className = className;
  element.textContent = text;
  return element;
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
