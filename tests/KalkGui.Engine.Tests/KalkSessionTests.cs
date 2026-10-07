using KalkGui.Engine;

namespace KalkGui.Engine.Tests;

public class KalkSessionTests
{
    private string _clipboard = string.Empty;
    private bool _clearRequested;

    private KalkSession CreateSession()
    {
        return new KalkSession(() => _clipboard, text => _clipboard = text, () => _clearRequested = true, loadUserConfig: false);
    }

    [Fact]
    public async Task Evaluate_VectorExpression_EchoesInputAndResult()
    {
        KalkSession session = CreateSession();

        EvaluationResult result = await session.EvaluateAsync("float4(1,2,3,4) * 2");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Contains("# float4(1, 2, 3, 4) * 2", result.Output);
        Assert.Contains("out = float4(2, 4, 6, 8)", result.Output);
    }

    [Fact]
    public async Task Evaluate_UserFunction_PersistsAcrossEvaluationsAndIsListed()
    {
        KalkSession session = CreateSession();

        await session.EvaluateAsync("f(x) = x^2 + 1");
        EvaluationResult result = await session.EvaluateAsync("f(3)");

        Assert.Contains("out = 10", result.Output);
        UserSymbol symbol = Assert.Single(session.GetUserSymbols());
        Assert.Equal("f", symbol.Name);
        Assert.True(symbol.IsFunction);
    }

    [Fact]
    public async Task Evaluate_DeveloperDisplay_ShowsBitPattern()
    {
        KalkSession session = CreateSession();

        await session.EvaluateAsync("display dev");
        EvaluationResult result = await session.EvaluateAsync("0xff030201");

        Assert.Equal(Kalk.Core.KalkDisplayMode.Developer, session.DisplayMode);
        Assert.Contains("0b_1111_1111_0000_0011_0000_0010_0000_0001", result.Output);
    }

    [Fact]
    public async Task Evaluate_UnitConversion_AfterImport()
    {
        KalkSession session = CreateSession();

        await session.EvaluateAsync("import StandardUnits");
        EvaluationResult result = await session.EvaluateAsync("10 kg/s |> to kg/h");

        Assert.Contains("out = 36000 * kg / h", result.Output);
        Assert.Contains(session.GetCompletions("km"), item => item.Name == "km" && item.Kind == CompletionKind.Unit);
    }

    [Fact]
    public async Task Evaluate_ParseError_ReportsMessage()
    {
        KalkSession session = CreateSession();

        EvaluationResult result = await session.EvaluateAsync("1 +");

        Assert.False(result.IsSuccess);
        Assert.Contains("expression", result.Error);
    }

    [Fact]
    public async Task Evaluate_UnknownVariable_ReportsRuntimeError()
    {
        KalkSession session = CreateSession();

        EvaluationResult result = await session.EvaluateAsync("unknown_var * 2");

        Assert.False(result.IsSuccess);
        Assert.Contains("unknown_var", result.Error);
        Assert.Equal(0, result.ErrorColumn);
    }

    [Fact]
    public async Task Evaluate_InfiniteLoop_StopsWhenCancelled()
    {
        KalkSession session = CreateSession();

        Task<EvaluationResult> evaluation = session.EvaluateAsync("while true; end");
        await Task.Delay(200);
        Assert.True(session.IsEvaluating);
        session.CancelEvaluation();
        Task finished = await Task.WhenAny(evaluation, Task.Delay(5000));

        Assert.Same(evaluation, finished);
        Assert.True((await evaluation).IsCancelled);
        Assert.False(session.IsEvaluating);
    }

    [Fact]
    public async Task Evaluate_ClipboardFunctions_UseHostCallbacks()
    {
        KalkSession session = CreateSession();
        _clipboard = "42";

        await session.EvaluateAsync("clipboard \"hello\"");
        await session.EvaluateAsync("clear screen");

        Assert.Equal("hello", _clipboard);
        Assert.True(_clearRequested);
    }

    [Fact]
    public void Highlight_ColoursNumbersAndComments()
    {
        KalkSession session = CreateSession();
        const string text = "out = 42 # note";

        IReadOnlyList<StyledSpan> spans = session.Highlight(text);

        StyledSpan number = Assert.Single(spans, span => text.Substring(span.Start, span.Length) == "42");
        Assert.True(number.Style.Bold);
        StyledSpan comment = Assert.Single(spans, span => text.Substring(span.Start, span.Length) == "# note");
        Assert.Equal(KalkColor.BrightGreen, comment.Style.Foreground);
    }

    [Fact]
    public async Task Documentation_IncludesFunctionsOfModulesNotImportedYet()
    {
        KalkSession session = CreateSession();

        DocEntry before = Assert.Single(session.GetDocumentation(), entry => entry.Name == "capitalize");
        await session.EvaluateAsync("import Strings");
        DocEntry after = Assert.Single(session.GetDocumentation(), entry => entry.Name == "capitalize");

        Assert.Equal("Strings", before.ModuleName);
        Assert.False(before.IsAvailable);
        Assert.True(after.IsAvailable);
        Assert.Contains(session.GetDocumentation(), entry => entry.Name == "bin" && entry.ModuleName == null && entry.Signature == "bin(value, prefix?, separator?)");
    }
}
