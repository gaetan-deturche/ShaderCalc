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
        KalkDescriptor descriptor = entry.Descriptor;
        // FlowDocument defaults to Georgia and justified text instead of inheriting the UI's look
        FlowDocument document = new FlowDocument { PagePadding = new Thickness(6, 8, 6, 8), FontSize = 13, FontFamily = SystemFonts.MessageFontFamily, TextAlignment = TextAlignment.Left };

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
        FlowDocument document = new FlowDocument { PagePadding = new Thickness(6, 8, 6, 8), FontSize = 13, FontFamily = SystemFonts.MessageFontFamily };
        document.Blocks.Add(new Paragraph(new Run("Select an entry to see its documentation, or press F1 on a word in the input.")) { FontStyle = FontStyles.Italic });
        return document;
    }

    /// <summary>Doc-comment text: paragraphs, "- " bullets, ``` fences and `inline code`.</summary>
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
            bool isBullet = paragraphText.StartsWith("- ", StringComparison.Ordinal);
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
            else
            {
                if (trimmedLine.StartsWith("- ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                }
                paragraphLines.Add(trimmedLine);
            }
        }
        FlushParagraph();
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
        string[] parts = text.Split('`');
        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            if (parts[partIndex].Length == 0)
            {
                continue;
            }

            // Odd parts sit between backticks
            inlines.Add(partIndex % 2 == 1
                ? new Run(parts[partIndex]) { FontFamily = monoFont, Foreground = KalkPalette.Code }
                : new Run(parts[partIndex]));
        }
    }

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
