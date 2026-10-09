using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ShaderCalc.Reference;

namespace ShaderCalc.App.Ui;

/// <summary>A name the editor can complete: what it is (function, type...) and a description for the tooltip.</summary>
internal sealed record CompletionEntry(string Text, string Kind, string Description);

/// <summary>
/// One worksheet tab: the code editor and, on its right, the result column. Problems are underlined (hover for
/// the message); the line under the caret, or a clicked result, is the selected line.
/// </summary>
internal sealed class WorksheetView : Grid
{
    private readonly SquiggleRenderer _squiggles = new SquiggleRenderer();
    private readonly ToolTip _problemTip = new ToolTip { Placement = PlacementMode.Mouse };
    private readonly Func<IEnumerable<CompletionEntry>> _completions;
    private readonly ColumnDefinition _resultColumnDefinition;
    private IReadOnlyList<WorksheetLine> _lines = Array.Empty<WorksheetLine>();
    private IReadOnlyList<Diagnostic> _diagnostics = Array.Empty<Diagnostic>();
    private readonly Dictionary<WorksheetLine, ReferenceOutcome> _references = new Dictionary<WorksheetLine, ReferenceOutcome>();
    private CompletionWindow? _completionWindow;
    private bool _isReplacingText;

    public WorksheetView(string documentName, string text, IHighlightingDefinition highlighting, Func<IEnumerable<CompletionEntry>> completions, double resultWidth)
    {
        DocumentName = documentName;
        _completions = completions;
        Palette palette = Palette.Current;
        FontFamily monoFont = (FontFamily)Application.Current.Resources["MonoFont"];

        Editor = new TextEditor
        {
            FontFamily = monoFont,
            FontSize = 14,
            ShowLineNumbers = true,
            SyntaxHighlighting = highlighting,
            Background = palette.EditorBackground,
            Foreground = palette.EditorForeground,
            LineNumbersForeground = palette.LineNumbers,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Padding = new Thickness(4, 2, 0, 0),
        };
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Document.Text = text;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        AutomationProperties.SetAutomationId(Editor, "Editor");
        AutomationProperties.SetName(Editor, documentName);

        Results = new ResultColumn(Editor.TextArea.TextView, monoFont, 14, delta => Editor.ScrollToVerticalOffset(Editor.VerticalOffset + delta));
        AutomationProperties.SetAutomationId(Results, "Results");

        _resultColumnDefinition = new ColumnDefinition { Width = new GridLength(resultWidth), MinWidth = 80 };
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(_resultColumnDefinition);
        GridSplitter splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = palette.ResultBackground };
        SetColumn(Editor, 0);
        SetColumn(splitter, 1);
        SetColumn(Results, 2);
        Children.Add(Editor);
        Children.Add(splitter);
        Children.Add(Results);

        Editor.TextChanged += (_, _) =>
        {
            if (!_isReplacingText)
            {
                TextEdited?.Invoke();
            }
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => SelectLine(Editor.TextArea.Caret.Line);
        Results.LineClicked += line =>
        {
            SelectLine(line);
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = Editor.Document.GetLineByNumber(line).Length + 1;
            Editor.Focus();
        };
        Editor.MouseHover += OnMouseHover;
        Editor.MouseHoverStopped += (_, _) => _problemTip.IsOpen = false;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
    }

    public string DocumentName { get; set; }

    public TextEditor Editor { get; }

    public ResultColumn Results { get; }

    public string Text => Editor.Document.Text;

    public double ResultColumnWidth => _resultColumnDefinition.ActualWidth > 0 ? _resultColumnDefinition.ActualWidth : _resultColumnDefinition.Width.Value;

    /// <summary>The worksheet line under the caret (or clicked), if it has a result.</summary>
    public WorksheetLine? SelectedLine { get; private set; }

    public event Action? TextEdited;

    public event Action<WorksheetView>? SelectionChanged;

    public ReferenceOutcome? ReferenceFor(WorksheetLine line) => _references.TryGetValue(line, out ReferenceOutcome? outcome) ? outcome : null;

    /// <summary>Text of a line's statement, for the inspector.</summary>
    public string SourceOf(WorksheetLine line)
    {
        int start = Math.Clamp(line.Span.Offset, 0, Editor.Document.TextLength);
        int length = Math.Clamp(line.Span.Length, 0, Editor.Document.TextLength - start);
        return Editor.Document.GetText(start, length);
    }

    /// <summary>Replaces the text after an outside edit, keeping the caret where it was.</summary>
    public void ReplaceText(string text)
    {
        if (text == Editor.Document.Text)
        {
            return;
        }
        int caret = Editor.CaretOffset;
        _isReplacingText = true;
        Editor.Document.Text = text;
        _isReplacingText = false;
        Editor.CaretOffset = Math.Min(caret, text.Length);
    }

    public void ApplyEvaluation(IEnumerable<WorksheetLine> lines, IEnumerable<Diagnostic> diagnostics)
    {
        Palette palette = Palette.Current;
        _lines = lines.ToList();
        _diagnostics = diagnostics.ToList();
        _references.Clear();

        Dictionary<int, ResultCell> cells = new Dictionary<int, ResultCell>();
        foreach (WorksheetLine line in _lines)
        {
            Diagnostic? problem = line.Result.Diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info)
                .OrderByDescending(diagnostic => diagnostic.Severity).FirstOrDefault();
            string details = string.Join("\n", line.Result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
            if (line.Value != null)
            {
                cells[line.Line] = new ResultCell
                {
                    Text = line.Value.ToString(),
                    Foreground = palette.Result,
                    Line = line,
                    Problem = problem?.Severity,
                    Details = details.Length > 0 ? details : null,
                };
            }
            else if (problem != null)
            {
                cells[line.Line] = new ResultCell
                {
                    Text = problem.Message,
                    Foreground = problem.IsError ? palette.Error : palette.Warning,
                    Line = line,
                    Problem = problem.Severity,
                    Details = details,
                };
            }
        }
        // Problems outside any line (in a function, a struct...) still get a cell on their line
        foreach (Diagnostic diagnostic in _diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info && diagnostic.Span.Line > 0))
        {
            if (!cells.ContainsKey(diagnostic.Span.Line))
            {
                cells[diagnostic.Span.Line] = new ResultCell
                {
                    Text = diagnostic.Message,
                    Foreground = diagnostic.IsError ? palette.Error : palette.Warning,
                    Problem = diagnostic.Severity,
                    Details = diagnostic.ToString(),
                };
            }
        }
        Results.SetCells(cells);

        _squiggles.Marks = _diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info)
            .Select(diagnostic => (diagnostic.Span.Offset, Math.Max(diagnostic.Span.Length, 1), diagnostic.IsError ? palette.Error : palette.Warning))
            .ToList();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        SelectLine(Editor.TextArea.Caret.Line, force: true);
    }

    public void ApplyReference(WorksheetLine line, ReferenceOutcome outcome)
    {
        if (!_lines.Contains(line))
        {
            return;
        }
        _references[line] = outcome;
        Results.SetReference(line.Line, outcome);
        if (SelectedLine == line)
        {
            SelectionChanged?.Invoke(this);
        }
    }

    private void SelectLine(int lineNumber, bool force = false)
    {
        WorksheetLine? line = _lines.FirstOrDefault(candidate => candidate.Line == lineNumber)
            ?? _lines.FirstOrDefault(candidate => candidate.Span.Line <= lineNumber && lineNumber <= candidate.Line);
        Results.SelectedLine = line?.Line ?? 0;
        if (force || line != SelectedLine)
        {
            SelectedLine = line;
            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>The identifier under the caret (for F1).</summary>
    public string? WordAtCaret()
    {
        TextDocument document = Editor.Document;
        int offset = Editor.CaretOffset;
        int start = offset;
        while (start > 0 && IsWordCharacter(document.GetCharAt(start - 1)))
        {
            start--;
        }
        int end = offset;
        while (end < document.TextLength && IsWordCharacter(document.GetCharAt(end)))
        {
            end++;
        }
        return end > start ? document.GetText(start, end - start) : null;
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        TextViewPosition? position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position == null)
        {
            return;
        }
        int offset = Editor.Document.GetOffset(position.Value.Location);
        List<string> messages = _diagnostics
            .Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info && diagnostic.Span.Offset <= offset
                && offset <= diagnostic.Span.Offset + Math.Max(diagnostic.Span.Length, 1))
            .Select(diagnostic => diagnostic.Function == null ? diagnostic.Message : $"{diagnostic.Message} (in {diagnostic.Function})")
            .Distinct()
            .ToList();
        if (messages.Count == 0)
        {
            return;
        }
        _problemTip.Content = string.Join("\n", messages);
        _problemTip.PlacementTarget = Editor;
        _problemTip.IsOpen = true;
        e.Handled = true;
    }

    // ---- Completion ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowCompletion(minimumLength: 0);
            e.Handled = true;
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_completionWindow == null && e.Text.Length == 1 && IsWordCharacter(e.Text[0]) && !char.IsDigit(e.Text[0]))
        {
            ShowCompletion(minimumLength: 2);
        }
    }

    private void ShowCompletion(int minimumLength)
    {
        TextDocument document = Editor.Document;
        int start = Editor.CaretOffset;
        while (start > 0 && IsWordCharacter(document.GetCharAt(start - 1)))
        {
            start--;
        }
        string prefix = document.GetText(start, Editor.CaretOffset - start);
        if (prefix.Length < minimumLength || (prefix.Length > 0 && char.IsDigit(prefix[0])))
        {
            return;
        }
        List<CompletionEntry> entries = _completions()
            .Where(entry => entry.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Text).Select(group => group.First())
            .OrderBy(entry => entry.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0 || (entries.Count == 1 && entries[0].Text == prefix))
        {
            return;
        }
        _completionWindow = new CompletionWindow(Editor.TextArea) { StartOffset = start, MinWidth = 320 };
        foreach (CompletionEntry entry in entries)
        {
            _completionWindow.CompletionList.CompletionData.Add(new CompletionData(entry));
        }
        _completionWindow.CompletionList.SelectItem(prefix);
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    private sealed class CompletionData : ICompletionData
    {
        private readonly CompletionEntry _entry;

        public CompletionData(CompletionEntry entry)
        {
            _entry = entry;
        }

        public ImageSource? Image => null;

        public string Text => _entry.Text;

        public object Content => new TextBlock
        {
            Inlines =
            {
                new System.Windows.Documents.Run(_entry.Text),
                new System.Windows.Documents.Run("  " + _entry.Kind) { Foreground = Palette.Current.Dim, FontSize = 11 },
            },
        };

        public object Description => _entry.Description;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }

    /// <summary>Wavy underlines under problem spans.</summary>
    private sealed class SquiggleRenderer : IBackgroundRenderer
    {
        public List<(int Start, int Length, Brush Brush)> Marks { get; set; } = new List<(int Start, int Length, Brush Brush)>();

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            int documentLength = textView.Document?.TextLength ?? 0;
            foreach ((int start, int length, Brush brush) in Marks)
            {
                int clampedStart = Math.Clamp(start, 0, documentLength);
                int clampedLength = Math.Clamp(length, 0, documentLength - clampedStart);
                if (clampedLength == 0 && clampedStart > 0)
                {
                    clampedStart--;
                    clampedLength = 1;
                }
                TextSegment segment = new TextSegment { StartOffset = clampedStart, Length = clampedLength };
                Pen pen = new Pen(brush, 1.2);
                pen.Freeze();
                foreach (Rect rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    StreamGeometry geometry = new StreamGeometry();
                    using (StreamGeometryContext context = geometry.Open())
                    {
                        double y = rectangle.Bottom - 1;
                        context.BeginFigure(new Point(rectangle.Left, y), false, false);
                        bool isUp = true;
                        for (double x = rectangle.Left + 2; x <= rectangle.Right + 2; x += 2)
                        {
                            context.LineTo(new Point(x, isUp ? y - 2 : y), true, false);
                            isUp = !isUp;
                        }
                    }
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, pen, geometry);
                }
            }
        }
    }
}
