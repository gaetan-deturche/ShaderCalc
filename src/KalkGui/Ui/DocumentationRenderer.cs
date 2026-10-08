using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Kalk.Core;
using KalkGui.Engine;

namespace KalkGui.Ui;

/// <summary>Renders a kalk descriptor (the data behind `help`) as a FlowDocument.</summary>
internal static class DocumentationRenderer
{
    public static FlowDocument Build(DocEntry entry, KalkSession session, FontFamily monoFont, Action<string> importModule)
    {
        if (entry.IsLanguage)
        {
            return BuildLanguageSection(entry, session, monoFont);
        }

        KalkDescriptor descriptor = entry.Descriptor;
        FlowDocument document = CreateDocument();

        Paragraph signature = StyledText.CreateParagraph(entry.Signature, session.Highlight(entry.Signature));
        signature.FontFamily = monoFont;
        signature.FontSize = 15;
        signature.Background = KalkPalette.Background;
        signature.Padding = new Thickness(8, 6, 8, 6);
        signature.Margin = new Thickness(0, 0, 0, 8);
        document.Blocks.Add(signature);

        if (descriptor.Names.Count > 1)
        {
            document.Blocks.Add(new Paragraph(new Run($"Aliases: {string.Join(", ", descriptor.Names.Skip(1))}")) { Margin = new Thickness(0, 0, 0, 6), FontStyle = FontStyles.Italic });
        }

        if (!entry.IsAvailable && entry.ModuleName != null)
        {
            string moduleName = entry.ModuleName;
            Hyperlink importLink = new Hyperlink(new Run($"import {moduleName}")) { FontFamily = monoFont };
            importLink.Click += (_, _) => importModule(moduleName);
            Paragraph requirement = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            requirement.Inlines.Add(new Run("Requires "));
            requirement.Inlines.Add(importLink);
            document.Blocks.Add(requirement);
        }

        AddMarkdown(document, string.IsNullOrWhiteSpace(descriptor.Description) ? "No documentation available." : descriptor.Description, session, monoFont);

        if (descriptor.Params.Count > 0)
        {
            AddHeading(document, "Parameters");
            List parameters = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(18, 0, 0, 0) };
            foreach (KalkParamDescriptor parameter in descriptor.Params)
            {
                Paragraph item = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                item.Inlines.Add(new Run(parameter.IsOptional ? $"{parameter.Name}?" : parameter.Name) { FontFamily = monoFont, Foreground = KalkPalette.Code });
                item.Inlines.Add(new Run(": "));
                AppendInlineCode(item.Inlines, CollapseWhitespace(parameter.Description), monoFont);
                parameters.ListItems.Add(new ListItem(item));
            }
            document.Blocks.Add(parameters);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Returns))
        {
            AddHeading(document, "Returns");
            AddMarkdown(document, descriptor.Returns, session, monoFont);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Remarks))
        {
            AddHeading(document, "Remarks");
            AddMarkdown(document, descriptor.Remarks, session, monoFont);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Example))
        {
            AddHeading(document, "Example");
            document.Blocks.Add(CreateCodeBlock(SplitLines(descriptor.Example), session, monoFont));
        }

        return document;
    }

    public static FlowDocument BuildHint()
    {
        FlowDocument document = CreateDocument();
        document.Blocks.Add(new Paragraph(new Run("Select an entry to see its documentation, or press F1 on a word in the input.")) { FontStyle = FontStyles.Italic });
        return document;
    }

    /// <summary>A section of kalk's language guides (or KalkGui's supplement): title, keywords, then its markdown.</summary>
    private static FlowDocument BuildLanguageSection(DocEntry entry, KalkSession session, FontFamily monoFont)
    {
        FlowDocument document = CreateDocument();
        document.Blocks.Add(new Paragraph(new Run(entry.Name)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        document.Blocks.Add(new Paragraph(new Run($"{entry.Group} · {entry.Source}") { Foreground = KalkPalette.Prompt }) { FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });

        List<string> keywords = entry.Descriptor.Names.Skip(1).ToList();
        if (keywords.Count > 0)
        {
            Paragraph keywordLine = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            keywordLine.Inlines.Add(new Run("Keywords: "));
            foreach (string keyword in keywords)
            {
                keywordLine.Inlines.Add(new Run(keyword) { FontFamily = monoFont, Foreground = KalkPalette.Code });
                keywordLine.Inlines.Add(new Run("  "));
            }
            document.Blocks.Add(keywordLine);
        }

        AddMarkdown(document, entry.Descriptor.Description ?? string.Empty, session, monoFont);
        return document;
    }

    private static FlowDocument CreateDocument()
    {
        // FlowDocument defaults to Georgia and justified text instead of inheriting the UI's look
        return new FlowDocument { PagePadding = new Thickness(6, 8, 6, 8), FontSize = 13, FontFamily = SystemFonts.MessageFontFamily, TextAlignment = TextAlignment.Left };
    }

    /// <summary>
    /// The markdown of doc comments and kalk's guides: paragraphs, `-`/`*` bullets, ``` fences, `| tables |`,
    /// `> syntax` lines, `inline code`, **bold** and [links](…) (text only).
    /// </summary>
    private static void AddMarkdown(FlowDocument document, string text, KalkSession session, FontFamily monoFont)
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
            AppendInlineCode(paragraph.Inlines, isBullet ? $"• {paragraphText[2..]}" : paragraphText, monoFont);
            document.Blocks.Add(paragraph);
        }

        List<string> lines = SplitLines(text);
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
                document.Blocks.Add(CreateCodeBlock(codeLines, session, monoFont));
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
            else if (trimmedLine.StartsWith("> ", StringComparison.Ordinal))
            {
                // kalk's guides state a statement's syntax this way
                FlushParagraph();
                Paragraph syntax = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(8, 2, 0, 2),
                    BorderBrush = KalkPalette.Prompt,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                };
                AppendInlineCode(syntax.Inlines, trimmedLine[2..], monoFont);
                document.Blocks.Add(syntax);
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

    private static bool IsBullet(string text)
    {
        return text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);
    }

    private static Table CreateTable(List<string> rows, FontFamily monoFont)
    {
        List<List<string>> cells = rows.Where(row => !IsTableSeparator(row)).Select(SplitTableRow).ToList();
        int columnCount = cells.Max(row => row.Count);
        Table table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        for (int column = 0; column < columnCount; column++)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(column == 0 ? 1 : 2, GridUnitType.Star) });
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
                    AppendInlineCode(content.Inlines, cells[rowIndex][column], monoFont);
                }
                row.Cells.Add(new TableCell(content)
                {
                    Padding = new Thickness(4, 3, 8, 3),
                    BorderBrush = KalkPalette.Prompt,
                    BorderThickness = new Thickness(0, 0, 0, rowIndex == 0 ? 1 : 0),
                });
            }
            rowGroup.Rows.Add(row);
        }
        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static bool IsTableSeparator(string row)
    {
        return row.Trim().All(character => character is '|' or '-' or ':' or ' ');
    }

    /// <summary>Cells of a `| a | b |` row; a `|` inside backticks stays in its cell (`<left> || <right>`).</summary>
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

    /// <summary>A console-like block: `>>> ` lines get the prompt, everything is kalk-highlighted.</summary>
    private static Paragraph CreateCodeBlock(List<string> lines, KalkSession session, FontFamily monoFont)
    {
        Paragraph block = new Paragraph
        {
            FontFamily = monoFont,
            Foreground = KalkPalette.Foreground,
            Background = KalkPalette.Background,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };

        List<string> dedentedLines = Dedent(lines);
        for (int lineIndex = 0; lineIndex < dedentedLines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                block.Inlines.Add(new LineBreak());
            }

            string line = dedentedLines[lineIndex];
            if (line.StartsWith(">>> ", StringComparison.Ordinal))
            {
                block.Inlines.Add(new Run(">>> ") { Foreground = KalkPalette.Prompt });
                line = line[4..];
            }
            StyledText.AppendTo(block.Inlines, line, session.Highlight(line));
        }
        return block;
    }

    private static void AddHeading(FlowDocument document, string title)
    {
        document.Blocks.Add(new Paragraph(new Run(title)) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
    }

    private static void AppendInlineCode(InlineCollection inlines, string text, FontFamily monoFont)
    {
        // Links point into kalk's website: keep their text only
        string[] parts = MarkdownLink.Replace(text, "$1").Split('`');
        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            if (parts[partIndex].Length == 0)
            {
                continue;
            }

            // Odd parts sit between backticks
            if (partIndex % 2 == 1)
            {
                inlines.Add(new Run(parts[partIndex]) { FontFamily = monoFont, Foreground = KalkPalette.Code });
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

    private static readonly Regex MarkdownLink = new Regex(@"\[([^\]]*)\]\([^)]*\)");

    private static List<string> SplitLines(string text)
    {
        return text.Replace("\r", string.Empty).Split('\n').ToList();
    }

    private static List<string> Dedent(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines = lines.Take(lines.Count - 1).ToList();
        }

        int indentation = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return lines.Select(line => line.Length >= indentation ? line[indentation..] : line.TrimStart()).ToList();
    }

    private static string CollapseWhitespace(string? text)
    {
        return text == null ? string.Empty : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
