# <img src="app/icon.png" width="48" align="top" alt=""> ShaderCalc

A Windows desktop worksheet for checking shader maths. Paste HLSL (or plain C++ math) functions, call them on
the lines below, and every line shows its result, checked against a real HLSL compiler and GPU executor.

```hlsl
float KineticEnergy(float m, float v) { return 0.5 * m * v * v; }
KineticEnergy(2 kg, 3 m/s)                 // ✓ 9 kg·m²/s² (J)
float3 n = normalize(float3(1, 2, 3))      // ✓ float3(0.26726124, 0.5345225, 0.8017837)
asuint(n.x)                                // ✓ 1049155191
tanh(7.0)                                  // ⊘ 0.99999833 (WARP's tanh is off: 1.0364964)
```

- **Worksheet**: each tab is an `.hlsl` file. The first, `scratch`, is a scratch pad whose lines show their value
  on the right; the other tabs are libraries it sees, as if included in front of it. Each library also runs on its
  own, so its own lines are local tests and a dependency on another library shows up as an error. A line break
  ends a line. Results update as you type. Inside loops and ifs every statement shows its value too, with an
  iteration stepper on each loop and every iteration listed in the inspector.
- **Library**: a side tab lists what every library declares (functions, structs, macros, globals) with their
  comments; a click types the call into the scratch pad, Ctrl+click opens the declaration.
- **HLSL semantics**: a custom interpreter computes what DXC + WARP compute: 64-bit literals until they meet a
  type, denormal flushing, unfused `mad`, masked shift counts, round-half-even, DXC's lowering of intrinsics
  (`pow`, `smoothstep`, `normalize`, `fmod`, ...). Scalars (incl. `int64`/`double`), vectors, matrices,
  arrays, structs and about 70 intrinsics.
- **Reference check**: every line is also compiled by DXC and run on Direct3D 12's WARP adapter, then compared
  bit by bit: `✓` identical, `≈` within tolerance on functions GPUs approximate, `≠` different, `⊘` different
  where WARP itself is known to be wrong (hyperbolic functions, trigonometry beyond ±100π).
- **Units**: `3 km`, `9.81 m/s^2`, `100 nit`, `16 MiB` follow the value through every operation; mismatches are
  reported where they happen. SI units with every prefix, bytes and bits, US units.
- **Profiles**: HLSL as DXC and WARP compute it (the default), or Slang's CPU target (C rules, the C library's
  functions, a 16-bit `half`), switched in the toolbar.
- **Bits**: the inspector shows each component's type, value, hex and bits (in groups of 4 under their bit
  index, a float's sign, exponent and mantissa coloured, the bits that differ from WARP marked), the problems on
  the line and the reference verdict.
- **Docs**: F1 on a name opens its page (signature, DXC lowering, WARP behaviour); the Docs tab searches them.

Worksheets live in `~/.shadercalc` (one `.hlsl` per tab, edits made outside the app reload live).

The exe is portable: download `shadercalc.exe` from the
[latest release](https://github.com/gaetan-deturche/ShaderCalc/releases/latest) and run it. It looks for a newer
release at start-up and every hour (Check for updates in the status bar, next to the version, looks now); the
status bar offers it, and on a click the app checks its signature, replaces its own exe and restarts.

## Build

Requires Windows 10/11 (WARP and WebView2 ship with Windows), a current stable Rust and Node.js 22+.

```
powershell -File scripts\fetch-dxc.ps1
cargo test --workspace
cd app
npm install
npx tauri build --no-bundle
```

`fetch-dxc.ps1` downloads the DirectX Shader Compiler DLLs into `third_party/dxc` (once). The exe is
`target/release/shadercalc.exe`, a single file: the DXC DLLs are embedded and unpacked to
`%LOCALAPPDATA%\ShaderCalc` on first run.

To work on the UI in a browser, serve the backend over HTTP, start Vite, then open http://localhost:1420:

```
cargo run -p shadercalc-devserver
cd app
npm run dev
```

## Releasing

Once: `scripts\new-signing-key.ps1` creates the update signing key. Its private half goes straight into the
repository's `SHADERCALC_SIGNING_KEY` Actions secret; its public half is written to
`app/src-tauri/update-public-key.txt` and must be committed (a build without it never updates).

Each release:

```
powershell -File scripts\bump-version.ps1 1.2.3
git commit -am "Version 1.2.3"
git tag v1.2.3
git push origin main v1.2.3
```

The tag starts `.github/workflows/release.yml`: it tests, builds the exe, signs `update.json` and publishes the
GitHub release, with the commits since the previous tag as notes.

## Credits

`third_party/kalk/units.kalk` comes from [kalk](https://github.com/xoofx/kalk) by Alexandre Mutel
(BSD-2-Clause, `third_party/kalk/license.txt`). Reference compiler:
[DirectX Shader Compiler](https://github.com/microsoft/DirectXShaderCompiler). The Slang CPU profile follows
the C++ prelude and core module of [Slang](https://github.com/shader-slang/slang) (Apache-2.0 WITH LLVM-exception). Built with
[Tauri](https://tauri.app), [CodeMirror](https://codemirror.net) and [windows-rs](https://github.com/microsoft/windows-rs).
