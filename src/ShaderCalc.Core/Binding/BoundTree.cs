using ShaderCalc.Units;

namespace ShaderCalc.Binding;

// ---- Expressions: every node carries its static type; conversions are explicit nodes ----

public abstract class BoundExpression
{
    protected BoundExpression(ShaderType type, SourceSpan span)
    {
        Type = type;
        Span = span;
    }

    public ShaderType Type { get; }

    public SourceSpan Span { get; }
}

/// <summary>Placeholder after an error, so binding can go on and report more.</summary>
public sealed class BoundErrorExpression : BoundExpression
{
    public BoundErrorExpression(SourceSpan span) : base(VoidType.Instance, span)
    {
    }
}

public sealed class BoundLiteral : BoundExpression
{
    public BoundLiteral(Value value, SourceSpan span) : base(value.Type, span)
    {
        Value = value;
    }

    public Value Value { get; }
}

/// <summary>`3 km` in a calculator line: a literal float in SI that carries a unit.</summary>
public sealed class BoundUnitLiteral : BoundExpression
{
    public BoundUnitLiteral(double siValue, Dimension dimension, string unitText, SourceSpan span)
        : base(NumericType.Scalar(ScalarKind.LiteralFloat), span)
    {
        SiValue = siValue;
        Dimension = dimension;
        UnitText = unitText;
    }

    public double SiValue { get; }

    public Dimension Dimension { get; }

    public string UnitText { get; }
}

public sealed class BoundVariable : BoundExpression
{
    public BoundVariable(VariableSymbol variable, SourceSpan span) : base(variable.Type, span)
    {
        Variable = variable;
    }

    public VariableSymbol Variable { get; }
}

public enum UnaryOperator
{
    Plus,
    Negate,
    LogicalNot,
    BitwiseNot,
}

public sealed class BoundUnary : BoundExpression
{
    public BoundUnary(UnaryOperator operation, BoundExpression operand, ShaderType type, SourceSpan span) : base(type, span)
    {
        Operator = operation;
        Operand = operand;
    }

    public UnaryOperator Operator { get; }

    public BoundExpression Operand { get; }
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>
/// Both operands are already converted to the operation type (shifts: the right one to the left one's kind);
/// comparisons produce bools of the same shape.
/// </summary>
public sealed class BoundBinary : BoundExpression
{
    public BoundBinary(BinaryOperator operation, BoundExpression left, BoundExpression right, ShaderType type, SourceSpan span) : base(type, span)
    {
        Operator = operation;
        Left = left;
        Right = right;
    }

    public BinaryOperator Operator { get; }

    public BoundExpression Left { get; }

    public BoundExpression Right { get; }
}

/// <summary>Short-circuit && / || on scalar bools (HLSL 2021).</summary>
public sealed class BoundLogical : BoundExpression
{
    public BoundLogical(bool isAnd, BoundExpression left, BoundExpression right, SourceSpan span) : base(NumericType.Bool, span)
    {
        IsAnd = isAnd;
        Left = left;
        Right = right;
    }

    public bool IsAnd { get; }

    public BoundExpression Left { get; }

    public BoundExpression Right { get; }
}

public sealed class BoundAssignment : BoundExpression
{
    public BoundAssignment(BoundExpression target, BoundExpression value, SourceSpan span) : base(target.Type, span)
    {
        Target = target;
        Value = value;
    }

    public BoundExpression Target { get; }

    /// <summary>Already converted to the target's type.</summary>
    public BoundExpression Value { get; }
}

/// <summary>`a op= b`: (a converted to the operation type) op b, converted back to a's type.</summary>
public sealed class BoundCompoundAssignment : BoundExpression
{
    public BoundCompoundAssignment(BinaryOperator operation, BoundExpression target, BoundExpression value, ShaderType operationType, SourceSpan span)
        : base(target.Type, span)
    {
        Operator = operation;
        Target = target;
        Value = value;
        OperationType = operationType;
    }

    public BinaryOperator Operator { get; }

    public BoundExpression Target { get; }

    public BoundExpression Value { get; }

    public ShaderType OperationType { get; }
}

public sealed class BoundIncrement : BoundExpression
{
    public BoundIncrement(BoundExpression target, bool isIncrement, bool isPrefix, SourceSpan span) : base(target.Type, span)
    {
        Target = target;
        IsIncrement = isIncrement;
        IsPrefix = isPrefix;
    }

    public BoundExpression Target { get; }

    public bool IsIncrement { get; }

    public bool IsPrefix { get; }
}

public sealed class BoundConditional : BoundExpression
{
    public BoundConditional(BoundExpression condition, BoundExpression whenTrue, BoundExpression whenFalse, SourceSpan span) : base(whenTrue.Type, span)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    public BoundExpression Condition { get; }

    public BoundExpression WhenTrue { get; }

    public BoundExpression WhenFalse { get; }
}

/// <summary>A user function call. `in` arguments are converted to the parameter type; out/inout arguments are places.</summary>
public sealed class BoundCall : BoundExpression
{
    public BoundCall(FunctionSymbol function, IReadOnlyList<BoundExpression> arguments, SourceSpan span) : base(function.ReturnType, span)
    {
        Function = function;
        Arguments = arguments;
    }

    public FunctionSymbol Function { get; }

    public IReadOnlyList<BoundExpression> Arguments { get; }
}

public sealed class BoundIntrinsicCall : BoundExpression
{
    public BoundIntrinsicCall(Intrinsic intrinsic, IntrinsicSignature signature, IReadOnlyList<BoundExpression> arguments,
        IReadOnlyList<Value?> constantArguments, SourceSpan span) : base(signature.ReturnType, span)
    {
        Intrinsic = intrinsic;
        Signature = signature;
        Arguments = arguments;
        ConstantArguments = constantArguments;
    }

    public Intrinsic Intrinsic { get; }

    public IntrinsicSignature Signature { get; }

    /// <summary>Converted to the resolved parameter types (out arguments stay places).</summary>
    public IReadOnlyList<BoundExpression> Arguments { get; }

    /// <summary>Compile-time values of constant arguments, as DXC folds them (pow(x, 2) becomes x * x).</summary>
    public IReadOnlyList<Value?> ConstantArguments { get; }
}

public enum ConversionKind
{
    Identity,
    /// <summary>Same shape, other component kind.</summary>
    Numeric,
    /// <summary>Scalar to vector / matrix (or, cast only, to a struct or array).</summary>
    Splat,
    /// <summary>Keeps the leading components: float4 → float3, float4 → float, float4x4 → float3x3.</summary>
    Truncation,
    /// <summary>Explicit cast between same-size layouts: float4 ↔ float2x2, struct ↔ struct.</summary>
    Flat,
}

public sealed class BoundConversion : BoundExpression
{
    public BoundConversion(BoundExpression operand, ShaderType type, ConversionKind kind, bool isExplicit, SourceSpan span) : base(type, span)
    {
        Operand = operand;
        Kind = kind;
        IsExplicit = isExplicit;
    }

    public BoundExpression Operand { get; }

    public ConversionKind Kind { get; }

    public bool IsExplicit { get; }
}

/// <summary>
/// float3(a, b.xy), or an initializer list: every component of the sources, in order, converted to the
/// target's component kinds. A single scalar source fills every component.
/// </summary>
public sealed class BoundConstruct : BoundExpression
{
    public BoundConstruct(ShaderType type, IReadOnlyList<BoundExpression> sources, bool isInitializerList, SourceSpan span) : base(type, span)
    {
        Sources = sources;
        IsInitializerList = isInitializerList;
    }

    public IReadOnlyList<BoundExpression> Sources { get; }

    public bool IsInitializerList { get; }
}

/// <summary>Vector/scalar swizzle (.xzy, .rgb) or matrix element selection (._m00_m11): indices into the flattened components.</summary>
public sealed class BoundSwizzle : BoundExpression
{
    public BoundSwizzle(BoundExpression operand, int[] indices, string text, ShaderType type, SourceSpan span) : base(type, span)
    {
        Operand = operand;
        Indices = indices;
        Text = text;
    }

    public BoundExpression Operand { get; }

    public int[] Indices { get; }

    public string Text { get; }
}

/// <summary>array[i], vector[i], matrix[i] (a row).</summary>
public sealed class BoundIndex : BoundExpression
{
    public BoundIndex(BoundExpression operand, BoundExpression index, int length, ShaderType type, SourceSpan span) : base(type, span)
    {
        Operand = operand;
        Index = index;
        Length = length;
    }

    public BoundExpression Operand { get; }

    public BoundExpression Index { get; }

    /// <summary>Number of elements that can be indexed.</summary>
    public int Length { get; }
}

public sealed class BoundField : BoundExpression
{
    public BoundField(BoundExpression operand, StructField field, SourceSpan span) : base(field.Type, span)
    {
        Operand = operand;
        Field = field;
    }

    public BoundExpression Operand { get; }

    public StructField Field { get; }
}

public sealed class BoundComma : BoundExpression
{
    public BoundComma(BoundExpression left, BoundExpression right, SourceSpan span) : base(right.Type, span)
    {
        Left = left;
        Right = right;
    }

    public BoundExpression Left { get; }

    public BoundExpression Right { get; }
}

// ---- Statements ----

public abstract class BoundStatement
{
    protected BoundStatement(SourceSpan span)
    {
        Span = span;
    }

    public SourceSpan Span { get; }
}

public sealed class BoundBlock : BoundStatement
{
    public BoundBlock(IReadOnlyList<BoundStatement> statements, SourceSpan span, bool isScope = true) : base(span)
    {
        Statements = statements;
        IsScope = isScope;
    }

    public IReadOnlyList<BoundStatement> Statements { get; }

    /// <summary>False for `float a, b;`: several declarations, no braces.</summary>
    public bool IsScope { get; }
}

public sealed class BoundVariableDeclaration : BoundStatement
{
    public BoundVariableDeclaration(VariableSymbol variable, BoundExpression? initializer, SourceSpan span) : base(span)
    {
        Variable = variable;
        Initializer = initializer;
    }

    public VariableSymbol Variable { get; }

    public BoundExpression? Initializer { get; }
}

public sealed class BoundExpressionStatement : BoundStatement
{
    public BoundExpressionStatement(BoundExpression expression, SourceSpan span) : base(span)
    {
        Expression = expression;
    }

    public BoundExpression Expression { get; }
}

public sealed class BoundIf : BoundStatement
{
    public BoundIf(BoundExpression condition, BoundStatement then, BoundStatement? otherwise, SourceSpan span) : base(span)
    {
        Condition = condition;
        Then = then;
        Else = otherwise;
    }

    public BoundExpression Condition { get; }

    public BoundStatement Then { get; }

    public BoundStatement? Else { get; }
}

public sealed class BoundLoop : BoundStatement
{
    public BoundLoop(BoundStatement? initializer, BoundExpression? condition, BoundExpression? step, BoundStatement body, bool isDoWhile, SourceSpan span)
        : base(span)
    {
        Initializer = initializer;
        Condition = condition;
        Step = step;
        Body = body;
        IsDoWhile = isDoWhile;
    }

    public BoundStatement? Initializer { get; }

    public BoundExpression? Condition { get; }

    public BoundExpression? Step { get; }

    public BoundStatement Body { get; }

    public bool IsDoWhile { get; }
}

public sealed record BoundSwitchSection(IReadOnlyList<long> Labels, bool IsDefault, IReadOnlyList<BoundStatement> Statements);

public sealed class BoundSwitch : BoundStatement
{
    public BoundSwitch(BoundExpression value, IReadOnlyList<BoundSwitchSection> sections, SourceSpan span) : base(span)
    {
        Value = value;
        Sections = sections;
    }

    public BoundExpression Value { get; }

    public IReadOnlyList<BoundSwitchSection> Sections { get; }
}

public sealed class BoundReturn : BoundStatement
{
    public BoundReturn(BoundExpression? value, SourceSpan span) : base(span)
    {
        Value = value;
    }

    public BoundExpression? Value { get; }
}

public sealed class BoundJump : BoundStatement
{
    public BoundJump(bool isBreak, SourceSpan span) : base(span)
    {
        IsBreak = isBreak;
    }

    public bool IsBreak { get; }
}
