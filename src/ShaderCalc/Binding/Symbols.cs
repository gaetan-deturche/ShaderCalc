using ShaderCalc.Syntax;

namespace ShaderCalc.Binding;

public enum VariableKind
{
    Local,
    Parameter,
    /// <summary>static global: re-initialised at the start of every evaluation, like a shader invocation.</summary>
    Static,
    /// <summary>Non-static global (uniform, cbuffer member): read-only in functions, set from calculator lines.</summary>
    Uniform,
    /// <summary>A calculator variable: lives across lines.</summary>
    Session,
}

public class VariableSymbol
{
    public VariableSymbol(string name, ShaderType type, VariableKind kind, bool isConst, SourceSpan span)
    {
        Name = name;
        Type = type;
        Kind = kind;
        IsConst = isConst;
        Span = span;
    }

    public string Name { get; }

    public ShaderType Type { get; }

    public VariableKind Kind { get; }

    public bool IsConst { get; }

    public SourceSpan Span { get; }

    /// <summary>Initializer of a global (null: zero).</summary>
    public BoundExpression? Initializer { get; set; }

    /// <summary>Compile-time value of a const with a constant initializer (array lengths, case labels).</summary>
    public Value? ConstantValue { get; set; }

    public override string ToString() => $"{Type} {Name}";
}

public sealed class ParameterSymbol : VariableSymbol
{
    public ParameterSymbol(string name, ShaderType type, ParameterMode mode, bool isConst, SourceSpan span)
        : base(name, type, VariableKind.Parameter, isConst, span)
    {
        Mode = mode;
    }

    public ParameterMode Mode { get; }

    public BoundExpression? DefaultValue { get; set; }
}

public sealed class FunctionSymbol
{
    public FunctionSymbol(string name, ShaderType returnType, IReadOnlyList<ParameterSymbol> parameters, FunctionSyntax syntax)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        Syntax = syntax;
    }

    public string Name { get; }

    public ShaderType ReturnType { get; }

    public IReadOnlyList<ParameterSymbol> Parameters { get; }

    public FunctionSyntax Syntax { get; private set; }

    public BoundBlock? Body { get; set; }

    /// <summary>A prototype gets its body from a later definition.</summary>
    public void AttachDefinition(FunctionSyntax definition) => Syntax = definition;

    public int RequiredParameterCount => Parameters.Count(parameter => parameter.DefaultValue == null);

    public string Signature => $"{ReturnType} {Name}({string.Join(", ", Parameters.Select(parameter => (parameter.Mode == ParameterMode.In ? string.Empty : parameter.Mode.ToString().ToLowerInvariant() + " ") + parameter.Type))})";

    public override string ToString() => Signature;
}
