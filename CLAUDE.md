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
  `panels` (docs, import menu, F1, library, config save + restart). All pass as of 2026-10-07.
- App hooks for tests: `KALKGUI_DATA_DIR` (history) and `KALKGUI_KALK_FOLDER` (config.kalk) isolate runs
  from the user's files. `KALKGUI_AUTOMATION=1` (Debug builds only) enables the `WM_COPYDATA` "KALK"
  snapshot hook (`Ui/AutomationSnapshot.cs`), because nothing else can capture the hidden desktop.
- Keyboard focus there: posted keys work, posted clicks don't move WPF focus, UIA SetFocus is refused. The
  driver focuses the input with Esc (the app returns focus to the input on an unused Esc).
- `--background` (ShowActivated=false + send to back) still exists for launching on a real desktop, but
  that path leaked focus in practice. Don't use it for tests.
