# <img src="src/ShaderCalc.App/Assets/shadercalc.png" width="48" align="top" alt=""> ShaderCalc

A Windows desktop worksheet for checking shader maths. Paste HLSL (or plain C++ math) functions, call them on
the lines below, and every line shows its result, checked against a real HLSL compiler and GPU executor.

```hlsl
float KineticEnergy(float m, float v) { return 0.5 * m * v * v; }
KineticEnergy(2 kg, 3 m/s)                 // ✓ 9 kg·m²/s² (J)
float3 n = normalize(float3(1, 2, 3))      // ✓ float3(0.26726124, 0.5345225, 0.8017837)
asuint(n.x)                                // ✓ 1049155191
tanh(100.0)                                // ≠ 1 (WARP gives NaN)
```

- **Worksheet**: each tab is an `.hlsl` file, and all tabs form one program, like headers included in tab order.
  Functions, structs, `#define`s and globals are shared; top-level lines run in order and show their value on
  the right. A line break ends a line. Results update as you type.
- **HLSL semantics**: a custom interpreter computes what DXC + WARP compute: 64-bit literals until they meet a
  type, denormal flushing, unfused `mad`, masked shift counts, round-half-even, DXC's lowering of intrinsics
  (`pow`, `smoothstep`, `normalize`, `fmod`, ...). Scalars (incl. `int64`/`double`), vectors, matrices,
  arrays, structs and about 70 intrinsics.
- **Reference check**: every line is also compiled by DXC and run on Direct3D 12's WARP adapter, then compared
  bit by bit: `✓` identical, `≈` within tolerance on functions GPUs approximate, `≠` different.
- **Units**: `3 km`, `9.81 m/s^2`, `100 cd` follow the value through every operation; mismatches are reported
  where they happen.
- **Bits**: the inspector shows each component's type, value, hex and bit pattern, the problems on the line
  and the reference verdict.
- **Docs**: F1 on a name opens its page (signature, DXC lowering, WARP behaviour); the Docs tab searches them.

Worksheets live in `~/.shadercalc` (one `.hlsl` per tab, edits made outside the app reload live).

## Build

```
dotnet build ShaderCalc.sln
dotnet test tests/ShaderCalc.Tests
```

Requires the .NET 9 SDK (pinned in `global.json`) and Windows 10/11 (WARP ships with Windows).

Single-file exe:

```
dotnet publish src/ShaderCalc.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Credits

`third_party/kalk/units.kalk` comes from [kalk](https://github.com/xoofx/kalk) by Alexandre Mutel
(BSD-2-Clause, `third_party/kalk/license.txt`). DXC via [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows),
editor by [AvalonEdit](https://github.com/icsharpcode/AvalonEdit).
