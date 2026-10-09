# ShaderCalc (agent notes)

Desktop worksheet for checking HLSL / C++ shader maths: a custom HLSL interpreter, with DXC + D3D12 WARP as the
bit-exact reference. Rust core, Tauri 2 shell, TypeScript + CodeMirror 6 UI. History: KalkGui (a kalk front end,
2026-10-07), then a C# WPF app with its own interpreter (2026-10-08, last C# state = commit `5671300`), then ported
to Rust (2026-10-09). The port is a literal translation of the C# code: both gave identical output on a 5,560-line
differential corpus (values, diagnostics with spans, reference verdicts). Only `third_party/kalk/units.kalk` is
left of kalk: `units.rs` translates it.

## Layout

- `crates/shadercalc-core`: interpreter, worksheet engine, reference runner (DXC + WARP through `windows` 0.62),
  docs. No UI.
- `crates/shadercalc-backend`: `Backend::handle(command, json)`, the one API the UI talks to. `store.rs` (worksheet
  files), `dto.rs` (camelCase JSON), `lib.rs` (commands, evaluation generations). Commands: `load`, `save`,
  `create`, `rename`, `delete`, `saveState`, `pollChanges`, `openFolder`, `evaluate`, `checkReference`, `docs`,
  `searchDocs`, `units`.
- `crates/shadercalc-devserver`: the same commands over HTTP (`POST /api/<command>` on 127.0.0.1:5191), so the
  UI runs in a browser. Vite proxies `/api` to it.
- `app/`: the frontend (Vite, strict TS). `app/src-tauri` (package `shadercalc`): the Tauri shell, a single
  `run_command` command that runs `Backend::handle` on a blocking thread. `app/src/api.ts` picks Tauri `invoke` or
  `fetch`.
- `third_party/dxc/` (gitignored): `dxcompiler.dll` + `dxil.dll`, from `scripts/fetch-dxc.ps1`.

Conventions: explicit types on `let` bindings, `rustfmt.toml` (max_width 120), clippy clean
(`cargo clippy --workspace --all-targets`; the two crate-level allows in core `lib.rs` say why). Spans are UTF-16
offsets so they match JS strings and CodeMirror positions.

## Interpreter pipeline (core)

`syntax::lexer` → `syntax::preprocessor` (one shared instance across worksheet files) → `syntax::parser` →
`binding::binder` (static types, overloads, conversions, a typed bound tree) → `evaluation::evaluator` (every
component is raw `u64` bits plus a `UnitTag`).

- Unsuffixed literals are DXC's `literal int` / `literal float` (64-bit) until they meet a type.
- Intrinsics (`evaluation/intrinsics.rs`) follow DXC's lowering as read from `-Gis` disassembly: e.g. `pow(x, 2)`
  with a constant 2 is `x*x`, otherwise `exp2(log2(x) * y)`; `smoothstep` is `t*(t*(3-t*2))`. Under
  `Lowering::SlangCpu` each one follows Slang's C++ prelude and core-module default body instead (`powf`,
  `x / length(x)`...), translated from Slang commit `6818f2a` (copies in `Claude/slang-source`, gitignored).
  Resolvers get the profile: DXIL has no double for most float ops, Slang has them all.
- `semantics.rs`: `SemanticsProfile` holds every point where implementations disagree. `HLSL` (the default) is
  what WARP does: ties-to-even, masked shift counts, unfused mad, denormal flush on input and output, `f32tof16`
  toward zero, float `%` from the quotient, signed `/0` = INT_MAX, `uint`→`float` via signed, 64-bit literals,
  `half` = float. `SLANG_CPU` is C on x86-64: none of that, plus 32-bit literals (int, float), a 16-bit `half`
  (stored with Slang's `f32tof16`) and x86 casts (INT_MIN "indefinite"). `has_reference()`: only HLSL is checked.
- DXC rules the binder enforces: a constructor needs exactly its component count (`float2(3.7)` is an error, the
  cast splats); vector↔matrix converts at the same size or through a 1×N / N×1 matrix; `normalize`, `reflect`,
  `refract`, `faceforward` reject matrices; `%` rejects doubles. Untyped literals convert at compile time (no
  flush; `%` is fmod), and a float tested as a condition is compared with 0 (flushed).
- Units: `units.rs` is kalk's table translated (two data errors fixed), with every SI prefix, names and plurals,
  bytes as an 8th base dimension and `nit`; a name the code declares stays the variable (the calculator and
  worksheets reparse without it). `evaluation/unit_checker.rs` reports mismatches without stopping.
- Symbols are `Arc<VariableSymbol>` / `Arc<FunctionSymbol>`, hashed and compared by id. Function bodies are set
  after binding (RwLock fields), and `BoundProgram`'s `Drop` clears them to break recursion cycles.
- The evaluator stops through `Interrupt::{Error, NotConstant, Cancelled}`; the cancellation flag is checked every
  0x1000 steps.
- Parity with the C# version: `values.rs` reproduces .NET's `"R"` float formatting, `as` casts saturate like .NET 9,
  `exp2` is `powf(2, x)` (.NET `float.Exp2`), HLSL's `f16tof32` comes from the `half` crate.

## Worksheet (`worksheet::evaluate`)

- All tabs are one program in tab order. Declarations are hoisted; top-level lines run in order.
- A newline ends a top-level statement unless a bracket is open (`Parser::at_line_break`).
- A top-level variable (declared, or `x = v` for a new name) is a global that functions can read.
- Each line gets its own evaluator over shared storage, its own diagnostics, and a snapshot of the globals it
  starts from (`LineResult::inputs`, used by the reference). A line with bind errors isn't run.

## Reference (`reference/`)

- `warp_device.rs`: DXC is loaded at runtime (`dxil.dll` first, then `dxcompiler.dll`) from `SHADERCALC_DXC_DIR`,
  else `<exe folder>/dxc`, else the exe folder. `.cargo/config.toml` points `SHADERCALC_DXC_DIR` at
  `third_party/dxc` for `cargo test` / `cargo run`. Signed DXIL → compute dispatch on one shared WARP device;
  `run_cached` caches by source and inputs.
- `checker.rs` emits a harness around the line (`emitter.rs`), in a block of its own (a line may redeclare a
  session variable). Uniforms, globals and session variables come from an input buffer (ordered by symbol id, so
  the HLSL is deterministic), and so do the line's literals, typed (`1e-40f`, `0x1u`) and untyped (converted to
  their type, constructor components too), so DXC can't constant-fold the line. `Intrinsic::
  constant_sensitive_arguments` keeps arguments like `pow`'s exponent literal (DXC lowers a constant 2
  differently). The substitution applies only while emitting the line (`Harness::is_emitting_line`).
- A shader that removes the WARP device (a double fma does) yields `ReferenceError::Crashed`; the dead device is
  dropped and recreated on the next run (D3D12 hands back the same device while any reference to it lives).
- Verdicts: bit-identical `✓`; `≈` for approximate intrinsics within 64 ulp or 0.0008 absolute; `⊘` WarpLimit when
  the line ran something WARP gets wrong (intrinsics note it in `IntrinsicContext::limit_reference`, carried in
  `LineResult::reference_limits`: sinh/cosh/tanh always, sin/cos/tan beyond ±100π or near a tan pole); `≠`
  otherwise.
- `tests/conformance.rs` sweeps an expression over special and random inputs in one dispatch. Set
  `SHADERCALC_CONFORMANCE_DUMP=<file>` to write the reports.
- Corpus status (2026-10-09): every line is `✓`, `≈` or `⊘` except the double `mad` that crashes WARP and three
  lines of sessions whose program has errors.

## App

- `store.rs`: every `*.hlsl` in `~/.shadercalc` (or `SHADERCALC_DATA_DIR`) is a tab. `state.json` holds the tab
  order, the active tab, the panel widths and the profile id (PascalCase keys, like the C# app). Writes go to a `.tmp` file
  and are renamed into place. Delete sends the file to the Recycle Bin (`trash`). Outside edits are found by
  mtime + length; the frontend polls `pollChanges` every second.
- Evaluation (`backend/lib.rs` + `app/src/main.ts`): the frontend debounces 200 ms. Each `evaluate` bumps a
  generation and cancels the previous run, which then returns `{cancelled: true}`. The frontend then calls
  `checkReference` line by line, active tab first; a stale generation returns null. Saves debounce 400 ms.
- Profile: the toolbar `<select>` (from `load`'s `profiles`) is sent with every `evaluate`; under a profile without
  a reference `checkReference` answers "not checked" and the status bar says so.
- `app/src/worksheet-view.ts`: the CodeMirror editor plus a result column aligned with `lineBlockAt`. No line
  wrapping: it would break the alignment. Squiggles and hover through `@codemirror/lint`, plus completion.
  `hlsl.ts`: a `StreamLanguage` from the legacy clike mode (`indentStatements: false`, else new lines indent).
- `inspector.ts`: type, units, per-component value/hex/bits, problems, the reference verdict and the emitted HLSL.
  `docs.ts`: the docs panel (`marked`).
- Docs: `crates/shadercalc-core/src/docs/reference.md` (`include_str!`) has the topics plus one `## name` page per
  intrinsic; the signature line is formatted `` `sig` · kinds ``. `tests/documentation.rs` checks that the pages and
  the intrinsic table match.
- Keys: F1 docs for the word at the caret, F2 rename, Ctrl+T new tab, Ctrl+S save, Esc focuses the editor.
- Window placement: `tauri-plugin-window-state`. The close request saves the worksheets and `state.json` first.
- DXC in the exe: `app/src-tauri/build.rs` requires `third_party/dxc`; `src/dxc.rs` embeds both DLLs
  (`include_bytes!`), unpacks them to `%LOCALAPPDATA%\ShaderCalc\dxc-<sizes>\` and sets `SHADERCALC_DXC_DIR` unless
  it is already set. The exe is ~30 MB.
- Icons: `app/icon.png` (window/page), `app/src-tauri/icons/` (exe).

## Build / test

- `scripts/fetch-dxc.ps1` once (or copy `dxcompiler.dll` + `dxil.dll` 1.9.2602 into `third_party/dxc`). Its
  download is byte-identical to the 1.9.2602.17 DLLs from NuGet `vortice.dxc.native` 1.0.5.
- `cargo test --workspace`: the reference and conformance tests run on WARP, so they need the DLLs.
- Frontend: `cd app && npm install && npm run build` (`tsc --noEmit` + `vite build`).
- Exe: `cd app && npx tauri build --no-bundle` → `target/release/shadercalc.exe`. The NSIS bundle target is
  configured but untested.

## UI testing (never steal the user's focus)

- Browser, for frontend work: `cargo run -p shadercalc-devserver` (with `SHADERCALC_DATA_DIR=Claude\ui-data` to
  keep the user's worksheets out of it) and `npm run dev` in `app`, then http://localhost:1420 in the built-in
  browser pane. Vite listens on `::1`: use `localhost`, not 127.0.0.1. Test hook:
  `window.shaderCalc.{results, activeName, tabNames, profile}`. Pane screenshots time out while the pane is hidden; read
  the page with `get_page_text` / `javascript_tool` instead.
- Desktop exe: hidden desktop only (shared memory `no-focus-steal-gui-testing`). The tooling is in `Claude/`
  (gitignored). `run-hidden.ps1 -Driver tauri-drive.ps1 -TimeoutSeconds 300` creates the `ShaderCalcTest` desktop
  and runs `tauri-drive.ps1` there: it seeds a data folder, starts `target/release/shadercalc.exe` without
  `SHADERCALC_DXC_DIR` (so the embedded DXC is used), runs `tauri-scenario.mjs`, closes the window with UI
  Automation's `WindowPattern.Close` and checks the saved files.
- `tauri-scenario.mjs` (node) drives the page through WebView2's DevTools port
  (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=...`, in a separate `WEBVIEW2_USER_DATA_FOLDER`).
  CDP input events reach the page without OS focus. It covers state.json, results and marks, the inspector, F1,
  outside edits, Ctrl+T, typing + autosave, F2 rename, completion, Delete + confirm, and page errors. Wait 200 ms
  before accepting a completion: CodeMirror ignores Enter for 75 ms after the list opens.
- The watchdog only acts on the PIDs drivers write to `Claude/out/app.pid`, so a ShaderCalc the user runs is never
  killed. It kills the run's app if a window of it shows up on the user's desktop.
- Corpus against WARP: `Claude/diff/` (`make_corpus.py` → `corpus.json`, `rust/` runner). `DIFF_DETAILS=1` prints
  WARP's value and why a line wasn't checked; `mismatches.py` lists the lines without a ✓, `changes.py` diffs two
  runs. The C# runner (`csharp/`) needs a checkout of `5671300`.
- `Claude/probe` (dxc-probe): `rules.txt`-style files of statements compiled by DXC (does DXC accept this?), and
  `--eval file` to run lines through the interpreter and the reference (`PROBE_PROFILE=slang-cpu`, `PROBE_HLSL=1`
  for the harness, `PROBE_BITS=1`).
