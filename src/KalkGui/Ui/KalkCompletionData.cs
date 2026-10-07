using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using KalkGui.Engine;

namespace KalkGui.Ui;

internal sealed class KalkCompletionData : ICompletionData
{
    private readonly CompletionItem _item;
    private object? _content;

    public KalkCompletionData(CompletionItem item)
    {
        _item = item;
    }

    public ImageSource? Image => null;

    public string Text => _item.Name;

    // Built lazily: Ctrl+Space on an empty prefix lists ~1500 symbols
    public object Content => _content ??= CreateContent();

    public object Description => _item.Description ?? _item.Kind.ToString();

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }

    private object CreateContent()
    {
        StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = _item.Name });
        panel.Children.Add(new TextBlock
        {
            Text = KindLabel(_item.Kind),
            Foreground = KalkPalette.Prompt,
            Margin = new Thickness(10, 0, 0, 0),
        });
        return panel;
    }

    private static string KindLabel(CompletionKind kind)
    {
        return kind switch
        {
            CompletionKind.UserSymbol => "user",
            CompletionKind.Builtin => "builtin",
            CompletionKind.Module => "module",
            CompletionKind.Keyword => "keyword",
            CompletionKind.Unit => "unit",
            _ => string.Empty,
        };
    }
}
