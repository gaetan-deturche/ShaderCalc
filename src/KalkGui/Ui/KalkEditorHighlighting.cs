using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using KalkGui.Engine;

namespace KalkGui.Ui;

/// <summary>Colours an AvalonEdit editor with kalk's own highlighter, re-run on every edit.</summary>
internal sealed class KalkEditorHighlighting
{
    private readonly TextEditor _editor;
    private readonly Func<KalkSession?> _getSession;
    private readonly bool _matchBraces;
    private readonly SpanColorizer _colorizer = new SpanColorizer();

    public KalkEditorHighlighting(TextEditor editor, Func<KalkSession?> getSession, bool matchBraces)
    {
        _editor = editor;
        _getSession = getSession;
        _matchBraces = matchBraces;
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextChanged += (_, _) => Refresh();
        if (matchBraces)
        {
            editor.TextArea.Caret.PositionChanged += (_, _) => Refresh();
        }
    }

    public void Refresh()
    {
        KalkSession? session = _getSession();
        int caretIndex = _matchBraces ? _editor.CaretOffset : -1;
        _colorizer.Spans = session?.Highlight(_editor.Text, caretIndex) ?? Array.Empty<StyledSpan>();
        _editor.TextArea.TextView.Redraw();
    }

    private sealed class SpanColorizer : DocumentColorizingTransformer
    {
        public IReadOnlyList<StyledSpan> Spans { get; set; } = Array.Empty<StyledSpan>();

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (StyledSpan span in Spans)
            {
                // Clamp: spans may lag one edit behind the document
                int start = Math.Max(span.Start, line.Offset);
                int end = Math.Min(span.End, line.EndOffset);
                if (start < end)
                {
                    KalkTextStyle style = span.Style;
                    ChangeLinePart(start, end, element => KalkPalette.Apply(element, style));
                }
            }
        }
    }
}
