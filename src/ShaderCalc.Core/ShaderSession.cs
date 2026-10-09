using ShaderCalc.Binding;
using ShaderCalc.Evaluation;
using ShaderCalc.Syntax;
using ShaderCalc.Units;

namespace ShaderCalc;

/// <summary>The outcome of one calculator line.</summary>
public sealed record LineResult(Value? Value, IReadOnlyList<Diagnostic> Diagnostics, BoundInteractive? Line, string? Message = null)
{
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.IsError);

    /// <summary>The program the line ran against.</summary>
    public BoundProgram? Program { get; init; }

    /// <summary>Calculator variables and uniforms as the line found them (what a reference run must start from).</summary>
    public IReadOnlyDictionary<VariableSymbol, Value> Inputs { get; init; } = new Dictionary<VariableSymbol, Value>();

    public override string ToString() => Value?.ToString() ?? Message ?? string.Empty;
}

/// <summary>
/// A calculator session: pasted HLSL / C++ code (the program) plus calculator lines evaluated against it. Lines can
/// declare variables (kept across lines), set uniforms, and define functions or structs.
/// </summary>
public sealed class ShaderSession
{
    public const string ProgramSourceName = "program";
    public const string LineSourceName = "input";

    private readonly Dictionary<string, VariableSymbol> _variables = new Dictionary<string, VariableSymbol>(StringComparer.Ordinal);
    private readonly Dictionary<VariableSymbol, Value> _variableValues = new Dictionary<VariableSymbol, Value>();
    private readonly Dictionary<string, Value> _uniformValues = new Dictionary<string, Value>(StringComparer.Ordinal);
    private readonly List<DeclarationSyntax> _lineDeclarations = new List<DeclarationSyntax>();
    private IReadOnlyList<DeclarationSyntax> _programDeclarations = Array.Empty<DeclarationSyntax>();
    private IReadOnlyList<Diagnostic> _parseDiagnostics = Array.Empty<Diagnostic>();

    public ShaderSession(SemanticsProfile? profile = null)
    {
        Profile = profile ?? SemanticsProfile.Hlsl;
    }

    public SemanticsProfile Profile { get; }

    public EvaluationOptions Options { get; set; } = new EvaluationOptions();

    public string ProgramSource { get; private set; } = string.Empty;

    public BoundProgram Program { get; private set; } = BoundProgram.Empty;

    /// <summary>Calculator variables by name.</summary>
    public IReadOnlyDictionary<string, VariableSymbol> Variables => _variables;

    public Value? GetVariableValue(string name) =>
        _variables.TryGetValue(name, out VariableSymbol? variable) && _variableValues.TryGetValue(variable, out Value? value) ? value : null;

    /// <summary>Replaces the pasted code. Returns its diagnostics (the session keeps working with what bound).</summary>
    public IReadOnlyList<Diagnostic> SetProgram(string source)
    {
        ProgramSource = source;
        DiagnosticBag diagnostics = new DiagnosticBag();
        CompilationUnitSyntax unit = Parser.ParseProgram(source, ProgramSourceName, diagnostics);
        _programDeclarations = unit.Declarations;
        _parseDiagnostics = diagnostics.Items.ToList();
        Rebind();
        return Program.Diagnostics;
    }

    public void ResetVariables()
    {
        _variables.Clear();
        _variableValues.Clear();
        _uniformValues.Clear();
        _lineDeclarations.Clear();
        Rebind();
    }

    private void Rebind()
    {
        CompilationUnitSyntax unit = new CompilationUnitSyntax(_programDeclarations.Concat(_lineDeclarations).ToList());
        Program = Binder.BindProgram(unit, ProgramSourceName, Profile, _parseDiagnostics);
    }

    /// <summary>Names a calculator literal must not read as a unit (`2 h` is hours unless h is a variable).</summary>
    private bool IsUnitName(string name) =>
        UnitTable.Units.ContainsKey(name)
        && !_variables.ContainsKey(name)
        && Program.Globals.All(global => global.Name != name)
        && Program.Functions.All(function => function.Name != name)
        && !Program.TypeNames.ContainsKey(name);

    public LineResult Evaluate(string line)
    {
        DiagnosticBag diagnostics = new DiagnosticBag();
        List<SyntaxNode> items = Parser.ParseInteractive(line, LineSourceName, diagnostics, Program.TypeNames.Keys, IsUnitName);
        if (diagnostics.HasErrors)
        {
            return new LineResult(null, diagnostics.Items, null);
        }

        // Functions, structs and typedefs typed in a line join the program
        List<DeclarationSyntax> declarations = items.OfType<DeclarationSyntax>().ToList();
        if (declarations.Count > 0)
        {
            List<DeclarationSyntax> previous = _lineDeclarations.ToList();
            _lineDeclarations.AddRange(declarations);
            Rebind();
            List<Diagnostic> newProblems = Program.Diagnostics.Where(diagnostic => diagnostic.IsError).ToList();
            if (newProblems.Count > 0 && declarations.Any(declaration => newProblems.Any(problem => problem.Span.Offset >= declaration.Span.Offset)))
            {
                _lineDeclarations.Clear();
                _lineDeclarations.AddRange(previous);
                Rebind();
                return new LineResult(null, newProblems, null);
            }
        }

        List<StatementSyntax> statements = items.OfType<StatementSyntax>().ToList();
        if (statements.Count == 0)
        {
            string names = string.Join(", ", declarations.Select(declaration => declaration switch
            {
                FunctionSyntax function => function.Name,
                StructSyntax structure => structure.Name,
                TypedefSyntax typedef => typedef.Name,
                _ => "declaration",
            }));
            return new LineResult(null, diagnostics.Items, null, $"defined {names}");
        }

        (BoundInteractive bound, IReadOnlyList<Diagnostic> bindDiagnostics) = Binder.BindInteractive(statements, Program, _variables, LineSourceName, Profile);
        diagnostics.AddRange(bindDiagnostics);
        if (diagnostics.HasErrors)
        {
            return new LineResult(null, diagnostics.Items, bound);
        }

        // Run on copies, so a failing line leaves the session as it was
        Dictionary<VariableSymbol, Value> storage = _variableValues.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        foreach (VariableSymbol uniform in Program.Globals.Where(global => global.Kind == VariableKind.Uniform))
        {
            if (_uniformValues.TryGetValue(uniform.Name, out Value? value) && value.Type.Equals(uniform.Type))
            {
                storage[uniform] = value.Clone();
            }
        }
        Evaluator evaluator = new Evaluator(Profile, Options, storage, diagnostics, LineSourceName);
        Value? result;
        Dictionary<VariableSymbol, Value> inputs;
        try
        {
            evaluator.InitializeGlobals(Program);
            inputs = storage.Where(pair => pair.Key.Kind is VariableKind.Session or VariableKind.Uniform)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
            result = evaluator.Run(bound);
        }
        catch (EvaluationException exception)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, exception.Message, LineSourceName, exception.Span));
            return new LineResult(null, diagnostics.Items, bound);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "evaluation cancelled", LineSourceName, SourceSpan.None));
            return new LineResult(null, diagnostics.Items, bound);
        }

        if (result != null)
        {
            result = MaterializeResult(result, diagnostics);
        }

        // Commit: variables (new and updated) and uniforms
        foreach (VariableSymbol variable in bound.DeclaredVariables)
        {
            if (_variables.TryGetValue(variable.Name, out VariableSymbol? replaced))
            {
                _variableValues.Remove(replaced);
            }
            _variables[variable.Name] = variable;
        }
        foreach ((VariableSymbol variable, Value value) in storage)
        {
            if (variable.Kind == VariableKind.Session && _variables.TryGetValue(variable.Name, out VariableSymbol? current) && current == variable)
            {
                _variableValues[variable] = value;
            }
            else if (variable.Kind == VariableKind.Uniform)
            {
                _uniformValues[variable.Name] = value;
            }
        }
        return new LineResult(result, diagnostics.Items, bound) { Program = Program, Inputs = inputs };
    }

    /// <summary>
    /// A literal-only line has no type yet: show it as DXC uses it (float, int), but as int64 when the integer
    /// doesn't fit in an int, rather than wrapping silently.
    /// </summary>
    private Value MaterializeResult(Value value, DiagnosticBag diagnostics)
    {
        Value result = Materialize(value, Profile, out string? note);
        if (note != null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, note, LineSourceName, SourceSpan.None));
        }
        return result;
    }

    /// <summary>A literal-typed value as a line shows it (float / int, int64 when it doesn't fit), and why if widened.</summary>
    public static Value Materialize(Value value, SemanticsProfile profile, out string? note)
    {
        note = null;
        if (value.Type is not NumericType numeric || !numeric.Kind.IsLiteral())
        {
            return value;
        }
        ScalarKind kind = numeric.Kind.Materialized();
        if (numeric.Kind == ScalarKind.LiteralInt && value.Bits.Any(bits => (long)bits is < int.MinValue or > int.MaxValue))
        {
            kind = ScalarKind.Int64;
            note = "doesn't fit in an int: shown as int64 (an int would wrap)";
        }
        NumericType type = numeric.WithKind(kind);
        return new Value(type, value.Bits.Select(bits => Scalars.Convert(numeric.Kind, kind, bits, profile)).ToArray(), value.Units.ToArray());
    }
}
