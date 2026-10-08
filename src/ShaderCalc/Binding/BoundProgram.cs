namespace ShaderCalc.Binding;

/// <summary>The bound result of pasted code: types, globals and functions, all type-checked.</summary>
public sealed class BoundProgram
{
    public BoundProgram(IReadOnlyList<StructType> structs, IReadOnlyDictionary<string, ShaderType> typeNames, IReadOnlyList<VariableSymbol> globals,
        IReadOnlyList<FunctionSymbol> functions, IReadOnlyList<Diagnostic> diagnostics)
    {
        Structs = structs;
        TypeNames = typeNames;
        Globals = globals;
        Functions = functions;
        Diagnostics = diagnostics;
    }

    public static BoundProgram Empty { get; } = new BoundProgram(Array.Empty<StructType>(), new Dictionary<string, ShaderType>(),
        Array.Empty<VariableSymbol>(), Array.Empty<FunctionSymbol>(), Array.Empty<Diagnostic>());

    /// <summary>Structs in declaration order.</summary>
    public IReadOnlyList<StructType> Structs { get; }

    /// <summary>Struct and typedef names.</summary>
    public IReadOnlyDictionary<string, ShaderType> TypeNames { get; }

    /// <summary>Static and uniform globals in declaration order.</summary>
    public IReadOnlyList<VariableSymbol> Globals { get; }

    public IReadOnlyList<FunctionSymbol> Functions { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.IsError);

    public IEnumerable<FunctionSymbol> FindFunctions(string name) => Functions.Where(function => function.Name == name);
}

/// <summary>A bound calculator line.</summary>
public sealed class BoundInteractive
{
    public BoundInteractive(IReadOnlyList<BoundStatement> statements, BoundExpression? result, VariableSymbol? resultVariable,
        IReadOnlyList<VariableSymbol> declaredVariables)
    {
        Statements = statements;
        Result = result;
        ResultVariable = resultVariable;
        DeclaredVariables = declaredVariables;
    }

    public IReadOnlyList<BoundStatement> Statements { get; }

    /// <summary>The expression whose value the line shows (the last expression statement), if any.</summary>
    public BoundExpression? Result { get; }

    /// <summary>When the line ends with a declaration, the variable to show.</summary>
    public VariableSymbol? ResultVariable { get; }

    /// <summary>Session variables this line declares (explicitly or by assigning a new name).</summary>
    public IReadOnlyList<VariableSymbol> DeclaredVariables { get; }
}
