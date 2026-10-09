using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using ShaderCalc.Reference;

namespace ShaderCalc.App.Ui;

/// <summary>What one line shows on the right: its value (or problem) and the reference mark.</summary>
internal sealed class ResultCell
{
    public required string Text { get; init; }

    public required Brush Foreground { get; init; }

    public WorksheetLine? Line { get; init; }

    public DiagnosticSeverity? Problem { get; init; }

    public string? Details { get; init; }

    public ReferenceOutcome? Reference { get; set; }
}

/// <summary>
/// The result column next to a worksheet editor: one cell per line, drawn aligned with the editor's visual lines
/// (it follows its scrolling). Clicking a cell selects the line.
/// </summary>
internal sealed class ResultColumn : FrameworkElement
{
    private const double MarkWidth = 22;

    private readonly TextView _textView;
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly Action<double> _scroll;
    private Dictionary<int, ResultCell> _cells = new Dictionary<int, ResultCell>();
    private int _selectedLine;

    public ResultColumn(TextView textView, FontFamily fontFamily, double fontSize, Action<double> scroll)
    {
        _textView = textView;
        _typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = fontSize;
        _scroll = scroll;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        textView.VisualLinesChanged += (_, _) => InvalidateVisual();
        ToolTipService.SetInitialShowDelay(this, 300);
        ToolTipService.SetShowDuration(this, 60_000);
    }

    /// <summary>The 1-based document line of a cell that was clicked.</summary>
    public event Action<int>? LineClicked;

    public IReadOnlyDictionary<int, ResultCell> Cells => _cells;

    public int SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (_selectedLine != value)
            {
                _selectedLine = value;
                InvalidateVisual();
            }
        }
    }

    public void SetCells(Dictionary<int, ResultCell> cells)
    {
        _cells = cells;
        UpdateAutomationText();
        InvalidateVisual();
    }

    public void SetReference(int line, ReferenceOutcome outcome)
    {
        if (_cells.TryGetValue(line, out ResultCell? cell))
        {
            cell.Reference = outcome;
            UpdateAutomationText();
            InvalidateVisual();
        }
    }

    /// <summary>The mark in front of a cell: problems first, then the reference verdict.</summary>
    public static (string Mark, Brush Brush) MarkOf(ResultCell cell)
    {
        Palette palette = Palette.Current;
        if (cell.Problem == DiagnosticSeverity.Error)
        {
            return ("✗", palette.Error);
        }
        return cell.Reference?.Verdict switch
        {
            ReferenceVerdict.Mismatch => ("≠", palette.Error),
            _ when cell.Problem == DiagnosticSeverity.Warning => ("⚠", palette.Warning),
            ReferenceVerdict.Match => ("✓", palette.Match),
            ReferenceVerdict.WithinTolerance => ("≈", palette.Approximate),
            ReferenceVerdict.NotChecked => ("–", palette.Dim),
            _ => cell.Line?.Value != null ? ("…", palette.Dim) : (string.Empty, palette.Dim),
        };
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ResultColumnAutomationPeer(this);

    private sealed class ResultColumnAutomationPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public ResultColumnAutomationPeer(ResultColumn owner) : base(owner)
        {
        }

        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(ResultColumn);
    }

    /// <summary>The cells as text ("12 ✓ 9 J"), readable by UI Automation for tests.</summary>
    private void UpdateAutomationText() =>
        AutomationProperties.SetItemStatus(this, string.Join("\n", _cells.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {MarkOf(pair.Value).Mark} {pair.Value.Text}")));

    protected override void OnRender(DrawingContext drawingContext)
    {
        Palette palette = Palette.Current;
        drawingContext.DrawRectangle(palette.ResultBackground, null, new Rect(RenderSize));
        if (!_textView.VisualLinesValid)
        {
            return;
        }
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (VisualLine visualLine in _textView.VisualLines)
        {
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;
            double top = visualLine.VisualTop - _textView.VerticalOffset;
            double height = visualLine.Height;
            if (lineNumber == _selectedLine)
            {
                drawingContext.DrawRectangle(palette.SelectedLine, null, new Rect(0, top, ActualWidth, height));
            }
            if (!_cells.TryGetValue(lineNumber, out ResultCell? cell))
            {
                continue;
            }

            (string mark, Brush markBrush) = MarkOf(cell);
            FormattedText markText = new FormattedText(mark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, markBrush, pixelsPerDip);
            drawingContext.DrawText(markText, new Point(6, top + (height - markText.Height) / 2));

            FormattedText valueText = new FormattedText(cell.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, cell.Foreground,
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, ActualWidth - MarkWidth - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawingContext.DrawText(valueText, new Point(MarkWidth, top + (height - valueText.Height) / 2));
        }
    }

    private int LineAt(double y)
    {
        VisualLine? visualLine = _textView.VisualLinesValid ? _textView.GetVisualLineFromVisualTop(y + _textView.VerticalOffset) : null;
        return visualLine?.FirstDocumentLine.LineNumber ?? 0;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int line = LineAt(e.GetPosition(this).Y);
        if (line > 0)
        {
            LineClicked?.Invoke(line);
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _scroll(-e.Delta);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int line = LineAt(e.GetPosition(this).Y);
        ToolTip = _cells.TryGetValue(line, out ResultCell? cell) ? Describe(cell) : null;
    }

    private static string Describe(ResultCell cell)
    {
        List<string> parts = new List<string> { cell.Text };
        if (cell.Details != null)
        {
            parts.Add(cell.Details);
        }
        if (cell.Reference != null)
        {
            parts.Add(cell.Reference.ToString());
        }
        return string.Join("\n", parts);
    }
}
