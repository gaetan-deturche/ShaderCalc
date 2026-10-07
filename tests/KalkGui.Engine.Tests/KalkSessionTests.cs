using KalkGui.Engine;

namespace KalkGui.Engine.Tests;

public class KalkSessionTests : IDisposable
{
    // Per-test stand-in for ~/.kalk, so tests never touch the user's config.kalk / library.kalk
    private readonly string _kalkFolder = Path.Combine(Path.GetTempPath(), "KalkGuiTests", Guid.NewGuid().ToString("N"));
    private string _clipboard = string.Empty;
    private bool _clearRequested;

    public KalkSessionTests()
    {
        Directory.CreateDirectory(_kalkFolder);
    }

    public void Dispose()
    {
        Directory.Delete(_kalkFolder, recursive: true);
    }

    private string LibraryPath => Path.Combine(_kalkFolder, "library.kalk");

    private KalkSession CreateSession()
    {
        return new KalkSession(() => _clipboard, text => _clipboard = text, () => _clearRequested = true, kalkUserFolder: _kalkFolder);
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
    public async Task Library_DefinitionsPersistAcrossSessions()
    {
        KalkSession firstSession = CreateSession();
        await firstSession.EvaluateAsync("f(x) = x^2 + 1");
        await firstSession.EvaluateAsync("gain = 2.5");

        KalkSession secondSession = CreateSession();
        EvaluationResult function = await secondSession.EvaluateAsync("f(3)");
        EvaluationResult variable = await secondSession.EvaluateAsync("gain * 2");

        Assert.Empty(secondSession.LibraryLoadErrors);
        Assert.Contains("out = 10", function.Output);
        Assert.Contains("out = 5", variable.Output);
        Assert.All(secondSession.GetUserSymbols(), symbol => Assert.True(symbol.IsInLibrary));
    }

    [Fact]
    public async Task Library_DeleteRemovesTheEntry()
    {
        KalkSession session = CreateSession();
        await session.EvaluateAsync("keep = 1");
        await session.EvaluateAsync("drop = 2");

        await session.EvaluateAsync("del drop");

        string library = File.ReadAllText(LibraryPath);
        Assert.Contains("keep = 1", library);
        Assert.DoesNotContain("drop", library);
    }

    [Fact]
    public async Task Library_ConfigDefinitionsStayInConfig()
    {
        File.WriteAllText(Path.Combine(_kalkFolder, "config.kalk"), "from_config = 7\n");
        KalkSession session = CreateSession();

        await session.EvaluateAsync("from_session = 8");

        string library = File.ReadAllText(LibraryPath);
        Assert.DoesNotContain("from_config", library);
        Assert.Contains("from_session = 8", library);
        Assert.False(Assert.Single(session.GetUserSymbols(), symbol => symbol.Name == "from_config").IsInLibrary);
    }

    [Fact]
    public async Task Library_ImportsArePersistedSoUnitValuesReload()
    {
        KalkSession firstSession = CreateSession();
        await firstSession.EvaluateAsync("import StandardUnits");
        await firstSession.EvaluateAsync("speed = 10 kg/s");

        KalkSession secondSession = CreateSession();
        EvaluationResult result = await secondSession.EvaluateAsync("speed |> to kg/h");

        Assert.Contains("import StandardUnits", File.ReadAllText(LibraryPath));
        Assert.Empty(secondSession.LibraryLoadErrors);
        Assert.Contains("out = 36000 * kg / h", result.Output);
    }

    [Fact]
    public void Library_BrokenEntryIsReportedAndTheOthersStillLoad()
    {
        File.WriteAllText(LibraryPath, "good = 1\nbroken = 1 +\nalso_good = 2\n");

        KalkSession session = CreateSession();

        LibraryLoadError error = Assert.Single(session.LibraryLoadErrors);
        Assert.Contains("broken", error.Entry);
        Assert.Equal(new[] { "also_good", "good" }, session.GetUserSymbols().Where(symbol => !symbol.IsBroken).Select(symbol => symbol.Name));
        Assert.Equal("broken = 1 +", Assert.Single(session.GetUserSymbols(), symbol => symbol.IsBroken).Definition);
    }

    [Fact]
    public async Task Library_BrokenEntryIsKeptUntilFixed()
    {
        File.WriteAllText(LibraryPath, "broken = 1 +\n");
        KalkSession session = CreateSession();

        await session.EvaluateAsync("other = 1");
        Assert.Contains("broken = 1 +", File.ReadAllText(LibraryPath));

        EvaluationResult fixedResult = await session.EvaluateAsync("broken = 1", replacesSymbol: "broken = 1 +");

        Assert.True(fixedResult.IsSuccess);
        Assert.Empty(session.LibraryLoadErrors);
        string library = File.ReadAllText(LibraryPath);
        Assert.Contains("broken = 1", library);
        Assert.DoesNotContain("1 +", library);
    }

    [Fact]
    public void Library_DiscardedBrokenEntryLeavesTheFile()
    {
        File.WriteAllText(LibraryPath, "good = 1\nbroken = 1 +\n");
        KalkSession session = CreateSession();

        Assert.True(session.DiscardBrokenEntry("broken = 1 +"));

        Assert.Empty(session.LibraryLoadErrors);
        Assert.DoesNotContain("broken", File.ReadAllText(LibraryPath));
        Assert.Contains("good = 1", File.ReadAllText(LibraryPath));
    }

    [Fact]
    public async Task Library_EditingAnEntryUnderANewNameReplacesIt()
    {
        KalkSession session = CreateSession();
        await session.EvaluateAsync("gain = 2.5");

        EvaluationResult result = await session.EvaluateAsync("exposure = 3", replacesSymbol: "gain");

        Assert.Equal(new[] { "exposure" }, result.DefinedNames);
        Assert.Equal(new[] { "exposure" }, session.GetUserSymbols().Select(symbol => symbol.Name));
        string library = File.ReadAllText(LibraryPath);
        Assert.Contains("exposure = 3", library);
        Assert.DoesNotContain("gain", library);
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
