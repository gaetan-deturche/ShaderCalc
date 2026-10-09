<!-- group: Worksheet -->
## Worksheet
Each tab is an `.hlsl` file in the data folder. All tabs form one program, like headers included in tab order:

- Functions, structs, `typedef`s, `#define`s and `cbuffer`s are shared by every tab and can be used before they are declared.
- Top-level lines run in tab order, then top to bottom. A line break ends a line (no `;` needed) unless a bracket is still open.
- A line shows its value on the right: an expression, an assignment, or the variable a declaration creates.
- Variables created at the top level (`float3 n = ...`, or `x = 3` for a new name) are globals: later lines and functions can read them.
- An error stays on its line; the lines after it still run.

```
float KineticEnergy(float m, float v) { return 0.5 * m * v * v; }
KineticEnergy(2 kg, 3 m/s)
float3 n = normalize(float3(1, 2, 3))
asuint(n.x)
```

Click a result (or put the caret on its line) to see its type, every component's bits, its units and the reference check.

## Reference check
Every result also runs on a real HLSL compiler and GPU executor: DXC compiles the line (with the code it uses) to DXIL, and Direct3D 12's WARP adapter, the software GPU shipped with Windows, executes it. Literal arguments, variables and uniforms are fed through a buffer, so WARP really computes the line instead of DXC folding it.

| Mark | Meaning |
|---|---|
| `✓` | bit-identical |
| `≈` | differs only through functions GPUs approximate (`sin`, `exp2`, `log2`...), within 64 ulp or 0.0008 |
| `≠` | different: the inspector shows WARP's value |
| `…` | still running |

The interpreter keeps the exact maths for approximate functions; a `≠` there shows how far one GPU implementation is from it (WARP's `asin(0)` is `6.76e-5`, its `tanh(100)` is NaN).

## HLSL semantics
The interpreter computes what DXC + WARP compute, including the places where they differ from plain IEEE or C code:

- Float denormals are flushed to zero, on input and on output (D3D float rules). Copies and `asuint`/`asfloat` keep them.
- `mad` and `mul` are not fused: the product is rounded, then the sum.
- Float `a % b` is `(q - trunc(q)) * b` with `q = a / b`, so `3.0 % INF` is NaN.
- `f32tof16` rounds toward zero: `0.9999999` gives `0x3BFF`, large values clamp to 65504.
- Signed `x / 0`, `x % 0` and `INT_MIN / -1` give `INT_MAX`; unsigned division by 0 gives `0xFFFFFFFF`.
- `uint` to `float` above 2^31 rounds twice.
- Shift counts are masked: `x << 33` is `x << 1`.
- `min`/`max` with a NaN return the other operand.
- `round` rounds halves to even; float to int truncates and saturates, NaN gives 0.

## Literals and types
- Unsuffixed literals are 64-bit (`literal int`, `literal float`) until they meet a type, as in DXC: `int(1 << 33)` is 0 but `int x = 1; x << 33` is 2.
- `1u` uint, `1l` int64, `1ul` uint64, `1.0f` float, `1.0h` half, `1.0L` double. `010` is octal, `0x10` hex.
- `half` and the `min16*` types are 32-bit, as DXC compiles them without `-enable-16bit-types`.
- Scalars, vectors (`float3`, `vector<float, 3>`), matrices (`float3x3`, row by row), arrays, structs.
- Swizzles: `.xyzw`, `.rgba`; matrix elements `._m01` (0-based) or `._12` (1-based).
- HLSL 2021 rules: `&&`, `||` and `?:` need scalars; use `and()`, `or()` and `select()` for vectors.

## Units
A number followed by a unit is a unit literal in SI: `3 km`, `9.81 m/s^2`, `100 cd`, `2 m^-1`. The unit follows the value through every operation and function:

- `+ - %`, comparisons, `min`, `max`, `clamp`, `lerp` need the same unit. A bare number takes the other operand's unit (`d + 1.0` with `d` in metres).
- `*` and `/` combine units; `sqrt` halves the exponents; `pow` needs a constant exponent.
- `sin`, `exp`, `log`, `saturate`... need a dimensionless value.
- A mismatch is reported where it happens, inside the function, without stopping the evaluation.

Known units: m km cm mm um nm, s ms us ns min h, kg g, A, K, mol, cd lm lx, N J W Hz, rad sr.

## C++ code
Pasted C++ math works: `const T&` parameters are inputs, `T&` are inout, `std::` names are the HLSL intrinsics (`std::clamp`, `std::sqrt`), `static_cast<T>(x)`, `auto`, `inline`, `constexpr`, `namespace`, `using X = T;`, brace initialisation. `#include` lines are ignored.

## Not supported
Resources (textures, buffers, samplers: their declarations are skipped), pixel-shader functions (`ddx`, `clip`, `discard`), wave and atomic operations, 16-bit types, templates, struct member functions, recursion (HLSL forbids it).

<!-- group: Rounding -->
## floor
`T floor(T x)` · float
Largest integer not greater than `x`, per component. Keeps the unit.

## ceil
`T ceil(T x)` · float
Smallest integer not less than `x`, per component. Keeps the unit.

## trunc
`T trunc(T x)` · float
Integer part of `x` (rounds toward zero). Keeps the unit.

## round
`T round(T x)` · float
Nearest integer; halves go to the even one (`round(2.5)` is 2, `round(-0.5)` is -0). C's `roundf` rounds halves away from zero instead.

## frac
`T frac(T x)` · float
`x - floor(x)`, kept below 1: `frac(-1e-8)` is `0.99999994` on WARP, not 1.

## saturate
`T saturate(T x)` · float
Clamps to [0, 1]; NaN gives 0, -0 gives +0. Needs a dimensionless value.

<!-- group: Arithmetic -->
## abs
`T abs(T x)` · numeric
Absolute value. Floats clear the sign bit (a denormal is flushed first); integers compute `max(x, -x)`, so `abs(INT_MIN)` stays `INT_MIN`.

## sign
`int_T sign(T x)` · numeric
-1, 0 or 1 per component, as an int. NaN gives 0.

## min
`T min(T a, T b)` · numeric
Smaller component. A NaN second operand returns the first one untouched; a NaN first operand returns the second one. Same unit needed.

## max
`T max(T a, T b)` · numeric
Larger component, NaN handled as in `min`. Same unit needed.

## clamp
`T clamp(T x, T low, T high)` · numeric
`min(max(x, low), high)`. Same unit needed.

## mad
`T mad(T a, T b, T c)` · numeric
`a * b + c`. DXIL FMad, which WARP does not fuse: the product is rounded, then the sum.

## fma
`double fma(double a, double b, double c)`
Fused multiply-add on doubles: one rounding.

## rcp
`T rcp(T x)` · float or double
`1 / x` (a division, correctly rounded). The unit is inverted.

## fmod
`T fmod(T x, T y)` · float
DXC computes `±frac(|x / y|) * y` (sign of `x / y`), not C's exact `fmod`. Same unit needed.

## ldexp
`T ldexp(T x, T e)` · float
`x * exp2(e)`. Approximate (through `exp2`).

## modf
`T modf(T x, out T integer)` · float
Splits `x`: `integer = trunc(x)`, returns `x - trunc(x)`.

## frexp
`T frexp(T x, out T exponent)` · float
Mantissa in [0.5, 1) and exponent, read from the float's bits as DXC does: the mantissa comes back without the sign (`frexp(-8)` gives 0.5 and 4).

<!-- group: Exponentials and roots -->
## sqrt
`T sqrt(T x)` · float
Square root, correctly rounded. Halves the unit's exponents (`sqrt(4 m^2)` is `2 m`).

## rsqrt
`T rsqrt(T x)` · float
`1 / sqrt(x)`; WARP's DXIL Rsqrt can differ by 1 ulp. Approximate.

## exp
`T exp(T x)` · float
DXC computes `exp2(x * 1.44269502)`. Approximate. Needs a dimensionless value.

## exp2
`T exp2(T x)` · float
`2^x` (DXIL Exp). Approximate.

## log
`T log(T x)` · float
DXC computes `log2(x) * 0.693147182`. Approximate.

## log2
`T log2(T x)` · float
Base-2 logarithm (DXIL Log). Approximate: near 1 the error is small in absolute terms but large in ulps.

## log10
`T log10(T x)` · float
DXC computes `log2(x) * 0.30103001`. Approximate.

## pow
`T pow(T x, T y)` · float
DXC computes `exp2(log2(x) * y)`, so a negative `x` gives NaN: `pow(-2, 3)` is NaN on a GPU. Only a constant exponent of exactly 2 becomes `x * x` (`pow(-2, 2)` is 4). A unit needs a constant exponent: `pow(3 m, 2)` is `9 m²`.

<!-- group: Trigonometry -->
## sin
`T sin(T x)` · float
Sine (radians). Approximate: GPUs lose precision for large arguments (WARP gives 0 for `sin(1e30)`). Needs a dimensionless value.

## cos
`T cos(T x)` · float
Cosine (radians). Approximate.

## tan
`T tan(T x)` · float
Tangent. Approximate.

## sincos
`void sincos(T x, out T s, out T c)` · float
Writes `sin(x)` and `cos(x)`.

## asin
`T asin(T x)` · float
Arc sine. Approximate; WARP's is coarse (`asin(0)` gives `6.76e-5`).

## acos
`T acos(T x)` · float
Arc cosine. Approximate.

## atan
`T atan(T x)` · float
Arc tangent. Approximate.

## atan2
`T atan2(T y, T x)` · float
Angle of (x, y). DXC computes `atan(y / x)` and fixes the quadrant. Approximate. `y` and `x` need the same unit; the result is dimensionless.

## sinh
`T sinh(T x)` · float
Hyperbolic sine. Approximate (WARP stays finite where the exact value overflows).

## cosh
`T cosh(T x)` · float
Hyperbolic cosine. Approximate.

## tanh
`T tanh(T x)` · float
Hyperbolic tangent. Approximate: WARP returns NaN for large arguments where the exact value is 1.

## degrees
`T degrees(T x)` · float
`x * 57.2957802`.

## radians
`T radians(T x)` · float
`x * 0.0174532924`.

<!-- group: Interpolation -->
## lerp
`T lerp(T a, T b, T t)` · float
`a + t * (b - a)`. `a` and `b` need the same unit; `t` dimensionless.

## smoothstep
`T smoothstep(T low, T high, T x)` · float
With `t = saturate((x - low) / (high - low))`, DXC computes `t * (t * (3 - t * 2))`. Dimensionless result.

## step
`T step(T edge, T x)` · float
`x < edge ? 0 : 1` (NaN gives 1).

<!-- group: Vectors and matrices -->
## dot
`scalar dot(vector a, vector b)` · numeric
Sum of the products, in order (DXIL Dot2/3/4 for floats). The units multiply.

## cross
`float3 cross(float3 a, float3 b)`
`a.yzx * b.zxy - a.zxy * b.yzx`.

## length
`scalar length(vector v)` · float
`sqrt(x*x + y*y + ...)` (for a scalar: `abs(x)`). Keeps the unit.

## distance
`scalar distance(vector a, vector b)` · float
`length(a - b)`.

## normalize
`vector normalize(vector v)` · float
DXC computes `v * rsqrt(dot(v, v))`, so the result can differ from `v / length(v)` by an ulp. Dimensionless result.

## reflect
`vector reflect(vector i, vector n)` · float
DXC computes `i - n * (dot(i, n) * 2)`.

## refract
`vector refract(vector i, vector n, scalar eta)` · float
`k = 1 - eta*eta*(1 - dot(i, n)²)`; zero when `k < 0`, otherwise `i*eta - (sqrt(k) + dot(i, n)*eta) * n`.

## faceforward
`vector faceforward(vector n, vector i, vector ng)` · float
`dot(i, ng) < 0 ? n : -n`.

## mul
`mul(a, b)` · numeric
Matrix product; a vector is a row on the left and a column on the right, a scalar scales. Each result is a multiply-add chain, not fused on WARP. `mul(v, v)` is a dot product.

## transpose
`matrix transpose(matrix m)`
Rows become columns.

## determinant
`float determinant(floatNxN m)`
Laplace expansion along the first row, in DXC's order.

## lit
`float4 lit(float n_dot_l, float n_dot_h, float m)`
`(1, n_dot_l < 0 ? 0 : n_dot_l, n_dot_l < 0 || n_dot_h < 0 ? 0 : pow(n_dot_h, m), 1)`.

## dst
`float4 dst(float4 a, float4 b)`
`(1, a.y * b.y, a.z, b.w)` (distance vector).

<!-- group: Logic -->
## any
`bool any(T x)`
True if any component is non-zero.

## all
`bool all(T x)`
True if every component is non-zero.

## and
`bool_T and(T a, T b)`
Component-wise logical and (HLSL 2021; `&&` only takes scalars).

## or
`bool_T or(T a, T b)`
Component-wise logical or (HLSL 2021).

## select
`T select(bool_T condition, T a, T b)`
Component-wise `condition ? a : b` (HLSL 2021; `?:` only takes a scalar condition).

## isnan
`bool_T isnan(T x)` · float
True for NaN components.

## isinf
`bool_T isinf(T x)` · float
True for infinite components.

## isfinite
`bool_T isfinite(T x)` · float
True for components neither NaN nor infinite.

<!-- group: Bits -->
## countbits
`uint_T countbits(T x)` · int, uint
Number of set bits.

## reversebits
`T reversebits(T x)` · int, uint
Bit order reversed (bit 0 becomes bit 31).

## firstbitlow
`T firstbitlow(T x)` · int, uint
Index of the lowest set bit; -1 (`0xFFFFFFFF`) when none.

## firstbithigh
`T firstbithigh(T x)` · int, uint
Index of the highest set bit; for a negative int, of the highest bit that differs from the sign. -1 when none.

## asuint
`uint_T asuint(T x)` · int, uint, float
The same 32 bits read as uint. `asuint(double d, out uint low, out uint high)` splits a double.

## asint
`int_T asint(T x)` · int, uint, float
The same 32 bits read as int.

## asfloat
`float_T asfloat(T x)` · int, uint, float
The same 32 bits read as float (denormals and NaN payloads are kept).

## asdouble
`double_T asdouble(uint low, uint high)`
A double from its two 32-bit halves.

## f32tof16
`uint_T f32tof16(float_T x)`
The half-precision bits of `x`, in the low 16 bits. WARP rounds toward zero: values beyond the half range clamp to 65504, NaN gives `0x7FFF`.

## f16tof32
`float_T f16tof32(uint_T x)`
The float value of the half in the low 16 bits (exact).

## D3DCOLORtoUBYTE4
`int4 D3DCOLORtoUBYTE4(float4 color)`
`int4(color.zyxw * 255.001953)`: swizzled, scaled and truncated.
