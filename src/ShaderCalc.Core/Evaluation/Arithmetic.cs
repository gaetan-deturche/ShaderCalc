using ShaderCalc.Binding;

namespace ShaderCalc.Evaluation;

/// <summary>
/// Float arithmetic for one kind, done in double and rounded after every operation: for + - * / and sqrt this gives
/// the correctly rounded float result (53 ≥ 2·24 + 2 bits), i.e. exactly what IEEE float hardware computes.
/// </summary>
public readonly struct FloatOps
{
    private readonly ScalarKind _kind;
    private readonly SemanticsProfile _profile;

    public FloatOps(ScalarKind kind, SemanticsProfile profile)
    {
        _kind = kind is ScalarKind.LiteralFloat ? ScalarKind.Double : kind;
        _profile = profile;
    }

    public bool IsDouble => _kind == ScalarKind.Double;

    /// <summary>Rounds to the kind's precision (and flushes denormals if the profile says so).</summary>
    public double Round(double value) => _kind switch
    {
        ScalarKind.Float => Scalars.Flush((float)value, _profile),
        ScalarKind.Half => (double)(Half)value,
        _ => value,
    };

    public double Add(double left, double right) => Round(left + right);

    public double Subtract(double left, double right) => Round(left - right);

    public double Multiply(double left, double right) => Round(left * right);

    public double Divide(double left, double right) => Round(left / right);

    public double Negate(double value) => -value;

    /// <summary>Float `%`: WARP's (q - trunc(q)) * b, or C's exact fmod (computing it in double loses nothing).</summary>
    public double Remainder(double left, double right)
    {
        if (!_profile.FloatRemainderFromQuotient)
        {
            return Round(left % right);
        }
        double quotient = Divide(left, right);
        return Multiply(Subtract(quotient, Math.Truncate(quotient)), right);
    }

    public double Fma(double a, double b, double c) => _kind == ScalarKind.Float
        ? Scalars.Flush(MathF.FusedMultiplyAdd((float)a, (float)b, (float)c), _profile)
        : Round(Math.FusedMultiplyAdd(a, b, c));

    /// <summary>DXIL FMad: fused when the profile says the backend fuses it.</summary>
    public double Mad(double a, double b, double c) => _profile.FuseMad ? Fma(a, b, c) : Add(Multiply(a, b), c);

    public double Sqrt(double value) => Round(Math.Sqrt(value));

    /// <summary>Applies a math function at the kind's precision (MathF for float).</summary>
    public double Apply(Func<float, float> single, Func<double, double> wide, double value) =>
        _kind == ScalarKind.Double ? wide(value) : Round(single((float)value));

    /// <summary>IEEE minNum / maxNum: a NaN operand yields the other one (DXIL FMin / FMax).</summary>
    public static double Min(double left, double right) => double.IsNaN(left) ? right : double.IsNaN(right) ? left : left < right ? left : right;

    public static double Max(double left, double right) => double.IsNaN(left) ? right : double.IsNaN(right) ? left : left > right ? left : right;

    /// <summary>
    /// FMin / FMax on encoded bits, as WARP computes them (x86 MINSS-like): a NaN second operand returns the first
    /// untouched (even a denormal); a NaN first operand returns the second, flushed.
    /// </summary>
    public static ulong MinMaxBits(ScalarKind kind, ulong left, ulong right, bool isMin, SemanticsProfile profile)
    {
        double leftValue = Scalars.ReadFloat(kind, left, profile);
        double rightValue = Scalars.ReadFloat(kind, right, profile);
        if (double.IsNaN(rightValue))
        {
            return left;
        }
        if (double.IsNaN(leftValue))
        {
            return Scalars.EncodeFloat(kind, rightValue, profile);
        }
        double result = isMin ? (leftValue < rightValue ? leftValue : rightValue) : (leftValue > rightValue ? leftValue : rightValue);
        return Scalars.EncodeFloat(kind, result, profile);
    }

    /// <summary>DXIL Frc: x - floor(x), kept below 1 (WARP returns 0.99999994 where the subtraction rounds up to 1).</summary>
    public double Frc(double value)
    {
        double fraction = Subtract(value, Math.Floor(value));
        return fraction >= 1 ? (_kind == ScalarKind.Float ? MathF.BitDecrement(1f) : Math.BitDecrement(1.0)) : fraction;
    }

    /// <summary>DXIL Saturate: NaN → 0.</summary>
    public static double Saturate(double value) => double.IsNaN(value) || value <= 0 ? 0 : value > 1 ? 1 : value;
}

/// <summary>Component-wise scalar operations on encoded bits (<see cref="Scalars"/>), for every kind.</summary>
public static class ScalarArithmetic
{
    /// <summary>Evaluates `left op right` for one component. <paramref name="fault"/> reports undefined behaviour (x / 0).</summary>
    public static ulong Binary(BinaryOperator operation, ScalarKind kind, ulong left, ulong right, SemanticsProfile profile, out string? fault)
    {
        fault = null;
        if (TypeRules.IsComparison(operation))
        {
            return Scalars.FromBool(Compare(operation, kind, left, right, profile));
        }
        if (kind.IsFloat())
        {
            FloatOps math = new FloatOps(kind, profile);
            double a = Scalars.ReadFloat(kind, left, profile);
            double b = Scalars.ReadFloat(kind, right, profile);
            double result = operation switch
            {
                BinaryOperator.Add => math.Add(a, b),
                BinaryOperator.Subtract => math.Subtract(a, b),
                BinaryOperator.Multiply => math.Multiply(a, b),
                BinaryOperator.Divide => math.Divide(a, b),
                BinaryOperator.Remainder => math.Remainder(a, b),
                _ => throw new InvalidOperationException($"{operation} on {kind}"),
            };
            return Scalars.EncodeFloat(kind is ScalarKind.LiteralFloat ? ScalarKind.Double : kind, result, profile);
        }
        if (kind == ScalarKind.Bool)
        {
            return operation switch
            {
                BinaryOperator.BitwiseAnd => left & right,
                BinaryOperator.BitwiseOr => left | right,
                BinaryOperator.BitwiseXor => left ^ right,
                _ => throw new InvalidOperationException($"{operation} on bool"),
            };
        }
        return IntegerBinary(operation, kind, left, right, profile, out fault);
    }

    private static int Width(ScalarKind kind, SemanticsProfile profile) => kind switch
    {
        ScalarKind.Int or ScalarKind.UInt => 32,
        ScalarKind.LiteralInt => profile.LiteralIntBits,
        _ => 64,
    };

    private static ulong IntegerBinary(BinaryOperator operation, ScalarKind kind, ulong left, ulong right, SemanticsProfile profile, out string? fault)
    {
        fault = null;
        int width = Width(kind, profile);
        bool isSigned = kind is ScalarKind.Int or ScalarKind.Int64 or ScalarKind.LiteralInt;
        // Work in 64 bits, then wrap to the width
        long signedLeft = width == 32 ? (isSigned ? (int)left : (uint)left) : (long)left;
        long signedRight = width == 32 ? (isSigned ? (int)right : (uint)right) : (long)right;
        ulong unsignedLeft = width == 32 ? (uint)left : left;
        ulong unsignedRight = width == 32 ? (uint)right : right;

        ulong raw;
        switch (operation)
        {
            case BinaryOperator.Add:
                raw = unsignedLeft + unsignedRight;
                break;
            case BinaryOperator.Subtract:
                raw = unsignedLeft - unsignedRight;
                break;
            case BinaryOperator.Multiply:
                raw = unsignedLeft * unsignedRight;
                break;
            case BinaryOperator.Divide:
            case BinaryOperator.Remainder:
                bool isDivision = operation == BinaryOperator.Divide;
                long maximum = width == 32 ? int.MaxValue : long.MaxValue;
                if (unsignedRight == 0)
                {
                    // D3D defines udiv by 0 as all ones; signed division by 0 is undefined (WARP: INT_MAX)
                    fault = "integer division by zero (undefined: the GPU result is implementation-defined)";
                    raw = isSigned && profile.UndefinedSignedDivisionIsMax ? (ulong)maximum : ulong.MaxValue;
                    break;
                }
                if (isSigned)
                {
                    long minimum = width == 32 ? int.MinValue : long.MinValue;
                    if (signedLeft == minimum && signedRight == -1)
                    {
                        fault = "signed division overflow (INT_MIN / -1, undefined)";
                        raw = profile.UndefinedSignedDivisionIsMax ? (ulong)maximum : isDivision ? (ulong)minimum : 0;
                        break;
                    }
                    raw = (ulong)(isDivision ? signedLeft / signedRight : signedLeft % signedRight);
                }
                else
                {
                    raw = isDivision ? unsignedLeft / unsignedRight : unsignedLeft % unsignedRight;
                }
                break;
            case BinaryOperator.BitwiseAnd:
                raw = unsignedLeft & unsignedRight;
                break;
            case BinaryOperator.BitwiseOr:
                raw = unsignedLeft | unsignedRight;
                break;
            case BinaryOperator.BitwiseXor:
                raw = unsignedLeft ^ unsignedRight;
                break;
            case BinaryOperator.ShiftLeft:
            case BinaryOperator.ShiftRight:
                ulong count = unsignedRight;
                if (profile.MaskShiftCount)
                {
                    count &= (ulong)(width - 1);
                }
                else if (count >= (ulong)width)
                {
                    fault = $"shift count {count} ≥ {width} (undefined in C++)";
                    raw = operation == BinaryOperator.ShiftRight && isSigned && signedLeft < 0 ? ulong.MaxValue : 0;
                    break;
                }
                if (operation == BinaryOperator.ShiftLeft)
                {
                    raw = unsignedLeft << (int)count;
                }
                else
                {
                    raw = isSigned ? (ulong)(signedLeft >> (int)count) : unsignedLeft >> (int)count;
                }
                break;
            default:
                throw new InvalidOperationException($"{operation} on {kind}");
        }
        return kind switch
        {
            ScalarKind.Int => Scalars.FromInt((int)raw),
            ScalarKind.UInt => (uint)raw,
            ScalarKind.LiteralInt when width == 32 => Scalars.FromInt((int)raw),
            _ => raw,
        };
    }

    public static bool Compare(BinaryOperator operation, ScalarKind kind, ulong left, ulong right, SemanticsProfile profile)
    {
        if (kind.IsFloat())
        {
            double a = Scalars.ReadFloat(kind, left, profile);
            double b = Scalars.ReadFloat(kind, right, profile);
            return operation switch
            {
                BinaryOperator.Equal => a == b,
                // `!=` is unordered-or-not-equal: true when a NaN is involved
                BinaryOperator.NotEqual => !(a == b),
                BinaryOperator.Less => a < b,
                BinaryOperator.LessOrEqual => a <= b,
                BinaryOperator.Greater => a > b,
                _ => a >= b,
            };
        }
        int order = kind switch
        {
            ScalarKind.UInt or ScalarKind.UInt64 or ScalarKind.Bool => left.CompareTo(right),
            _ => ((long)left).CompareTo((long)right),
        };
        return operation switch
        {
            BinaryOperator.Equal => order == 0,
            BinaryOperator.NotEqual => order != 0,
            BinaryOperator.Less => order < 0,
            BinaryOperator.LessOrEqual => order <= 0,
            BinaryOperator.Greater => order > 0,
            _ => order >= 0,
        };
    }

    public static ulong Unary(UnaryOperator operation, ScalarKind kind, ulong value, SemanticsProfile profile) => operation switch
    {
        UnaryOperator.Plus => value,
        UnaryOperator.LogicalNot => Scalars.FromBool(!Scalars.IsTrue(kind, value)),
        UnaryOperator.BitwiseNot => kind switch
        {
            ScalarKind.Int => Scalars.FromInt(~(int)value),
            ScalarKind.UInt => (uint)~(uint)value,
            _ => ~value,
        },
        _ => kind switch
        {
            // Negation flips the sign bit (NaN keeps its payload); a float denormal is flushed first
            ScalarKind.Half => value ^ 0x8000UL,
            ScalarKind.Float => Scalars.FromFloat(Scalars.Flush(Scalars.ToFloat(value), profile)) ^ 0x80000000UL,
            ScalarKind.Double or ScalarKind.LiteralFloat => value ^ 0x8000000000000000UL,
            ScalarKind.Int => Scalars.FromInt(unchecked(-(int)value)),
            ScalarKind.UInt => (uint)(0u - (uint)value),
            _ => unchecked(0UL - value),
        },
    };
}
