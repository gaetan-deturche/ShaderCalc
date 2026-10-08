using Kalk.Core;

namespace KalkGui.Engine;

/// <summary>Outcome of one evaluation. Error positions are 0-based; -1 when unknown.</summary>
public sealed record EvaluationResult(string Input, string Output, string? Error, int ErrorLine, int ErrorColumn, bool IsCancelled, bool HasExit)
{
    public bool IsSuccess => Error == null;

    /// <summary>Variables and functions (re)defined by this evaluation.</summary>
    public IReadOnlyList<string> DefinedNames { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal problem, e.g. the library file could not be written.</summary>
    public string? Warning { get; init; }
}

public enum CompletionKind
{
    UserSymbol,
    Builtin,
    Module,
    Keyword,
    Unit,
}

public sealed record CompletionItem(string Name, CompletionKind Kind, string? Description);

/// <summary>
/// A documented function, constant, module or language section; <see cref="IsAvailable"/> is false until its module
/// is imported. Language sections carry their markdown in Descriptor.Description and their keywords in its Names.
/// </summary>
public sealed record DocEntry(string Name, string Signature, string Group, string Summary, string? ModuleName, bool IsAvailable, bool IsModule,
    KalkDescriptor Descriptor, bool IsLanguage = false, string? Source = null);

/// <summary>
/// A user variable or function; <see cref="IsInLibrary"/> is false for config.kalk definitions. A library entry
/// that failed to load has its raw text as Name and Definition, and the error in <see cref="LoadError"/>.
/// </summary>
public sealed record UserSymbol(string Name, bool IsFunction, string Definition, bool IsInLibrary, string? LoadError = null)
{
    public bool IsBroken => LoadError != null;

    /// <summary>One line for lists: a multi-line definition shows its first line.</summary>
    public string Summary => Definition.Contains('\n') ? $"{Definition.ReplaceLineEndings("\n").Split('\n')[0]} …" : Definition;
}

public sealed record LibraryLoadError(string Entry, string Message);

/// <summary>First parse error of a text; <see cref="Offset"/> is clamped inside the text.</summary>
public sealed record SyntaxProblem(string Message, int Offset);

/// <summary>What applying an outside edit of library.kalk changed in the session.</summary>
public sealed record LibraryReloadResult(IReadOnlyList<string> UpdatedNames, IReadOnlyList<string> RemovedNames, IReadOnlyList<LibraryLoadError> Errors);

public sealed record ModuleInfo(string Name, bool IsImported);
