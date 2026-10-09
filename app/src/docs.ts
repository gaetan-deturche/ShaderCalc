import { marked } from "marked";
import { call } from "./api";
import { DocEntry } from "./types";

/** The Docs panel: search box, ranked page list, and the selected page. */
export class DocsPanel {
  entries: DocEntry[] = [];
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly page: HTMLElement;
  private shown: DocEntry[] = [];
  private selected: DocEntry | null = null;
  private searchVersion: number = 0;

  constructor() {
    this.search = document.getElementById("docs-search") as HTMLInputElement;
    this.list = document.getElementById("docs-list") as HTMLElement;
    this.page = document.getElementById("docs-page") as HTMLElement;
    this.search.addEventListener("input", () => void this.runSearch());
    this.search.addEventListener("keydown", (event: KeyboardEvent) => this.onSearchKey(event));
  }

  async load(): Promise<void> {
    this.entries = await call<DocEntry[]>("docs");
    this.show(this.entries);
  }

  find(name: string): DocEntry | undefined {
    return this.entries.find((entry: DocEntry) => entry.name === name) ?? this.entries.find((entry: DocEntry) => entry.name.toLowerCase() === name.toLowerCase());
  }

  focusSearch(): void {
    this.search.focus();
    this.search.select();
  }

  /** F1: the page of a name (or a search for it). */
  async showFor(word: string | null): Promise<void> {
    if (word === null) {
      this.focusSearch();
      return;
    }
    this.search.value = word;
    await this.runSearch();
    const exact: DocEntry | undefined = this.find(word);
    if (exact !== undefined) {
      this.select(exact);
    }
  }

  private async runSearch(): Promise<void> {
    const version: number = ++this.searchVersion;
    const names: string[] = await call<string[]>("searchDocs", { query: this.search.value });
    if (version !== this.searchVersion) {
      return;
    }
    const byName: Map<string, DocEntry> = new Map(this.entries.map((entry: DocEntry) => [entry.name, entry]));
    this.show(names.map((name: string) => byName.get(name)).filter((entry): entry is DocEntry => entry !== undefined));
  }

  private show(entries: DocEntry[]): void {
    this.shown = entries;
    const fragment: DocumentFragment = document.createDocumentFragment();
    for (const entry of entries) {
      const item: HTMLElement = document.createElement("div");
      item.className = "docs-item";
      item.setAttribute("role", "option");
      item.dataset.name = entry.name;
      const header: HTMLElement = document.createElement("div");
      header.className = "docs-item-header";
      const name: HTMLElement = document.createElement("span");
      name.className = entry.signature === null ? "docs-name" : "docs-name mono";
      name.textContent = entry.name;
      const group: HTMLElement = document.createElement("span");
      group.className = "docs-group";
      group.textContent = entry.group;
      header.append(name, group);
      const summary: HTMLElement = document.createElement("div");
      summary.className = "docs-summary";
      summary.textContent = entry.summary;
      item.append(header, summary);
      item.addEventListener("click", () => this.select(entry));
      fragment.append(item);
    }
    this.list.replaceChildren(fragment);
    if (entries.length > 0) {
      this.select(entries[0]);
    } else {
      this.page.replaceChildren();
    }
  }

  private select(entry: DocEntry): void {
    this.selected = entry;
    for (const item of this.list.querySelectorAll<HTMLElement>(".docs-item")) {
      const isSelected: boolean = item.dataset.name === entry.name;
      item.classList.toggle("selected", isSelected);
      item.setAttribute("aria-selected", String(isSelected));
      if (isSelected) {
        item.scrollIntoView({ block: "nearest" });
      }
    }
    this.page.replaceChildren(buildPage(entry));
  }

  private onSearchKey(event: KeyboardEvent): void {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") {
      return;
    }
    const index: number = this.selected === null ? -1 : this.shown.indexOf(this.selected);
    const next: number = Math.min(Math.max(index + (event.key === "ArrowDown" ? 1 : -1), 0), this.shown.length - 1);
    if (next >= 0) {
      this.select(this.shown[next]);
    }
    event.preventDefault();
  }
}

function buildPage(entry: DocEntry): HTMLElement {
  const page: HTMLElement = document.createElement("article");
  const title: HTMLElement = document.createElement("h2");
  title.textContent = entry.name;
  if (entry.signature !== null) {
    title.className = "mono";
  }
  const group: HTMLElement = document.createElement("div");
  group.className = "dim small";
  group.textContent = entry.group;
  page.append(title, group);
  let body: string = entry.markdown;
  if (entry.signature !== null) {
    // "`T pow(T x, T y)` · float": the signature as code, the argument kinds after it
    const end: number = entry.signature.indexOf("`", 1);
    const signature: string = end > 0 ? entry.signature.slice(1, end) : entry.signature.replace(/`/g, "");
    const kinds: string = end > 0 ? entry.signature.slice(end + 1).trim().replace(/^·/, "").trim() : "";
    const line: HTMLElement = document.createElement("div");
    line.className = "signature";
    const code: HTMLElement = document.createElement("code");
    code.textContent = signature;
    line.append(code);
    if (kinds) {
      const kindsElement: HTMLElement = document.createElement("span");
      kindsElement.className = "dim";
      kindsElement.textContent = `    ${kinds}`;
      line.append(kindsElement);
    }
    page.append(line);
    body = body.slice(body.indexOf(entry.signature) + entry.signature.length);
  }
  const content: HTMLElement = document.createElement("div");
  content.className = "markdown";
  content.innerHTML = marked.parse(body, { async: false }) as string;
  page.append(content);
  return page;
}
