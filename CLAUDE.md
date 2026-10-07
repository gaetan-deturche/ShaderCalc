# KalkGui (agent notes)

WPF desktop front end for [xoofx/kalk](https://github.com/xoofx/kalk) (the HLSL-flavoured developer calculator),
created 2026-10-07. kalk is consumed **unmodified** as a git submodule (`external/kalk`); its engine
`Kalk.Core` runs in-process.

## Layout

- `src/KalkGui.Engine` (net9.0, no UI): `KalkSession` wraps one `KalkEngine` - evaluation on a worker thread
  with cancellation, syntax spans, completions, docs, user symbols, modules. Covered by `tests/KalkGui.Engine.Tests`.
- `src/KalkGui` (net9.0-windows, WPF + AvalonEdit 6.3, Fluent `ThemeMode.System`): transcript (RichTextBox),
  input editor, Docs / Library / Config tabs. Code-behind, no MVVM.
- `external/kalk`: submodule. Change it only through upstream PRs; a local fork is the fallback.

## How Kalk.Core is hosted (the non-obvious parts)

- `new KalkEngine { InputReader = new StringReader(""), OutputWriter = ..., IsOutputSupportHighlighting = false }`
  then `Run()`: non-interactive mode loads `~/.kalk/config.kalk` and returns. After that, `Parse` + `EvaluatePage`
  per input, and the state persists. `Run()` enters a blocking console REPL if an interactive console is attached
  (`KalkSession` guards against it).
- **`IsOutputSupportHighlighting` must stay false**: kalk's ANSI renderer pads lines to `Console.BufferWidth`
  (Consolus `ConsoleText.cs` ~586, `KalkEngine.Highlight.cs` ~477) → "invalid handle" without a console.
  Colours come from the public `KalkEngine.Highlight(ConsoleText, caretIndex)` instead; it fills per-char
  `StyleMarkers`, which `KalkStyleConverter` flattens into spans.
- UI-thread reads use `Monitor.TryEnter` on the engine lock and return empty while an evaluation runs. Never
  block the UI on it.
- Docs = `module.Descriptors` (also for modules not yet imported) + `engine.Descriptors`. Modules are the
  `KalkModule` values in `engine.Builtins`.
- Not reachable without patching kalk: user `shortcut(...)` key bindings (`OnKey` is internal) and the
  completion list builder (rebuilt from `Builtins` / `Variables` / `Units` keys).
- Scriban 6.3.0 (kalk's dependency) raises NuGet audit warnings (NU1902-1904). Not addressed yet.

## Editor assist (input + library editors)

`Ui/KalkEditorAssist` owns Enter, Ctrl+Enter, Tab, Ctrl+Space and F1 for an AvalonEdit editor. It's created
before the window's own key handlers, so it gets those keys first. Only the 4 keys from this list are handled
by the window: Up, Down, Esc.
- Enter: a `func`/`if`/`for`/`while`/`case` header opens the block (inserting `end` if the text has unclosed
  blocks), Enter inside a multi-line text adds a line, and the last line submits. Ctrl+Enter always submits.
- Templates are AvalonEdit snippets (`Ui/KalkSnippets`). While one is active (`TextArea.StackedInputHandlers`
  not empty) it handles Tab, Enter and Esc before the instance handlers. `IsBusy` covers it and the completion
  window.
- Live check: `KalkSession.CheckSyntax` (parse only, `TryEnter` on the engine lock), debounced 400 ms. It shows
  as an amber hint plus a red underline (`KalkEditorHighlighting.SetError`). Evaluation errors are red.
  Call `ResetSyntaxCheck` after a programmatic text change.
- kalk prints `func` bodies flush left: `DescribeUserSymbol` re-indents them (`IndentBlocks`) for the editor
  and library.kalk, and the Library list shows only the first line (`UserSymbol.Summary`).

## Persistent library (`~/.kalk/library.kalk`)

- After every evaluation `KalkSession` diffs `engine.Variables` **by reference** (an assignment always stores
  a new object) plus newly imported modules. It updates `_librarySymbols` / `_libraryModules` and rewrites the
  file (write-then-move). Definitions are serialised from engine state: a function as kalk prints it, a
  variable as `name = <value>`. The original input is not replayed, so `a = rnd` reloads as the same value.
- Names that exist right after config.kalk loads belong to config.kalk and are never written to the library,
  unless the user redefines them. `del` / `reset` remove entries.
- Load: the file is split into top-level statements with kalk's parser (one per line if the file doesn't
  parse), and each statement is evaluated on its own. Failures become `LibraryLoadErrors`. They stay in the
  file verbatim, are listed as broken entries, and are fixed through `EvaluateAsync(text, replacesSymbol:
  <raw entry>)` or dropped with `DiscardBrokenEntry`.
- Editing: `EvaluateAsync(text, replacesSymbol: name)`. If the text defines another name, the old one is deleted.
- Outside edits apply live: a `FileSystemWatcher` (filter `library.kalk*`, which catches the `.tmp` rename KalkGui
  saves with) is debounced 250 ms and then calls `ReloadLibraryAsync`. That method ignores text equal to
  `_lastLibraryText` (KalkGui's own last write or read), skips entries matching the current definition
  verbatim, re-evaluates the rest and removes library names no longer in the file. It doesn't rewrite the file.
- Multi-line `func name(x) ... end` definitions round-trip (statement spans keep them whole).
- Tests always pass a temp `kalkUserFolder`, so they never touch the real `~/.kalk`.

## Icon

`src/KalkGui/Assets/kalkgui.{png,ico}` is kalk's `img/kalk.png` with Windows caption buttons drawn into its title
bar. It's generated by `Claude/make-icon.ps1` (GDI+, gitignored), which writes the 512 px PNG and a 16-256 px ICO
with PNG entries. kalk's own `.ico` only holds 128 px. The icon is both the `ApplicationIcon` (exe) and `Window.Icon`.

## Build / test

- `global.json` pins SDK 9.0.308 with `rollForward: disable` (this machine's 9.0.312 cannot run).
- `dotnet build KalkGui.sln`, `dotnet test tests/KalkGui.Engine.Tests`.
- Warnings from `external/kalk` (NU190x, SYSLIB, CA2022) are upstream noise.

## UI testing: hidden desktop only (never steal the user's focus)

Shared memory `no-focus-steal-gui-testing` has the rule and its history. The tooling is in `Claude/` (gitignored):
- `run-hidden.ps1 -SpecPath Claude\scenarios\<x>.json`: creates the `KalkGuiTest` desktop and runs the driver
  there. It watches the user's desktop and kills the run if KalkGui shows up there or takes the foreground.
- `ui-drive.ps1`: executes a JSON list of steps (`launch`, `eval`, `type`, `key`, `submit`, `set`, `invoke`,
  `invokeName`, `selectTab`, `selectDoc`, `selectSymbol`, `selectCombo`, `expect`, `read`, `snapshot`, ...).
  Exit code 1 on any failed `expect`.
- Scenarios: `smoke`, `calculator` (eval, display combo, errors, history, completion, cancel, clear),
  `panels` (docs, import menu, F1, library, config save + restart), `library` (persist, edit, rename,
  invalid edit + revert, restart, delete), `library-broken` (seeded broken entry: reported, kept, fixed in place),
  `library-live` (outside edits of library.kalk applied live, New entry), `editor-help` (live check,
  auto-close, Tab templates with mirrored parameter, completion, library templates),
  `publish-smoke` (the published exe). All pass as of 2026-10-07. `launch` takes `exe` and `library`
  (seed library.kalk) options.
- App hooks for tests: `KALKGUI_DATA_DIR` (history) and `KALKGUI_KALK_FOLDER` (config.kalk) isolate runs
  from the user's files. `KALKGUI_AUTOMATION=1` (Debug builds only) enables the `WM_COPYDATA` "KALK"
  snapshot hook (`Ui/AutomationSnapshot.cs`), because nothing else can capture the hidden desktop.
- Keyboard focus there: posted keys work, posted clicks don't move WPF focus, UIA SetFocus is refused. The
  driver focuses the input with Esc (the app returns focus to the input on an unused Esc).
- `--background` (ShowActivated=false + send to back) still exists for launching on a real desktop, but
  that path leaked focus in practice. Don't use it for tests.
