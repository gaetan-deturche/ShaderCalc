using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Snippets;
using KalkGui.Engine;

namespace KalkGui.Ui;

/// <summary>
/// Editing help shared by the input and library editors: Tab / Ctrl+Space completion, Tab-expanded block
/// templates, Enter that opens and closes `func`/`if`/`for`/`while` blocks, F1 docs and a parse-only syntax
/// check while typing.
/// </summary>
internal sealed class KalkEditorAssist
{
    private const string Indent = "    ";
    private static readonly HashSet<string> BlockOpeners = new HashSet<string>(StringComparer.Ordinal) { "func", "if", "for", "while", "case" };
    private static readonly HashSet<string> BlockContinuations = new HashSet<string>(StringComparer.Ordinal) { "else", "when" };

    private readonly TextEditor _editor;
    private readonly KalkEditorHighlighting _highlighting;
    private readonly Func<KalkSession?> _getSession;
    private readonly Func<Task> _submit;
    private readonly Action<string> _showDocumentation;
    private readonly Action<SyntaxProblem?> _onSyntaxChecked;
    private readonly DispatcherTimer _syntaxCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
    private CompletionWindow? _completionWindow;

    public KalkEditorAssist(TextEditor editor, KalkEditorHighlighting highlighting, Func<KalkSession?> getSession, Func<Task> submit,
        Action<string> showDocumentation, Action<SyntaxProblem?> onSyntaxChecked)
    {
        _editor = editor;
        _highlighting = highlighting;
        _getSession = getSession;
        _submit = submit;
        _showDocumentation = showDocumentation;
        _onSyntaxChecked = onSyntaxChecked;

        editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        editor.TextChanged += (_, _) =>
        {
            _highlighting.ClearError();
            _syntaxCheckTimer.Stop();
            _syntaxCheckTimer.Start();
        };
        _syntaxCheckTimer.Tick += (_, _) =>
        {
            _syntaxCheckTimer.Stop();
            CheckSyntax();
        };
    }

    /// <summary>True while the completion window or a template handles the keys itself.</summary>
    public bool IsBusy => _completionWindow != null || !_editor.TextArea.StackedInputHandlers.IsEmpty;

    /// <summary>Drops the pending check and underline after a programmatic text change.</summary>
    public void ResetSyntaxCheck()
    {
        _syntaxCheckTimer.Stop();
        _highlighting.ClearError();
    }

    public void InsertTemplate(Snippet snippet)
    {
        snippet.Insert(_editor.TextArea);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Templates handle their keys before this (TextArea stacked input handler); the completion window after
        if (_completionWindow != null)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when modifiers == ModifierKeys.Control:
                e.Handled = true;
                await _submit();
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                e.Handled = true;
                if (!TryContinueBlock())
                {
                    await _submit();
                }
                break;
            case Key.Tab when modifiers == ModifierKeys.None:
                e.Handled = TryExpandTemplate() || TryOpenCompletion(isExplicitRequest: false);
                break;
            case Key.Space when modifiers == ModifierKeys.Control:
                e.Handled = true;
                TryOpenCompletion(isExplicitRequest: true);
                break;
            case Key.F1:
                e.Handled = true;
                _showDocumentation(GetWordAtCaret());
                break;
        }
    }

    /// <summary>
    /// Enter on a block header opens the block (and closes it with `end` when needed); Enter inside a
    /// multi-line entry adds a line. False on the last line of a complete entry: the caller submits.
    /// </summary>
    private bool TryContinueBlock()
    {
        TextDocument document = _editor.Document;
        DocumentLine caretLine = document.GetLineByOffset(_editor.CaretOffset);
        string lineText = document.GetText(caretLine);
        string indentation = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        string[] words = SplitWords(lineText);
        bool opensBlock = words.Length > 0 && BlockOpeners.Contains(words[0]) && words[^1] != "end";
        bool continuesBlock = words.Length > 0 && BlockContinuations.Contains(words[0]);
        bool isLastLine = caretLine.LineNumber == document.LineCount;
        if (!opensBlock && !continuesBlock && isLastLine)
        {
            return false;
        }

        string newLine = TextUtilities.GetNewLineFromDocument(document, caretLine.LineNumber);
        string bodyIndentation = opensBlock || continuesBlock ? indentation + Indent : indentation;
        if (opensBlock && CountOpenBlocks(document.Text) > 0)
        {
            // Keep the header whole even if the caret is inside it
            int headerEnd = caretLine.EndOffset;
            document.Insert(headerEnd, newLine + bodyIndentation + newLine + indentation + "end");
            _editor.CaretOffset = headerEnd + newLine.Length + bodyIndentation.Length;
            return true;
        }

        int caretOffset = _editor.CaretOffset;
        document.Insert(caretOffset, newLine + bodyIndentation);
        _editor.CaretOffset = caretOffset + newLine.Length + bodyIndentation.Length;
        return true;
    }

    private static int CountOpenBlocks(string text)
    {
        int openBlocks = 0;
        foreach (string line in text.Split('\n'))
        {
            string[] words = SplitWords(line);
            if (words.Length == 0)
            {
                continue;
            }
            if (BlockOpeners.Contains(words[0]) && words[^1] != "end")
            {
                openBlocks++;
            }
            else if (words[0] == "end")
            {
                openBlocks--;
            }
        }
        return openBlocks;
    }

    private static string[] SplitWords(string line)
    {
        return line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>`func`, `if`, `for` or `while` alone at the start of a line expands to its template.</summary>
    private bool TryExpandTemplate()
    {
        (int start, string keyword) = GetIdentifierBeforeCaret();
        Snippet? snippet = KalkSnippets.ForKeyword(keyword);
        DocumentLine line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
        if (snippet == null || _editor.Document.GetText(line.Offset, start - line.Offset).Trim().Length > 0)
        {
            return false;
        }

        _editor.Document.Remove(start, keyword.Length);
        snippet.Insert(_editor.TextArea);
        return true;
    }

    private bool TryOpenCompletion(bool isExplicitRequest)
    {
        KalkSession? session = _getSession();
        if (session == null)
        {
            return false;
        }

        (int prefixStart, string prefix) = GetIdentifierBeforeCaret();
        if (prefix.Length == 0 ? !isExplicitRequest : char.IsDigit(prefix[0]))
        {
            return false;
        }

        IReadOnlyList<CompletionItem> items = session.GetCompletions(prefix);
        if (items.Count == 0)
        {
            return false;
        }
        if (items.Count == 1 && !isExplicitRequest)
        {
            // A single match completes in place, like kalk's console Tab
            _editor.Document.Replace(prefixStart, prefix.Length, items[0].Name);
            return true;
        }

        CompletionWindow completionWindow = new CompletionWindow(_editor.TextArea) { StartOffset = prefixStart, MinWidth = 320 };
        foreach (CompletionItem item in items)
        {
            completionWindow.CompletionList.CompletionData.Add(new KalkCompletionData(item));
        }
        completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow = completionWindow;
        completionWindow.Show();
        if (prefix.Length > 0)
        {
            completionWindow.CompletionList.SelectItem(prefix);
        }
        return true;
    }

    private (int Start, string Identifier) GetIdentifierBeforeCaret()
    {
        string text = _editor.Text;
        int caretOffset = _editor.CaretOffset;
        int start = caretOffset;
        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
        {
            start--;
        }
        return (start, text[start..caretOffset]);
    }

    /// <summary>The identifier at the caret, else the operator token there (`<<`, `|>`, `??`...).</summary>
    private string GetWordAtCaret()
    {
        string text = _editor.Text;
        int end = _editor.CaretOffset;
        while (end < text.Length && IsIdentifierCharacter(text[end]))
        {
            end++;
        }
        (int start, _) = GetIdentifierBeforeCaret();
        if (end > start)
        {
            return text[start..end];
        }

        start = end = _editor.CaretOffset;
        while (start > 0 && IsOperatorCharacter(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && IsOperatorCharacter(text[end]))
        {
            end++;
        }
        return text[start..end];
    }

    private static bool IsOperatorCharacter(char character)
    {
        return "<>=!&|^%*/+-?:.@$#;~".Contains(character);
    }

    private static bool IsIdentifierCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }

    private void CheckSyntax()
    {
        string text = _editor.Text;
        SyntaxProblem? problem = _getSession()?.CheckSyntax(text);
        if (problem != null)
        {
            // Underline from the error position to the end of that token
            int end = problem.Offset;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }
            _highlighting.SetError(problem.Offset, Math.Max(1, end - problem.Offset));
        }
        _onSyntaxChecked(problem);
    }
}
