using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using ShaderCalc.App.Services;
using ShaderCalc.App.Ui;
using ShaderCalc.Binding;
using ShaderCalc.Docs;
using ShaderCalc.Evaluation;
using ShaderCalc.Reference;
using ShaderCalc.Units;

namespace ShaderCalc.App;

public partial class MainWindow : Window
{
    private static readonly string[] CommonTypes =
    {
        "bool", "int", "uint", "float", "double", "half", "int2", "int3", "int4", "uint2", "uint3", "uint4", "float2", "float3", "float4",
        "float2x2", "float3x3", "float4x4", "int64_t", "uint64_t", "min16float", "vector", "matrix",
    };

    private readonly IHighlightingDefinition _highlighting = HlslHighlighting.Create(Palette.Current);
    private readonly HashSet<WorksheetView> _unsaved = new HashSet<WorksheetView>();
    private readonly DispatcherTimer _saveTimer;
    private WorksheetStore? _store;
    private EvaluationController? _evaluation;
    private WorksheetResult? _lastResult;
    private int _matches;
    private int _approximations;
    private int _mismatches;

    public MainWindow()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveUnsaved(), Dispatcher) { IsEnabled = false };
#if DEBUG
        AutomationSnapshot.Attach(this);
#endif
        InspectorViewer.Document = Inspector.BuildEmpty();
        DocsList.ItemsSource = Documentation.Entries;
    }

    private FontFamily MonoFont => (FontFamily)Application.Current.Resources["MonoFont"];

    private IEnumerable<WorksheetView> Views => DocumentTabs.Items.OfType<TabItem>().Select(tab => tab.Content).OfType<WorksheetView>();

    private WorksheetView? ActiveView => (DocumentTabs.SelectedItem as TabItem)?.Content as WorksheetView;

    // ---------------------------------------------------------------- Startup and shutdown

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _store = WorksheetStore.Open(Dispatcher);
        AppState state = _store.State;
        if (!double.IsNaN(state.Left) && !double.IsNaN(state.Top))
        {
            Left = state.Left;
            Top = state.Top;
        }
        Width = state.Width;
        Height = state.Height;
        WindowState = state.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        SideColumn.Width = new GridLength(state.SidePanelWidth);
        FolderText.Text = _store.Folder;

        foreach (string name in _store.OrderedNames())
        {
            AddTab(name, _store.Read(name));
        }
        DocumentTabs.SelectedItem = DocumentTabs.Items.OfType<TabItem>().FirstOrDefault(tab => ((WorksheetView)tab.Content).DocumentName == state.ActiveTab)
            ?? DocumentTabs.Items.OfType<TabItem>().FirstOrDefault();
        _store.ChangedOutside += OnChangedOutside;

        _evaluation = new EvaluationController(Dispatcher, Snapshot, () => ActiveView?.DocumentName);
        _evaluation.Evaluated += OnEvaluated;
        _evaluation.ReferenceChecked += OnReferenceChecked;
        _evaluation.ReferenceProgress += OnReferenceProgress;
        _evaluation.Failed += exception => StatusText.Text = "Evaluation failed: " + exception.Message;
        _evaluation.RunNow();
        ActiveView?.Editor.Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_store == null)
        {
            return;
        }
        SaveUnsaved();
        AppState state = _store.State;
        state.IsMaximized = WindowState == WindowState.Maximized;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (state.Left, state.Top, state.Width, state.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        state.SidePanelWidth = SideColumn.ActualWidth;
        state.ResultColumnWidth = ActiveView?.ResultColumnWidth ?? state.ResultColumnWidth;
        state.ActiveTab = ActiveView?.DocumentName;
        state.TabOrder = Views.Select(view => view.DocumentName).ToList();
        _store.SaveState();
        _store.Dispose();
    }

    // ---------------------------------------------------------------- Tabs

    private TabItem AddTab(string name, string text, int index = -1)
    {
        WorksheetView view = new WorksheetView(name, text, _highlighting, CompletionEntries, _store?.State.ResultColumnWidth ?? 340);
        view.TextEdited += () =>
        {
            _unsaved.Add(view);
            _saveTimer.Stop();
            _saveTimer.Start();
            _evaluation?.Request();
        };
        view.SelectionChanged += OnLineSelected;
        TabItem tab = new TabItem { Header = CreateHeader(name), Content = view };
        tab.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { TemplatedParent: not TextBox } && ReferenceEquals(DocumentTabs.SelectedItem, tab) && e.GetPosition(tab).Y < 40)
            {
                RenameActive();
            }
        };
        if (index < 0 || index > DocumentTabs.Items.Count)
        {
            DocumentTabs.Items.Add(tab);
        }
        else
        {
            DocumentTabs.Items.Insert(index, tab);
        }
        return tab;
    }

    private static TextBlock CreateHeader(string name) => new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(name), ToolTip = name };

    private TabItem? FindTab(string name) =>
        DocumentTabs.Items.OfType<TabItem>().FirstOrDefault(tab => string.Equals(((WorksheetView)tab.Content).DocumentName, name, StringComparison.OrdinalIgnoreCase));

    private void OnDocumentTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DocumentTabs) && ActiveView is WorksheetView view)
        {
            OnLineSelected(view);
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => NewWorksheet();

    private void NewWorksheet()
    {
        if (_store == null)
        {
            return;
        }
        string name = _store.Create();
        TabItem tab = AddTab(name, string.Empty);
        DocumentTabs.SelectedItem = tab;
        ((WorksheetView)tab.Content).Editor.Focus();
        _evaluation?.Request();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameActive();

    private void RenameActive()
    {
        if (_store == null || ActiveView is not WorksheetView view || DocumentTabs.SelectedItem is not TabItem tab)
        {
            return;
        }
        string? title = PromptWindow.Ask(this, "Rename worksheet", "Name:", System.IO.Path.GetFileNameWithoutExtension(view.DocumentName));
        if (title == null)
        {
            return;
        }
        SaveUnsaved();
        string? newName = _store.Rename(view.DocumentName, title, out string? problem);
        if (newName == null)
        {
            MessageBox.Show(this, problem, "Rename worksheet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        view.DocumentName = newName;
        tab.Header = CreateHeader(newName);
        _evaluation?.Request();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_store == null || ActiveView is not WorksheetView view || DocumentTabs.SelectedItem is not TabItem tab)
        {
            return;
        }
        if (MessageBox.Show(this, $"Send {view.DocumentName} to the Recycle Bin?", "Delete worksheet", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            return;
        }
        _unsaved.Remove(view);
        _store.Delete(view.DocumentName);
        DocumentTabs.Items.Remove(tab);
        if (DocumentTabs.Items.Count == 0)
        {
            NewWorksheet();
        }
        _evaluation?.Request();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_store != null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Folder}\"") { UseShellExecute = true });
        }
    }

    private void SaveUnsaved()
    {
        _saveTimer.Stop();
        if (_store == null)
        {
            return;
        }
        foreach (WorksheetView view in _unsaved.ToList())
        {
            _store.Write(view.DocumentName, view.Text);
        }
        _unsaved.Clear();
    }

    /// <summary>Files edited, added or removed outside the app: reload them live.</summary>
    private void OnChangedOutside(IReadOnlyList<string> changed, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        if (_store == null)
        {
            return;
        }
        foreach (string name in changed)
        {
            if (FindTab(name)?.Content is WorksheetView view && !_unsaved.Contains(view))
            {
                view.ReplaceText(_store.Read(name));
            }
        }
        foreach (string name in added.Where(name => FindTab(name) == null))
        {
            AddTab(name, _store.Read(name));
        }
        foreach (string name in removed)
        {
            if (FindTab(name) is TabItem tab)
            {
                _unsaved.Remove((WorksheetView)tab.Content);
                DocumentTabs.Items.Remove(tab);
            }
        }
        if (DocumentTabs.Items.Count == 0)
        {
            NewWorksheet();
        }
        _evaluation?.Request();
    }

    // ---------------------------------------------------------------- Evaluation

    private IReadOnlyList<WorksheetDocument> Snapshot() => Views.Select(view => new WorksheetDocument(view.DocumentName, view.Text)).ToList();

    private void OnEvaluated(WorksheetResult result)
    {
        _lastResult = result;
        _matches = _approximations = _mismatches = 0;
        foreach (WorksheetView view in Views)
        {
            view.ApplyEvaluation(result.LinesOf(view.DocumentName), result.DiagnosticsOf(view.DocumentName));
        }
        int errors = result.Diagnostics.Count(diagnostic => diagnostic.IsError);
        int shown = result.Lines.Count(line => line.Value != null);
        StatusText.Text = $"{shown} result{(shown == 1 ? string.Empty : "s")}"
            + (errors > 0 ? $" · {errors} problem{(errors == 1 ? string.Empty : "s")}" : string.Empty)
            + $" · {result.Duration.TotalMilliseconds:F0} ms";
    }

    private void OnReferenceChecked(WorksheetLine line, ReferenceOutcome outcome)
    {
        switch (outcome.Verdict)
        {
            case ReferenceVerdict.Match:
                _matches++;
                break;
            case ReferenceVerdict.WithinTolerance:
                _approximations++;
                break;
            case ReferenceVerdict.Mismatch:
                _mismatches++;
                break;
        }
        Views.FirstOrDefault(view => view.DocumentName == line.Document)?.ApplyReference(line, outcome);
    }

    private void OnReferenceProgress(int done, int total)
    {
        string counts = $"✓ {_matches}" + (_approximations > 0 ? $"  ≈ {_approximations}" : string.Empty) + (_mismatches > 0 ? $"  ≠ {_mismatches}" : string.Empty);
        ReferenceText.Text = total == 0 ? string.Empty : done < total ? $"Reference {done}/{total} · {counts}" : $"Reference (DXC + WARP): {counts}";
    }

    private void OnLineSelected(WorksheetView view)
    {
        if (!ReferenceEquals(view, ActiveView))
        {
            return;
        }
        InspectorViewer.Document = view.SelectedLine is WorksheetLine line
            ? Inspector.Build(line, view.SourceOf(line), view.ReferenceFor(line), MonoFont)
            : Inspector.BuildEmpty();
    }

    // ---------------------------------------------------------------- Completion and docs

    private IEnumerable<CompletionEntry> CompletionEntries()
    {
        foreach (DocEntry entry in Documentation.Entries.Where(entry => !entry.IsTopic))
        {
            yield return new CompletionEntry(entry.Name, "intrinsic", $"{entry.Signature?.Trim('`')}\n{entry.Summary}");
        }
        foreach (string keyword in HlslHighlighting.Keywords)
        {
            yield return new CompletionEntry(keyword, "keyword", keyword);
        }
        foreach (string type in CommonTypes)
        {
            yield return new CompletionEntry(type, "type", type);
        }
        if (_lastResult?.Program is BoundProgram program)
        {
            foreach (FunctionSymbol function in program.Functions)
            {
                yield return new CompletionEntry(function.Name, "function", $"{function.Signature}\n{function.SourceName}");
            }
            foreach (VariableSymbol global in program.Globals)
            {
                yield return new CompletionEntry(global.Name, "variable", global.ToString());
            }
            foreach (string type in program.TypeNames.Keys)
            {
                yield return new CompletionEntry(type, "type", type);
            }
        }
        foreach (UnitDefinition unit in UnitTable.Units.Values)
        {
            yield return new CompletionEntry(unit.Name, "unit", $"{unit.Name} = {unit.ToSi} {unit.Dimension}");
        }
    }

    private void OnDocsSearchChanged(object sender, TextChangedEventArgs e)
    {
        IReadOnlyList<DocEntry> entries = Documentation.Search(DocsSearch.Text);
        DocsList.ItemsSource = entries;
        if (entries.Count > 0)
        {
            DocsList.SelectedIndex = 0;
        }
    }

    private void OnDocsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocsList.SelectedItem is DocEntry entry)
        {
            DocsViewer.Document = BuildDocPage(entry);
            DocsList.ScrollIntoView(entry);
        }
    }

    private FlowDocument BuildDocPage(DocEntry entry)
    {
        FlowDocument document = MarkdownRenderer.CreateDocument();
        document.Blocks.Add(new Paragraph(new Run(entry.Name) { FontFamily = entry.IsTopic ? null : MonoFont }) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        document.Blocks.Add(new Paragraph(new Run(entry.Group) { Foreground = Palette.Current.Dim }) { FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        string body = entry.Markdown;
        if (entry.Signature != null)
        {
            // "`T pow(T x, T y)` · float": the signature as code, the argument kinds after it
            int end = entry.Signature.IndexOf('`', 1);
            string signature = end > 0 ? entry.Signature[1..end] : entry.Signature.Trim('`');
            string kinds = end > 0 ? entry.Signature[(end + 1)..].Trim().TrimStart('·').Trim() : string.Empty;
            Paragraph signatureLine = MarkdownRenderer.CreateCodeBlock(signature, MonoFont);
            if (kinds.Length > 0)
            {
                signatureLine.Inlines.Add(new Run("    " + kinds) { Foreground = Palette.Current.Dim, FontFamily = SystemFonts.MessageFontFamily });
            }
            document.Blocks.Add(signatureLine);
            body = body[(body.IndexOf(entry.Signature, StringComparison.Ordinal) + entry.Signature.Length)..];
        }
        MarkdownRenderer.AddMarkdown(document, body, MonoFont);
        return document;
    }

    /// <summary>F1: the page of the name under the caret (or a search for it).</summary>
    private void ShowDocsForCaret()
    {
        string? word = ActiveView?.WordAtCaret();
        SideTabs.SelectedItem = DocsTab;
        if (word == null)
        {
            DocsSearch.Focus();
            return;
        }
        DocsSearch.Text = word;
        if (Documentation.Find(word) is DocEntry exact)
        {
            DocsList.SelectedItem = exact;
        }
    }

    // ---------------------------------------------------------------- Keys

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            ShowDocsForCaret();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            RenameActive();
            e.Handled = true;
        }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NewWorksheet();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveUnsaved();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ActiveView is WorksheetView view && !view.Editor.TextArea.IsKeyboardFocusWithin)
        {
            // Esc anywhere else (docs search, inspector...) goes back to the code
            view.Editor.Focus();
            e.Handled = true;
        }
    }
}
