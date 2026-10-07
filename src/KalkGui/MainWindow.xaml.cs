using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using Kalk.Core;
using KalkGui.Engine;
using KalkGui.Ui;

namespace KalkGui;

public partial class MainWindow : Window
{
    private const int MaxTranscriptBlocks = 4000;

    private readonly InputHistory _history;
    private readonly KalkEditorHighlighting _inputHighlighting;
    private readonly KalkEditorHighlighting _configHighlighting;
    private readonly FontFamily _monoFont;
    private readonly string? _kalkUserFolder;
    private KalkSession? _session;
    private CompletionWindow? _completionWindow;
    private ListCollectionView? _docView;
    private string _importedModules = string.Empty;
    private bool _isSyncingDisplayMode;
    private bool _isConfigDirty;
    // Set from the evaluation thread by kalk's `clear` command
    private volatile bool _clearRequested;

    public MainWindow()
    {
        InitializeComponent();
        _monoFont = (FontFamily)FindResource("MonoFont");
        // KALKGUI_DATA_DIR / KALKGUI_KALK_FOLDER isolate UI test runs from the user's history and config.kalk
        string dataFolder = Environment.GetEnvironmentVariable("KALKGUI_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalkGui");
        _history = new InputHistory(Path.Combine(dataFolder, "history.json"));
        _kalkUserFolder = Environment.GetEnvironmentVariable("KALKGUI_KALK_FOLDER");

        ConfigureEditor(InputEditor);
        ConfigureEditor(ConfigEditor);
        _inputHighlighting = new KalkEditorHighlighting(InputEditor, () => _session, matchBraces: true);
        _configHighlighting = new KalkEditorHighlighting(ConfigEditor, () => _session, matchBraces: false);
        InputEditor.TextArea.PreviewKeyDown += OnInputPreviewKeyDown;
        ConfigEditor.TextChanged += (_, _) => SetConfigDirty(true);

        // --background: open behind the other windows without taking focus (UI automation, autostart)
        if (Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            ShowActivated = false;
            ContentRendered += (_, _) => WindowZOrder.SendToBack(this);
        }
#if DEBUG
        AutomationSnapshot.Attach(this);
#endif
    }

    private static void ConfigureEditor(TextEditor editor)
    {
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.LineNumbersForeground = KalkPalette.Prompt;
        editor.TextArea.Caret.CaretBrush = KalkPalette.Foreground;
        editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x3B, 0x78, 0xFF));
        // Keep the syntax colours inside the selection
        editor.TextArea.SelectionForeground = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartSession();
        LoadConfigFile();
        FocusInput();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // First activation (immediate in a normal launch, on user click with --background)
        if (Keyboard.FocusedElement == null || Keyboard.FocusedElement == this)
        {
            InputEditor.TextArea.Focus();
        }
    }

    private void FocusInput()
    {
        // Keyboard focus in an inactive window would pull the OS focus away from the user's app
        if (IsActive)
        {
            InputEditor.TextArea.Focus();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isConfigDirty)
        {
            MessageBoxResult answer = MessageBox.Show(this, "Save changes to config.kalk?", "KalkGui", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == MessageBoxResult.Yes)
            {
                SaveConfig();
            }
        }
        _session?.CancelEvaluation();
    }

    // ---- Engine session ----

    private void StartSession()
    {
        _session = new KalkSession(GetClipboardText, SetClipboardText, () => _clearRequested = true, kalkUserFolder: _kalkUserFolder);
        // Drop the "+<commit hash>" build metadata
        string version = _session.Version.Split('+')[0];
        Title = $"KalkGui - kalk {version}";
        VersionText.Text = $"kalk {version}";
        AppendOutput(_session.StartupOutput);
        _docView = null;
        RefreshPanels();
        _inputHighlighting.Refresh();
        _configHighlighting.Refresh();
    }

    private void RestartSession()
    {
        _session?.CancelEvaluation();
        AppendNote("# Engine restarted: config.kalk reloaded, session variables and functions cleared");
        StartSession();
    }

    private async Task EvaluateAsync(string input, bool fromInputEditor)
    {
        KalkSession? session = _session;
        if (session == null || session.IsEvaluating || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _clearRequested = false;
        SetBusy(true);
        EvaluationResult result;
        try
        {
            result = await session.EvaluateAsync(input);
        }
        finally
        {
            SetBusy(false);
        }

        // The engine was restarted while this evaluation ran
        if (session != _session)
        {
            return;
        }

        if (result.IsSuccess)
        {
            HideError();
            if (_clearRequested)
            {
                ClearTranscript();
            }
            else
            {
                AppendInput(input);
                AppendOutput(result.Output);
            }
            if (fromInputEditor)
            {
                _history.Add(input.Trim());
                InputEditor.Text = string.Empty;
            }
        }
        else
        {
            ShowError(result, fromInputEditor);
        }

        RefreshPanels();
        if (result.HasExit)
        {
            Close();
        }
    }

    private async Task ImportModuleAsync(string moduleName)
    {
        await EvaluateAsync($"import {moduleName}", fromInputEditor: false);
        FocusInput();
    }

    private void SetBusy(bool isBusy)
    {
        StatusText.Text = isBusy ? "Evaluating... (Esc to cancel)" : "Ready";
        Cursor = isBusy ? Cursors.AppStarting : null;
    }

    private void RefreshPanels()
    {
        if (_session == null)
        {
            return;
        }

        SymbolList.ItemsSource = _session.GetUserSymbols();
        SyncDisplayMode();

        string importedModules = string.Join(",", _session.GetModules().Where(module => module.IsImported).Select(module => module.Name));
        if (_docView == null || importedModules != _importedModules)
        {
            _importedModules = importedModules;
            RefreshDocumentation();
        }
    }

    private void SyncDisplayMode()
    {
        string mode = _session!.DisplayMode switch
        {
            KalkDisplayMode.Developer => "dev",
            KalkDisplayMode.Raw => "raw",
            _ => "std",
        };
        _isSyncingDisplayMode = true;
        DisplayModeBox.SelectedItem = DisplayModeBox.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == mode);
        _isSyncingDisplayMode = false;
    }

    private string GetClipboardText()
    {
        return Dispatcher.Invoke(() =>
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (COMException)
            {
                // Clipboard held open by another process
                return string.Empty;
            }
        });
    }

    private void SetClipboardText(string text)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
            }
            catch (COMException)
            {
                // Clipboard held open by another process
            }
        });
    }

    // ---- Transcript ----

    private void AppendInput(string input)
    {
        string text = input.Trim();
        Paragraph paragraph = StyledText.CreateParagraph(text, Highlight(text), prompt: ">>> ");
        paragraph.Margin = new Thickness(0, 8, 0, 0);
        paragraph.Tag = text;
        paragraph.ToolTip = "Double-click to edit again";
        AddTranscriptBlock(paragraph);
    }

    private void AppendOutput(string output)
    {
        string text = output.TrimEnd('\r', '\n');
        if (text.Length > 0)
        {
            AddTranscriptBlock(StyledText.CreateParagraph(text, Highlight(text)));
        }
    }

    private void AppendNote(string note)
    {
        AddTranscriptBlock(new Paragraph(new Run(note) { Foreground = KalkPalette.Prompt }) { Margin = new Thickness(0, 8, 0, 0) });
    }

    private void AddTranscriptBlock(Block block)
    {
        Transcript.Blocks.Add(block);
        while (Transcript.Blocks.Count > MaxTranscriptBlocks)
        {
            Transcript.Blocks.Remove(Transcript.Blocks.FirstBlock);
        }
        TranscriptBox.ScrollToEnd();
    }

    private void ClearTranscript()
    {
        Transcript.Blocks.Clear();
    }

    private IReadOnlyList<StyledSpan> Highlight(string text)
    {
        return _session?.Highlight(text) ?? Array.Empty<StyledSpan>();
    }

    private void OnTranscriptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TextPointer? pointer = TranscriptBox.GetPositionFromPoint(e.GetPosition(TranscriptBox), snapToText: true);
        if (pointer?.Paragraph?.Tag is string input)
        {
            SetInputText(input);
            FocusInput();
            e.Handled = true;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ClearTranscript();
        FocusInput();
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        RestartSession();
        FocusInput();
    }

    // ---- Input editor ----

    private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // An open completion window handles its own keys (Enter/Tab commit, Esc closes)
        if (_completionWindow != null)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when (modifiers & ModifierKeys.Shift) == 0:
                e.Handled = true;
                await EvaluateAsync(InputEditor.Text, fromInputEditor: true);
                break;
            case Key.Up when InputEditor.TextArea.Caret.Line == 1:
                if (_history.TryGetPrevious(InputEditor.Text, out string previous))
                {
                    SetInputText(previous);
                    e.Handled = true;
                }
                break;
            case Key.Down when InputEditor.TextArea.Caret.Line == InputEditor.Document.LineCount:
                if (_history.TryGetNext(out string next))
                {
                    SetInputText(next);
                    e.Handled = true;
                }
                break;
            case Key.Tab when modifiers == ModifierKeys.None:
                e.Handled = TryOpenCompletion(isExplicitRequest: false);
                break;
            case Key.Space when modifiers == ModifierKeys.Control:
                e.Handled = true;
                TryOpenCompletion(isExplicitRequest: true);
                break;
            case Key.F1:
                e.Handled = true;
                ShowDocumentationAtCaret();
                break;
            case Key.Escape when ErrorText.Visibility == Visibility.Visible:
                HideError();
                e.Handled = true;
                break;
        }
    }

    private async void OnEvaluateClick(object sender, RoutedEventArgs e)
    {
        await EvaluateAsync(InputEditor.Text, fromInputEditor: true);
        FocusInput();
    }

    private bool TryOpenCompletion(bool isExplicitRequest)
    {
        if (_session == null)
        {
            return false;
        }

        (int prefixStart, string prefix) = GetIdentifierBeforeCaret();
        if (prefix.Length == 0 ? !isExplicitRequest : char.IsDigit(prefix[0]))
        {
            return false;
        }

        IReadOnlyList<CompletionItem> items = _session.GetCompletions(prefix);
        if (items.Count == 0)
        {
            return false;
        }
        if (items.Count == 1 && !isExplicitRequest)
        {
            // A single match completes in place, like kalk's console Tab
            InputEditor.Document.Replace(prefixStart, prefix.Length, items[0].Name);
            return true;
        }

        CompletionWindow completionWindow = new CompletionWindow(InputEditor.TextArea) { StartOffset = prefixStart, MinWidth = 320 };
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
        string text = InputEditor.Text;
        int caretOffset = InputEditor.CaretOffset;
        int start = caretOffset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }
        return (start, text[start..caretOffset]);
    }

    private void ShowDocumentationAtCaret()
    {
        string text = InputEditor.Text;
        int end = InputEditor.CaretOffset;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }
        (int start, _) = GetIdentifierBeforeCaret();
        string word = text[start..end];

        SideTabs.SelectedItem = DocsTab;
        DocSearchBox.Text = string.Empty;
        DocEntry? entry = _docView?.OfType<DocEntry>().FirstOrDefault(candidate => candidate.Descriptor.Names.Contains(word));
        if (entry != null)
        {
            DocList.SelectedItem = entry;
            DocList.ScrollIntoView(entry);
        }
    }

    private void InsertIntoInput(string text)
    {
        int caretOffset = InputEditor.CaretOffset;
        InputEditor.Document.Insert(caretOffset, text);
        InputEditor.CaretOffset = caretOffset + text.Length;
        FocusInput();
    }

    private void SetInputText(string text)
    {
        InputEditor.Text = text;
        InputEditor.CaretOffset = text.Length;
    }

    private void ShowError(EvaluationResult result, bool fromInputEditor)
    {
        ErrorText.Text = fromInputEditor ? result.Error : $"{result.Input.Trim()}: {result.Error}";
        ErrorText.Visibility = Visibility.Visible;

        TextDocument document = InputEditor.Document;
        if (fromInputEditor && result.ErrorLine >= 0 && result.ErrorLine < document.LineCount && result.ErrorColumn >= 0)
        {
            DocumentLine line = document.GetLineByNumber(result.ErrorLine + 1);
            InputEditor.CaretOffset = line.Offset + Math.Min(result.ErrorColumn, line.Length);
        }
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape && _session?.IsEvaluating == true)
        {
            _session.CancelEvaluation();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            ClearTranscript();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.S && SideTabs.SelectedItem == ConfigTab)
        {
            SaveConfig();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Esc left unused by the focused control (docs search, lists, tabs...) returns to the input
        if (e.Key == Key.Escape && !InputEditor.TextArea.IsKeyboardFocusWithin)
        {
            InputEditor.TextArea.Focus();
            e.Handled = true;
        }
    }

    // ---- Toolbar ----

    private async void OnDisplayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingDisplayMode || DisplayModeBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }
        await EvaluateAsync($"display {mode}", fromInputEditor: false);
        FocusInput();
    }

    private void OnImportButtonClick(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        ContextMenu menu = new ContextMenu { PlacementTarget = ImportButton, Placement = PlacementMode.Bottom };
        foreach (ModuleInfo module in _session.GetModules())
        {
            MenuItem item = new MenuItem { Header = module.Name, IsChecked = module.IsImported, IsEnabled = !module.IsImported };
            item.Click += async (_, _) => await ImportModuleAsync(module.Name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- Docs tab ----

    private void RefreshDocumentation()
    {
        string? selectedName = (DocList.SelectedItem as DocEntry)?.Name;
        List<DocEntry> entries = _session!.GetDocumentation().ToList();
        ListCollectionView view = new ListCollectionView(entries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DocEntry.Group)));
        view.Filter = MatchesDocSearch;
        _docView = view;
        DocList.ItemsSource = view;
        DocList.SelectedItem = entries.FirstOrDefault(entry => entry.Name == selectedName);
        if (DocList.SelectedItem == null)
        {
            DocDetails.Document = DocumentationRenderer.BuildHint();
        }
    }

    private bool MatchesDocSearch(object item)
    {
        string search = DocSearchBox.Text.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        DocEntry entry = (DocEntry)item;
        return entry.Descriptor.Names.Any(name => name.Contains(search, StringComparison.OrdinalIgnoreCase))
            || entry.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDocSearchChanged(object sender, TextChangedEventArgs e)
    {
        _docView?.Refresh();
    }

    private void OnDocSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DocDetails.Document = DocList.SelectedItem is DocEntry entry && _session != null
            ? DocumentationRenderer.Build(entry, _session, _monoFont, async moduleName => await ImportModuleAsync(moduleName))
            : DocumentationRenderer.BuildHint();
    }

    private void OnDocListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DocList.SelectedItem is DocEntry entry)
        {
            InsertDocEntry(entry);
        }
    }

    private void OnDocInsertClick(object sender, RoutedEventArgs e)
    {
        if (DocList.SelectedItem is DocEntry entry)
        {
            InsertDocEntry(entry);
        }
    }

    private void InsertDocEntry(DocEntry entry)
    {
        if (entry.IsModule)
        {
            InsertIntoInput(entry.Signature);
            return;
        }

        string text = entry.Signature.Contains('(') ? $"{entry.Name}(" : entry.Signature.Contains(' ') ? $"{entry.Name} " : entry.Name;
        InsertIntoInput(text);
    }

    // ---- Library tab ----

    private UserSymbol? SelectedSymbol => SymbolList.SelectedItem as UserSymbol;

    private void InsertSymbol(UserSymbol symbol)
    {
        InsertIntoInput(symbol.IsFunction ? $"{symbol.Name}(" : symbol.Name);
    }

    private void OnSymbolDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedSymbol is UserSymbol symbol)
        {
            InsertSymbol(symbol);
        }
    }

    private void OnSymbolInsertClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSymbol is UserSymbol symbol)
        {
            InsertSymbol(symbol);
        }
    }

    private async void OnSymbolDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSymbol is UserSymbol symbol)
        {
            await EvaluateAsync($"del {symbol.Name}", fromInputEditor: false);
        }
    }

    private async void OnSymbolResetClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all variables and functions defined in this session?", "KalkGui", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            await EvaluateAsync("reset", fromInputEditor: false);
        }
    }

    private void OnSymbolToConfigClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSymbol is not UserSymbol symbol)
        {
            return;
        }

        string configText = ConfigEditor.Text;
        string separator = configText.Length == 0 || configText.EndsWith('\n') ? string.Empty : Environment.NewLine;
        ConfigEditor.Document.Insert(ConfigEditor.Document.TextLength, separator + symbol.Definition + Environment.NewLine);
        SideTabs.SelectedItem = ConfigTab;
        StatusText.Text = $"Added {symbol.Name} to config.kalk (not saved yet)";
    }

    // ---- Config tab ----

    private void LoadConfigFile()
    {
        string path = _session!.ConfigFilePath;
        ConfigPathText.Text = path;
        ConfigEditor.Text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        SetConfigDirty(false);
    }

    private void SaveConfig()
    {
        string path = _session!.ConfigFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ConfigEditor.Text, new UTF8Encoding(false));
        SetConfigDirty(false);
        StatusText.Text = $"Saved {path}";
    }

    private void SetConfigDirty(bool isDirty)
    {
        _isConfigDirty = isDirty;
        ConfigTab.Header = isDirty ? "Config *" : "Config";
    }

    private void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        SaveConfig();
    }

    private void OnSaveAndRestartClick(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        RestartSession();
    }

    private void OnReloadConfigClick(object sender, RoutedEventArgs e)
    {
        if (_isConfigDirty && MessageBox.Show(this, "Discard unsaved changes to config.kalk?", "KalkGui", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        LoadConfigFile();
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        string path = _session!.ConfigFilePath;
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
