#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum RoundTies {
    ToEven,
    AwayFromZero,
}

/// The points where HLSL implementations disagree, so another backend (e.g. Slang's CPU target) can be selected
/// later. The default is HLSL as DXC compiles it and WARP executes it.
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct SemanticsProfile {
    /// round(): DXIL Round_ne rounds ties to even; C's roundf (Slang CPU) rounds them away from zero.
    pub round_ties: RoundTies,
    /// DXC masks shift counts to the operand width (`x << (n & 31)`); C++ leaves larger counts undefined.
    pub mask_shift_count: bool,
    /// mad() and mul() lower to DXIL FMad; WARP rounds the product then the sum (not fused).
    pub fuse_mad: bool,
    /// Width of unsuffixed integer literal arithmetic: DXC folds literal-only expressions in 64 bits.
    pub literal_int_bits: u32,
    /// Float to int conversions saturate and map NaN to 0 (D3D ftoi/ftou) instead of being undefined.
    pub saturate_float_to_int: bool,
    /// 32-bit float operations flush denormal inputs and results to a zero of the same sign (D3D float rules;
    /// WARP does). Data movement (copies, asuint/asfloat) keeps them.
    pub flush_float_denormals: bool,
    /// f32tof16 rounds toward zero (WARP): 0.9999999 → 0x3BFF, finite overflow → ±65504, NaN → 0x7FFF.
    /// False: round to nearest even.
    pub half_conversion_toward_zero: bool,
    /// Float `a % b` (DXIL frem) as WARP computes it: (q - trunc(q)) * b with q = a / b, so 3 % INF is NaN.
    /// False: C's exact fmod.
    pub float_remainder_from_quotient: bool,
    /// Signed x / 0, x % 0 and INT_MIN / -1 (undefined) give INT_MAX, as on WARP.
    pub undefined_signed_division_is_max: bool,
    /// uint → float rounds twice for values ≥ 2^31, as WARP converts them: (float)(int)(x - 2^31) + 2^31.
    /// False: one correct rounding.
    pub unsigned_to_float_via_signed: bool,
}

impl SemanticsProfile {
    pub const HLSL: SemanticsProfile = SemanticsProfile {
        round_ties: RoundTies::ToEven,
        mask_shift_count: true,
        fuse_mad: false,
        literal_int_bits: 64,
        saturate_float_to_int: true,
        flush_float_denormals: true,
        half_conversion_toward_zero: true,
        float_remainder_from_quotient: true,
        undefined_signed_division_is_max: true,
        unsigned_to_float_via_signed: true,
    };
}

impl Default for SemanticsProfile {
    fn default() -> SemanticsProfile {
        SemanticsProfile::HLSL
    }
}
