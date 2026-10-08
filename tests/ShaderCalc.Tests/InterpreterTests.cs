using ShaderCalc;

namespace ShaderCalc.Tests;

public class InterpreterTests
{
    internal const string EngineHelpers = """
        // Pasted as found in engine shaders
        float2 UnitVectorToOctahedron(float3 N)
        {
            N.xy /= dot(1, abs(N));
            if (N.z <= 0)
            {
                N.xy = (1 - abs(N.yx)) * select(N.xy >= 0, float2(1, 1), float2(-1, -1));
            }
            return N.xy;
        }

        uint PackRGBA8(float4 Color)
        {
            uint4 Bytes = uint4(saturate(Color) * 255.0 + 0.5);
            return Bytes.x | (Bytes.y << 8) | (Bytes.z << 16) | (Bytes.w << 24);
        }

        float KineticEnergy(float Mass, float Speed) { return 0.5 * Mass * Speed * Speed; }
        float Illuminance(float Intensity, float Distance) { return Intensity / (Distance * Distance); }
        float Bad(float Distance, float Time) { return Distance + Time; }
        float Offset(float Distance) { return Distance + 1.0; }

        void SinCos(float Angle, out float S, out float C) { S = sin(Angle); C = cos(Angle); }
        float Pythagoras(float Angle) { float s, c; SinCos(Angle, s, c); return s * s + c * c; }

        float SumSeries(int Count)
        {
            float Sum = 0;
            for (int i = 1; i <= Count; ++i)
            {
                Sum += 1.0 / (i * i);
            }
            return Sum;
        }

        uint Hash(uint Seed)
        {
            Seed ^= Seed >> 16;
            Seed *= 0x7feb352du;
            Seed ^= Seed >> 15;
            Seed *= 0x846ca68bu;
            return Seed ^ (Seed >> 16);
        }
        """;

    internal const string CppHelpers = """
        #include <algorithm>

        inline float SmoothStep01(const float& x)
        {
            const float t = std::clamp(x, 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        inline int RoundToInt(float value) { return static_cast<int>(value + 0.5f); }
        inline void Accumulate(float& total, const float amount) { total += amount; }
        inline float AccumulateTwice(float start, float amount)
        {
            auto total = start;
            Accumulate(total, amount);
            Accumulate(total, amount);
            return total;
        }
        """;

    private static ShaderSession Session(string program = "")
    {
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> diagnostics = session.SetProgram(program);
        Assert.False(diagnostics.Any(diagnostic => diagnostic.IsError), string.Join("\n", diagnostics));
        return session;
    }

    private static string Eval(ShaderSession session, string line, bool expectProblems = false)
    {
        LineResult result = session.Evaluate(line);
        if (!expectProblems)
        {
            Assert.False(result.Diagnostics.Any(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info), $"{line}: {string.Join(" | ", result.Diagnostics)}");
        }
        return result.ToString();
    }

    [Theory]
    [InlineData("PackRGBA8(float4(0.25, 0.5, 0.75, 1.0))", "4290740288")]
    [InlineData("KineticEnergy(2 kg, 3 m/s)", "9 kg·m²/s² (J)")]
    [InlineData("Illuminance(100 cd, 2 m)", "25 cd/m² (lx)")]
    [InlineData("Offset(2 m)", "3 m")]
    [InlineData("Pythagoras(0.7)", "1")]
    [InlineData("Hash(42u)", "388445122")]
    [InlineData("Hash(0xDEADBEEFu)", "3861431939")]
    [InlineData("UnitVectorToOctahedron(normalize(float3(0.3, -0.5, -0.8)))", "float2(0.6875, -0.8125)")]
    public void EngineHelpers_Evaluate(string line, string expected) => Assert.Equal(expected, Eval(Session(EngineHelpers), line));

    [Fact]
    public void Units_ErrorPointsInsideTheFunction()
    {
        LineResult result = Session(EngineHelpers).Evaluate("Bad(3 m, 2 s)");
        Assert.Equal("5 m", result.ToString());
        Diagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal("Bad", error.Function);
        Assert.Contains("mixes units: m and s", error.Message);
    }

    [Fact]
    public void Units_StrictModeReportsAdoption()
    {
        ShaderSession session = Session(EngineHelpers);
        session.Options = session.Options with { StrictUnits = true };
        LineResult result = session.Evaluate("Offset(2 m)");
        Assert.Equal("3 m", result.ToString());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Message.Contains("taken as m"));
    }

    [Theory]
    [InlineData("SmoothStep01(0.25)", "0.15625")]
    [InlineData("SmoothStep01(1.7)", "1")]
    [InlineData("RoundToInt(2.5)", "3")]
    [InlineData("AccumulateTwice(1.0, 0.1)", "1.2")]
    [InlineData("AccumulateTwice(2 m, 30 cm)", "2.6 m")]
    public void CppForms_Evaluate(string line, string expected) => Assert.Equal(expected, Eval(Session(CppHelpers), line));

    [Theory]
    // HLSL semantics kalk gets wrong
    [InlineData("firstbithigh(11)", "3")]
    [InlineData("firstbithigh(-12)", "3")]
    [InlineData("uint(5) - uint(7)", "4294967294")]
    [InlineData("int(3.7)", "3")]
    [InlineData("int(-3.7)", "-3")]
    [InlineData("-2 ^ 2", "-4")]
    [InlineData("~0u", "4294967295")]
    [InlineData("-7 / 2", "-3")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("7.5 % 2", "1.5")]
    [InlineData("int(2147483647) + 1", "-2147483648")]
    // Literals are 64-bit until they meet a type (DXC)
    [InlineData("2147483647 + 1", "2147483648")]
    [InlineData("int(1 << 33)", "0")]
    [InlineData("int x = 1; x << 33", "2")]
    // Intrinsics
    [InlineData("asuint(sqrt(2.0))", "1068827891")]
    [InlineData("0.1f + 0.2f", "0.3")]
    [InlineData("1.0f / 3.0f", "0.33333334")]
    [InlineData("f16tof32(f32tof16(0.1))", "0.099975586")]
    [InlineData("round(2.5)", "2")]
    [InlineData("round(-0.5)", "-0")]
    [InlineData("countbits(0xF0F0u)", "8")]
    [InlineData("reversebits(1u)", "2147483648")]
    [InlineData("length(float3(3, 4, 12))", "13")]
    [InlineData("smoothstep(0.0, 1.0, 0.25)", "0.15625")]
    [InlineData("frac(-1.25)", "0.75")]
    [InlineData("mad(0.1, 0.2, 0.3)", "0.32000002")]
    [InlineData("max(3, 2.5)", "3")]
    [InlineData("saturate(1.5)", "1")]
    [InlineData("asfloat(0x7F800000u)", "+INF")]
    [InlineData("float3(1, 2, 3).zyx * 2", "float3(6, 4, 2)")]
    [InlineData("dot(float3(1, 2, 3), float3(4, 5, 6))", "32")]
    [InlineData("cross(float3(1, 0, 0), float3(0, 1, 0))", "float3(0, 0, 1)")]
    [InlineData("pow(-2.0, 2.0)", "4")]
    [InlineData("pow(-2.0, 3.0)", "NaN")]
    // Units
    [InlineData("sqrt(4 m^2)", "2 m")]
    [InlineData("length(float3(3 m, 4 m, 0 m))", "5 m")]
    [InlineData("2 km / 30 min", "1.1111112 m/s")]
    [InlineData("3 km + 200 m", "3200 m")]
    [InlineData("pow(3 m, 2)", "9 m²")]
    [InlineData("60Hz * 2 s", "120")]
    [InlineData("100 km / 2 h", "13.888889 m/s")]
    public void Calculator_Evaluates(string line, string expected) => Assert.Equal(expected, Eval(Session(), line));

    [Theory]
    [InlineData("3 km + 2 s", "mixes units")]
    [InlineData("sin(2 m)", "dimensionless")]
    public void Calculator_ReportsUnitErrors(string line, string message)
    {
        LineResult result = Session().Evaluate(line);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(message));
    }

    [Fact]
    public void Matrices_MultiplyTransposeDeterminant()
    {
        ShaderSession session = Session();
        Eval(session, "float2x2 m = float2x2(1, 2, 3, 4)");
        Assert.Equal("float2(5, 11)", Eval(session, "mul(m, float2(1, 2))"));
        Assert.Equal("float2(7, 10)", Eval(session, "mul(float2(1, 2), m)"));
        Assert.Equal("float2x2(1, 3, 2, 4)", Eval(session, "transpose(m)"));
        Assert.Equal("-2", Eval(session, "determinant(m)"));
        Assert.Equal("float2(3, 4)", Eval(session, "m[1]"));
        Assert.Equal("2", Eval(session, "m._m01"));
        Assert.Equal("float2(1, 4)", Eval(session, "m._11_22"));
        Assert.Equal("float2x2(7, 10, 15, 22)", Eval(session, "mul(m, m)"));
    }

    [Fact]
    public void StructsArraysAndInitializers()
    {
        ShaderSession session = Session("""
            struct Light { float3 Color; float Intensity; };
            static const float Weights[3] = { 0.25, 0.5, 0.25 };
            float Sum(float values[3]) { float total = 0; for (int i = 0; i < 3; i++) total += values[i]; return total; }
            Light MakeLight() { Light light = { 1, 0.5, 0.25, 2 }; return light; }
            Light Zero() { return (Light)0; }
            """);
        Assert.Equal("1", Eval(session, "Sum(Weights)"));
        Assert.Equal("{ Color = float3(1, 0.5, 0.25), Intensity = 2 }", Eval(session, "MakeLight()"));
        Assert.Equal("2", Eval(session, "MakeLight().Intensity"));
        Assert.Equal("{ Color = float3(0, 0, 0), Intensity = 0 }", Eval(session, "Zero()"));
        Assert.Equal("{1, 2, 3}", Eval(session, "int a[] = { 1, 2, 3 }"));
    }

    [Fact]
    public void Macros_ExpandLikeThePreprocessor()
    {
        ShaderSession session = Session("""
            #define SQUARE(x) ((x) * (x))
            #define PI 3.14159265
            #if defined(PI) && 1
            float Area(float r) { return PI * SQUARE(r); }
            #else
            float Area(float r) { return 0; }
            #endif
            """);
        Assert.Equal("12.566371", Eval(session, "Area(2)"));
    }

    [Fact]
    public void Overloads_PickTheBestMatch()
    {
        ShaderSession session = Session("""
            int Kind(int x) { return 1; }
            int Kind(float x) { return 2; }
            int Kind(float3 x) { return 3; }
            """);
        Assert.Equal("1", Eval(session, "Kind(5)"));
        Assert.Equal("2", Eval(session, "Kind(5.0)"));
        Assert.Equal("3", Eval(session, "Kind(float3(1, 2, 3))"));
    }

    [Fact]
    public void ControlFlow_SwitchAndLoops()
    {
        ShaderSession session = Session("""
            int Classify(int x)
            {
                switch (x)
                {
                    case 0: return 10;
                    case 1:
                    case 2: return 20;
                    default: break;
                }
                int n = 0;
                do { n++; } while (n < x);
                return n;
            }
            """);
        Assert.Equal("10", Eval(session, "Classify(0)"));
        Assert.Equal("20", Eval(session, "Classify(2)"));
        Assert.Equal("7", Eval(session, "Classify(7)"));
    }

    [Fact]
    public void SessionVariablesAndUniforms()
    {
        ShaderSession session = Session("""
            float Scale;
            float Scaled(float x) { return x * Scale; }
            """);
        Assert.Equal("0", Eval(session, "Scaled(3)"));
        Eval(session, "Scale = 2");
        Assert.Equal("6", Eval(session, "Scaled(3)"));
        Eval(session, "v = float3(1, 2, 3)");
        Eval(session, "v.y = 5");
        Assert.Equal("float3(1, 5, 3)", Eval(session, "v"));
        Assert.Equal("defined Twice", Eval(session, "float Twice(float x) { return 2 * x; }"));
        Assert.Equal("10", Eval(session, "Twice(v.y)"));
        LineResult truncated = session.Evaluate("Twice(v)");
        Assert.Equal("2", truncated.ToString());
        Assert.Contains(truncated.Diagnostics, diagnostic => diagnostic.Message.Contains("truncation"));
    }

    [Theory]
    [InlineData("float Bad(float x) { return x + Missing; }", "unknown name 'Missing'")]
    [InlineData("int F(int x) { return F(x - 1); }", "recursive")]
    [InlineData("float3 F(float3 v) { return v && v; }", "and()")]
    [InlineData("float F(float x) { x.w = 1; return x; }", "reads past")]
    public void Binder_ReportsErrors(string program, string message)
    {
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> diagnostics = session.SetProgram(program);
        Assert.Contains(diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains(message));
    }

    [Fact]
    public void Binder_WarnsOnImplicitTruncation()
    {
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> diagnostics = session.SetProgram("float3 F(float4 v) { return v; }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Message.Contains("truncation"));
    }
}
