using KalkGui.Engine;

namespace KalkGui.Engine.Tests;

public class LanguageDocsTests
{
    [Theory]
    [InlineData("func", "Multiline functions")]
    [InlineData("if", "If statements")]
    [InlineData("|>", "Pipe Expressions")]
    [InlineData("<<", "Bit shifts")]
    [InlineData("xor", "XOR and NOT")]
    [InlineData("+=", "Compound assignment and increments")]
    [InlineData("..<", "Ranges")]
    public void Keyword_FindsItsLanguageSection(string keyword, string expectedTitle)
    {
        DocEntry entry = Assert.Single(LanguageDocs.All, candidate => candidate.Name == expectedTitle);

        Assert.Contains(keyword, entry.Descriptor.Names);
        Assert.True(entry.IsLanguage);
        Assert.NotEmpty(entry.Summary);
    }

    [Theory]
    [InlineData("func", "Multiline functions")]
    [InlineData("<<", "Bit shifts")]
    [InlineData("bitwise and", "Bitwise AND and OR")]
    [InlineData("pipe", "Pipe Expressions")]
    public void Search_RanksTheMatchingSectionFirst(string query, string expectedTitle)
    {
        DocEntry best = LanguageDocs.All
            .Select(entry => (Entry: entry, Score: DocSearch.Score(entry, query)))
            .Where(candidate => candidate.Score != null)
            .OrderBy(candidate => candidate.Score)
            .First().Entry;

        Assert.Equal(expectedTitle, best.Name);
    }

    [Fact]
    public void Search_RequiresEveryTerm()
    {
        DocEntry shifts = Assert.Single(LanguageDocs.All, entry => entry.Name == "Bit shifts");

        Assert.NotNull(DocSearch.Score(shifts, "shift signed"));
        Assert.Null(DocSearch.Score(shifts, "shift zebra"));
    }

    /// <summary>The supplement documents kalk behaviour kalk itself doesn't: every example must still hold.</summary>
    [Fact]
    public async Task SupplementExamples_MatchKalk()
    {
        string kalkFolder = Path.Combine(Path.GetTempPath(), "KalkGuiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kalkFolder);
        try
        {
            List<string> mismatches = new List<string>();
            foreach (List<(string Input, List<string> Expected)> block in KalkExampleBlocks(LanguageDocs.ReadEmbedded("supplement.md")))
            {
                // One session per block: state carries between the lines of a block only
                KalkSession session = new KalkSession(() => string.Empty, _ => { }, () => { }, loadUserConfig: false, kalkUserFolder: kalkFolder, useLibrary: false);
                foreach ((string input, List<string> expected) in block)
                {
                    EvaluationResult result = await session.EvaluateAsync(input);
                    List<string> actual = result.IsSuccess ? NonEmptyLines(result.Output) : new List<string> { $"ERROR: {result.Error}" };
                    if (!actual.SequenceEqual(expected))
                    {
                        mismatches.Add($">>> {input}\n  expected: {string.Join(" | ", expected)}\n  actual:   {string.Join(" | ", actual)}");
                    }
                }
            }

            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        }
        finally
        {
            Directory.Delete(kalkFolder, recursive: true);
        }
    }

    private static IEnumerable<List<(string Input, List<string> Expected)>> KalkExampleBlocks(string markdown)
    {
        List<(string Input, List<string> Expected)>? block = null;
        foreach (string rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (block != null)
                {
                    yield return block;
                    block = null;
                }
                else if (line == "```kalk")
                {
                    block = new List<(string Input, List<string> Expected)>();
                }
            }
            else if (block != null && line.StartsWith(">>> ", StringComparison.Ordinal))
            {
                block.Add((line[4..], new List<string>()));
            }
            else if (block != null && block.Count > 0 && line.Length > 0)
            {
                block[^1].Expected.Add(line);
            }
        }
    }

    private static List<string> NonEmptyLines(string text)
    {
        return text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    }
}
