using ShaderCalc;
using ShaderCalc.Reference;
using Xunit.Abstractions;

namespace ShaderCalc.Tests;

/// <summary>Language coverage: each case is evaluated, then (when it has a value) checked against DXC + WARP.</summary>
public class LanguageTests
{
    private const string Program = """
        #define CONCAT(a, b) a##b
        #define STRINGIFY(x) #x
        #define CHANNELS 3
        #ifndef CHANNELS
        #error CHANNELS must be defined
        #endif

        namespace Lighting
        {
            typedef float3 Color;
            using Scalar = float;

            struct Surface
            {
                Color Albedo;
                Scalar Roughness;
                float2 Uv[2];
            };
        }

        cbuffer View : register(b0)
        {
            float Exposure;
            float4 Tint;
        };

        static const int Lut[4] = { 1, 3, 5, 7 };
        static float Accumulator = 0.25;

        float Weight(float x, float scale = 2.0) { return x * scale; }
        float Forward(float x);
        float Forward(float x) { return x + 1; }

        void Order(inout float a, out float b, in float c)
        {
            b = a + c;
            a = b * 2;
        }

        float CallOrder() { float a = 1, b = 0; Order(a, b, 10); return a * 100 + b; }

        Surface MakeSurface(float r)
        {
            Surface s = (Surface)0;
            s.Albedo = float3(0.5, 0.25, 1);
            s.Roughness = r;
            s.Uv[1] = float2(r, 1 - r);
            return s;
        }

        float Sum(int count)
        {
            float total = 0;
            for (int i = 0, j = 10; i < count; ++i, --j)
            {
                if (i == 2) continue;
                total += Lut[i % 4] * j;
            }
            return total;
        }

        int Fallthrough(int x)
        {
            int result = 0;
            switch (x)
            {
                case 0: result += 1;
                case 1: result += 10; break;
                default: result = -1;
            }
            return result;
        }

        float Statics() { Accumulator += 1; return Accumulator; }

        float3 Swizzles(float4 v)
        {
            float3 r = v.wzy;
            r.xz *= 2;
            r.y += v[3];
            return r;
        }

        int CONCAT(Pas, ted)() { return CHANNELS; }
        """;

    private readonly ITestOutputHelper _output;

    public LanguageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("Weight(3)", "6")]
    [InlineData("Weight(3, 0.5)", "1.5")]
    [InlineData("Forward(1)", "2")]
    [InlineData("CallOrder()", "2211")]
    [InlineData("MakeSurface(0.25).Uv[1]", "float2(0.25, 0.75)")]
    [InlineData("MakeSurface(0.75).Roughness", "0.75")]
    [InlineData("Sum(4)", "86")]
    [InlineData("Fallthrough(0)", "11")]
    [InlineData("Fallthrough(1)", "10")]
    [InlineData("Fallthrough(5)", "-1")]
    [InlineData("Statics() + Statics()", "3.5")]
    [InlineData("Swizzles(float4(1, 2, 3, 4))", "float3(8, 7, 4)")]
    [InlineData("Pasted()", "3")]
    [InlineData("Lut[2] * Exposure", "0")]
    [InlineData("float3(1, 2, 3) > 2", "bool3(false, false, true)")]
    [InlineData("any(float3(0, 0, 1)) && !all(int2(1, 0))", "true")]
    [InlineData("select(bool2(true, false), float2(1, 2), float2(3, 4))", "float2(1, 4)")]
    [InlineData("and(bool2(true, true), bool2(false, true))", "bool2(false, true)")]
    [InlineData("010 + 0x10 + 1.f + .5 + 1e1", "35.5")]
    [InlineData("min16float3(1, 2, 3).y", "2")]
    [InlineData("half2(0.1, 0.2) * 2", "float2(0.2, 0.4)")]
    [InlineData("uint64_t(1) << 40", "1099511627776")]
    [InlineData("2.5L * 2", "5")]
    [InlineData("(int2)float2(1.9, -1.9)", "int2(1, -1)")]
    [InlineData("vector<float, 2>(1, 2) + matrix<float, 1, 2>(3, 4)", "float2(4, 6)")]
    [InlineData("float2x2 m = { 1, 2, 3, 4 }; m._m10_m01", "float2(3, 2)")]
    [InlineData("int x = 5; x += 3; x <<= 2; x", "32")]
    [InlineData("float3 v = float3(1, 2, 3); v.zx = v.xz; v", "float3(3, 2, 1)")]
    public void Evaluates_AndMatchesReference(string line, string expected)
    {
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> problems = session.SetProgram(Program);
        Assert.False(problems.Any(problem => problem.IsError), string.Join("\n", problems));
        LineResult result = session.Evaluate(line);
        Assert.False(result.Diagnostics.Any(diagnostic => diagnostic.IsError), string.Join(" | ", result.Diagnostics));
        Assert.Equal(expected, result.ToString());

        ReferenceOutcome outcome = ReferenceChecker.Check(result);
        _output.WriteLine($"{line} = {result} -> {outcome}");
        if (outcome.Verdict != ReferenceVerdict.Match)
        {
            _output.WriteLine(outcome.Hlsl);
        }
        Assert.Equal(ReferenceVerdict.Match, outcome.Verdict);
    }

    [Fact]
    public void Uniforms_PersistAndReachTheReference()
    {
        ShaderSession session = new ShaderSession();
        session.SetProgram(Program);
        session.Evaluate("Exposure = 2");
        session.Evaluate("Tint = float4(1, 0.5, 0.25, 1)");
        LineResult result = session.Evaluate("Tint.rgb * Exposure");
        Assert.Equal("float3(2, 1, 0.5)", result.ToString());
        Assert.Equal(ReferenceVerdict.Match, ReferenceChecker.Check(result).Verdict);
    }

    [Theory]
    [InlineData("float F() { return ddx(1.0); }", "only exists inside a GPU pipeline")]
    [InlineData("float16_t F() { return 1; }", "16-bit types")]
    [InlineData("float F(float3 v) { return v ? 1 : 0; }", "select()")]
    [InlineData("struct S { float a; }; S F() { return S(1); }", "no constructors")]
    [InlineData("float F() { int a[2] = { 1, 2, 3 }; return a[0]; }", "needs 2 values")]
    [InlineData("float F() { float2 v = 1; return v[2]; }", "out of range")]
    [InlineData("float F(float x) { return x; } float F(float y) { return y; }", "already defined")]
    [InlineData("float F(int a) { return 1; } float F(uint a) { return 2; } float G() { return F(1.5); }", "ambiguous")]
    [InlineData("float F() { const float k = 1; k = 2; return k; }", "is const")]
    [InlineData("float Exposure; void F() { Exposure = 1; }", "read-only")]
    public void Reports_Errors(string program, string message)
    {
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> diagnostics = session.SetProgram(program);
        Assert.Contains(diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(message));
    }

    [Fact]
    public void Approximations_AreFlaggedNotHidden()
    {
        ShaderSession session = new ShaderSession();
        LineResult result = session.Evaluate("tanh(100.0)");
        Assert.Equal("1", result.ToString());
        ReferenceOutcome outcome = ReferenceChecker.Check(result);
        _output.WriteLine(outcome.ToString());
        // WARP's tanh overflows to NaN here; the difference is reported, attributed to approximate functions
        Assert.Equal(ReferenceVerdict.Mismatch, outcome.Verdict);
        Assert.True(outcome.UsesApproximations);
    }
}
