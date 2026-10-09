using ShaderCalc;
using ShaderCalc.Reference;

namespace ShaderCalc.Tests;

public class WorksheetTests
{
    private static WorksheetResult Run(params (string Name, string Text)[] documents) =>
        Worksheet.Evaluate(documents.Select(document => new WorksheetDocument(document.Name, document.Text)).ToList());

    /// <summary>"line: value" for every line that shows one.</summary>
    private static List<string> Shown(WorksheetResult result, string document) =>
        result.LinesOf(document).Where(line => line.Value != null).Select(line => $"{line.Line}: {line.Value}").ToList();

    [Fact]
    public void Lines_ShowTheirValueAtTheirLastLine()
    {
        WorksheetResult result = Run(("main.hlsl", """
            float KineticEnergy(float m, float v)
            {
                return 0.5 * m * v * v;
            }

            KineticEnergy(2 kg, 3 m/s)
            float3 n = normalize(float3(1, 2, 3))
            asuint(n.x)
            total = KineticEnergy(1, 2) +
                    KineticEnergy(3,
                                  4)
            x = 1
            -2
            """));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(new[]
        {
            "6: 9 kg·m²/s² (J)",
            "7: float3(0.26726124, 0.5345225, 0.8017837)",
            "8: 1049155191",
            "11: 26",
            "12: 1",
            "13: -2",
        }, Shown(result, "main.hlsl"));
    }

    [Fact]
    public void TopLevelVariables_AreGlobalsFunctionsCanRead()
    {
        WorksheetResult result = Run(("main.hlsl", """
            float Scaled(float x) { return x * Scale; }
            Scale = 3
            Scaled(2)
            Scale = 10
            Scaled(2)
            """));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(new[] { "2: 3", "3: 6", "4: 10", "5: 20" }, Shown(result, "main.hlsl"));
    }

    [Fact]
    public void Tabs_ShareDeclarationsAndMacros_InTabOrder()
    {
        WorksheetResult result = Run(
            ("engine.hlsl", """
                #define SQUARE(x) ((x) * (x))
                struct Light { float3 Color; float Intensity; };
                Texture2D SceneColor;
                float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
                """),
            ("scratch.hlsl", """
                Light l = { 1, 1, 1, 2 }
                SQUARE(l.Intensity) * Luminance(l.Color)
                """));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(new[] { "1: { Color = float3(1, 1, 1), Intensity = 2 }", "2: 4" }, Shown(result, "scratch.hlsl"));
        Assert.Contains(result.DiagnosticsOf("engine.hlsl"), diagnostic => diagnostic.Message.Contains("resources aren't supported"));
    }

    [Fact]
    public void Errors_StayOnTheirLine_AndNameTheirFile()
    {
        WorksheetResult result = Run(
            ("lib.hlsl", "float Bad(float d, float t) { return d + t; }\nint Div(int a, int b) { return a / b; }"),
            ("main.hlsl", "a = 1\nb = Missing + 1\nBad(3 m, 2 s)\nint arr[2] = { 1, 2 }\narr[a + 5]\nc = a + 1"));
        Assert.Contains(result.DiagnosticsOf("main.hlsl"), diagnostic => diagnostic.IsError && diagnostic.Message.Contains("unknown name 'Missing'"));
        // The unit error points into lib.hlsl, inside Bad
        Assert.Contains(result.DiagnosticsOf("lib.hlsl"), diagnostic => diagnostic.IsError && diagnostic.Function == "Bad" && diagnostic.Message.Contains("mixes units"));
        WorksheetLine outOfRange = result.LinesOf("main.hlsl").Single(line => line.Line == 5);
        Assert.Contains(outOfRange.Result.Diagnostics, diagnostic => diagnostic.Message.Contains("out of range"));
        // Later lines still run
        Assert.Contains("6: 2", Shown(result, "main.hlsl"));
    }

    [Fact]
    public void Lines_MatchTheReference_FromTheirInputs()
    {
        WorksheetResult result = Run(("main.hlsl", """
            float Scaled(float x) { return x * Scale; }
            Scale = 0.1
            float3 n = normalize(float3(1, 2, 3)) * Scale
            asuint(n.y)
            Scaled(n.z)
            Scale = Scale * 3
            """));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
        foreach (WorksheetLine line in result.Lines)
        {
            ReferenceOutcome outcome = ReferenceChecker.Check(line.Result);
            Assert.True(outcome.Verdict == ReferenceVerdict.Match, $"line {line.Line} = {line.Value}: {outcome}\n{outcome.Hlsl}");
        }
    }
}
