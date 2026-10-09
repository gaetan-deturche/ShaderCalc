using System.Reflection;
using System.Text.RegularExpressions;

namespace ShaderCalc.Docs;

/// <summary>One documentation page: an intrinsic or a topic (worksheet, units...). <see cref="Markdown"/> is its body.</summary>
public sealed record DocEntry(string Name, string Group, string? Signature, string Markdown)
{
    public bool IsTopic => Signature == null;

    /// <summary>First sentence of the body, for lists.</summary>
    public string Summary
    {
        get
        {
            string firstLine = Markdown.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('`')) ?? string.Empty;
            int sentenceEnd = firstLine.IndexOf(". ", StringComparison.Ordinal);
            return (sentenceEnd > 0 ? firstLine[..(sentenceEnd + 1)] : firstLine).Replace("`", string.Empty).Replace("**", string.Empty);
        }
    }
}

/// <summary>The embedded reference (Docs/reference.md): `&lt;!-- group: X --&gt;` starts a group, `## Name` a page.</summary>
public static class Documentation
{
    private static readonly Regex GroupMarker = new Regex(@"^<!--\s*group:\s*(.+?)\s*-->$");

    public static IReadOnlyList<DocEntry> Entries { get; } = Load();

    public static DocEntry? Find(string name) =>
        Entries.FirstOrDefault(entry => entry.Name == name) ?? Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pages matching every term, best first: exact name, name prefix, name contains, signature, body.
    /// </summary>
    public static IReadOnlyList<DocEntry> Search(string query)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return Entries;
        }
        return Entries
            .Select(entry => (Entry: entry, Score: terms.Max(term => Score(entry, term)), Matches: terms.All(term => Score(entry, term) < int.MaxValue)))
            .Where(candidate => candidate.Matches)
            .OrderBy(candidate => candidate.Score)
            // Name matches: functions first; text matches: topic pages explain more
            .ThenBy(candidate => candidate.Score >= 3 ? !candidate.Entry.IsTopic : candidate.Entry.IsTopic)
            .Select(candidate => candidate.Entry)
            .ToList();
    }

    private static int Score(DocEntry entry, string term)
    {
        if (string.Equals(entry.Name, term, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (entry.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (entry.Signature?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        {
            return 3;
        }
        return entry.Markdown.Contains(term, StringComparison.OrdinalIgnoreCase) ? 4 : int.MaxValue;
    }

    private static IReadOnlyList<DocEntry> Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShaderCalc.Docs.reference.md")
            ?? throw new InvalidOperationException("Docs/reference.md is not embedded");
        using StreamReader reader = new StreamReader(stream);
        string[] lines = reader.ReadToEnd().Replace("\r", string.Empty).Split('\n');

        List<DocEntry> entries = new List<DocEntry>();
        string group = string.Empty;
        string? name = null;
        List<string> body = new List<string>();

        void Flush()
        {
            if (name == null)
            {
                return;
            }
            // A page whose first line is `code` is an intrinsic: that line is its signature
            string? signatureLine = body.FirstOrDefault(line => line.Trim().Length > 0);
            string? signature = signatureLine != null && signatureLine.StartsWith('`') ? signatureLine.Trim() : null;
            entries.Add(new DocEntry(name, group, signature, string.Join("\n", body).Trim()));
            name = null;
            body.Clear();
        }

        foreach (string line in lines)
        {
            Match marker = GroupMarker.Match(line.Trim());
            if (marker.Success)
            {
                Flush();
                group = marker.Groups[1].Value;
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                name = line[3..].Trim();
            }
            else if (name != null)
            {
                body.Add(line);
            }
        }
        Flush();
        return entries;
    }
}
