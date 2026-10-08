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
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Snippets;
using Kalk.Core;
using KalkGui.Engine;
using KalkGui.Ui;

namespace KalkGui;

public partial class MainWindow : Window
{
    private const int MaxTranscriptBlocks = 4000;

    private enum EvaluationSource
    {
        InputEditor,
        Command,
        LibraryEditor,
    }

    private readonly InputHistory _history;
    private readonly KalkEditorHighlighting _inputHighlighting;
    private readonly KalkEditorHighlighting _configHighlighting;
    private readonly KalkEditorHighlighting _libraryHighlighting;
    private readonly FontFamily _monoFont;
    private readonly string? _kalkUserFolder;
    private KalkSession? _session;
    private readonly KalkEditorAssist _inputAssist;
    private readonly KalkEditorAssist _libraryAssist;
    private ListCollectionView? _docView;
    // Rank of each entry matching the docs search; null when the search box is empty
    private Dictionary<DocEntry, int>? _docSearchScores;
    private string _importedModules = string.Empty;
    private bool _isSyncingDisplayMode;
    private bool _isConfigDirty;
    // Library entry shown in the Library tab editor, and whether the user changed its text
    private string? _editedSymbolName;
    private bool _isLibraryEditDirty;
    private bool _isLoadingLibraryEditor;
    private bool _isRefreshingSymbols;
    // Outside edits of library.kalk: watcher events are debounced (editors write in several steps)
    private readonly DispatcherTimer _libraryReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    private FileSystemWatcher? _libraryWatcher;
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
        ConfigureEditor(LibraryEditor);
        _inputHighlighting = new KalkEditorHighlighting(InputEditor, () => _session, matchBraces: true);
        _configHighlighting = new KalkEditorHighlighting(ConfigEditor, () => _session, matchBraces: false);
        _libraryHighlighting = new KalkEditorHighlighting(LibraryEditor, () => _session, matchBraces: true);
        // Created before the window's own key handlers: Enter/Tab/Ctrl+Space/F1 go to the assist first
        _inputAssist = new KalkEditorAssist(InputEditor, _inputHighlighting, () => _session,
            () => EvaluateAsync(InputEditor.Text, EvaluationSource.InputEditor), ShowDocumentation, problem => ShowSyntaxHint(ErrorText, problem));
        _libraryAssist = new KalkEditorAssist(LibraryEditor, _libraryHighlighting, () => _session,
            ApplyLibraryEditAsync, ShowDocumentation, problem => ShowSyntaxHint(LibraryErrorText, problem));
        InputEditor.TextArea.PreviewKeyDown += OnInputPreviewKeyDown;
        LibraryEditor.TextArea.PreviewKeyDown += OnLibraryEditorPreviewKeyDown;
        ConfigEditor.TextChanged += (_, _) => SetConfigDirty(true);
        LibraryEditor.TextChanged += (_, _) => _isLibraryEditDirty |= !_isLoadingLibraryEditor;
        _libraryReloadTimer.Tick += async (_, _) =>
        {
            _libraryReloadTimer.Stop();
            await ReloadLibraryFromDiskAsync();
        };

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
        _libraryWatcher?.Dispose();
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
        foreach (LibraryLoadError error in _session.LibraryLoadErrors)
        {
            AppendNote($"# library.kalk: `{error.Entry}` was not loaded: {error.Message}", KalkPalette.Error);
        }
        LibraryPathText.Text = $"Definitions apply immediately. Saved to {_session.LibraryFilePath}; outside edits are picked up live.";
        WatchLibrary(_session.LibraryFilePath);
        _docView = null;
        RefreshPanels();
        _inputHighlighting.Refresh();
        _configHighlighting.Refresh();
        _libraryHighlighting.Refresh();
    }

    private void WatchLibrary(string? path)
    {
        _libraryWatcher?.Dispose();
        _libraryWatcher = null;
        if (path == null)
        {
            return;
        }

        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        // "library.kalk*" also sees the .tmp rename KalkGui saves with; the session ignores its own writes
        _libraryWatcher = new FileSystemWatcher(folder, Path.GetFileName(path) + "*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onFileEvent = (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _libraryReloadTimer.Stop();
            _libraryReloadTimer.Start();
        }));
        _libraryWatcher.Changed += onFileEvent;
        _libraryWatcher.Created += onFileEvent;
        _libraryWatcher.Deleted += onFileEvent;
        _libraryWatcher.Renamed += (sender, e) => onFileEvent(sender, e);
        _libraryWatcher.EnableRaisingEvents = true;
    }

    private async Task ReloadLibraryFromDiskAsync()
    {
        KalkSession? session = _session;
        if (session == null)
        {
            return;
        }

        LibraryReloadResult? reload = await session.ReloadLibraryAsync();
        if (reload == null || session != _session)
        {
            return;
        }

        List<string> changes = new List<string>();
        if (reload.UpdatedNames.Count > 0)
        {
            changes.Add($"updated {string.Join(", ", reload.UpdatedNames)}");
        }
        if (reload.RemovedNames.Count > 0)
        {
            changes.Add($"removed {string.Join(", ", reload.RemovedNames)}");
        }
        AppendNote($"# library.kalk changed on disk: {(changes.Count > 0 ? string.Join("; ", changes) : "no definition changed")}");
        foreach (LibraryLoadError error in reload.Errors)
        {
            AppendNote($"# library.kalk: `{error.Entry}` was not loaded: {error.Message}", KalkPalette.Error);
        }
        RefreshPanels();
    }

    private void RestartSession()
    {
        _session?.CancelEvaluation();
        AppendNote("# Engine restarted: config.kalk and library.kalk reloaded");
        StartSession();
    }

    private async Task<EvaluationResult?> EvaluateAsync(string input, EvaluationSource source, string? replacesSymbol = null)
    {
        KalkSession? session = _session;
        if (session == null || session.IsEvaluating || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        _clearRequested = false;
        SetBusy(true);
        EvaluationResult result;
        try
        {
            result = await session.EvaluateAsync(input, replacesSymbol);
        }
        finally
        {
            SetBusy(false);
        }

        // The engine was restarted while this evaluation ran
        if (session != _session)
        {
            return null;
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
            if (source == EvaluationSource.InputEditor)
            {
                _history.Add(input.Trim());
                InputEditor.Text = string.Empty;
            }
        }
        else
        {
            ShowError(result, source);
        }
        if (result.Warning != null)
        {
            StatusText.Text = result.Warning;
        }

        RefreshPanels();
        if (result.HasExit)
        {
            Close();
        }
        return result;
    }

    private async Task ImportModuleAsync(string moduleName)
    {
        await EvaluateAsync($"import {moduleName}", EvaluationSource.Command);
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

        RefreshSymbols();
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

    private void AppendNote(string note, Brush? brush = null)
    {
        AddTranscriptBlock(new Paragraph(new Run(note) { Foreground = brush ?? KalkPalette.Prompt }) { Margin = new Thickness(0, 8, 0, 0) });
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

    // Enter, Tab, Ctrl+Space and F1 are handled by _inputAssist
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // An open completion window or template handles its own keys
        if (_inputAssist.IsBusy)
        {
            return;
        }

        switch (e.Key)
        {
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
            case Key.Escape when ErrorText.Visibility == Visibility.Visible:
                HideError();
                e.Handled = true;
                break;
        }
    }

    private async void OnEvaluateClick(object sender, RoutedEventArgs e)
    {
        await EvaluateAsync(InputEditor.Text, EvaluationSource.InputEditor);
        FocusInput();
    }

    private void ShowDocumentation(string word)
    {
        SideTabs.SelectedItem = DocsTab;
        DocSearchBox.Text = string.Empty;
        // A function's own page beats the language section that merely mentions it (e.g. `float`)
        DocEntry? entry = _docView?.OfType<DocEntry>()
            .Where(candidate => candidate.Descriptor.Names.Contains(word, StringComparer.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.IsLanguage)
            .FirstOrDefault();
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

    private void ShowError(EvaluationResult result, EvaluationSource source)
    {
        if (source == EvaluationSource.LibraryEditor)
        {
            LibraryErrorText.Text = result.Error;
            LibraryErrorText.Foreground = KalkPalette.Error;
            LibraryErrorText.Visibility = Visibility.Visible;
            return;
        }

        bool isFromInputEditor = source == EvaluationSource.InputEditor;
        ErrorText.Text = isFromInputEditor ? result.Error : $"{result.Input.Trim()}: {result.Error}";
        ErrorText.Foreground = KalkPalette.Error;
        ErrorText.Visibility = Visibility.Visible;

        TextDocument document = InputEditor.Document;
        if (isFromInputEditor && result.ErrorLine >= 0 && result.ErrorLine < document.LineCount && result.ErrorColumn >= 0)
        {
            DocumentLine line = document.GetLineByNumber(result.ErrorLine + 1);
            InputEditor.CaretOffset = line.Offset + Math.Min(result.ErrorColumn, line.Length);
        }
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Live parse result while typing: amber hint, or nothing once the text parses.</summary>
    private static void ShowSyntaxHint(TextBlock target, SyntaxProblem? problem)
    {
        if (problem == null)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }
        target.Text = problem.Message;
        target.Foreground = KalkPalette.Warning;
        target.Visibility = Visibility.Visible;
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
        await EvaluateAsync($"display {mode}", EvaluationSource.Command);
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
        ListCollectionView view = new ListCollectionView(entries) { Filter = MatchesDocSearch };
        _docView = view;
        DocList.ItemsSource = view;
        ApplyDocSearch(selectBestMatch: false);
        DocList.SelectedItem = entries.FirstOrDefault(entry => entry.Name == selectedName);
        if (DocList.SelectedItem == null)
        {
            DocDetails.Document = DocumentationRenderer.BuildHint();
        }
    }

    /// <summary>No query: grouped by category. With a query: one list ranked by <see cref="DocSearch"/>.</summary>
    private void ApplyDocSearch(bool selectBestMatch)
    {
        if (_docView == null)
        {
            return;
        }

        string query = DocSearchBox.Text.Trim();
        _docSearchScores = null;
        if (query.Length > 0)
        {
            _docSearchScores = new Dictionary<DocEntry, int>(ReferenceEqualityComparer.Instance);
            foreach (DocEntry entry in _docView.SourceCollection.OfType<DocEntry>())
            {
                if (DocSearch.Score(entry, query) is int score)
                {
                    _docSearchScores[entry] = score;
                }
            }
        }

        using (_docView.DeferRefresh())
        {
            _docView.GroupDescriptions.Clear();
            _docView.CustomSort = _docSearchScores == null ? null : new DocSearchComparer(_docSearchScores);
            if (_docSearchScores == null)
            {
                _docView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DocEntry.Group)));
            }
        }
        // The item template shows each result's category while searching
        DocList.Tag = _docSearchScores == null ? null : "searching";

        if (selectBestMatch && _docSearchScores != null && _docView.Count > 0)
        {
            DocList.SelectedIndex = 0;
            DocList.ScrollIntoView(DocList.SelectedItem);
        }
    }

    private bool MatchesDocSearch(object item)
    {
        return _docSearchScores == null || _docSearchScores.ContainsKey((DocEntry)item);
    }

    private void OnDocSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDocSearch(selectBestMatch: true);
    }

    /// <summary>Best score first; on a tie a function's own page before a language section, then shorter names.</summary>
    private sealed class DocSearchComparer : System.Collections.IComparer
    {
        private readonly Dictionary<DocEntry, int> _scores;

        public DocSearchComparer(Dictionary<DocEntry, int> scores)
        {
            _scores = scores;
        }

        public int Compare(object? x, object? y)
        {
            DocEntry left = (DocEntry)x!;
            DocEntry right = (DocEntry)y!;
            int order = _scores[left].CompareTo(_scores[right]);
            if (order == 0)
            {
                order = left.IsLanguage.CompareTo(right.IsLanguage);
            }
            if (order == 0)
            {
                order = left.Name.Length.CompareTo(right.Name.Length);
            }
            return order != 0 ? order : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }
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

        if (entry.IsLanguage)
        {
            // Its first keyword: as a template when it opens a block (func, if, for, while)
            string? keyword = entry.Descriptor.Names.Skip(1).FirstOrDefault();
            if (keyword != null && KalkSnippets.ForKeyword(keyword) is Snippet template)
            {
                FocusInput();
                _inputAssist.InsertTemplate(template);
            }
            else if (keyword != null)
            {
                InsertIntoInput(keyword);
            }
            return;
        }

        string text = entry.Signature.Contains('(') ? $"{entry.Name}(" : entry.Signature.Contains(' ') ? $"{entry.Name} " : entry.Name;
        InsertIntoInput(text);
    }

    // ---- Library tab ----

    private UserSymbol? SelectedSymbol => SymbolList.SelectedItem as UserSymbol;

    /// <summary>Reloads the list, keeping the edited entry selected and any unapplied edit of it.</summary>
    private void RefreshSymbols()
    {
        IReadOnlyList<UserSymbol> symbols = _session!.GetUserSymbols();
        UserSymbol? editedSymbol = symbols.FirstOrDefault(symbol => symbol.Name == _editedSymbolName);
        _isRefreshingSymbols = true;
        SymbolList.ItemsSource = symbols;
        SymbolList.SelectedItem = editedSymbol;
        _isRefreshingSymbols = false;
        if (!_isLibraryEditDirty)
        {
            LoadLibraryEditor(editedSymbol);
        }
    }

    private void OnSymbolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Picking another entry discards an unapplied edit, like a list-detail form
        if (!_isRefreshingSymbols)
        {
            LoadLibraryEditor(SelectedSymbol);
        }
    }

    private void LoadLibraryEditor(UserSymbol? symbol)
    {
        _editedSymbolName = symbol?.Name;
        _isLoadingLibraryEditor = true;
        LibraryEditor.Text = symbol?.Definition ?? string.Empty;
        _isLoadingLibraryEditor = false;
        _isLibraryEditDirty = false;
        _libraryAssist.ResetSyntaxCheck();
        // A broken entry shows why it did not load, so it can be fixed in place
        LibraryErrorText.Text = symbol?.LoadError ?? string.Empty;
        LibraryErrorText.Foreground = KalkPalette.Error;
        LibraryErrorText.Visibility = symbol?.IsBroken == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Evaluates the edited entry; a new name replaces the old entry.</summary>
    private async Task ApplyLibraryEditAsync()
    {
        EvaluationResult? result = await EvaluateAsync(LibraryEditor.Text, EvaluationSource.LibraryEditor, replacesSymbol: _editedSymbolName);
        if (result is { IsSuccess: true })
        {
            _editedSymbolName = result.DefinedNames.FirstOrDefault() ?? _editedSymbolName;
            _isLibraryEditDirty = false;
            RefreshSymbols();
        }
    }

    // Enter (apply), Tab, Ctrl+Space and F1 are handled by _libraryAssist
    private void OnLibraryEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc reverts an unapplied edit; with nothing to revert it bubbles up and returns to the input
        if (e.Key == Key.Escape && !_libraryAssist.IsBusy && _isLibraryEditDirty)
        {
            e.Handled = true;
            LoadLibraryEditor(SelectedSymbol);
        }
    }

    private async void OnLibraryApplyClick(object sender, RoutedEventArgs e)
    {
        await ApplyLibraryEditAsync();
    }

    private void OnLibraryRevertClick(object sender, RoutedEventArgs e)
    {
        LoadLibraryEditor(SelectedSymbol);
    }

    private void OnLibraryNewClick(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        AddNewEntryItem(menu, "Variable", KalkSnippets.Variable);
        AddNewEntryItem(menu, "One-line function", KalkSnippets.OneLineFunction);
        AddNewEntryItem(menu, "Multi-line function", KalkSnippets.MultiLineFunction);
        menu.Items.Add(new Separator());
        AddNewEntryItem(menu, "Empty", null);
        menu.IsOpen = true;
    }

    private void AddNewEntryItem(ContextMenu menu, string header, Func<Snippet>? createTemplate)
    {
        MenuItem item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            // Empty editor, no entry replaced: Apply adds whatever it defines
            SymbolList.SelectedItem = null;
            LoadLibraryEditor(null);
            if (IsActive)
            {
                LibraryEditor.TextArea.Focus();
            }
            if (createTemplate != null)
            {
                _libraryAssist.InsertTemplate(createTemplate());
            }
        };
        menu.Items.Add(item);
    }

    private void OnOpenLibraryClick(object sender, RoutedEventArgs e)
    {
        if (_session?.LibraryFilePath is string path)
        {
            RevealInExplorer(path);
        }
    }

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
        if (SelectedSymbol is not UserSymbol symbol)
        {
            return;
        }

        if (symbol.IsBroken)
        {
            // Not a kalk symbol: only the library file holds it
            if (_session?.DiscardBrokenEntry(symbol.Name) == true)
            {
                RefreshPanels();
            }
            return;
        }
        await EvaluateAsync($"del {symbol.Name}", EvaluationSource.Command);
    }

    private async void OnSymbolResetClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete every variable and function, including the whole library?", "KalkGui", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
        {
            await EvaluateAsync("reset", EvaluationSource.Command);
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
        RevealInExplorer(_session!.ConfigFilePath);
    }

    private static void RevealInExplorer(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
