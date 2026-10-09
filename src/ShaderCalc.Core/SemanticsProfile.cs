namespace ShaderCalc;

/// <summary>
/// The points where HLSL implementations disagree, so another backend (e.g. Slang's CPU target) can be selected
/// later. The default is HLSL as DXC compiles it and WARP executes it.
/// </summary>
public sealed record SemanticsProfile
{
    public static SemanticsProfile Hlsl { get; } = new SemanticsProfile();

    /// <summary>round(): DXIL Round_ne rounds ties to even; C's roundf (Slang CPU) rounds them away from zero.</summary>
    public MidpointRounding RoundTies { get; init; } = MidpointRounding.ToEven;

    /// <summary>DXC masks shift counts to the operand width (`x &lt;&lt; (n &amp; 31)`); C++ leaves larger counts undefined.</summary>
    public bool MaskShiftCount { get; init; } = true;

    /// <summary>mad() and mul() lower to DXIL FMad; WARP rounds the product then the sum (not fused).</summary>
    public bool FuseMad { get; init; }

    /// <summary>Width of unsuffixed integer literal arithmetic: DXC folds literal-only expressions in 64 bits.</summary>
    public int LiteralIntBits { get; init; } = 64;

    /// <summary>Float to int conversions saturate and map NaN to 0 (D3D ftoi/ftou) instead of being undefined.</summary>
    public bool SaturateFloatToInt { get; init; } = true;

    /// <summary>
    /// 32-bit float operations flush denormal inputs and results to a zero of the same sign (D3D float rules;
    /// WARP does). Data movement (copies, asuint/asfloat) keeps them.
    /// </summary>
    public bool FlushFloatDenormals { get; init; } = true;

    /// <summary>
    /// f32tof16 rounds toward zero (WARP): 0.9999999 → 0x3BFF, finite overflow → ±65504, NaN → 0x7FFF.
    /// False: round to nearest even.
    /// </summary>
    public bool HalfConversionTowardZero { get; init; } = true;

    /// <summary>
    /// Float `a % b` (DXIL frem) as WARP computes it: (q - trunc(q)) * b with q = a / b, so 3 % INF is NaN.
    /// False: C's exact fmod.
    /// </summary>
    public bool FloatRemainderFromQuotient { get; init; } = true;

    /// <summary>Signed x / 0, x % 0 and INT_MIN / -1 (undefined) give INT_MAX, as on WARP.</summary>
    public bool UndefinedSignedDivisionIsMax { get; init; } = true;

    /// <summary>
    /// uint → float rounds twice for values ≥ 2^31, as WARP converts them: (float)(int)(x - 2^31) + 2^31.
    /// False: one correct rounding.
    /// </summary>
    public bool UnsignedToFloatViaSigned { get; init; } = true;
}
