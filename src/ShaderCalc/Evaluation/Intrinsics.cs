using System.Numerics;
using ShaderCalc.Binding;
using ShaderCalc.Syntax;
using ShaderCalc.Units;

namespace ShaderCalc.Evaluation;

/// <summary>
/// HLSL intrinsics: overload resolution plus an implementation that follows DXC's lowering to DXIL op for op
/// (exp(x) = exp2(x · log2 e), fmod via frac, normalize via rsqrt(dot), mul as an FMad chain...), so results
/// match a DXC + WARP build bit for bit, except for the functions WARP approximates.
/// </summary>
public static class Intrinsics
{
    private enum KindClass
    {
        /// <summary>half/float; integers and literals become float; double is rejected (no such DXIL op).</summary>
        Float,
        FloatOrDouble,
        /// <summary>Any numeric kind; bool becomes int, literals int / float.</summary>
        Numeric,
        Integer,
        /// <summary>Any kind, bool included.</summary>
        Any,
    }

    // Constants exactly as DXC emits them (float precision)
    private static readonly double Log2OfE = BitConverter.UInt32BitsToSingle(0x3FB8AA3B);
    private static readonly double LnOf2 = BitConverter.UInt32BitsToSingle(0x3F317218);
    private static readonly double Log10Of2 = BitConverter.UInt32BitsToSingle(0x3E9A209B);
    private static readonly double Pi = BitConverter.UInt32BitsToSingle(0x40490FDB);
    private static readonly double HalfPi = BitConverter.UInt32BitsToSingle(0x3FC90FDB);
    private static readonly double DegreesPerRadian = BitConverter.UInt32BitsToSingle(0x42652EE1);
    private static readonly double RadiansPerDegree = BitConverter.UInt32BitsToSingle(0x3C8EFA35);
    private static readonly double ColorToByte = BitConverter.UInt32BitsToSingle(0x437F0080);

    private static readonly Dictionary<string, Intrinsic> Table = Build();

    /// <summary>Intrinsics that exist in HLSL but make no sense outside a GPU pipeline.</summary>
    private static readonly HashSet<string> Unsupported = new HashSet<string>(StringComparer.Ordinal)
    {
        "ddx", "ddy", "ddx_coarse", "ddy_coarse", "ddx_fine", "ddy_fine", "fwidth", "clip", "noise", "abort", "errorf", "printf",
        "GroupMemoryBarrier", "GroupMemoryBarrierWithGroupSync", "AllMemoryBarrier", "DeviceMemoryBarrier",
        "InterlockedAdd", "InterlockedMin", "InterlockedMax", "InterlockedAnd", "InterlockedOr", "InterlockedXor", "InterlockedExchange",
        "WaveActiveSum", "WaveReadLaneAt", "WaveGetLaneIndex", "EvaluateAttributeAtSample", "GetRenderTargetSampleCount",
    };

    public static bool TryGet(string name, out Intrinsic intrinsic) => Table.TryGetValue(name, out intrinsic!);

    public static bool IsUnsupported(string name) => Unsupported.Contains(name);

    public static IEnumerable<string> Names => Table.Keys;

    private static Dictionary<string, Intrinsic> Build()
    {
        Dictionary<string, Intrinsic> table = new Dictionary<string, Intrinsic>(StringComparer.Ordinal);
        void Add(string name, IntrinsicPrecision precision, Func<IReadOnlyList<ShaderType>, IntrinsicResolution> resolve, IntrinsicImplementation implementation) =>
            table[name] = new Intrinsic(name, precision, resolve, implementation);

        // ---- Rounding and simple float functions (exact) ----
        Add("floor", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => Math.Floor(operands[0]), Keep));
        Add("ceil", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => Math.Ceiling(operands[0]), Keep));
        Add("trunc", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => Math.Truncate(operands[0]), Keep));
        Add("round", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => Math.Round(operands[0], context.Profile.RoundTies), Keep));
        Add("frac", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => Frc(math, operands[0]), Keep));
        Add("saturate", IntrinsicPrecision.Exact, Same(1, KindClass.FloatOrDouble), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => FloatOps.Saturate(operands[0]), units => context.Units.Dimensionless(units[0], "saturate")));
        Add("sqrt", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Sqrt(operands[0]), units => context.Units.Power(units[0], 0.5, "sqrt")));
        Add("rsqrt", IntrinsicPrecision.Approximate, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => Rsqrt(math, operands[0]), units => context.Units.Power(units[0], -0.5, "rsqrt")));
        Add("rcp", IntrinsicPrecision.Exact, Same(1, KindClass.FloatOrDouble), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Divide(1, operands[0]), units => context.Units.Power(units[0], -1, "rcp")));
        Add("degrees", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Multiply(operands[0], DegreesPerRadian), Keep));
        Add("radians", IntrinsicPrecision.Exact, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Multiply(operands[0], RadiansPerDegree), Keep));

        // ---- Transcendentals: DXIL Exp/Log are base 2; WARP approximates them all ----
        AddTranscendental(table, "sin", MathF.Sin, Math.Sin);
        AddTranscendental(table, "cos", MathF.Cos, Math.Cos);
        AddTranscendental(table, "tan", MathF.Tan, Math.Tan);
        AddTranscendental(table, "asin", MathF.Asin, Math.Asin);
        AddTranscendental(table, "acos", MathF.Acos, Math.Acos);
        AddTranscendental(table, "atan", MathF.Atan, Math.Atan);
        AddTranscendental(table, "sinh", MathF.Sinh, Math.Sinh);
        AddTranscendental(table, "cosh", MathF.Cosh, Math.Cosh);
        AddTranscendental(table, "tanh", MathF.Tanh, Math.Tanh);
        AddTranscendental(table, "exp2", float.Exp2, double.Exp2);
        AddTranscendental(table, "log2", MathF.Log2, Math.Log2);
        Add("exp", IntrinsicPrecision.Approximate, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => Exp2(math, math.Multiply(operands[0], Log2OfE)), units => context.Units.Dimensionless(units[0], "exp")));
        Add("log", IntrinsicPrecision.Approximate, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Multiply(Log2(math, operands[0]), LnOf2), units => context.Units.Dimensionless(units[0], "log")));
        Add("log10", IntrinsicPrecision.Approximate, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Multiply(Log2(math, operands[0]), Log10Of2), units => context.Units.Dimensionless(units[0], "log10")));
        table["pow"] = new Intrinsic("pow", IntrinsicPrecision.Approximate, Same(2, KindClass.Float), Pow) { ConstantSensitiveArguments = new[] { 1 } };
        Add("ldexp", IntrinsicPrecision.Approximate, Same(2, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Multiply(Exp2(math, operands[1]), operands[0]),
                units => { context.Units.Dimensionless(units[1], "ldexp's exponent"); return units[0]; }));
        Add("atan2", IntrinsicPrecision.Approximate, Same(2, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => Atan2(math, operands[0], operands[1]),
                units => new UnitTag(Dimension.None, context.Units.Same(units[0], units[1], "atan2").IsAdoptable)));
        Add("sincos", IntrinsicPrecision.Approximate, OutputsLike(KindClass.Float, ParameterMode.In, ParameterMode.Out, ParameterMode.Out), (context, arguments) =>
        {
            Func<UnitTag[], UnitTag> units = tags => context.Units.Dimensionless(tags[0], "sincos");
            context.Outputs[1] = MapFloat(context, new[] { arguments[0] }, (math, operands) => math.Apply(MathF.Sin, Math.Sin, operands[0]), units);
            context.Outputs[2] = MapFloat(context, new[] { arguments[0] }, (math, operands) => math.Apply(MathF.Cos, Math.Cos, operands[0]), units);
            return Value.Void;
        });

        // ---- Combining functions ----
        Add("fmod", IntrinsicPrecision.Exact, Same(2, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => Fmod(math, operands[0], operands[1]), units => context.Units.Same(units[0], units[1], "fmod")));
        Add("lerp", IntrinsicPrecision.Exact, Same(3, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Add(math.Multiply(math.Subtract(operands[1], operands[0]), operands[2]), operands[0]),
                units => { context.Units.Dimensionless(units[2], "lerp's weight"); return context.Units.Same(units[0], units[1], "lerp"); }));
        Add("smoothstep", IntrinsicPrecision.Exact, Same(3, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) =>
            {
                // t * (t * (3 - t * 2)), as DXC lowers it without fast math
                double ratio = FloatOps.Saturate(math.Divide(math.Subtract(operands[2], operands[0]), math.Subtract(operands[1], operands[0])));
                return math.Multiply(ratio, math.Multiply(ratio, math.Subtract(3, math.Multiply(ratio, 2))));
            }, units =>
            {
                context.Units.Same(units[0], units[1], "smoothstep");
                return new UnitTag(Dimension.None, context.Units.Same(units[0], units[2], "smoothstep").IsAdoptable);
            }));
        Add("step", IntrinsicPrecision.Exact, Same(2, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => operands[1] < operands[0] ? 0 : 1,
                units => new UnitTag(Dimension.None, context.Units.Same(units[0], units[1], "step").IsAdoptable)));
        Add("min", IntrinsicPrecision.Exact, Same(2, KindClass.Numeric), (context, arguments) => MinMax(context, arguments, isMin: true));
        Add("max", IntrinsicPrecision.Exact, Same(2, KindClass.Numeric), (context, arguments) => MinMax(context, arguments, isMin: false));
        Add("clamp", IntrinsicPrecision.Exact, Same(3, KindClass.Numeric), Clamp);
        Add("abs", IntrinsicPrecision.Exact, Same(1, KindClass.Numeric), Abs);
        Add("sign", IntrinsicPrecision.Exact, Same(1, KindClass.Numeric, type => type.WithKind(ScalarKind.Int)), Sign);
        Add("mad", IntrinsicPrecision.Exact, Same(3, KindClass.Numeric), Mad);
        Add("fma", IntrinsicPrecision.Exact, ResolveDoubleOnly(3), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Fma(operands[0], operands[1], operands[2]),
                units => context.Units.Same(UnitChecker.Multiply(units[0], units[1]), units[2], "fma")));
        Add("isnan", IntrinsicPrecision.Exact, Same(1, KindClass.Float, type => type.WithKind(ScalarKind.Bool)), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => double.IsNaN(operands[0]) ? 1 : 0, DropSilently, ResultKind(arguments, ScalarKind.Bool)));
        Add("isinf", IntrinsicPrecision.Exact, Same(1, KindClass.Float, type => type.WithKind(ScalarKind.Bool)), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => double.IsInfinity(operands[0]) ? 1 : 0, DropSilently, ResultKind(arguments, ScalarKind.Bool)));
        Add("isfinite", IntrinsicPrecision.Exact, Same(1, KindClass.Float, type => type.WithKind(ScalarKind.Bool)), (context, arguments) =>
            MapFloat(context, arguments, (_, operands) => double.IsFinite(operands[0]) ? 1 : 0, DropSilently, ResultKind(arguments, ScalarKind.Bool)));
        Add("modf", IntrinsicPrecision.Exact, OutputsLike(KindClass.Float, ParameterMode.In, ParameterMode.Out), (context, arguments) =>
        {
            context.Outputs[1] = MapFloat(context, arguments[..1], (_, operands) => Math.Truncate(operands[0]), Keep);
            return MapFloat(context, arguments[..1], (math, operands) => math.Subtract(operands[0], Math.Truncate(operands[0])), Keep);
        });
        Add("frexp", IntrinsicPrecision.Exact, OutputsLike(KindClass.Float, ParameterMode.In, ParameterMode.Out), Frexp);

        // ---- Logic ----
        Add("any", IntrinsicPrecision.Exact, Reduce(KindClass.Any, ScalarKind.Bool), (context, arguments) =>
            Value.FromBool(Enumerable.Range(0, arguments[0].Bits.Length).Any(arguments[0].GetBool)));
        Add("all", IntrinsicPrecision.Exact, Reduce(KindClass.Any, ScalarKind.Bool), (context, arguments) =>
            Value.FromBool(Enumerable.Range(0, arguments[0].Bits.Length).All(arguments[0].GetBool)));
        Add("and", IntrinsicPrecision.Exact, Same(2, KindClass.Any, type => type.WithKind(ScalarKind.Bool)), (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.Bool), arguments, (bits, kinds) => Scalars.FromBool(Scalars.IsTrue(kinds[0], bits[0]) && Scalars.IsTrue(kinds[1], bits[1])), DropSilently));
        Add("or", IntrinsicPrecision.Exact, Same(2, KindClass.Any, type => type.WithKind(ScalarKind.Bool)), (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.Bool), arguments, (bits, kinds) => Scalars.FromBool(Scalars.IsTrue(kinds[0], bits[0]) || Scalars.IsTrue(kinds[1], bits[1])), DropSilently));
        Add("select", IntrinsicPrecision.Exact, ResolveSelect, (context, arguments) =>
            MapBits(context, arguments[1].Type, arguments, (bits, kinds) => Scalars.IsTrue(kinds[0], bits[0]) ? bits[1] : bits[2],
                units => context.Units.Same(units[1], units[2], "select")));

        // ---- Vectors and matrices ----
        Add("dot", IntrinsicPrecision.Exact, ResolveDot, Dot);
        Add("cross", IntrinsicPrecision.Exact, ResolveCross, Cross);
        Add("length", IntrinsicPrecision.Exact, ResolveLength(1), (context, arguments) => Length(context, arguments[0]));
        Add("distance", IntrinsicPrecision.Exact, ResolveLength(2), (context, arguments) => Length(context, Subtract(context, arguments[0], arguments[1])));
        Add("normalize", IntrinsicPrecision.Approximate, Same(1, KindClass.Float), Normalize);
        Add("reflect", IntrinsicPrecision.Exact, Same(2, KindClass.Float), Reflect);
        Add("refract", IntrinsicPrecision.Exact, ResolveRefract, Refract);
        Add("faceforward", IntrinsicPrecision.Exact, Same(3, KindClass.Float), FaceForward);
        Add("mul", IntrinsicPrecision.Exact, ResolveMul, Mul);
        Add("transpose", IntrinsicPrecision.Exact, ResolveTranspose, Transpose);
        Add("determinant", IntrinsicPrecision.Exact, ResolveDeterminant, Determinant);
        Add("lit", IntrinsicPrecision.Approximate, ResolveLit, Lit);
        Add("dst", IntrinsicPrecision.Exact, ResolveDst, Dst);

        // ---- Bits ----
        Add("countbits", IntrinsicPrecision.Exact, Same(1, KindClass.Integer, type => type.WithKind(ScalarKind.UInt)), (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.UInt), arguments, (bits, _) => (ulong)BitOperations.PopCount((uint)bits[0]), DropUnit(context, "countbits")));
        Add("reversebits", IntrinsicPrecision.Exact, Same(1, KindClass.Integer), (context, arguments) =>
            MapBits(context, arguments[0].Type, arguments, (bits, kinds) => Scalars.Convert(ScalarKind.UInt, kinds[0], ReverseBits((uint)bits[0]), context.Profile),
                DropUnit(context, "reversebits")));
        Add("firstbitlow", IntrinsicPrecision.Exact, Same(1, KindClass.Integer), (context, arguments) =>
            MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
                Scalars.Convert(ScalarKind.UInt, kinds[0], (uint)bits[0] == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount((uint)bits[0]), context.Profile),
                DropUnit(context, "firstbitlow")));
        Add("firstbithigh", IntrinsicPrecision.Exact, Same(1, KindClass.Integer), (context, arguments) =>
            MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
            {
                uint word = (uint)bits[0];
                // Signed: the first bit that differs from the sign bit
                if (kinds[0] == ScalarKind.Int && (int)word < 0)
                {
                    word = ~word;
                }
                uint position = word == 0 ? uint.MaxValue : 31 - (uint)BitOperations.LeadingZeroCount(word);
                return Scalars.Convert(ScalarKind.UInt, kinds[0], position, context.Profile);
            }, DropUnit(context, "firstbithigh")));
        Add("asuint", IntrinsicPrecision.Exact, ResolveAsUInt, AsUInt);
        Add("asint", IntrinsicPrecision.Exact, ResolveReinterpret(ScalarKind.Int), (context, arguments) => Reinterpret(context, arguments[0], ScalarKind.Int));
        Add("asfloat", IntrinsicPrecision.Exact, ResolveReinterpret(ScalarKind.Float), (context, arguments) => Reinterpret(context, arguments[0], ScalarKind.Float));
        Add("asdouble", IntrinsicPrecision.Exact, ResolveAsDouble, (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.Double), arguments, (bits, _) => (uint)bits[0] | ((ulong)(uint)bits[1] << 32), DropSilently));
        Add("f32tof16", IntrinsicPrecision.Exact, Same(1, KindClass.Float, type => type.WithKind(ScalarKind.UInt)), (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.UInt), arguments, (bits, _) => F32ToF16((float)Read(context, ScalarKind.Float, bits[0]), context.Profile),
                DropUnit(context, "f32tof16")));
        Add("f16tof32", IntrinsicPrecision.Exact, ResolveF16ToF32, (context, arguments) =>
            MapBits(context, ResultKind(arguments, ScalarKind.Float), arguments,
                (bits, _) => Scalars.FromFloat((float)BitConverter.UInt16BitsToHalf((ushort)bits[0])), DropSilently));
        Add("D3DCOLORtoUBYTE4", IntrinsicPrecision.Exact, ResolveColorToUByte4, ColorToUByte4);

        return table;
    }

    // ---------------------------------------------------------------- Resolution

    private static IntrinsicResolution Fail(string message) => IntrinsicResolution.Fail(message);

    /// <summary>All arguments share one type T (scalars splat to the vector/matrix shape); returns T unless told otherwise.</summary>
    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> Same(int arity, KindClass kindClass, Func<NumericType, ShaderType>? returns = null) =>
        arguments =>
        {
            if (arguments.Count != arity)
            {
                return Fail($"takes {arity} argument{(arity == 1 ? string.Empty : "s")}, got {arguments.Count}");
            }
            if (!TryCommonType(arguments, kindClass, out NumericType common, out string error))
            {
                return Fail(error);
            }
            return IntrinsicSignature.AllIn(Enumerable.Repeat<ShaderType>(common, arity).ToArray(), returns?.Invoke(common) ?? common);
        };

    /// <summary>sincos(x, out s, out c), modf(x, out ip), frexp(x, out e): every parameter has x's type.</summary>
    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> OutputsLike(KindClass kindClass, params ParameterMode[] modes) =>
        arguments =>
        {
            if (arguments.Count != modes.Length)
            {
                return Fail($"takes {modes.Length} arguments, got {arguments.Count}");
            }
            if (!TryCommonType(arguments.Take(1).ToList(), kindClass, out NumericType common, out string error))
            {
                return Fail(error);
            }
            ShaderType returnType = modes.Length == 3 ? VoidType.Instance : common;
            return new IntrinsicSignature(Enumerable.Repeat<ShaderType>(common, modes.Length).ToArray(), modes, returnType);
        };

    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> Reduce(KindClass kindClass, ScalarKind resultKind) =>
        arguments =>
        {
            if (arguments.Count != 1)
            {
                return Fail($"takes 1 argument, got {arguments.Count}");
            }
            if (!TryCommonType(arguments, kindClass, out NumericType common, out string error))
            {
                return Fail(error);
            }
            return IntrinsicSignature.AllIn(new ShaderType[] { common }, NumericType.Scalar(resultKind));
        };

    private static bool TryCommonType(IReadOnlyList<ShaderType> arguments, KindClass kindClass, out NumericType common, out string error)
    {
        common = NumericType.Float;
        error = string.Empty;
        NumericType? result = null;
        foreach (ShaderType argument in arguments)
        {
            if (argument is not NumericType numeric)
            {
                error = $"doesn't take {argument}";
                return false;
            }
            if (result == null)
            {
                result = numeric;
                continue;
            }
            ScalarKind kind = TypeRules.CommonKind(result.Kind, numeric.Kind);
            if (!result.IsScalar && !numeric.IsScalar && (result.Shape != numeric.Shape || result.Rows != numeric.Rows || result.Columns != numeric.Columns))
            {
                error = $"arguments have different sizes ({result} and {numeric})";
                return false;
            }
            result = (result.IsScalar ? numeric : result).WithKind(kind);
        }
        if (result == null)
        {
            error = "needs arguments";
            return false;
        }

        ScalarKind adjusted = kindClass switch
        {
            KindClass.Float or KindClass.FloatOrDouble => result.Kind switch
            {
                ScalarKind.Double => ScalarKind.Double,
                ScalarKind.Half => ScalarKind.Half,
                _ => ScalarKind.Float,
            },
            KindClass.Numeric => result.Kind switch
            {
                ScalarKind.Bool or ScalarKind.LiteralInt => ScalarKind.Int,
                ScalarKind.LiteralFloat => ScalarKind.Float,
                _ => result.Kind,
            },
            KindClass.Integer => result.Kind is ScalarKind.LiteralInt or ScalarKind.Bool ? ScalarKind.Int : result.Kind,
            _ => result.Kind.Materialized(),
        };
        if (kindClass == KindClass.Float && adjusted == ScalarKind.Double)
        {
            error = "doesn't support double (no DXIL instruction for it)";
            return false;
        }
        if (kindClass == KindClass.Integer && !adjusted.IsInteger())
        {
            error = $"needs integers, got {result}";
            return false;
        }
        if (kindClass == KindClass.Integer && adjusted is ScalarKind.Int64 or ScalarKind.UInt64)
        {
            error = "doesn't support 64-bit integers here";
            return false;
        }
        common = result.WithKind(adjusted);
        return true;
    }

    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> ResolveDoubleOnly(int arity) => arguments =>
    {
        IntrinsicResolution resolution = Same(arity, KindClass.FloatOrDouble)(arguments);
        return resolution.Signature?.ReturnType is NumericType { Kind: ScalarKind.Double } ? resolution : Fail("only takes double");
    };

    private static IntrinsicResolution ResolveSelect(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 3)
        {
            return Fail($"takes 3 arguments, got {arguments.Count}");
        }
        if (!TryCommonType(arguments.Skip(1).ToList(), KindClass.Any, out NumericType values, out string error))
        {
            return Fail(error);
        }
        if (arguments[0] is not NumericType condition)
        {
            return Fail($"needs a bool condition, got {arguments[0]}");
        }
        NumericType shape = condition.IsScalar ? values : values.IsScalar ? condition.WithKind(values.Kind) : values;
        if (!condition.IsScalar && !values.IsScalar && condition.ComponentCount != values.ComponentCount)
        {
            return Fail($"condition {condition} and values {values} have different sizes");
        }
        return IntrinsicSignature.AllIn(new ShaderType[] { shape.WithKind(ScalarKind.Bool), shape, shape }, shape);
    }

    private static IntrinsicResolution ResolveDot(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 2)
        {
            return Fail($"takes 2 arguments, got {arguments.Count}");
        }
        if (!TryCommonType(arguments, KindClass.Numeric, out NumericType common, out string error))
        {
            return Fail(error);
        }
        if (common.IsMatrix)
        {
            return Fail("needs vectors, got a matrix");
        }
        return IntrinsicSignature.AllIn(new ShaderType[] { common, common }, NumericType.Scalar(common.Kind));
    }

    private static IntrinsicResolution ResolveCross(IReadOnlyList<ShaderType> arguments)
    {
        IntrinsicResolution resolution = Same(2, KindClass.Float)(arguments);
        if (resolution.Signature?.ReturnType is NumericType { IsVector: true, Size: 3 } || resolution.Signature == null)
        {
            return resolution;
        }
        return Fail("needs float3 vectors");
    }

    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> ResolveLength(int arity) => arguments =>
    {
        IntrinsicResolution resolution = Same(arity, KindClass.Float)(arguments);
        if (resolution.Signature == null)
        {
            return resolution;
        }
        NumericType common = (NumericType)resolution.Signature.ReturnType;
        return common.IsMatrix ? Fail("needs vectors, got a matrix")
            : IntrinsicSignature.AllIn(resolution.Signature.ParameterTypes, NumericType.Scalar(common.Kind));
    };

    private static IntrinsicResolution ResolveRefract(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 3)
        {
            return Fail($"takes 3 arguments, got {arguments.Count}");
        }
        if (!TryCommonType(arguments.Take(2).ToList(), KindClass.Float, out NumericType common, out string error))
        {
            return Fail(error);
        }
        return IntrinsicSignature.AllIn(new ShaderType[] { common, common, NumericType.Scalar(common.Kind) }, common);
    }

    private static IntrinsicResolution ResolveMul(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 2)
        {
            return Fail($"takes 2 arguments, got {arguments.Count}");
        }
        if (arguments[0] is not NumericType left || arguments[1] is not NumericType right)
        {
            return Fail("needs numeric arguments");
        }
        ScalarKind kind = TypeRules.PromoteKinds(left.Kind, right.Kind);
        kind = kind switch { ScalarKind.LiteralInt => ScalarKind.Int, ScalarKind.LiteralFloat => ScalarKind.Float, _ => kind };
        NumericType leftType = left.WithKind(kind);
        NumericType rightType = right.WithKind(kind);
        if (left.IsScalar || right.IsScalar)
        {
            NumericType shape = left.IsScalar ? rightType : leftType;
            return IntrinsicSignature.AllIn(new ShaderType[] { leftType, rightType }, shape);
        }
        // A vector is a row on the left, a column on the right
        int leftColumns = left.IsVector ? left.Size : left.Columns;
        int rightRows = right.IsVector ? right.Size : right.Rows;
        if (leftColumns != rightRows)
        {
            return Fail($"can't multiply {left} by {right}: {leftColumns} columns vs {rightRows} rows");
        }
        ShaderType result = (left.IsVector, right.IsVector) switch
        {
            (true, true) => NumericType.Scalar(kind),
            (true, false) => NumericType.Vector(kind, right.Columns),
            (false, true) => NumericType.Vector(kind, left.Rows),
            _ => NumericType.Matrix(kind, left.Rows, right.Columns),
        };
        return IntrinsicSignature.AllIn(new ShaderType[] { leftType, rightType }, result);
    }

    private static IntrinsicResolution ResolveTranspose(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is not NumericType { IsMatrix: true } matrix)
        {
            return Fail("takes one matrix");
        }
        NumericType materialized = matrix.WithKind(matrix.Kind.Materialized());
        return IntrinsicSignature.AllIn(new ShaderType[] { materialized }, NumericType.Matrix(materialized.Kind, matrix.Columns, matrix.Rows));
    }

    private static IntrinsicResolution ResolveDeterminant(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is not NumericType { IsMatrix: true } matrix || matrix.Rows != matrix.Columns)
        {
            return Fail("takes one square matrix");
        }
        if (matrix.Kind == ScalarKind.Double)
        {
            return Fail("doesn't support double");
        }
        NumericType floatMatrix = matrix.WithKind(ScalarKind.Float);
        return IntrinsicSignature.AllIn(new ShaderType[] { floatMatrix }, NumericType.Float);
    }

    private static IntrinsicResolution ResolveLit(IReadOnlyList<ShaderType> arguments) =>
        arguments.Count == 3 && arguments.All(argument => argument is NumericType { IsScalar: true })
            ? IntrinsicSignature.AllIn(new ShaderType[] { NumericType.Float, NumericType.Float, NumericType.Float }, NumericType.Vector(ScalarKind.Float, 4))
            : Fail("takes 3 scalars");

    private static IntrinsicResolution ResolveDst(IReadOnlyList<ShaderType> arguments)
    {
        NumericType float4 = NumericType.Vector(ScalarKind.Float, 4);
        return arguments.Count == 2 ? IntrinsicSignature.AllIn(new ShaderType[] { float4, float4 }, float4) : Fail("takes 2 vectors");
    }

    private static Func<IReadOnlyList<ShaderType>, IntrinsicResolution> ResolveReinterpret(ScalarKind target) => arguments =>
    {
        if (arguments.Count != 1 || arguments[0] is not NumericType numeric)
        {
            return Fail("takes one numeric argument");
        }
        ScalarKind kind = numeric.Kind.Materialized();
        if (kind is not (ScalarKind.Int or ScalarKind.UInt or ScalarKind.Float))
        {
            return Fail($"needs 32-bit int, uint or float, got {numeric}");
        }
        if (target == ScalarKind.Float && kind == ScalarKind.Float)
        {
            kind = ScalarKind.Float;
        }
        NumericType source = numeric.WithKind(kind);
        return IntrinsicSignature.AllIn(new ShaderType[] { source }, source.WithKind(target));
    };

    private static IntrinsicResolution ResolveAsUInt(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count == 3)
        {
            // asuint(double value, out uint low, out uint high)
            if (arguments[0] is not NumericType { Kind: ScalarKind.Double } value)
            {
                return Fail("with 3 arguments takes a double and two out uints");
            }
            NumericType words = value.WithKind(ScalarKind.UInt);
            return new IntrinsicSignature(new ShaderType[] { value, words, words }, new[] { ParameterMode.In, ParameterMode.Out, ParameterMode.Out },
                VoidType.Instance);
        }
        return ResolveReinterpret(ScalarKind.UInt)(arguments);
    }

    private static IntrinsicResolution ResolveAsDouble(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 2 || !TryCommonType(arguments, KindClass.Integer, out NumericType common, out string error))
        {
            return Fail("takes two uints (low, high)");
        }
        NumericType words = common.WithKind(ScalarKind.UInt);
        return IntrinsicSignature.AllIn(new ShaderType[] { words, words }, words.WithKind(ScalarKind.Double));
    }

    private static IntrinsicResolution ResolveF16ToF32(IReadOnlyList<ShaderType> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is not NumericType numeric)
        {
            return Fail("takes one uint");
        }
        NumericType words = numeric.WithKind(ScalarKind.UInt);
        return IntrinsicSignature.AllIn(new ShaderType[] { words }, words.WithKind(ScalarKind.Float));
    }

    private static IntrinsicResolution ResolveColorToUByte4(IReadOnlyList<ShaderType> arguments)
    {
        NumericType float4 = NumericType.Vector(ScalarKind.Float, 4);
        return arguments.Count == 1 ? IntrinsicSignature.AllIn(new ShaderType[] { float4 }, float4.WithKind(ScalarKind.Int)) : Fail("takes a float4");
    }

    // ---------------------------------------------------------------- Implementation helpers

    private static UnitTag Keep(UnitTag[] units) => units[0];

    private static UnitTag DropSilently(UnitTag[] units) => new UnitTag(Dimension.None, units.All(unit => unit.IsAdoptable));

    private static Func<UnitTag[], UnitTag> DropUnit(IntrinsicContext context, string name) => units => context.Units.Drop(units[0], name);

    private static ShaderType ResultKind(Value[] arguments, ScalarKind kind) => ((NumericType)arguments[0].Type).WithKind(kind);

    /// <summary>
    /// Component-wise function of float arguments (all of the same type after conversion). Non-float results (isnan,
    /// sign) are encoded from the returned number.
    /// </summary>
    private static Value MapFloat(IntrinsicContext context, Value[] arguments, Func<FloatOps, double[], double> compute, Func<UnitTag[], UnitTag> units,
        ShaderType? resultType = null)
    {
        ShaderType type = resultType ?? arguments[0].Type;
        int count = type.ComponentCount;
        ScalarKind operandKind = arguments[0].KindAt(0);
        FloatOps math = new FloatOps(operandKind, context.Profile);
        ulong[] bits = new ulong[count];
        UnitTag[] tags = new UnitTag[count];
        double[] operands = new double[arguments.Length];
        UnitTag[] operandUnits = new UnitTag[arguments.Length];
        for (int component = 0; component < count; component++)
        {
            for (int argument = 0; argument < arguments.Length; argument++)
            {
                int index = arguments[argument].Bits.Length == 1 ? 0 : component;
                operands[argument] = Read(context, operandKind, arguments[argument].Bits[index]);
                operandUnits[argument] = arguments[argument].Units[index];
            }
            bits[component] = Encode(type.ComponentKinds[component], compute(math, operands), context.Profile);
            tags[component] = units(operandUnits);
        }
        return new Value(type, bits, tags);
    }

    /// <summary>Component-wise function of raw bits (integers, bools, reinterpretation).</summary>
    private static Value MapBits(IntrinsicContext context, ShaderType resultType, Value[] arguments, Func<ulong[], ScalarKind[], ulong> compute,
        Func<UnitTag[], UnitTag> units)
    {
        int count = resultType.ComponentCount;
        ulong[] bits = new ulong[count];
        UnitTag[] tags = new UnitTag[count];
        ulong[] operands = new ulong[arguments.Length];
        ScalarKind[] kinds = new ScalarKind[arguments.Length];
        UnitTag[] operandUnits = new UnitTag[arguments.Length];
        for (int component = 0; component < count; component++)
        {
            for (int argument = 0; argument < arguments.Length; argument++)
            {
                int index = arguments[argument].Bits.Length == 1 ? 0 : component;
                operands[argument] = arguments[argument].Bits[index];
                kinds[argument] = arguments[argument].KindAt(index);
                operandUnits[argument] = arguments[argument].Units[index];
            }
            bits[component] = compute(operands, kinds);
            tags[component] = units(operandUnits);
        }
        return new Value(resultType, bits, tags);
    }

    private static ulong Encode(ScalarKind kind, double number, SemanticsProfile profile) => kind switch
    {
        ScalarKind.Bool => Scalars.FromBool(number != 0),
        ScalarKind.Int => Scalars.FromInt((int)number),
        ScalarKind.UInt => (uint)number,
        ScalarKind.Int64 or ScalarKind.LiteralInt => (ulong)(long)number,
        ScalarKind.UInt64 => (ulong)number,
        _ => Scalars.EncodeFloat(kind, number, profile),
    };

    private static void AddTranscendental(Dictionary<string, Intrinsic> table, string name, Func<float, float> single, Func<double, double> wide) =>
        table[name] = new Intrinsic(name, IntrinsicPrecision.Approximate, Same(1, KindClass.Float), (context, arguments) =>
            MapFloat(context, arguments, (math, operands) => math.Apply(single, wide, operands[0]), units => context.Units.Dimensionless(units[0], name)));

    // ---- DXIL op semantics ----

    private static double Frc(FloatOps math, double value) => math.Frc(value);

    private static double Exp2(FloatOps math, double value) => math.Apply(float.Exp2, double.Exp2, value);

    private static double Log2(FloatOps math, double value) => math.Apply(MathF.Log2, Math.Log2, value);

    /// <summary>DXIL Rsqrt, taken as the correctly rounded 1/sqrt(x).</summary>
    private static double Rsqrt(FloatOps math, double value) => math.Round(1.0 / Math.Sqrt(value));

    // ---- Formulas from DXC's lowering ----

    private static Value Pow(IntrinsicContext context, Value[] arguments)
    {
        // DXC turns pow(x, 2) (after constant folding) into x * x; everything else is exp2(log2(x) * y)
        bool isSquare = context.IsConstant(1, 2);
        bool hasConstantExponent = context.Call.ConstantArguments[1] != null;
        return MapFloat(context, arguments, (math, operands) => isSquare
                ? math.Multiply(operands[0], operands[0])
                : Exp2(math, math.Multiply(Log2(math, operands[0]), operands[1])),
            units =>
            {
                context.Units.Dimensionless(units[1], "pow's exponent");
                if (units[0].Dimension.IsNone)
                {
                    return new UnitTag(Dimension.None, units[0].IsAdoptable && units[1].IsAdoptable);
                }
                if (!hasConstantExponent)
                {
                    context.Report(DiagnosticSeverity.Error, $"pow of {units[0].Dimension} needs a constant exponent");
                    return UnitTag.Of(Dimension.None);
                }
                return context.Units.Power(units[0], context.Call.ConstantArguments[1]!.GetNumber(0), "pow");
            });
    }

    /// <summary>atan(y / x) plus quadrant fix-ups, as DXC lowers atan2.</summary>
    private static double Atan2(FloatOps math, double y, double x)
    {
        double angle = math.Apply(MathF.Atan, Math.Atan, math.Divide(y, x));
        bool xNegative = x < 0;
        bool xZero = x == 0;
        bool yNonNegative = y >= 0;
        bool yNegative = y < 0;
        double result = yNonNegative && xNegative ? math.Add(angle, Pi) : angle;
        result = yNegative && xNegative ? math.Add(angle, -Pi) : result;
        result = yNegative && xZero ? -HalfPi : result;
        return yNonNegative && xZero ? HalfPi : result;
    }

    /// <summary>fmod(x, y) = ±frac(|x / y|) · y (not C's exact fmod).</summary>
    private static double Fmod(FloatOps math, double x, double y)
    {
        double quotient = math.Divide(x, y);
        bool isPositive = quotient >= -quotient;
        double fraction = Frc(math, Math.Abs(quotient));
        return math.Multiply(isPositive ? fraction : -fraction, y);
    }

    private static Value MinMax(IntrinsicContext context, Value[] arguments, bool isMin)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        Func<UnitTag[], UnitTag> units = tags => context.Units.Same(tags[0], tags[1], isMin ? "min" : "max");
        if (kind.IsFloat())
        {
            return MapBits(context, arguments[0].Type, arguments, (bits, kinds) => FloatOps.MinMaxBits(kinds[0], bits[0], bits[1], isMin, context.Profile), units);
        }
        return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
        {
            bool leftIsLess = ScalarArithmetic.Compare(BinaryOperator.Less, kinds[0], bits[0], bits[1], context.Profile);
            return leftIsLess == isMin ? bits[0] : bits[1];
        }, units);
    }

    private static Value Clamp(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        Func<UnitTag[], UnitTag> units = tags =>
        {
            context.Units.Same(tags[0], tags[2], "clamp");
            return context.Units.Same(tags[0], tags[1], "clamp");
        };
        if (kind.IsFloat())
        {
            // FMin(FMax(x, low), high)
            return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
                FloatOps.MinMaxBits(kinds[0], FloatOps.MinMaxBits(kinds[0], bits[0], bits[1], false, context.Profile), bits[2], true, context.Profile), units);
        }
        return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
        {
            ulong raised = ScalarArithmetic.Compare(BinaryOperator.Less, kinds[0], bits[0], bits[1], context.Profile) ? bits[1] : bits[0];
            return ScalarArithmetic.Compare(BinaryOperator.Greater, kinds[0], raised, bits[2], context.Profile) ? bits[2] : raised;
        }, units);
    }

    private static Value Abs(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        if (kind.IsFloat())
        {
            // FAbs clears the sign bit (NaN payload kept); a float denormal is flushed first
            ulong signBit = kind switch { ScalarKind.Half => 0x8000UL, ScalarKind.Float => 0x80000000UL, _ => 0x8000000000000000UL };
            return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
                (kinds[0] == ScalarKind.Float ? Scalars.FromFloat(Scalars.Flush(Scalars.ToFloat(bits[0]), context.Profile)) : bits[0]) & ~signBit, Keep);
        }
        // IMax(x, -x): abs(INT_MIN) stays INT_MIN
        return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
        {
            if (kinds[0] is ScalarKind.UInt or ScalarKind.UInt64)
            {
                return bits[0];
            }
            ulong negated = ScalarArithmetic.Unary(UnaryOperator.Negate, kinds[0], bits[0], context.Profile);
            return ScalarArithmetic.Compare(BinaryOperator.Less, kinds[0], bits[0], negated, context.Profile) ? negated : bits[0];
        }, Keep);
    }

    private static Value Sign(IntrinsicContext context, Value[] arguments)
    {
        ShaderType result = ResultKind(arguments, ScalarKind.Int);
        return MapBits(context, result, arguments, (bits, kinds) =>
        {
            ulong zero = Scalars.Convert(ScalarKind.Int, kinds[0], 0, context.Profile);
            bool isPositive = ScalarArithmetic.Compare(BinaryOperator.Greater, kinds[0], bits[0], zero, context.Profile);
            bool isNegative = ScalarArithmetic.Compare(BinaryOperator.Less, kinds[0], bits[0], zero, context.Profile);
            return Scalars.FromInt((isPositive ? 1 : 0) - (isNegative ? 1 : 0));
        }, DropSilently);
    }

    private static Value Mad(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        Func<UnitTag[], UnitTag> units = tags => context.Units.Same(UnitChecker.Multiply(tags[0], tags[1]), tags[2], "mad");
        if (kind.IsFloat())
        {
            return MapFloat(context, arguments, (math, operands) => math.Mad(operands[0], operands[1], operands[2]), units);
        }
        return MapBits(context, arguments[0].Type, arguments, (bits, kinds) =>
        {
            ulong product = ScalarArithmetic.Binary(BinaryOperator.Multiply, kinds[0], bits[0], bits[1], context.Profile, out _);
            return ScalarArithmetic.Binary(BinaryOperator.Add, kinds[0], product, bits[2], context.Profile, out _);
        }, units);
    }

    private static Value Frexp(IntrinsicContext context, Value[] arguments)
    {
        // DXC: exponent and mantissa straight from the float bits; the mantissa loses the sign
        context.Outputs[1] = MapFloat(context, arguments[..1], (_, operands) =>
        {
            if (operands[0] == 0)
            {
                return 0;
            }
            int bits = (int)BitConverter.SingleToUInt32Bits((float)operands[0]);
            return ((bits & 0x7F800000) - 0x3F000000) >> 23;
        }, DropSilently);
        return MapFloat(context, arguments[..1], (_, operands) =>
        {
            if (operands[0] == 0)
            {
                return 0;
            }
            uint bits = BitConverter.SingleToUInt32Bits((float)operands[0]);
            return BitConverter.UInt32BitsToSingle((bits & 0x007FFFFF) | 0x3F000000);
        }, DropSilently);
    }

    /// <summary>DXIL Dot2/3/4 (float) or an IMad chain (integers); a scalar dot is a multiply.</summary>
    private static double DotFloat(FloatOps math, double[] left, double[] right)
    {
        double sum = math.Multiply(left[0], right[0]);
        for (int index = 1; index < left.Length; index++)
        {
            sum = math.Add(sum, math.Multiply(left[index], right[index]));
        }
        return sum;
    }

    private static Value Dot(IntrinsicContext context, Value[] arguments)
    {
        Value left = arguments[0];
        Value right = arguments[1];
        ScalarKind kind = left.KindAt(0);
        int size = left.Bits.Length;
        UnitTag unit = UnitChecker.Multiply(left.Units[0], right.Units[0]);
        for (int index = 1; index < size; index++)
        {
            unit = context.Units.Same(unit, UnitChecker.Multiply(left.Units[index], right.Units[index]), "dot");
        }

        ulong bits;
        if (kind.IsFloat())
        {
            FloatOps math = new FloatOps(kind, context.Profile);
            double[] leftComponents = Enumerable.Range(0, size).Select(index => Read(context, kind, left.Bits[index])).ToArray();
            double[] rightComponents = Enumerable.Range(0, size).Select(index => Read(context, kind, right.Bits[index])).ToArray();
            bits = Scalars.EncodeFloat(kind, DotFloat(math, leftComponents, rightComponents), context.Profile);
        }
        else
        {
            bits = ScalarArithmetic.Binary(BinaryOperator.Multiply, kind, left.Bits[0], right.Bits[0], context.Profile, out _);
            for (int index = 1; index < size; index++)
            {
                ulong product = ScalarArithmetic.Binary(BinaryOperator.Multiply, kind, left.Bits[index], right.Bits[index], context.Profile, out _);
                bits = ScalarArithmetic.Binary(BinaryOperator.Add, kind, product, bits, context.Profile, out _);
            }
        }
        return Value.Scalar(kind, bits, unit);
    }

    private static Value Cross(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        double[] a = Enumerable.Range(0, 3).Select(index => Read(context, kind, arguments[0].Bits[index])).ToArray();
        double[] b = Enumerable.Range(0, 3).Select(index => Read(context, kind, arguments[1].Bits[index])).ToArray();
        double Term(int i, int j, int k, int l) => math.Subtract(math.Multiply(a[i], b[j]), math.Multiply(a[k], b[l]));
        double[] result = { Term(1, 2, 2, 1), Term(2, 0, 0, 2), Term(0, 1, 1, 0) };
        UnitTag[] units = Enumerable.Range(0, 3).Select(index => UnitChecker.Multiply(arguments[0].Units[(index + 1) % 3], arguments[1].Units[(index + 2) % 3])).ToArray();
        return new Value(arguments[0].Type, result.Select(component => Scalars.EncodeFloat(kind, component, context.Profile)).ToArray(), units);
    }

    /// <summary>length: |x| for a scalar, else sqrt of the sum of squares (fmul/fadd, not the Dot op).</summary>
    private static Value Length(IntrinsicContext context, Value vector)
    {
        ScalarKind kind = vector.KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        UnitTag unit = vector.Units[0];
        for (int index = 1; index < vector.Units.Length; index++)
        {
            unit = context.Units.Same(unit, vector.Units[index], "length");
        }
        double[] components = Enumerable.Range(0, vector.Bits.Length).Select(index => Read(context, kind, vector.Bits[index])).ToArray();
        double length = components.Length == 1 ? Math.Abs(components[0]) : math.Sqrt(DotFloat(math, components, components));
        return Value.Scalar(kind, Scalars.EncodeFloat(kind, length, context.Profile), unit);
    }

    private static Value Subtract(IntrinsicContext context, Value left, Value right)
    {
        ScalarKind kind = left.KindAt(0);
        ulong[] bits = new ulong[left.Bits.Length];
        UnitTag[] units = new UnitTag[left.Bits.Length];
        for (int index = 0; index < bits.Length; index++)
        {
            bits[index] = ScalarArithmetic.Binary(BinaryOperator.Subtract, kind, left.Bits[index], right.Bits[index], context.Profile, out _);
            units[index] = context.Units.Same(left.Units[index], right.Units[index], "distance");
        }
        return new Value(left.Type, bits, units);
    }

    private static Value Normalize(IntrinsicContext context, Value[] arguments)
    {
        Value vector = arguments[0];
        ScalarKind kind = vector.KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        double[] components = Enumerable.Range(0, vector.Bits.Length).Select(index => Read(context, kind, vector.Bits[index])).ToArray();
        // v * rsqrt(dot(v, v))
        double inverseLength = Rsqrt(math, DotFloat(math, components, components));
        UnitTag unit = vector.Units[0];
        for (int index = 1; index < vector.Units.Length; index++)
        {
            unit = context.Units.Same(unit, vector.Units[index], "normalize");
        }
        UnitTag direction = new UnitTag(Dimension.None, unit.IsAdoptable);
        return new Value(vector.Type, components.Select(component => Scalars.EncodeFloat(kind, math.Multiply(inverseLength, component), context.Profile)).ToArray(),
            Enumerable.Repeat(direction, components.Length).ToArray());
    }

    private static Value Reflect(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        double[] incident = Components(context, arguments[0]);
        double[] normal = Components(context, arguments[1]);
        double dot = incident.Length == 1 ? math.Multiply(incident[0], normal[0]) : DotFloat(math, incident, normal);
        // i - n * (dot(i, n) * 2)
        double twiceDot = math.Multiply(dot, 2);
        double[] result = incident.Select((component, index) => math.Subtract(component, math.Multiply(normal[index], twiceDot))).ToArray();
        UnitTag[] units = arguments[0].Units.Select(unit => unit).ToArray();
        foreach (UnitTag normalUnit in arguments[1].Units)
        {
            context.Units.Dimensionless(normalUnit, "reflect's normal");
        }
        return new Value(arguments[0].Type, result.Select(component => Scalars.EncodeFloat(kind, component, context.Profile)).ToArray(), units);
    }

    private static Value Refract(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        double[] incident = Components(context, arguments[0]);
        double[] normal = Components(context, arguments[1]);
        double eta = Read(context, kind, arguments[2].Bits[0]);
        double dot = incident.Length == 1 ? math.Multiply(incident[0], normal[0]) : DotFloat(math, incident, normal);
        // k = 1 - (1 - d*d) * eta*eta; r = k >= 0 ? i*eta - (sqrt(k) + d*eta) * n : 0
        double k = math.Subtract(1, math.Multiply(math.Subtract(1, math.Multiply(dot, dot)), math.Multiply(eta, eta)));
        bool refracts = k >= 0;
        double scale = math.Add(math.Sqrt(k), math.Multiply(dot, eta));
        double[] result = incident.Select((component, index) =>
            refracts ? math.Subtract(math.Multiply(component, eta), math.Multiply(scale, normal[index])) : 0).ToArray();
        foreach (UnitTag unit in arguments[1].Units.Append(arguments[2].Units[0]))
        {
            context.Units.Dimensionless(unit, "refract's normal and eta");
        }
        return new Value(arguments[0].Type, result.Select(component => Scalars.EncodeFloat(kind, component, context.Profile)).ToArray(),
            arguments[0].Units.ToArray());
    }

    private static Value FaceForward(IntrinsicContext context, Value[] arguments)
    {
        ScalarKind kind = arguments[0].KindAt(0);
        FloatOps math = new FloatOps(kind, context.Profile);
        double[] incident = Components(context, arguments[1]);
        double[] reference = Components(context, arguments[2]);
        double dot = incident.Length == 1 ? math.Multiply(incident[0], reference[0]) : DotFloat(math, incident, reference);
        bool keep = dot < 0;
        ulong[] bits = arguments[0].Bits.Select(component => keep ? component : ScalarArithmetic.Unary(UnaryOperator.Negate, kind, component, context.Profile)).ToArray();
        return new Value(arguments[0].Type, bits, arguments[0].Units.ToArray());
    }

    private static double[] Components(IntrinsicContext context, Value value) =>
        Enumerable.Range(0, value.Bits.Length).Select(index => Read(context, value.KindAt(index), value.Bits[index])).ToArray();

    /// <summary>A float operand as the GPU reads it (denormals flushed per the profile).</summary>
    private static double Read(IntrinsicContext context, ScalarKind kind, ulong bits) => Scalars.ReadFloat(kind, bits, context.Profile);

    private static double ReadFloat(IntrinsicContext context, Value value, int index) => Read(context, ScalarKind.Float, value.Bits[index]);

    /// <summary>
    /// mul: scalar products are multiplies; otherwise each result is an FMad chain (first term a plain multiply),
    /// as DXC emits it. A vector is a row on the left, a column on the right.
    /// </summary>
    private static Value Mul(IntrinsicContext context, Value[] arguments)
    {
        Value left = arguments[0];
        Value right = arguments[1];
        NumericType leftType = (NumericType)left.Type;
        NumericType rightType = (NumericType)right.Type;
        ScalarKind kind = leftType.Kind;
        ShaderType resultType = context.Call.Type;

        if (leftType.IsScalar || rightType.IsScalar)
        {
            Value vector = leftType.IsScalar ? right : left;
            Value scalar = leftType.IsScalar ? left : right;
            ulong[] products = new ulong[vector.Bits.Length];
            UnitTag[] productUnits = new UnitTag[vector.Bits.Length];
            for (int index = 0; index < products.Length; index++)
            {
                products[index] = ScalarArithmetic.Binary(BinaryOperator.Multiply, kind, vector.Bits[index], scalar.Bits[0], context.Profile, out _);
                productUnits[index] = UnitChecker.Multiply(vector.Units[index], scalar.Units[0]);
            }
            return new Value(resultType, products, productUnits);
        }

        int rows = leftType.IsVector ? 1 : leftType.Rows;
        int inner = leftType.IsVector ? leftType.Size : leftType.Columns;
        int columns = rightType.IsVector ? 1 : rightType.Columns;
        int LeftIndex(int row, int k) => row * inner + k;
        int RightIndex(int k, int column) => rightType.IsVector ? k : k * columns + column;

        ulong[] bits = new ulong[rows * columns];
        UnitTag[] units = new UnitTag[rows * columns];
        FloatOps math = new FloatOps(kind, context.Profile);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                UnitTag unit = UnitChecker.Multiply(left.Units[LeftIndex(row, 0)], right.Units[RightIndex(0, column)]);
                for (int k = 1; k < inner; k++)
                {
                    unit = context.Units.Same(unit, UnitChecker.Multiply(left.Units[LeftIndex(row, k)], right.Units[RightIndex(k, column)]), "mul");
                }
                units[row * columns + column] = unit;

                if (kind.IsFloat())
                {
                    double accumulator = math.Multiply(Read(context, kind, left.Bits[LeftIndex(row, 0)]), Read(context, kind, right.Bits[RightIndex(0, column)]));
                    for (int k = 1; k < inner; k++)
                    {
                        accumulator = math.Mad(Read(context, kind, left.Bits[LeftIndex(row, k)]), Read(context, kind, right.Bits[RightIndex(k, column)]),
                            accumulator);
                    }
                    bits[row * columns + column] = Scalars.EncodeFloat(kind, accumulator, context.Profile);
                }
                else
                {
                    ulong accumulator = ScalarArithmetic.Binary(BinaryOperator.Multiply, kind, left.Bits[LeftIndex(row, 0)], right.Bits[RightIndex(0, column)],
                        context.Profile, out _);
                    for (int k = 1; k < inner; k++)
                    {
                        ulong product = ScalarArithmetic.Binary(BinaryOperator.Multiply, kind, left.Bits[LeftIndex(row, k)], right.Bits[RightIndex(k, column)],
                            context.Profile, out _);
                        accumulator = ScalarArithmetic.Binary(BinaryOperator.Add, kind, product, accumulator, context.Profile, out _);
                    }
                    bits[row * columns + column] = accumulator;
                }
            }
        }
        return new Value(resultType, bits, units);
    }

    private static Value Transpose(IntrinsicContext context, Value[] arguments)
    {
        NumericType matrix = (NumericType)arguments[0].Type;
        int count = matrix.ComponentCount;
        ulong[] bits = new ulong[count];
        UnitTag[] units = new UnitTag[count];
        for (int row = 0; row < matrix.Rows; row++)
        {
            for (int column = 0; column < matrix.Columns; column++)
            {
                bits[column * matrix.Rows + row] = arguments[0].Bits[row * matrix.Columns + column];
                units[column * matrix.Rows + row] = arguments[0].Units[row * matrix.Columns + column];
            }
        }
        return new Value(context.Call.Type, bits, units);
    }

    /// <summary>Laplace expansion along the first row, folded ((t0 - t1) + t2) - t3, as DXC expands it.</summary>
    private static Value Determinant(IntrinsicContext context, Value[] arguments)
    {
        NumericType matrix = (NumericType)arguments[0].Type;
        FloatOps math = new FloatOps(ScalarKind.Float, context.Profile);
        int size = matrix.Rows;
        double[,] elements = new double[size, size];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                elements[row, column] = ReadFloat(context, arguments[0], row * size + column);
            }
        }
        double result = DeterminantOf(math, elements, Enumerable.Range(0, size).ToArray(), Enumerable.Range(0, size).ToArray());

        Dimension? unit = arguments[0].Units.All(tag => tag.Dimension.Equals(arguments[0].Units[0].Dimension)) ? arguments[0].Units[0].Dimension.Power(size) : null;
        if (unit == null)
        {
            context.Report(DiagnosticSeverity.Warning, "determinant of a matrix with mixed units: unit not tracked");
        }
        return Value.Scalar(ScalarKind.Float, Scalars.EncodeFloat(ScalarKind.Float, result, context.Profile),
            new UnitTag(unit ?? Dimension.None, arguments[0].Units.All(tag => tag.IsAdoptable)));
    }

    private static double DeterminantOf(FloatOps math, double[,] elements, int[] rows, int[] columns)
    {
        if (rows.Length == 1)
        {
            return elements[rows[0], columns[0]];
        }
        if (rows.Length == 2)
        {
            return math.Subtract(math.Multiply(elements[rows[0], columns[0]], elements[rows[1], columns[1]]),
                math.Multiply(elements[rows[0], columns[1]], elements[rows[1], columns[0]]));
        }
        double result = 0;
        for (int column = 0; column < columns.Length; column++)
        {
            int[] minorColumns = columns.Where((_, index) => index != column).ToArray();
            double term = math.Multiply(elements[rows[0], columns[column]], DeterminantOf(math, elements, rows[1..], minorColumns));
            result = column == 0 ? term : column % 2 == 1 ? math.Subtract(result, term) : math.Add(term, result);
        }
        return result;
    }

    private static Value Lit(IntrinsicContext context, Value[] arguments)
    {
        FloatOps math = new FloatOps(ScalarKind.Float, context.Profile);
        double normalDotLight = ReadFloat(context, arguments[0], 0);
        double normalDotHalf = ReadFloat(context, arguments[1], 0);
        double exponent = ReadFloat(context, arguments[2], 0);
        double specular = normalDotLight < 0 || normalDotHalf < 0 ? 0 : Exp2(math, math.Multiply(exponent, Log2(math, normalDotHalf)));
        // The diffuse term is a select, not arithmetic: its bits are copied (a denormal survives)
        ulong one = Scalars.FromFloat(1);
        ulong diffuse = normalDotLight < 0 ? 0 : arguments[0].Bits[0];
        return new Value(context.Call.Type, new[] { one, diffuse, Scalars.EncodeFloat(ScalarKind.Float, specular, context.Profile), one },
            Enumerable.Repeat(UnitTag.Bare, 4).ToArray());
    }

    private static Value Dst(IntrinsicContext context, Value[] arguments)
    {
        FloatOps math = new FloatOps(ScalarKind.Float, context.Profile);
        // (1, a.y * b.y, a.z, b.w): z and w are copies, so their bits pass through unflushed
        ulong[] bits =
        {
            Scalars.FromFloat(1),
            Scalars.EncodeFloat(ScalarKind.Float, math.Multiply(ReadFloat(context, arguments[0], 1), ReadFloat(context, arguments[1], 1)), context.Profile),
            arguments[0].Bits[2],
            arguments[1].Bits[3],
        };
        UnitTag[] units = { UnitTag.Bare, UnitChecker.Multiply(arguments[0].Units[1], arguments[1].Units[1]), arguments[0].Units[2], arguments[1].Units[3] };
        return new Value(context.Call.Type, bits, units);
    }

    // ---- Bits ----

    private static ulong ReverseBits(uint value)
    {
        uint reversed = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }
        return reversed;
    }

    /// <summary>DXIL LegacyF32ToF16: WARP rounds toward zero (so finite overflow gives ±65504), NaN → 0x7FFF.</summary>
    private static ulong F32ToF16(float value, SemanticsProfile profile)
    {
        if (!profile.HalfConversionTowardZero)
        {
            return BitConverter.HalfToUInt16Bits((Half)value);
        }
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint sign = (bits >> 16) & 0x8000;
        uint magnitude = bits & 0x7FFFFFFF;
        if (magnitude > 0x7F800000)
        {
            return sign | 0x7FFF;
        }
        if (magnitude == 0x7F800000)
        {
            return sign | 0x7C00;
        }
        int exponent = (int)(magnitude >> 23) - 127;
        if (exponent < -14)
        {
            // Half denormal: a multiple of 2^-24, truncated
            return sign | (uint)MathF.Truncate(BitConverter.UInt32BitsToSingle(magnitude) * 16777216f);
        }
        if (exponent > 15)
        {
            return sign | 0x7BFF;
        }
        return sign | ((uint)(exponent + 15) << 10) | ((magnitude & 0x7FFFFF) >> 13);
    }

    private static Value Reinterpret(IntrinsicContext context, Value value, ScalarKind target)
    {
        NumericType type = ((NumericType)value.Type).WithKind(target);
        ulong[] bits = value.Bits.Select(component => target switch
        {
            ScalarKind.Int => Scalars.FromInt((int)(uint)component),
            ScalarKind.Float => (ulong)(uint)component,
            _ => (ulong)(uint)component,
        }).ToArray();
        string name = target switch { ScalarKind.Int => "asint", ScalarKind.Float => "asfloat", _ => "asuint" };
        bool changesKind = value.KindAt(0) != target;
        return new Value(type, bits, value.Units.Select(unit => changesKind ? context.Units.Drop(unit, name) : unit).ToArray());
    }

    private static Value AsUInt(IntrinsicContext context, Value[] arguments)
    {
        if (arguments.Length == 3)
        {
            NumericType words = ((NumericType)arguments[0].Type).WithKind(ScalarKind.UInt);
            UnitTag[] units = Enumerable.Repeat(UnitTag.Bare, words.ComponentCount).ToArray();
            context.Outputs[1] = new Value(words, arguments[0].Bits.Select(bits => (ulong)(uint)bits).ToArray(), units);
            context.Outputs[2] = new Value(words, arguments[0].Bits.Select(bits => (ulong)(uint)(bits >> 32)).ToArray(), (UnitTag[])units.Clone());
            return Value.Void;
        }
        return Reinterpret(context, arguments[0], ScalarKind.UInt);
    }

    private static Value ColorToUByte4(IntrinsicContext context, Value[] arguments)
    {
        FloatOps math = new FloatOps(ScalarKind.Float, context.Profile);
        int[] order = { 2, 1, 0, 3 };
        ulong[] bits = order.Select(index =>
            Scalars.Convert(ScalarKind.Float, ScalarKind.Int, Scalars.FromFloat((float)math.Multiply(ReadFloat(context, arguments[0], index), ColorToByte)), context.Profile)).ToArray();
        return new Value(context.Call.Type, bits, Enumerable.Repeat(UnitTag.Bare, 4).ToArray());
    }
}
