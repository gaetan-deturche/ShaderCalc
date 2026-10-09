using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShaderCalc.App.Ui;

/// <summary>The markdown of the reference pages: paragraphs, `-` bullets, ``` fences, | tables |, `inline code`, **bold**.</summary>
internal static class MarkdownRenderer
{
    public static FlowDocument CreateDocument() =>
        // FlowDocument defaults to Georgia and justified text instead of inheriting the UI's look
        new FlowDocument { PagePadding = new Thickness(8, 8, 8, 8), FontSize = 13, FontFamily = SystemFonts.MessageFontFamily, TextAlignment = TextAlignment.Left };

    public static void AddMarkdown(FlowDocument document, string text, FontFamily monoFont)
    {
        List<string> paragraphLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }
            string paragraphText = string.Join(" ", paragraphLines);
            paragraphLines.Clear();
            bool isBullet = IsBullet(paragraphText);
            Paragraph paragraph = new Paragraph { Margin = new Thickness(isBullet ? 12 : 0, 0, 0, 6) };
            AppendInline(paragraph.Inlines, isBullet ? $"• {paragraphText[2..]}" : paragraphText, monoFont);
            document.Blocks.Add(paragraph);
        }

        List<string> lines = text.Replace("\r", string.Empty).Split('\n').ToList();
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string trimmedLine = lines[lineIndex].Trim();
            if (trimmedLine.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                List<string> codeLines = new List<string>();
                for (lineIndex++; lineIndex < lines.Count && !lines[lineIndex].Trim().StartsWith("```", StringComparison.Ordinal); lineIndex++)
                {
                    codeLines.Add(lines[lineIndex]);
                }
                document.Blocks.Add(CreateCodeBlock(string.Join("\n", codeLines), monoFont));
            }
            else if (trimmedLine.Length == 0)
            {
                FlushParagraph();
            }
            else if (trimmedLine.StartsWith('|'))
            {
                FlushParagraph();
                List<string> tableRows = new List<string>();
                for (; lineIndex < lines.Count && lines[lineIndex].Trim().StartsWith('|'); lineIndex++)
                {
                    tableRows.Add(lines[lineIndex]);
                }
                lineIndex--;
                document.Blocks.Add(CreateTable(tableRows, monoFont));
            }
            else
            {
                if (IsBullet(trimmedLine))
                {
                    FlushParagraph();
                }
                paragraphLines.Add(trimmedLine);
            }
        }
        FlushParagraph();
    }

    public static Paragraph CreateCodeBlock(string code, FontFamily monoFont) => new Paragraph(new Run(code.TrimEnd()))
    {
        FontFamily = monoFont,
        FontSize = 12.5,
        Background = Palette.Current.CodeBackground,
        Padding = new Thickness(8, 6, 8, 6),
        Margin = new Thickness(0, 2, 0, 8),
    };

    public static Paragraph CreateHeading(string title) =>
        new Paragraph(new Run(title)) { FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 10, 0, 4) };

    private static bool IsBullet(string text) => text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);

    public static Table CreateTable(List<string> rows, FontFamily monoFont)
    {
        List<List<string>> cells = rows.Where(row => !row.Trim().All(character => character is '|' or '-' or ':' or ' ')).Select(SplitTableRow).ToList();
        int columnCount = cells.Max(row => row.Count);
        Table table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        for (int column = 0; column < columnCount; column++)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(column == 0 ? 1 : 3, GridUnitType.Star) });
        }
        TableRowGroup rowGroup = new TableRowGroup();
        for (int rowIndex = 0; rowIndex < cells.Count; rowIndex++)
        {
            TableRow row = new TableRow();
            for (int column = 0; column < columnCount; column++)
            {
                Paragraph content = new Paragraph { Margin = new Thickness(0), FontWeight = rowIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal };
                if (column < cells[rowIndex].Count)
                {
                    AppendInline(content.Inlines, cells[rowIndex][column], monoFont);
                }
                row.Cells.Add(new TableCell(content)
                {
                    Padding = new Thickness(4, 3, 8, 3),
                    BorderBrush = Palette.Current.Dim,
                    BorderThickness = new Thickness(0, 0, 0, rowIndex == 0 ? 1 : 0),
                });
            }
            rowGroup.Rows.Add(row);
        }
        table.RowGroups.Add(rowGroup);
        return table;
    }

    /// <summary>Cells of a `| a | b |` row; a `|` inside backticks stays in its cell.</summary>
    private static List<string> SplitTableRow(string row)
    {
        string trimmed = row.Trim();
        trimmed = trimmed.StartsWith('|') ? trimmed[1..] : trimmed;
        trimmed = trimmed.EndsWith('|') ? trimmed[..^1] : trimmed;
        List<string> cells = new List<string>();
        StringBuilder cell = new StringBuilder();
        bool isInCode = false;
        foreach (char character in trimmed)
        {
            if (character == '`')
            {
                isInCode = !isInCode;
            }
            if (character == '|' && !isInCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    public static void AppendInline(InlineCollection inlines, string text, FontFamily monoFont)
    {
        string[] parts = text.Split('`');
        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            if (parts[partIndex].Length == 0)
            {
                continue;
            }
            // Odd parts sit between backticks
            if (partIndex % 2 == 1)
            {
                inlines.Add(new Run(parts[partIndex]) { FontFamily = monoFont, Foreground = Palette.Current.Result });
                continue;
            }
            string[] boldParts = parts[partIndex].Split("**");
            for (int boldIndex = 0; boldIndex < boldParts.Length; boldIndex++)
            {
                if (boldParts[boldIndex].Length > 0)
                {
                    inlines.Add(new Run(boldParts[boldIndex]) { FontWeight = boldIndex % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
                }
            }
        }
    }
}
