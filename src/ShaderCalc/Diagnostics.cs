namespace ShaderCalc;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A position in a source text: 1-based line and column, plus the length of the marked span.</summary>
public readonly record struct SourceSpan(int Offset, int Length, int Line, int Column)
{
    public static readonly SourceSpan None = new SourceSpan(0, 0, 0, 0);

    public int End => Offset + Length;

    /// <summary>From the start of this span to the end of another one (same source).</summary>
    public SourceSpan To(SourceSpan end) => end.End <= Offset ? this : this with { Length = end.End - Offset };
}

/// <summary>
/// A problem found while compiling or evaluating. <see cref="Source"/> is the text it refers to ("program" for the
/// pasted code, "input" for a calculator line); <see cref="Function"/> is the function being evaluated, if any.
/// </summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, string Source, SourceSpan Span, string? Function = null)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString()
    {
        string where = Function == null ? Source : $"{Source}:{Function}";
        return $"{Severity.ToString().ToLowerInvariant()} {where}({Span.Line},{Span.Column}): {Message}";
    }
}

/// <summary>Collects diagnostics, dropping exact duplicates (a loop reports the same unit error once).</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
    private readonly HashSet<Diagnostic> _seen = new HashSet<Diagnostic>();

    public IReadOnlyList<Diagnostic> Items => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.IsError);

    public void Add(Diagnostic diagnostic)
    {
        if (_seen.Add(diagnostic))
        {
            _diagnostics.Add(diagnostic);
        }
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }
}
