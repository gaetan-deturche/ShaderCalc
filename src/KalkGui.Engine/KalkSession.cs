using System.Text;
using Consolus;
using Kalk.Core;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace KalkGui.Engine;

/// <summary>
/// One kalk engine hosted without a console. Evaluations run on a worker thread; every other
/// member is meant for the UI thread and never waits on a running evaluation.
/// </summary>
public sealed class KalkSession
{
    // Mirrors KalkEngine's private ScriptKeywords + ValueKeywords (completion only)
    private static readonly string[] Keywords =
    {
        "if", "else", "end", "for", "in", "case", "when", "while", "break", "continue", "func", "import",
        "readonly", "with", "capture", "ret", "wrap", "do", "null", "true", "false",
    };

    private const string LibraryFileName = "library.kalk";

    private readonly KalkEngine _engine;
    private readonly object _engineLock = new object();
    private readonly string? _libraryFilePath;
    // Symbols and modules owned by the library; config.kalk definitions stay out unless redefined
    private readonly HashSet<string> _librarySymbols = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _libraryModules = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<LibraryLoadError> _libraryLoadErrors = new List<LibraryLoadError>();
    // What KalkGui last wrote to or read from library.kalk: a file holding anything else was edited outside
    private string? _lastLibraryText;
    private CancellationTokenSource? _runningEvaluation;

    /// <param name="kalkUserFolder">Overrides ~/.kalk (where config.kalk and library.kalk live).</param>
    /// <param name="useLibrary">Load library.kalk at start and save every definition to it.</param>
    public KalkSession(Func<string> getClipboardText, Action<string> setClipboardText, Action onClearScreen, bool loadUserConfig = true, string? kalkUserFolder = null, bool useLibrary = true)
    {
        // Same test as KalkEngine.Run(): with an attached interactive console it would enter its blocking REPL
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected && ConsoleHelper.HasInteractiveConsole)
        {
            throw new InvalidOperationException("kalk cannot be hosted while an interactive console is attached to the process.");
        }

        StringWriter startupOutput = new StringWriter();
        _engine = new KalkEngine
        {
            InputReader = new StringReader(string.Empty),
            OutputWriter = startupOutput,
            ErrorWriter = startupOutput,
            DisplayVersion = false,
            DisableUserConfig = !loadUserConfig,
            // kalk's own ANSI renderer pads lines to Console.BufferWidth, which throws without a console
            IsOutputSupportHighlighting = false,
            GetClipboardText = getClipboardText,
            SetClipboardText = setClipboardText,
            OnClearScreen = onClearScreen,
        };
        if (kalkUserFolder != null)
        {
            _engine.KalkUserFolder = kalkUserFolder;
        }

        // Non-interactive run: loads ~/.kalk/config.kalk, reads the empty input, returns
        _engine.Run();
        StartupOutput = startupOutput.ToString();

        if (useLibrary)
        {
            _libraryFilePath = Path.Combine(_engine.KalkUserFolder, LibraryFileName);
            LoadLibrary();
        }
    }

    public string Version => _engine.Version;

    /// <summary>Whatever loading config.kalk printed (typically errors in it).</summary>
    public string StartupOutput { get; }

    public string ConfigFilePath => Path.Combine(_engine.KalkUserFolder, "config.kalk");

    /// <summary>The persistent library, or null when the session runs without one.</summary>
    public string? LibraryFilePath => _libraryFilePath;

    /// <summary>Library entries that don't load; they stay in the file until fixed (replacesSymbol) or discarded.</summary>
    public IReadOnlyList<LibraryLoadError> LibraryLoadErrors => _libraryLoadErrors;

    /// <summary>Drops a library entry that failed to load. False while an evaluation runs.</summary>
    public bool DiscardBrokenEntry(string entry)
    {
        return ReadEngine(() =>
        {
            if (_libraryLoadErrors.RemoveAll(error => error.Entry == entry) > 0)
            {
                TrySaveLibrary();
            }
            return true;
        }, false);
    }

    public KalkDisplayMode DisplayMode => _engine.CurrentDisplay;

    public bool IsEvaluating => _runningEvaluation != null;

    /// <param name="replacesSymbol">Library entry being edited: if the input defines another name, this one is deleted.</param>
    public async Task<EvaluationResult> EvaluateAsync(string input, string? replacesSymbol = null)
    {
        if (_runningEvaluation != null)
        {
            throw new InvalidOperationException("An evaluation is already running.");
        }

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        _runningEvaluation = cancellation;
        try
        {
            return await Task.Run(() => Evaluate(input, replacesSymbol, cancellation.Token));
        }
        finally
        {
            _runningEvaluation = null;
        }
    }

    public void CancelEvaluation()
    {
        _runningEvaluation?.Cancel();
    }

    /// <summary>kalk's own syntax colouring of <paramref name="text"/>; <paramref name="caretIndex"/> enables brace matching.</summary>
    public IReadOnlyList<StyledSpan> Highlight(string text, int caretIndex = -1)
    {
        if (text.Length == 0)
        {
            return Array.Empty<StyledSpan>();
        }

        return ReadEngine<IReadOnlyList<StyledSpan>>(() =>
        {
            ConsoleText consoleText = new ConsoleText();
            consoleText.Append(text);
            try
            {
                _engine.Highlight(consoleText, caretIndex);
            }
            catch (Exception)
            {
                // Half-typed input can trip the lexer or brace matching: it only costs the colours
                return Array.Empty<StyledSpan>();
            }

            return KalkStyleConverter.ToSpans(consoleText);
        }, Array.Empty<StyledSpan>());
    }

    /// <summary>Parse-only check (nothing is evaluated); null when the text parses.</summary>
    public SyntaxProblem? CheckSyntax(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return ReadEngine<SyntaxProblem?>(() =>
        {
            Template template = _engine.Parse(text, recordHistory: false);
            if (!template.HasErrors)
            {
                return null;
            }

            LogMessage error = template.Messages.FirstOrDefault(message => message.Type == ParserMessageType.Error) ?? template.Messages[0];
            int offset = error.Span.Start.Offset;
            // Errors at the end of the input (missing operand, missing `end`) point past the text
            if (offset < 0 || offset >= text.Length)
            {
                offset = Math.Max(0, text.TrimEnd().Length - 1);
            }
            return new SyntaxProblem(error.Message, offset);
        }, null);
    }

    public IReadOnlyList<CompletionItem> GetCompletions(string prefix)
    {
        return ReadEngine<IReadOnlyList<CompletionItem>>(() =>
        {
            List<CompletionItem> items = new List<CompletionItem>();
            HashSet<string> addedNames = new HashSet<string>(StringComparer.Ordinal);

            void AddMatching(IEnumerable<string> names, Func<string, CompletionItem> createItem)
            {
                foreach (string name in names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (addedNames.Add(name))
                    {
                        items.Add(createItem(name));
                    }
                }
            }

            AddMatching(_engine.Variables.Keys, name => new CompletionItem(name, CompletionKind.UserSymbol, DescribeUserSymbol(name, _engine.Variables[name])));
            AddMatching(_engine.Builtins.Keys, name => _engine.Builtins[name] is KalkModule
                ? new CompletionItem(name, CompletionKind.Module, $"Module: import {name}")
                : new CompletionItem(name, CompletionKind.Builtin, DescribeBuiltin(name)));
            AddMatching(Keywords, name => new CompletionItem(name, CompletionKind.Keyword, "keyword"));
            AddMatching(_engine.Units.Keys, name => new CompletionItem(name, CompletionKind.Unit, DescribeUnit(_engine.Units[name] as KalkUnit)));
            return items;
        }, Array.Empty<CompletionItem>());
    }

    /// <summary>Every documented symbol, including functions of modules not imported yet.</summary>
    public IReadOnlyList<DocEntry> GetDocumentation()
    {
        return ReadEngine<IReadOnlyList<DocEntry>>(() =>
        {
            List<DocEntry> entries = new List<DocEntry>();
            HashSet<KalkDescriptor> seenDescriptors = new HashSet<KalkDescriptor>();
            List<KalkModule> modules = GetModuleObjects();

            foreach (KalkModule module in modules)
            {
                entries.Add(CreateDocEntry(module.Descriptor, module.Name, true, isModule: true));
                seenDescriptors.Add(module.Descriptor);
                foreach (KalkDescriptor descriptor in module.Descriptors.Values)
                {
                    if (seenDescriptors.Add(descriptor))
                    {
                        entries.Add(CreateDocEntry(descriptor, module.Name, module.IsImported, isModule: false));
                    }
                }
            }

            foreach (KalkDescriptor descriptor in _engine.Descriptors.Values)
            {
                if (seenDescriptors.Add(descriptor))
                {
                    entries.Add(CreateDocEntry(descriptor, null, true, isModule: false));
                }
            }

            entries.Sort((left, right) =>
            {
                int groupOrder = string.Compare(left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
                return groupOrder != 0 ? groupOrder : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }, Array.Empty<DocEntry>());
    }

    public IReadOnlyList<UserSymbol> GetUserSymbols()
    {
        return ReadEngine<IReadOnlyList<UserSymbol>>(() => _engine.Variables
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UserSymbol(pair.Key, pair.Value is ScriptFunction { IsAnonymous: false }, DescribeUserSymbol(pair.Key, pair.Value), _librarySymbols.Contains(pair.Key)))
            .Concat(_libraryLoadErrors.Select(error => new UserSymbol(error.Entry, false, error.Entry, true, error.Message)))
            .ToList(), Array.Empty<UserSymbol>());
    }

    public IReadOnlyList<ModuleInfo> GetModules()
    {
        return ReadEngine<IReadOnlyList<ModuleInfo>>(() => GetModuleObjects()
            .Select(module => new ModuleInfo(module.Name, module.IsImported))
            .ToList(), Array.Empty<ModuleInfo>());
    }

    /// <summary>Same layout as the header of kalk's `help` output.</summary>
    public static string FormatSignature(KalkDescriptor descriptor, string name)
    {
        string arguments = string.Join(", ", descriptor.Params.Select(parameter => parameter.IsOptional ? $"{parameter.Name}?" : parameter.Name));
        if (arguments.Length == 0)
        {
            return name;
        }

        bool isParentless = descriptor.IsCommand && descriptor.Params.Count <= 1;
        return isParentless ? $"{name} {arguments}" : $"{name}({arguments})";
    }

    private EvaluationResult Evaluate(string input, string? replacesSymbol, CancellationToken cancellationToken)
    {
        lock (_engineLock)
        {
            StringWriter output = new StringWriter();
            _engine.OutputWriter = output;
            _engine.ErrorWriter = output;
            _engine.CancellationToken = cancellationToken;
            Dictionary<string, object> variablesBefore = SnapshotVariables();
            HashSet<string> modulesBefore = GetImportedModuleNames();
            EvaluationResult result;
            try
            {
                result = EvaluateCore(input, output, recordHistory: true);
            }
            finally
            {
                _engine.CancellationToken = CancellationToken.None;
            }

            List<string> definedNames = TrackLibraryChanges(variablesBefore, modulesBefore, out bool isLibraryChanged);
            if (result.IsSuccess && replacesSymbol != null)
            {
                // A fixed broken entry (identified by its raw text) or an entry edited under a new name
                isLibraryChanged |= _libraryLoadErrors.RemoveAll(error => error.Entry == replacesSymbol) > 0;
                if (!definedNames.Contains(replacesSymbol) && _engine.Variables.ContainsKey(replacesSymbol))
                {
                    _engine.Variables.Remove(replacesSymbol);
                    isLibraryChanged |= _librarySymbols.Remove(replacesSymbol);
                }
            }

            string? warning = isLibraryChanged ? TrySaveLibrary() : null;
            return result with { DefinedNames = definedNames, Warning = warning };
        }
    }

    private EvaluationResult EvaluateCore(string input, StringWriter output, bool recordHistory)
    {
        try
        {
            Template template = _engine.Parse(input, recordHistory: recordHistory);
            if (template.HasErrors)
            {
                LogMessage error = template.Messages.FirstOrDefault(message => message.Type == ParserMessageType.Error) ?? template.Messages[0];
                return Failure(input, output, error.Message, error.Span.Start);
            }

            // Statements echo themselves (`# input`, `out = ...`) into OutputWriter
            _engine.EvaluatePage(template.Page);
            return new EvaluationResult(input, output.ToString(), null, -1, -1, false, _engine.HasExit);
        }
        catch (ScriptAbortException)
        {
            return new EvaluationResult(input, output.ToString(), "Evaluation cancelled.", -1, -1, true, false);
        }
        catch (ScriptRuntimeException exception)
        {
            return Failure(input, output, exception.OriginalMessage, exception.Span.Start);
        }
        catch (Exception exception)
        {
            return Failure(input, output, exception.Message, new TextPosition(-1, -1, -1));
        }
    }

    // ---- Library ----

    private void LoadLibrary()
    {
        if (_libraryFilePath == null || !File.Exists(_libraryFilePath))
        {
            return;
        }

        _lastLibraryText = File.ReadAllText(_libraryFilePath);
        StringWriter discardedOutput = new StringWriter();
        _engine.OutputWriter = discardedOutput;
        _engine.ErrorWriter = discardedOutput;
        // One entry at a time: a broken entry must not take the others down
        foreach (string entry in SplitLibraryEntries(_lastLibraryText))
        {
            EvaluationResult result = EvaluateLibraryEntry(entry, discardedOutput, out _);
            if (!result.IsSuccess)
            {
                _libraryLoadErrors.Add(new LibraryLoadError(entry, result.Error!));
            }
        }
    }

    /// <summary>
    /// Applies library.kalk as edited outside KalkGui: changed entries are re-evaluated, deleted ones leave the
    /// session. Null when the file holds what KalkGui itself last wrote or read.
    /// </summary>
    public Task<LibraryReloadResult?> ReloadLibraryAsync()
    {
        return Task.Run(() =>
        {
            lock (_engineLock)
            {
                return ReloadLibrary();
            }
        });
    }

    private LibraryReloadResult? ReloadLibrary()
    {
        if (_libraryFilePath == null)
        {
            return null;
        }

        string text;
        try
        {
            text = File.Exists(_libraryFilePath) ? File.ReadAllText(_libraryFilePath) : string.Empty;
        }
        catch (IOException)
        {
            // Still being written: the editor's next change notification retries
            return null;
        }
        if (text == _lastLibraryText)
        {
            return null;
        }
        _lastLibraryText = text;

        StringWriter discardedOutput = new StringWriter();
        _engine.OutputWriter = discardedOutput;
        _engine.ErrorWriter = discardedOutput;
        Dictionary<string, string> currentDefinitions = _librarySymbols
            .Where(_engine.Variables.ContainsKey)
            .ToDictionary(name => name, name => DescribeUserSymbol(name, _engine.Variables[name]), StringComparer.Ordinal);
        HashSet<string> namesInFile = new HashSet<string>(StringComparer.Ordinal);
        List<string> updatedNames = new List<string>();
        List<LibraryLoadError> errors = new List<LibraryLoadError>();
        HashSet<string> modulesInFile = new HashSet<string>(StringComparer.Ordinal);

        foreach (string entry in SplitLibraryEntries(text))
        {
            if (entry.StartsWith("import ", StringComparison.Ordinal))
            {
                modulesInFile.Add(entry["import ".Length..].Trim());
            }

            // Entries KalkGui wrote itself match the current definition exactly: no need to re-run them
            string? unchangedName = currentDefinitions.FirstOrDefault(pair => pair.Value.ReplaceLineEndings() == entry.ReplaceLineEndings()).Key;
            if (unchangedName != null)
            {
                namesInFile.Add(unchangedName);
                continue;
            }

            EvaluationResult result = EvaluateLibraryEntry(entry, discardedOutput, out List<string> definedNames);
            namesInFile.UnionWith(definedNames);
            if (result.IsSuccess)
            {
                updatedNames.AddRange(definedNames);
            }
            else
            {
                errors.Add(new LibraryLoadError(entry, result.Error!));
            }
        }

        List<string> removedNames = _librarySymbols.Where(name => !namesInFile.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        foreach (string name in removedNames)
        {
            _engine.Variables.Remove(name);
            _librarySymbols.Remove(name);
        }

        // kalk cannot unload a module: dropping an import only stops recording it
        _libraryModules.Clear();
        _libraryModules.UnionWith(modulesInFile);
        _libraryLoadErrors.Clear();
        _libraryLoadErrors.AddRange(errors);
        return new LibraryReloadResult(updatedNames, removedNames, errors);
    }

    private EvaluationResult EvaluateLibraryEntry(string entry, StringWriter output, out List<string> definedNames)
    {
        if (entry.StartsWith("import ", StringComparison.Ordinal))
        {
            // Kept even when config.kalk already imports it
            _libraryModules.Add(entry["import ".Length..].Trim());
        }

        Dictionary<string, object> variablesBefore = SnapshotVariables();
        HashSet<string> modulesBefore = GetImportedModuleNames();
        EvaluationResult result = EvaluateCore(entry, output, recordHistory: false);
        definedNames = TrackLibraryChanges(variablesBefore, modulesBefore, out _);
        return result;
    }

    /// <summary>Top-level statements of the library file (multi-line definitions stay whole).</summary>
    private List<string> SplitLibraryEntries(string text)
    {
        Template template = _engine.Parse(text, recordHistory: false);
        IEnumerable<string> entries = !template.HasErrors && template.Page?.Body != null
            ? template.Page.Body.Statements.Select(statement => text.Substring(statement.Span.Start.Offset, statement.Span.End.Offset - statement.Span.Start.Offset + 1))
            // Unparsable file (hand edit gone wrong): fall back to one entry per line
            : text.Split('\n');
        return entries
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0 && !entry.StartsWith('#'))
            .ToList();
    }

    /// <summary>Updates the library sets from what an evaluation changed; returns the (re)defined names.</summary>
    private List<string> TrackLibraryChanges(Dictionary<string, object> variablesBefore, HashSet<string> modulesBefore, out bool isLibraryChanged)
    {
        isLibraryChanged = false;
        List<string> definedNames = new List<string>();
        foreach (KeyValuePair<string, object> pair in _engine.Variables)
        {
            // Assignments always store a new object, so a reference change means (re)defined
            if (!variablesBefore.TryGetValue(pair.Key, out object? previousValue) || !ReferenceEquals(previousValue, pair.Value))
            {
                definedNames.Add(pair.Key);
                _librarySymbols.Add(pair.Key);
                isLibraryChanged = true;
            }
        }

        foreach (string name in variablesBefore.Keys.Where(name => !_engine.Variables.ContainsKey(name)))
        {
            isLibraryChanged |= _librarySymbols.Remove(name);
        }

        foreach (string module in GetImportedModuleNames().Where(module => !modulesBefore.Contains(module)))
        {
            isLibraryChanged |= _libraryModules.Add(module);
        }

        return definedNames;
    }

    private string? TrySaveLibrary()
    {
        if (_libraryFilePath == null)
        {
            return null;
        }

        try
        {
            SaveLibrary(_libraryFilePath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Library not saved: {exception.Message}";
        }
    }

    private void SaveLibrary(string path)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine("# KalkGui library: every function and variable you define, reloaded on each start.");
        text.AppendLine("# Rewritten by KalkGui on every change (comments are not kept). Edit entries from the Library tab.");

        // `import All` already covers every other module
        IEnumerable<string> modules = _libraryModules.Contains("All") ? new[] { "All" } : _libraryModules.OrderBy(module => module, StringComparer.Ordinal);
        foreach (string module in modules)
        {
            text.AppendLine($"import {module}");
        }

        foreach (string name in _librarySymbols.Where(_engine.Variables.ContainsKey).OrderBy(name => name, StringComparer.Ordinal))
        {
            text.AppendLine(DescribeUserSymbol(name, _engine.Variables[name]).ReplaceLineEndings());
        }

        // Kept verbatim so nothing is lost; retried on every load
        foreach (LibraryLoadError error in _libraryLoadErrors)
        {
            text.AppendLine($"# Not loaded: {error.Message.ReplaceLineEndings(" ")}");
            text.AppendLine(error.Entry);
        }

        // Write-then-move so a crash never leaves a truncated library
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, text.ToString(), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
        _lastLibraryText = text.ToString();
    }

    private Dictionary<string, object> SnapshotVariables()
    {
        return _engine.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private HashSet<string> GetImportedModuleNames()
    {
        return GetModuleObjects().Where(module => module.IsImported).Select(module => module.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static EvaluationResult Failure(string input, StringWriter output, string error, TextPosition position)
    {
        return new EvaluationResult(input, output.ToString(), error, position.Line, position.Column, false, false);
    }

    private T ReadEngine<T>(Func<T> read, T fallbackWhileEvaluating)
    {
        // UI-thread reads must never block behind a running evaluation
        if (!Monitor.TryEnter(_engineLock))
        {
            return fallbackWhileEvaluating;
        }

        try
        {
            return read();
        }
        finally
        {
            Monitor.Exit(_engineLock);
        }
    }

    private List<KalkModule> GetModuleObjects()
    {
        return _engine.Builtins.Values.OfType<KalkModule>().OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string DescribeUserSymbol(string name, object? value)
    {
        return value is ScriptFunction { IsAnonymous: false } function
            ? IndentBlocks(function.ToString() ?? name)
            : $"{name} = {_engine.ObjectToString(value, true)}";
    }

    /// <summary>kalk prints `func ... end` bodies flush left: indent them by block depth (4 spaces).</summary>
    private static string IndentBlocks(string text)
    {
        if (!text.Contains('\n'))
        {
            return text;
        }

        StringBuilder indented = new StringBuilder();
        int depth = 0;
        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();
            string firstWord = line.Split(' ', 2)[0];
            if (firstWord is "end" or "else" or "when")
            {
                depth = Math.Max(0, depth - 1);
            }
            if (indented.Length > 0)
            {
                indented.Append('\n');
            }
            indented.Append(' ', depth * 4).Append(line);
            if (firstWord is "func" or "if" or "for" or "while" or "case" or "else" or "when" && !line.EndsWith(" end", StringComparison.Ordinal))
            {
                depth++;
            }
        }
        return indented.ToString();
    }

    private string? DescribeBuiltin(string name)
    {
        return _engine.Descriptors.TryGetValue(name, out KalkDescriptor? descriptor) ? Summarize(descriptor.Description) : null;
    }

    private static string? DescribeUnit(KalkUnit? unit)
    {
        if (unit == null)
        {
            return null;
        }

        string symbol = string.IsNullOrEmpty(unit.Symbol) ? unit.Name : $"{unit.Name} ({unit.Symbol})";
        return string.IsNullOrEmpty(unit.Description) ? $"Unit: {symbol}" : $"Unit: {symbol} - {unit.Description}";
    }

    private static DocEntry CreateDocEntry(KalkDescriptor descriptor, string? moduleName, bool isAvailable, bool isModule)
    {
        string name = descriptor.Names.Count > 0 ? descriptor.Names[0] : "?";
        string category = isModule ? "Modules" : string.IsNullOrEmpty(descriptor.Category) ? "General" : descriptor.Category;
        string group = isAvailable ? category : $"{category}  (import {moduleName})";
        string signature = isModule ? $"import {name}" : FormatSignature(descriptor, name);
        return new DocEntry(name, signature, group, Summarize(descriptor.Description), moduleName, isAvailable, isModule, descriptor);
    }

    private static string Summarize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        string collapsed = string.Join(" ", description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        int sentenceEnd = collapsed.IndexOf(". ", StringComparison.Ordinal);
        return sentenceEnd > 0 ? collapsed[..(sentenceEnd + 1)] : collapsed;
    }
}
