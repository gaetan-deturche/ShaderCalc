using System.IO;
using System.Security;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ShaderCalc.Evaluation;

namespace ShaderCalc.App.Ui;

/// <summary>HLSL syntax colouring for AvalonEdit: keywords, types, every intrinsic of the interpreter, numbers, comments.</summary>
internal static class HlslHighlighting
{
    public static readonly string[] Keywords =
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "discard", "struct", "typedef",
        "cbuffer", "tbuffer", "namespace", "using", "static", "const", "inline", "constexpr", "in", "out", "inout", "uniform", "true", "false",
        "auto", "static_cast", "unsigned", "register", "groupshared", "precise", "vector", "matrix", "void",
    };

    public static IHighlightingDefinition Create(Palette palette)
    {
        string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        string Words(IEnumerable<string> words) => string.Concat(words.Select(word => $"<Word>{SecurityElement.Escape(word)}</Word>"));

        string xshd = $$"""
            <SyntaxDefinition name="HLSL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{{Hex(palette.Comment)}}" />
              <Color name="String" foreground="{{Hex(palette.String)}}" />
              <Color name="Preprocessor" foreground="{{Hex(palette.Preprocessor)}}" />
              <Color name="Keyword" foreground="{{Hex(palette.Keyword)}}" />
              <Color name="Type" foreground="{{Hex(palette.Type)}}" />
              <Color name="Intrinsic" foreground="{{Hex(palette.Intrinsic)}}" />
              <Color name="Number" foreground="{{Hex(palette.Number)}}" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Span color="String">
                  <Begin>"</Begin>
                  <End>"</End>
                  <RuleSet><Span begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="Preprocessor" begin="^\s*\#" />
                <Keywords color="Keyword">{{Words(Keywords)}}</Keywords>
                <Rule color="Type">\b(bool|int|uint|dword|half|float|double|min16float|min10float|min16int|min12int|min16uint|int32_t|uint32_t|int64_t|uint64_t|float32_t|float64_t)([1-4](x[1-4])?)?\b</Rule>
                <Keywords color="Intrinsic">{{Words(Intrinsics.Names)}}</Keywords>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+[uUlL]*\b|(\b[0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?([fFhHuUlL]|lf|LF)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using XmlReader reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
