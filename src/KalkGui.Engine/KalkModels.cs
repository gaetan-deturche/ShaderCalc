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

/// <summary>A documented function, constant or module; <see cref="IsAvailable"/> is false until its module is imported.</summary>
public sealed record DocEntry(string Name, string Signature, string Group, string Summary, string? ModuleName, bool IsAvailable, bool IsModule, KalkDescriptor Descriptor);

/// <summary>
/// A user variable or function; <see cref="IsInLibrary"/> is false for config.kalk definitions. A library entry
/// that failed to load has its raw text as Name and Definition, and the error in <see cref="LoadError"/>.
/// </summary>
public sealed record UserSymbol(string Name, bool IsFunction, string Definition, bool IsInLibrary, string? LoadError = null)
{
    public bool IsBroken => LoadError != null;
}

public sealed record LibraryLoadError(string Entry, string Message);

public sealed record ModuleInfo(string Name, bool IsImported);
