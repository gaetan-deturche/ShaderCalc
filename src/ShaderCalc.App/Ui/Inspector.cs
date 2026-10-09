using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ShaderCalc.Reference;
using ShaderCalc.Units;

namespace ShaderCalc.App.Ui;

/// <summary>
/// The selected line in detail: its code, value, type, units, every component's bits (sign / exponent / mantissa
/// for floats), its problems, and the reference check with the HLSL that ran.
/// </summary>
internal static class Inspector
{
    public static FlowDocument BuildEmpty()
    {
        FlowDocument document = MarkdownRenderer.CreateDocument();
        document.Blocks.Add(new Paragraph(new Run("Put the caret on a line with a result, or click a result, to see its bits, units and the reference check."))
        {
            Foreground = Palette.Current.Dim,
        });
        return document;
    }

    public static FlowDocument Build(WorksheetLine line, string source, ReferenceOutcome? reference, FontFamily monoFont)
    {
        Palette palette = Palette.Current;
        FlowDocument document = MarkdownRenderer.CreateDocument();
        document.Blocks.Add(MarkdownRenderer.CreateCodeBlock(source.Trim(), monoFont));

        Value? value = line.Value;
        if (value != null)
        {
            document.Blocks.Add(new Paragraph(new Run(Value.Format(value, includeUnits: true)) { FontFamily = monoFont, Foreground = palette.Result })
            {
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 4),
            });
            Paragraph typeLine = new Paragraph { Margin = new Thickness(0, 0, 0, 8), Foreground = palette.Dim };
            typeLine.Inlines.Add(new Run(value.Type.ToString()) { FontFamily = monoFont });
            string units = DescribeUnits(value);
            if (units.Length > 0)
            {
                typeLine.Inlines.Add(new Run("   " + units));
            }
            document.Blocks.Add(typeLine);
            document.Blocks.Add(BuildComponents(value, reference, monoFont));
        }

        if (line.Result.Diagnostics.Count > 0)
        {
            document.Blocks.Add(MarkdownRenderer.CreateHeading("Problems"));
            foreach (Diagnostic diagnostic in line.Result.Diagnostics)
            {
                Brush brush = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => palette.Error,
                    DiagnosticSeverity.Warning => palette.Warning,
                    _ => palette.Dim,
                };
                string where = diagnostic.Function == null ? $"{diagnostic.Source}:{diagnostic.Span.Line}" : $"{diagnostic.Source}:{diagnostic.Span.Line} in {diagnostic.Function}";
                Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                paragraph.Inlines.Add(new Run(diagnostic.Message) { Foreground = brush });
                paragraph.Inlines.Add(new Run($"  ({where})") { Foreground = palette.Dim, FontSize = 11.5 });
                document.Blocks.Add(paragraph);
            }
        }

        if (value != null)
        {
            document.Blocks.Add(MarkdownRenderer.CreateHeading("Reference (DXC + WARP)"));
            document.Blocks.Add(BuildReference(value, reference, monoFont));
        }
        return document;
    }

    private static string DescribeUnits(Value value)
    {
        List<Dimension> dimensions = value.Units.Select(unit => unit.Dimension).Distinct().ToList();
        if (dimensions.Count == 1)
        {
            return dimensions[0].IsNone ? string.Empty : Value.DescribeDimension(dimensions[0]);
        }
        return "units per component";
    }

    private static Block BuildComponents(Value value, ReferenceOutcome? reference, FontFamily monoFont)
    {
        Palette palette = Palette.Current;
        Table table = new Table { CellSpacing = 0, FontFamily = monoFont, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        foreach (double width in new[] { 1.0, 2.2, 2.0 })
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(width, GridUnitType.Star) });
        }
        TableRowGroup rows = new TableRowGroup();
        List<string> names = ComponentNames(value.Type, string.Empty).ToList();
        Value? referenceValue = reference?.Verdict is ReferenceVerdict.Mismatch or ReferenceVerdict.WithinTolerance ? reference.ReferenceValue : null;
        for (int index = 0; index < value.Bits.Length; index++)
        {
            ScalarKind kind = value.KindAt(index);
            string text = Value.FormatComponent(kind, value.Bits[index]);
            if (!value.Units[index].Dimension.IsNone)
            {
                text += " " + value.Units[index].Dimension;
            }
            bool differs = referenceValue != null && referenceValue.Bits[index] != value.Bits[index];
            AddComponent(rows, names[index], text, kind, value.Bits[index], differs ? palette.Error : palette.EditorForeground);
            if (differs)
            {
                AddComponent(rows, "WARP", Value.FormatComponent(kind, referenceValue!.Bits[index]), kind, referenceValue.Bits[index], palette.Dim);
            }
        }
        table.RowGroups.Add(rows);
        return table;
    }

    /// <summary>name · value · hex, and the bit pattern on its own line underneath.</summary>
    private static void AddComponent(TableRowGroup rows, string name, string text, ScalarKind kind, ulong bits, Brush brush)
    {
        TableRow row = new TableRow();
        foreach (string cell in new[] { name, text, Hex(kind, bits) })
        {
            row.Cells.Add(new TableCell(new Paragraph(new Run(cell)) { Margin = new Thickness(0) }) { Padding = new Thickness(2, 3, 8, 0), Foreground = brush });
        }
        rows.Rows.Add(row);
        TableRow bitsRow = new TableRow();
        bitsRow.Cells.Add(new TableCell(new Paragraph(new Run(Bits(kind, bits))) { Margin = new Thickness(0) })
        {
            ColumnSpan = 3,
            Padding = new Thickness(12, 0, 8, 3),
            Foreground = Palette.Current.Dim,
            FontSize = 11.5,
        });
        rows.Rows.Add(bitsRow);
    }

    /// <summary>x/y/z/w, [row][column], [index], .field names for every flattened component.</summary>
    private static IEnumerable<string> ComponentNames(ShaderType type, string prefix)
    {
        switch (type)
        {
            case NumericType { IsScalar: true }:
                yield return prefix.Length == 0 ? "value" : prefix;
                break;
            case NumericType { IsVector: true } vector:
                for (int index = 0; index < vector.Size; index++)
                {
                    yield return prefix + (vector.Size <= 4 ? "." + "xyzw"[index] : $"[{index}]");
                }
                break;
            case NumericType matrix:
                for (int row = 0; row < matrix.Rows; row++)
                {
                    for (int column = 0; column < matrix.Columns; column++)
                    {
                        yield return $"{prefix}._m{row}{column}";
                    }
                }
                break;
            case ArrayType array:
                for (int index = 0; index < array.Length; index++)
                {
                    foreach (string name in ComponentNames(array.Element, $"{prefix}[{index}]"))
                    {
                        yield return name;
                    }
                }
                break;
            case StructType structure:
                foreach (StructField field in structure.Fields)
                {
                    foreach (string name in ComponentNames(field.Type, $"{prefix}.{field.Name}"))
                    {
                        yield return name;
                    }
                }
                break;
        }
    }

    public static string Hex(ScalarKind kind, ulong bits) => kind switch
    {
        ScalarKind.Bool => bits != 0 ? "1" : "0",
        _ when kind.Is64Bit() => $"0x{bits:X16}",
        _ => $"0x{(uint)bits:X8}",
    };

    /// <summary>Floats as sign | exponent | mantissa with the decoded exponent; integers in groups of 8 bits.</summary>
    public static string Bits(ScalarKind kind, ulong bits)
    {
        if (kind == ScalarKind.Float)
        {
            uint word = (uint)bits;
            uint exponent = (word >> 23) & 0xFF;
            string mantissa = Convert.ToString(word & 0x7FFFFF, 2).PadLeft(23, '0');
            string meaning = exponent switch
            {
                0 => (word & 0x7FFFFF) == 0 ? "zero" : "denormal",
                255 => (word & 0x7FFFFF) == 0 ? "infinity" : "NaN",
                _ => $"2^{(int)exponent - 127}",
            };
            return $"{word >> 31} {Convert.ToString(exponent, 2).PadLeft(8, '0')} {mantissa}  ({meaning})";
        }
        if (kind == ScalarKind.Double)
        {
            ulong exponent = (bits >> 52) & 0x7FF;
            string mantissa = Convert.ToString((long)(bits & 0xFFFFFFFFFFFFF), 2).PadLeft(52, '0');
            return $"{bits >> 63} {Convert.ToString((long)exponent, 2).PadLeft(11, '0')} {mantissa}";
        }
        if (kind == ScalarKind.Bool)
        {
            return bits != 0 ? "true" : "false";
        }
        int width = kind.Is64Bit() ? 64 : 32;
        string binary = Convert.ToString((long)(width == 64 ? bits : (uint)bits), 2).PadLeft(width, '0');
        StringBuilder grouped = new StringBuilder();
        for (int index = 0; index < binary.Length; index++)
        {
            if (index > 0 && index % 8 == 0)
            {
                grouped.Append(' ');
            }
            grouped.Append(binary[index]);
        }
        return grouped.ToString();
    }

    private static Block BuildReference(Value value, ReferenceOutcome? reference, FontFamily monoFont)
    {
        Palette palette = Palette.Current;
        Section section = new Section();
        if (reference == null)
        {
            section.Blocks.Add(new Paragraph(new Run("Running...") { Foreground = palette.Dim }));
            return section;
        }

        (string text, Brush brush) = reference.Verdict switch
        {
            ReferenceVerdict.Match => ("✓ Bit-identical to WARP.", palette.Match),
            ReferenceVerdict.WithinTolerance => ($"≈ Within {reference.MaxUlps} ulp: approximate functions (sin, exp2, log2...) differ between GPUs.",
                palette.Approximate),
            ReferenceVerdict.Mismatch => reference.UsesApproximations
                ? ($"≠ WARP gives {reference.ReferenceValue}. The line uses approximate functions, which GPUs implement differently.", palette.Error)
                : ($"≠ WARP gives {reference.ReferenceValue}.", palette.Error),
            _ => ($"Not checked: {reference.Message}", palette.Dim),
        };
        section.Blocks.Add(new Paragraph(new Run(text) { Foreground = brush }) { Margin = new Thickness(0, 0, 0, 4) });
        if (reference.Timings is ComputeTimings timings)
        {
            section.Blocks.Add(new Paragraph(new Run(
                $"DXC {timings.CompileMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms, " +
                $"WARP pipeline {timings.PipelineMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms, " +
                $"run {timings.RunMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms") { Foreground = palette.Dim, FontSize = 11.5 })
            {
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        if (reference.Hlsl.Length > 0)
        {
            TextBox code = new TextBox
            {
                Text = reference.Hlsl,
                IsReadOnly = true,
                FontFamily = monoFont,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 420,
            };
            Expander expander = new Expander { Header = "The HLSL that ran", Content = code };
            section.Blocks.Add(new BlockUIContainer(expander));
        }
        return section;
    }
}
