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

    private readonly KalkEngine _engine;
    private readonly object _engineLock = new object();
    private CancellationTokenSource? _runningEvaluation;

    /// <param name="kalkUserFolder">Overrides ~/.kalk (where config.kalk lives).</param>
    public KalkSession(Func<string> getClipboardText, Action<string> setClipboardText, Action onClearScreen, bool loadUserConfig = true, string? kalkUserFolder = null)
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
    }

    public string Version => _engine.Version;

    /// <summary>Whatever loading config.kalk printed (typically errors in it).</summary>
    public string StartupOutput { get; }

    public string ConfigFilePath => Path.Combine(_engine.KalkUserFolder, "config.kalk");

    public KalkDisplayMode DisplayMode => _engine.CurrentDisplay;

    public bool IsEvaluating => _runningEvaluation != null;

    public async Task<EvaluationResult> EvaluateAsync(string input)
    {
        if (_runningEvaluation != null)
        {
            throw new InvalidOperationException("An evaluation is already running.");
        }

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        _runningEvaluation = cancellation;
        try
        {
            return await Task.Run(() => Evaluate(input, cancellation.Token));
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
            .Select(pair => new UserSymbol(pair.Key, pair.Value is ScriptFunction { IsAnonymous: false }, DescribeUserSymbol(pair.Key, pair.Value)))
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

    private EvaluationResult Evaluate(string input, CancellationToken cancellationToken)
    {
        lock (_engineLock)
        {
            StringWriter output = new StringWriter();
            _engine.OutputWriter = output;
            _engine.ErrorWriter = output;
            _engine.CancellationToken = cancellationToken;
            try
            {
                Template template = _engine.Parse(input);
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
            finally
            {
                _engine.CancellationToken = CancellationToken.None;
            }
        }
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
            ? function.ToString() ?? name
            : $"{name} = {_engine.ObjectToString(value, true)}";
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
