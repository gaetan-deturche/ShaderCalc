#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum RoundTies {
    ToEven,
    AwayFromZero,
}

/// Where intrinsic formulas come from.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum Lowering {
    /// DXC's lowering to DXIL, as WARP runs it (pow = exp2(log2(x) * y), normalize = v * rsqrt(dot)...).
    Dxc,
    /// Slang's CPU target: its C++ prelude (powf, expf, fminf...) and the core module's default bodies
    /// (normalize = x / length(x), smoothstep = t * t * (3 - (t + t))...).
    SlangCpu,
}

/// The points where HLSL implementations disagree. HLSL is DXC compiling and WARP executing; SLANG_CPU is Slang's
/// C++ (CPU) target.
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct SemanticsProfile {
    /// Short name, saved in state.json and sent by the UI.
    pub id: &'static str,
    pub label: &'static str,
    pub lowering: Lowering,
    /// round(): DXIL Round_ne rounds ties to even; C's roundf (Slang CPU) rounds them away from zero.
    pub round_ties: RoundTies,
    /// DXC masks shift counts to the operand width (`x << (n & 31)`); C++ leaves larger counts undefined.
    pub mask_shift_count: bool,
    /// mad() lowers to DXIL FMad, which WARP rounds twice (product, then sum); Slang CPU calls fmaf.
    pub fuse_mad: bool,
    /// Width of unsuffixed integer literal arithmetic: DXC folds literal-only expressions in 64 bits; in Slang an
    /// unsuffixed literal is an int (int64_t when it doesn't fit).
    pub literal_int_bits: u32,
    /// Width of unsuffixed float literals: DXC's literal float is 64-bit; Slang's is a float.
    pub literal_float_bits: u32,
    /// `half` is a 16-bit type (Slang CPU); DXC without -enable-16bit-types compiles it as float.
    pub half_is_16_bit: bool,
    /// Float to int conversions saturate and map NaN to 0 (D3D ftoi/ftou). False: C++ casts as x86-64 does them
    /// (NaN and out of range give INT_MIN; uint goes through int64).
    pub saturate_float_to_int: bool,
    /// 32-bit float operations flush denormal inputs and results to a zero of the same sign (D3D float rules;
    /// WARP does). Data movement (copies, asuint/asfloat) keeps them.
    pub flush_float_denormals: bool,
    /// f32tof16 rounds toward zero (WARP): 0.9999999 → 0x3BFF, finite overflow → ±65504, NaN → 0x7FFF.
    /// False: round to nearest even (Slang's f32tof16).
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
        id: "hlsl",
        label: "HLSL (DXC + WARP)",
        lowering: Lowering::Dxc,
        round_ties: RoundTies::ToEven,
        mask_shift_count: true,
        fuse_mad: false,
        literal_int_bits: 64,
        literal_float_bits: 64,
        half_is_16_bit: false,
        saturate_float_to_int: true,
        flush_float_denormals: true,
        half_conversion_toward_zero: true,
        float_remainder_from_quotient: true,
        undefined_signed_division_is_max: true,
        unsigned_to_float_via_signed: true,
    };

    /// Slang's C++ target compiled for x86-64 with a regular C++ compiler (the slang-llvm build differs: its
    /// fma is a * b + c).
    pub const SLANG_CPU: SemanticsProfile = SemanticsProfile {
        id: "slang-cpu",
        label: "Slang CPU",
        lowering: Lowering::SlangCpu,
        round_ties: RoundTies::AwayFromZero,
        mask_shift_count: false,
        fuse_mad: true,
        literal_int_bits: 32,
        literal_float_bits: 32,
        half_is_16_bit: true,
        saturate_float_to_int: false,
        flush_float_denormals: false,
        half_conversion_toward_zero: false,
        float_remainder_from_quotient: false,
        undefined_signed_division_is_max: false,
        unsigned_to_float_via_signed: false,
    };

    pub const ALL: [&'static SemanticsProfile; 2] = [&SemanticsProfile::HLSL, &SemanticsProfile::SLANG_CPU];

    pub fn by_id(id: &str) -> Option<&'static SemanticsProfile> {
        SemanticsProfile::ALL.into_iter().find(|profile| profile.id == id)
    }

    /// Whether the DXC + WARP reference checks this profile's results.
    pub fn has_reference(&self) -> bool {
        self.lowering == Lowering::Dxc
    }
}

impl Default for SemanticsProfile {
    fn default() -> SemanticsProfile {
        SemanticsProfile::HLSL
    }
}
