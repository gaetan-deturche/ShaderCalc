using Kalk.Core;

namespace KalkGui.Engine;

/// <summary>Ranks doc entries for a query: names first, then titles, summaries and finally the full text.</summary>
public static class DocSearch
{
    /// <summary>Lower is better; null = no match. Every whitespace-separated term must match somewhere.</summary>
    public static int? Score(DocEntry entry, string query)
    {
        int worstTermScore = 0;
        foreach (string term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int? termScore = ScoreTerm(entry, term);
            if (termScore == null)
            {
                return null;
            }
            worstTermScore = Math.Max(worstTermScore, termScore.Value);
        }
        return worstTermScore;
    }

    private static int? ScoreTerm(DocEntry entry, string term)
    {
        List<string> names = entry.Descriptor.Names;
        if (names.Any(name => name.Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }
        if (names.Any(name => name.StartsWith(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }
        if (names.Any(name => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }
        if (entry.Signature.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (entry.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }
        if (entry.Group.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }
        return ContainsInFullText(entry.Descriptor, term) ? 6 : null;
    }

    private static bool ContainsInFullText(KalkDescriptor descriptor, string term)
    {
        return Contains(descriptor.Description, term)
            || Contains(descriptor.Remarks, term)
            || Contains(descriptor.Returns, term)
            || Contains(descriptor.Example, term)
            || descriptor.Params.Any(parameter => Contains(parameter.Name, term) || Contains(parameter.Description, term));
    }

    private static bool Contains(string? text, string term)
    {
        return text != null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
