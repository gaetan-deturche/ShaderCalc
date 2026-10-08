using System.Text;
using System.Text.RegularExpressions;
using Kalk.Core;

namespace KalkGui.Engine;

/// <summary>
/// The Language part of the docs: kalk's own user guide and syntax guide (embedded from the submodule) plus
/// KalkGui's supplement for what kalk leaves undocumented. One entry per `###` / `####` section.
/// </summary>
public static class LanguageDocs
{
    public const string KalkSource = "kalk's language guide";
    public const string SupplementSource = "KalkGui supplement, examples checked against kalk";

    // Search and F1 aliases of kalk's sections (the supplement declares its own in `<!-- keywords: -->`)
    private static readonly Dictionary<string, string[]> KalkSectionKeywords = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Variables"] = new[] { "out", "=", "variable" },
        ["Pipe Expressions"] = new[] { "|>", "pipe" },
        ["Modules"] = new[] { "import", "module" },
        ["Units and Currencies"] = new[] { "to", "unit", "currency" },
        ["Comments"] = new[] { "#", "##", "comment" },
        ["Integers"] = new[] { "int", "long", "integer" },
        ["Simple Integers"] = new[] { "byte", "sbyte", "short", "ushort", "uint", "ulong" },
        ["Big Integers"] = new[] { "bigint" },
        ["Hexadecimal Integers"] = new[] { "0x", "hexadecimal" },
        ["Binary Integers"] = new[] { "0b", "binary" },
        ["Floats"] = new[] { "float", "double" },
        ["Half"] = new[] { "half" },
        ["Float"] = new[] { "f" },
        ["Decimal"] = new[] { "decimal", "m" },
        ["Binary as floats"] = new[] { "0b" },
        ["Boolean"] = new[] { "true", "false", "bool" },
        ["Vectors"] = new[] { "vector", "swizzle", "xyzw", "rgba" },
        ["Matrices"] = new[] { "matrix" },
        ["Strings"] = new[] { "string", "$\"", "interpolation" },
        ["Arrays"] = new[] { "[]", "array", "size" },
        ["Ranges"] = new[] { "..", "..<", "range" },
        ["Objects"] = new[] { "{}", "object", "keys", "values" },
        ["ByteBuffers"] = new[] { "bytebuffer" },
        ["Nested"] = new[] { "(", ")", "parentheses" },
        ["With Numbers"] = new[] { "+", "-", "*", "/", "//", "%", "^", "operator", "power", "modulo" },
        ["With Strings"] = new[] { "+", "*", "concatenate" },
        ["With Arrays"] = new[] { "|", "&", "+", "*", "union", "intersection" },
        ["Conditional"] = new[] { "==", "!=", "<", ">", "<=", ">=", "&&", "||", "?", ":", "??", "ternary" },
        ["Multiple statements"] = new[] { ";" },
        ["If statements"] = new[] { "if", "else", "end" },
        ["Loop statements"] = new[] { "for", "in", "while", "end", "loop" },
        ["Special loop variables"] = new[] { "for.index", "for.rindex", "for.first", "for.last", "for.even", "for.odd", "for.changed", "while.index" },
        ["`break` and `continue`"] = new[] { "break", "continue" },
        ["Inline functions"] = new[] { "function", "=" },
        ["Multiline functions"] = new[] { "func", "ret", "end", "function" },
        ["Anonymous functions"] = new[] { "do", "ret", "lambda" },
        ["Function Pointers"] = new[] { "@" },
    };

    // User-guide sections that add to the syntax guide; the rest is console-specific or repeated there
    private static readonly HashSet<string> UserGuideSections = new HashSet<string>(StringComparer.Ordinal)
    {
        "Variables", "Pipe Expressions", "Modules", "Units and Currencies", "Comments",
    };

    private static readonly Regex KeywordsComment = new Regex(@"^<!--\s*keywords:\s*(.*?)\s*-->$");

    private static readonly Lazy<IReadOnlyList<DocEntry>> Entries = new Lazy<IReadOnlyList<DocEntry>>(Load);

    public static IReadOnlyList<DocEntry> All => Entries.Value;

    /// <summary>One of the embedded docs: "guide.md", "syntax.md" or "supplement.md".</summary>
    public static string ReadEmbedded(string name)
    {
        using Stream stream = typeof(LanguageDocs).Assembly.GetManifestResourceStream($"KalkGui.Engine.Docs.{name}")
            ?? throw new InvalidOperationException($"Missing embedded doc {name}");
        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<DocEntry> Load()
    {
        List<DocEntry> entries = new List<DocEntry>();
        entries.AddRange(Parse(ReadEmbedded("guide.md"), KalkSource, "Basics", UserGuideSections.Contains));
        entries.AddRange(Parse(ReadEmbedded("syntax.md"), KalkSource, null, _ => true));
        entries.AddRange(Parse(ReadEmbedded("supplement.md"), SupplementSource, null, _ => true));
        return entries;
    }

    /// <param name="chapterOverride">Group name for every entry instead of the `##` chapter titles.</param>
    private static IEnumerable<DocEntry> Parse(string markdown, string source, string? chapterOverride, Func<string, bool> includeSection)
    {
        string chapter = "General";
        string? title = null;
        List<string> body = new List<string>();
        bool isInFence = false;
        bool isInFrontMatter = false;
        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');

        for (int lineIndex = 0; lineIndex <= lines.Length; lineIndex++)
        {
            string? line = lineIndex < lines.Length ? lines[lineIndex] : null;
            if (line != null && lineIndex == 0 && line.Trim() == "---")
            {
                isInFrontMatter = true;
                continue;
            }
            if (isInFrontMatter)
            {
                isInFrontMatter = line?.Trim() != "---";
                continue;
            }
            if (line != null && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                isInFence = !isInFence;
            }

            bool isChapter = line != null && !isInFence && line.StartsWith("## ", StringComparison.Ordinal);
            bool isSection = line != null && !isInFence && (line.StartsWith("### ", StringComparison.Ordinal) || line.StartsWith("#### ", StringComparison.Ordinal));
            if (line != null && !isChapter && !isSection)
            {
                // kalk's "back to top" links mean nothing outside its website
                if (!line.TrimStart().StartsWith("[🢁", StringComparison.Ordinal))
                {
                    body.Add(line);
                }
                continue;
            }

            if (title != null && includeSection(title))
            {
                DocEntry? entry = CreateEntry(title, chapterOverride ?? chapter, source, body);
                if (entry != null)
                {
                    yield return entry;
                }
            }

            body.Clear();
            title = null;
            if (isChapter)
            {
                chapter = line![3..].Trim();
            }
            else if (isSection)
            {
                title = line!.TrimStart('#').Trim();
            }
        }
    }

    private static DocEntry? CreateEntry(string title, string chapter, string source, List<string> body)
    {
        List<string> keywords = KalkSectionKeywords.TryGetValue(title, out string[]? knownKeywords) ? knownKeywords.ToList() : new List<string>();
        List<string> text = new List<string>();
        foreach (string line in body)
        {
            Match match = KeywordsComment.Match(line.Trim());
            if (match.Success)
            {
                keywords.AddRange(match.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                text.Add(line);
            }
        }

        string description = string.Join("\n", text).Trim();
        if (description.Length == 0)
        {
            return null;
        }

        string displayTitle = title.Replace("`", string.Empty);
        KalkDescriptor descriptor = new KalkDescriptor { Category = chapter, Description = description };
        descriptor.Names.Add(displayTitle);
        descriptor.Names.AddRange(keywords);
        return new DocEntry(displayTitle, displayTitle, $"Language: {chapter}", Summarize(text), null, true, false, descriptor, IsLanguage: true, Source: source);
    }

    /// <summary>First sentence of the prose (skipping code, tables and quotes), else the `> syntax` line.</summary>
    private static string Summarize(List<string> lines)
    {
        StringBuilder prose = new StringBuilder();
        string? syntaxLine = null;
        bool isInFence = false;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                isInFence = !isInFence;
                continue;
            }
            if (!isInFence && trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                syntaxLine ??= trimmed[2..];
            }
            if (isInFence || trimmed.Length == 0 || trimmed.StartsWith('|') || trimmed.StartsWith('>'))
            {
                if (prose.Length > 0 && !isInFence && trimmed.Length == 0)
                {
                    break;
                }
                continue;
            }
            prose.Append(prose.Length > 0 ? " " : string.Empty).Append(trimmed);
        }

        string summaryText = prose.Length > 0 ? prose.ToString() : syntaxLine ?? string.Empty;
        string summary = Regex.Replace(summaryText, @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("`", string.Empty).Replace("**", string.Empty).Trim();
        int sentenceEnd = summary.IndexOf(". ", StringComparison.Ordinal);
        return sentenceEnd > 0 ? summary[..(sentenceEnd + 1)] : summary;
    }
}
