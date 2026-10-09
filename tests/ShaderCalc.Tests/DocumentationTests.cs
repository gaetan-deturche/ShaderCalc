using ShaderCalc.Docs;
using ShaderCalc.Evaluation;

namespace ShaderCalc.Tests;

public class DocumentationTests
{
    [Fact]
    public void EveryIntrinsic_IsDocumented()
    {
        List<string> missing = Intrinsics.Names.Where(name => Documentation.Find(name)?.Signature == null).OrderBy(name => name).ToList();
        Assert.True(missing.Count == 0, "undocumented: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIntrinsicPage_ExistsInTheTable()
    {
        List<string> unknown = Documentation.Entries.Where(entry => !entry.IsTopic && !Intrinsics.TryGet(entry.Name, out _)).Select(entry => entry.Name).ToList();
        Assert.True(unknown.Count == 0, "documented but unknown: " + string.Join(", ", unknown));
    }

    [Theory]
    [InlineData("pow", "pow")]
    [InlineData("units", "Units")]
    [InlineData("denormal", "HLSL semantics")]
    [InlineData("fmod", "fmod")]
    public void Search_RanksTheBestMatchFirst(string query, string expected) => Assert.Equal(expected, Documentation.Search(query)[0].Name);
}
