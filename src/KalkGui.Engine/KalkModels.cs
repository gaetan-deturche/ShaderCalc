using Kalk.Core;

namespace KalkGui.Engine;

/// <summary>Outcome of one evaluation. Error positions are 0-based; -1 when unknown.</summary>
public sealed record EvaluationResult(string Input, string Output, string? Error, int ErrorLine, int ErrorColumn, bool IsCancelled, bool HasExit)
{
    public bool IsSuccess => Error == null;
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

public sealed record UserSymbol(string Name, bool IsFunction, string Definition);

public sealed record ModuleInfo(string Name, bool IsImported);
