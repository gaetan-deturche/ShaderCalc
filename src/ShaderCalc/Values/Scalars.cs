namespace ShaderCalc;

/// <summary>
/// Encoding of one scalar component in a ulong: Int is sign-extended, UInt zero-extended, floats keep their exact
/// IEEE bits (so NaN payloads survive), literals are int64 / double.
/// </summary>
public static class Scalars
{
    public static ulong FromBool(bool value) => value ? 1UL : 0UL;

    public static ulong FromInt(int value) => (ulong)(long)value;

    public static ulong FromUInt(uint value) => value;

    public static ulong FromInt64(long value) => (ulong)value;

    public static ulong FromHalf(Half value) => BitConverter.HalfToUInt16Bits(value);

    public static ulong FromFloat(float value) => BitConverter.SingleToUInt32Bits(value);

    public static ulong FromDouble(double value) => BitConverter.DoubleToUInt64Bits(value);

    public static float ToFloat(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);

    public static double ToDouble(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    public static Half ToHalf(ulong bits) => BitConverter.UInt16BitsToHalf((ushort)bits);

    /// <summary>A float kind's value as a double (exact for half, float and double).</summary>
    public static double FloatValue(ScalarKind kind, ulong bits) => kind switch
    {
        ScalarKind.Half => (double)ToHalf(bits),
        ScalarKind.Float => ToFloat(bits),
        _ => ToDouble(bits),
    };

    /// <summary>An integer or bool kind's value as a signed 64-bit integer (UInt64 reinterpreted).</summary>
    public static long IntegerValue(ScalarKind kind, ulong bits) => kind switch
    {
        ScalarKind.UInt => (uint)bits,
        _ => (long)bits,
    };

    /// <summary>Any kind's value as a double, for display and unit arithmetic.</summary>
    public static double ToNumber(ScalarKind kind, ulong bits) => kind switch
    {
        ScalarKind.Bool => bits != 0 ? 1 : 0,
        ScalarKind.UInt64 => bits,
        _ when kind.IsFloat() => FloatValue(kind, bits),
        _ => IntegerValue(kind, bits),
    };

    public static bool IsTrue(ScalarKind kind, ulong bits) => kind.IsFloat() ? FloatValue(kind, bits) != 0 || double.IsNaN(FloatValue(kind, bits)) : bits != 0;

    /// <summary>Wraps an integer result to the kind's width.</summary>
    public static ulong WrapInteger(ScalarKind kind, long value) => kind switch
    {
        ScalarKind.Bool => value != 0 ? 1UL : 0UL,
        ScalarKind.Int => (ulong)(long)(int)value,
        ScalarKind.UInt => (uint)value,
        _ => (ulong)value,
    };

    /// <summary>Encodes a float result in the kind (rounding to its precision).</summary>
    public static ulong EncodeFloat(ScalarKind kind, double value, SemanticsProfile profile) => kind switch
    {
        ScalarKind.Half => FromHalf((Half)value),
        ScalarKind.Float => FromFloat(Flush((float)value, profile)),
        _ => FromDouble(value),
    };

    public static float Flush(float value, SemanticsProfile profile) =>
        profile.FlushFloatDenormals && float.IsSubnormal(value) ? (float.IsNegative(value) ? -0f : 0f) : value;

    /// <summary>A float operand as an arithmetic operation sees it: 32-bit denormals flushed when the profile says so.</summary>
    public static double ReadFloat(ScalarKind kind, ulong bits, SemanticsProfile profile) => kind switch
    {
        ScalarKind.Float => Flush(ToFloat(bits), profile),
        ScalarKind.LiteralFloat => ToDouble(bits),
        _ => FloatValue(kind, bits),
    };

    /// <summary>HLSL conversion between scalar kinds (casts, implicit conversions, constructors).</summary>
    public static ulong Convert(ScalarKind from, ScalarKind to, ulong bits, SemanticsProfile profile)
    {
        if (from == to)
        {
            return bits;
        }
        if (to == ScalarKind.Bool)
        {
            return from == ScalarKind.Float ? FromBool(ReadFloat(from, bits, profile) != 0.0) : FromBool(IsTrue(from, bits));
        }
        if (from == ScalarKind.Bool)
        {
            return to.IsFloat() ? EncodeFloat(to is ScalarKind.LiteralFloat ? ScalarKind.Double : to, bits, profile) : bits;
        }

        if (from.IsFloat())
        {
            double value = ReadFloat(from, bits, profile);
            return to switch
            {
                ScalarKind.Int => FromInt((int)FloatToInteger(value, int.MinValue, int.MaxValue, profile)),
                ScalarKind.UInt => FromUInt((uint)FloatToInteger(value, 0, uint.MaxValue, profile)),
                ScalarKind.Int64 or ScalarKind.LiteralInt => FromInt64((long)FloatToInteger(value, long.MinValue, long.MaxValue, profile)),
                ScalarKind.UInt64 => FloatToUInt64(value, profile),
                ScalarKind.LiteralFloat => FromDouble(value),
                _ => EncodeFloat(to, value, profile),
            };
        }

        // From an integer kind
        if (to.IsFloat())
        {
            double value = from switch
            {
                ScalarKind.UInt64 => bits,
                ScalarKind.UInt => (uint)bits,
                _ => (long)bits,
            };
            // int -> float must round once, from the exact integer (not via double for 64-bit values)
            if (to == ScalarKind.Float && from == ScalarKind.UInt && (uint)bits >= 0x80000000u && profile.UnsignedToFloatViaSigned)
            {
                return FromFloat((float)(int)((uint)bits - 0x80000000u) + 2147483648f);
            }
            if (to == ScalarKind.Float)
            {
                float single = from switch
                {
                    ScalarKind.UInt64 => bits,
                    ScalarKind.UInt => (uint)bits,
                    _ => (long)bits,
                };
                return FromFloat(single);
            }
            if (to == ScalarKind.Half)
            {
                return FromHalf((Half)value);
            }
            return FromDouble(from == ScalarKind.UInt64 ? (double)bits : (double)(long)bits);
        }
        return to switch
        {
            ScalarKind.Int => FromInt((int)bits),
            ScalarKind.UInt => FromUInt((uint)bits),
            ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.LiteralInt => from == ScalarKind.UInt ? (uint)bits : bits,
            _ => bits,
        };
    }

    private static double FloatToInteger(double value, double minimum, double maximum, SemanticsProfile profile)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }
        double truncated = Math.Truncate(value);
        return profile.SaturateFloatToInt ? Math.Clamp(truncated, minimum, maximum) : truncated;
    }

    private static ulong FloatToUInt64(double value, SemanticsProfile profile)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }
        return value >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)Math.Truncate(value);
    }
}
