using ShaderCalc;
using Xunit.Abstractions;

namespace ShaderCalc.Tests;

/// <summary>Each operation and intrinsic over hundreds of inputs, interpreter vs DXC + WARP.</summary>
public class ConformanceTests
{
    private readonly ITestOutputHelper _output;

    public ConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Operations whose result must be bit-identical (IEEE-exact DXIL ops and DXC's exact formulas).</summary>
    public static TheoryData<int, string> ExactFloat() => new TheoryData<int, string>
    {
        { 1, "-a" }, { 1, "abs(a)" }, { 1, "sqrt(a)" }, { 1, "rcp(a)" }, { 1, "frac(a)" }, { 1, "floor(a)" }, { 1, "ceil(a)" },
        { 1, "round(a)" }, { 1, "trunc(a)" }, { 1, "saturate(a)" }, { 1, "degrees(a)" }, { 1, "radians(a)" }, { 1, "(float)sign(a)" },
        { 1, "(float)(int)a" }, { 1, "(float)(uint)a" }, { 1, "f16tof32(f32tof16(a))" }, { 1, "(float)f32tof16(a)" },
        { 2, "a + b" }, { 2, "a - b" }, { 2, "a * b" }, { 2, "a / b" }, { 2, "a % b" }, { 2, "fmod(a, b)" }, { 2, "min(a, b)" },
        { 2, "max(a, b)" }, { 2, "step(a, b)" }, { 2, "(float)(a < b)" }, { 2, "(float)(a != b)" },
        { 3, "lerp(a, b, c)" }, { 3, "smoothstep(a, b, c)" }, { 3, "clamp(a, b, c)" }, { 3, "mad(a, b, c)" },
    };

    public static TheoryData<int, string> ApproximateFloat() => new TheoryData<int, string>
    {
        { 1, "rsqrt(a)" }, { 1, "exp(a)" }, { 1, "exp2(a)" }, { 1, "log(a)" }, { 1, "log2(a)" }, { 1, "log10(a)" }, { 1, "sin(a)" },
        { 1, "cos(a)" }, { 1, "tan(a)" }, { 1, "asin(a)" }, { 1, "acos(a)" }, { 1, "atan(a)" }, { 1, "sinh(a)" }, { 1, "cosh(a)" },
        { 1, "tanh(a)" }, { 2, "pow(a, b)" }, { 2, "atan2(a, b)" }, { 2, "ldexp(a, b)" },
    };

    [Theory]
    [MemberData(nameof(ExactFloat))]
    public void FloatOperation_IsBitExact(int arity, string expression)
    {
        ConformanceReport report = Conformance.Run(Parameters(arity), "float", expression, Conformance.FloatTuples(arity, 300, 7));
        _output.WriteLine(report.ToString());
        Assert.True(report.Exact == report.Samples, report.ToString());
    }

    [Theory]
    [MemberData(nameof(ApproximateFloat))]
    public void FloatApproximation_IsReported(int arity, string expression)
    {
        ConformanceReport report = Conformance.Run(Parameters(arity), "float", expression, Conformance.FloatTuples(arity, 300, 7));
        _output.WriteLine(report.ToString());
    }

    [Theory]
    [InlineData("float", "dot(a, b)", true)]
    [InlineData("float", "length(a)", true)]
    [InlineData("float", "distance(a, b)", true)]
    [InlineData("float3", "cross(a, b)", true)]
    [InlineData("float3", "reflect(a, b)", true)]
    [InlineData("float3", "faceforward(a, b, a.zxy)", true)]
    [InlineData("float3", "refract(a, b, a.x)", true)]
    [InlineData("float3", "a * b - b.zxy", true)]
    [InlineData("float3", "mul(float3x3(a, b, a.yzx), b)", true)]
    [InlineData("float3", "mul(a, float3x3(a, b, b.yzx))", true)]
    [InlineData("float3x3", "mul(float3x3(a, b, a.yzx), float3x3(b, a, b.zxy))", true)]
    [InlineData("float", "determinant(float3x3(a, b, a.yzx * b))", true)]
    [InlineData("float", "determinant(float4x4(a, b.x, b, a.y, a.zxy, b.z, b.yzx, a.x))", true)]
    [InlineData("float", "determinant(float2x2(a.xy, b.yz))", true)]
    [InlineData("float3x3", "transpose(float3x3(a, b, a.yzx))", true)]
    [InlineData("float4", "lit(a.x, a.y, b.x)", false)]
    [InlineData("float4", "dst(float4(a, b.x), float4(b, a.x))", true)]
    [InlineData("int4", "D3DCOLORtoUBYTE4(float4(a, b.x))", true)]
    [InlineData("float3", "normalize(a)", false)]
    public void VectorOperation_MatchesWarp(string returnType, string expression, bool isExact)
    {
        List<Value[]> left = Conformance.FloatTuples(3, 300, 11);
        List<Value[]> right = Conformance.FloatTuples(3, 300, 12);
        right.Reverse();
        List<Value[]> samples = left.Zip(right, (a, b) => new[] { Vector(a[0], a[1], a[2]), Vector(b[0], b[1], b[2]) }).ToList();
        ConformanceReport report = Conformance.Run("float3 a, float3 b", returnType, expression, samples);
        _output.WriteLine(report.ToString());
        if (isExact)
        {
            Assert.True(report.Exact == report.Samples, report.ToString());
        }
    }

    [Theory]
    [InlineData("uint", "countbits(a)")]
    [InlineData("uint", "reversebits(a)")]
    [InlineData("uint", "firstbitlow(a)")]
    [InlineData("uint", "firstbithigh(a)")]
    [InlineData("uint", "a * b + (a ^ b) - (b >> 3)")]
    [InlineData("uint", "a / (b | 1u) + a % (b | 1u)")]
    [InlineData("uint", "min(a, b) + clamp(a, b, a ^ b)")]
    [InlineData("float", "asfloat(a)")]
    [InlineData("float", "f16tof32(a)")]
    [InlineData("uint", "f32tof16(asfloat(a))")]
    [InlineData("bool", "isnan(asfloat(a)) || isinf(asfloat(b))")]
    [InlineData("int", "sign(asint(a)) + abs(asint(b))")]
    [InlineData("float", "(float)a + (float)asint(b)")]
    [InlineData("uint", "(uint)asfloat(a)")]
    [InlineData("int", "(int)asfloat(a)")]
    public void BitOperation_MatchesWarp(string returnType, string expression)
    {
        List<int> values = Conformance.Ints(60, 5).ToList();
        values.AddRange(new[] { 0x7F800000, unchecked((int)0xFF800000), 0x7FC00000, 0x00000001, 0x3F800000, 0x477FE000, 0x7BFF });
        List<Value[]> samples = values.SelectMany(left => values.Take(25).Select(right => new[] { Value.FromUInt((uint)left), Value.FromUInt((uint)right) })).ToList();
        ConformanceReport report = Conformance.Run("uint a, uint b", returnType, expression, samples);
        _output.WriteLine(report.ToString());
        Assert.True(report.Exact == report.Samples, report.ToString());
    }

    [Theory]
    [InlineData("a + b")]
    [InlineData("a * b - a")]
    [InlineData("a / b")]
    [InlineData("min(a, b)")]
    [InlineData("abs(a) + saturate(b)")]
    [InlineData("rcp(a)")]
    [InlineData("fma(a, b, a)")]
    [InlineData("(double)(float)a")]
    public void DoubleOperation_MatchesWarp(string expression)
    {
        double[] values = { 0, -0.0, 1, -1, 0.5, 0.1, 1e-310, -1e-310, 3.5, 1e300, -1e300, Math.PI, 2.5, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 123456.789, 1e-8 };
        List<Value[]> samples = values.SelectMany(left => values.Select(right => Conformance.Doubles(new[] { left, right }))).ToList();
        ConformanceReport report = Conformance.Run("double a, double b", "double", expression, samples);
        _output.WriteLine(report.ToString());
        Assert.True(report.Exact == report.Samples, report.ToString());
    }

    [Theory]
    [InlineData("a + b * 3")]
    [InlineData("a / (b | 1)")]
    [InlineData("(a >> 7) ^ (b << 9)")]
    [InlineData("min(a, b)")]
    public void Int64Operation_MatchesWarp(string expression)
    {
        long[] values = { 0, 1, -1, 2, 1L << 40, -(1L << 40), long.MaxValue, long.MinValue, 123456789012345, -987654321, 63, 64, 65 };
        List<Value[]> samples = values.SelectMany(left => values.Select(right => new[] { Conformance.Int64(left), Conformance.Int64(right) })).ToList();
        ConformanceReport report = Conformance.Run("int64_t a, int64_t b", "int64_t", expression, samples);
        _output.WriteLine(report.ToString());
        Assert.True(report.Exact == report.Samples, report.ToString());
    }

    [Theory]
    [InlineData("a / b")]
    [InlineData("a % b")]
    [InlineData("a >> b")]
    [InlineData("a << b")]
    [InlineData("abs(a)")]
    [InlineData("firstbithigh(a)")]
    [InlineData("(int)(uint(a) / uint(b))")]
    [InlineData("(int)(uint(a) % uint(b))")]
    public void IntOperation_IsBitExact(string expression)
    {
        List<int> values = Conformance.Ints(40, 3).ToList();
        List<Value[]> samples = values.SelectMany(left => values.Take(30).Select(right => new[] { Value.FromInt(left), Value.FromInt(right) })).ToList();
        ConformanceReport report = Conformance.Run("int a, int b", "int", expression, samples);
        _output.WriteLine(report.ToString());
        Assert.True(report.Exact == report.Samples, report.ToString());
    }

    private static string Parameters(int arity) => string.Join(", ", "abc".Take(arity).Select(name => $"float {name}"));

    private static Value Vector(Value x, Value y, Value z) =>
        new Value(NumericType.Vector(ScalarKind.Float, 3), new[] { x.Bits[0], y.Bits[0], z.Bits[0] }, Enumerable.Repeat(ShaderCalc.Units.UnitTag.Bare, 3).ToArray());
}
