using ICSharpCode.AvalonEdit.Snippets;

namespace KalkGui.Ui;

/// <summary>
/// kalk templates as AvalonEdit snippets: the first placeholder is selected, Tab moves to the next, Enter or
/// Esc ends; a function parameter is mirrored into its body.
/// </summary>
internal static class KalkSnippets
{
    public static Snippet Variable()
    {
        return new Snippet { Elements = { Field("name"), Text(" = "), Field("value") } };
    }

    public static Snippet OneLineFunction()
    {
        return new Snippet { Elements = { Field("name"), Text("("), Field("x"), Text(") = "), Field("expression") } };
    }

    public static Snippet MultiLineFunction()
    {
        SnippetReplaceableTextElement parameter = Field("x");
        return new Snippet
        {
            Elements = { Text("func "), Field("name"), Text("("), parameter, Text(")\n    ret "), Bound(parameter), new SnippetCaretElement(), Text("\nend") },
        };
    }

    /// <summary>The template a block keyword expands to on Tab, or null.</summary>
    public static Snippet? ForKeyword(string keyword)
    {
        return keyword switch
        {
            "func" => MultiLineFunction(),
            "if" => Block("if ", Field("condition")),
            "for" => Block("for ", Field("i"), Text(" in "), Field("1..10")),
            "while" => Block("while ", Field("condition")),
            _ => null,
        };
    }

    private static Snippet Block(string keyword, params SnippetElement[] header)
    {
        Snippet snippet = new Snippet();
        snippet.Elements.Add(Text(keyword));
        foreach (SnippetElement element in header)
        {
            snippet.Elements.Add(element);
        }
        snippet.Elements.Add(Text("\n    "));
        snippet.Elements.Add(new SnippetCaretElement());
        snippet.Elements.Add(Text("\nend"));
        return snippet;
    }

    private static SnippetTextElement Text(string text)
    {
        return new SnippetTextElement { Text = text };
    }

    private static SnippetReplaceableTextElement Field(string placeholder)
    {
        return new SnippetReplaceableTextElement { Text = placeholder };
    }

    private static SnippetBoundElement Bound(SnippetReplaceableTextElement target)
    {
        return new SnippetBoundElement { TargetElement = target };
    }
}
