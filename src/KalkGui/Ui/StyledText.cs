using System.Windows;
using System.Windows.Documents;
using KalkGui.Engine;

namespace KalkGui.Ui;

/// <summary>Builds FlowDocument content from text + kalk style spans.</summary>
internal static class StyledText
{
    public static Paragraph CreateParagraph(string text, IReadOnlyList<StyledSpan> spans, string? prompt = null)
    {
        Paragraph paragraph = new Paragraph { Margin = new Thickness(0) };
        if (prompt != null)
        {
            paragraph.Inlines.Add(new Run(prompt) { Foreground = KalkPalette.Prompt });
        }
        AppendTo(paragraph.Inlines, text, spans);
        return paragraph;
    }

    /// <summary>Appends <paramref name="text"/>, colouring each span; spans must be ordered and non-overlapping.</summary>
    public static void AppendTo(InlineCollection inlines, string text, IReadOnlyList<StyledSpan> spans)
    {
        int position = 0;
        foreach (StyledSpan span in spans)
        {
            int start = Math.Min(span.Start, text.Length);
            int end = Math.Min(span.End, text.Length);
            if (start > position)
            {
                AppendRuns(inlines, text[position..start], null);
            }
            if (end > start)
            {
                AppendRuns(inlines, text[start..end], span.Style);
            }
            position = Math.Max(position, end);
        }
        if (position < text.Length)
        {
            AppendRuns(inlines, text[position..], null);
        }
    }

    private static void AppendRuns(InlineCollection inlines, string text, KalkTextStyle? style)
    {
        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                inlines.Add(new LineBreak());
            }
            if (lines[lineIndex].Length == 0)
            {
                continue;
            }

            Run run = new Run(lines[lineIndex]);
            if (style.HasValue)
            {
                KalkPalette.Apply(run, style.Value);
            }
            inlines.Add(run);
        }
    }
}
