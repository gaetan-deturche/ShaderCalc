# <img src="src/KalkGui/Assets/kalkgui.png" width="48" align="top" alt=""> KalkGui

A Windows desktop front end for [kalk](https://github.com/xoofx/kalk), the developer calculator with
HLSL-style vectors and matrices, unit conversions, user functions and bit-pattern display.
The kalk engine runs unmodified inside the app.

- Transcript with kalk's syntax colours. `display dev` shows hex and binary breakdowns.
- Input editor: Enter evaluates, Shift+Enter adds a new line, Tab / Ctrl+Space complete, Up/Down browse
  history, F1 opens the docs for the word under the caret, Esc cancels a long evaluation (and, from any other
  control, returns to the input).
- Syntax help, in the input and in the Library editor:
  - Enter after `func name(x)` / `if` / `for` / `while` opens the block and adds its `end`. Enter inside a
    block adds a line, Enter on the last line evaluates, and Ctrl+Enter evaluates from anywhere.
  - `func`, `if`, `for` or `while` followed by Tab expands to a template: Tab moves between the fields, and
    the parameter name is mirrored into the body.
  - The text is parsed as you type: the first syntax error is underlined and explained before you evaluate.
- **Docs**: every documented function, including those in modules not imported yet.
- **Library**: every variable and function you define is saved to `~/.kalk/library.kalk` (with the modules
  it needs) and available immediately, now and on every later start. Select an entry to edit it in place
  (renaming replaces it) or use New to add one from a template (variable, one-line function, multi-line
  function). Edits made to `library.kalk` outside the app apply live.
  Entries that fail to load stay in the file, flagged, until you fix or delete them. Definitions from
  `config.kalk` are listed but stay in `config.kalk`.
- **Config**: edit `~/.kalk/config.kalk`, save it and restart the engine.

## Build

```
git clone --recurse-submodules <repo>
dotnet build KalkGui.sln
```

Requires the .NET 9 SDK (pinned in `global.json`).

## Credits

kalk is by Alexandre Mutel and released under the BSD-2-Clause license (`external/kalk/license.txt`).
The KalkGui icon is kalk's icon with window caption buttons added to its title bar.
