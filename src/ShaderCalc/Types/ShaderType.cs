namespace ShaderCalc;

/// <summary>
/// Scalar component kinds. Unsuffixed literals have their own kinds, as in DXC: they are 64-bit (int64 / double)
/// until an operation or a conversion gives them a concrete type.
/// </summary>
public enum ScalarKind
{
    Bool,
    Int,
    UInt,
    Int64,
    UInt64,
    Half,
    Float,
    Double,
    LiteralInt,
    LiteralFloat,
}

public static class ScalarKindExtensions
{
    public static bool IsFloat(this ScalarKind kind) => kind is ScalarKind.Half or ScalarKind.Float or ScalarKind.Double or ScalarKind.LiteralFloat;

    public static bool IsInteger(this ScalarKind kind) =>
        kind is ScalarKind.Int or ScalarKind.UInt or ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.LiteralInt;

    public static bool IsSigned(this ScalarKind kind) => kind is ScalarKind.Int or ScalarKind.Int64 or ScalarKind.LiteralInt || kind.IsFloat();

    public static bool IsLiteral(this ScalarKind kind) => kind is ScalarKind.LiteralInt or ScalarKind.LiteralFloat;

    public static bool Is64Bit(this ScalarKind kind) =>
        kind is ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.Double or ScalarKind.LiteralInt or ScalarKind.LiteralFloat;

    /// <summary>Promotion rank: in a binary operation the operand with the higher rank decides the kind.</summary>
    public static int Rank(this ScalarKind kind) => kind switch
    {
        ScalarKind.Bool => 0,
        ScalarKind.LiteralInt => 1,
        ScalarKind.Int => 2,
        ScalarKind.UInt => 3,
        ScalarKind.Int64 => 4,
        ScalarKind.UInt64 => 5,
        ScalarKind.LiteralFloat => 6,
        ScalarKind.Half => 7,
        ScalarKind.Float => 8,
        _ => 9,
    };

    /// <summary>The kind a literal becomes when nothing else decides: int and float, like DXC.</summary>
    public static ScalarKind Materialized(this ScalarKind kind) => kind switch
    {
        ScalarKind.LiteralInt => ScalarKind.Int,
        ScalarKind.LiteralFloat => ScalarKind.Float,
        _ => kind,
    };

    public static string Keyword(this ScalarKind kind) => kind switch
    {
        ScalarKind.Bool => "bool",
        ScalarKind.Int => "int",
        ScalarKind.UInt => "uint",
        ScalarKind.Int64 => "int64_t",
        ScalarKind.UInt64 => "uint64_t",
        ScalarKind.Half => "half",
        ScalarKind.Float => "float",
        ScalarKind.Double => "double",
        ScalarKind.LiteralInt => "literal int",
        _ => "literal float",
    };
}

/// <summary>Base of every HLSL type the interpreter knows. Values store their components flattened, in this order.</summary>
public abstract class ShaderType : IEquatable<ShaderType>
{
    /// <summary>Number of scalar components once flattened (structs and arrays included).</summary>
    public abstract int ComponentCount { get; }

    /// <summary>Kind of each flattened component.</summary>
    public abstract IReadOnlyList<ScalarKind> ComponentKinds { get; }

    public abstract bool Equals(ShaderType? other);

    public override bool Equals(object? obj) => Equals(obj as ShaderType);

    public abstract override int GetHashCode();

    public static bool operator ==(ShaderType? left, ShaderType? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(ShaderType? left, ShaderType? right) => !(left == right);
}

public enum NumericShape
{
    Scalar,
    Vector,
    Matrix,
}

/// <summary>Scalar, vector (1 row) or matrix. Matrix components are flattened row by row, like HLSL's `m[row][column]`.</summary>
public sealed class NumericType : ShaderType
{
    private ScalarKind[]? _componentKinds;

    private NumericType(ScalarKind kind, NumericShape shape, int rows, int columns)
    {
        Kind = kind;
        Shape = shape;
        Rows = rows;
        Columns = columns;
    }

    public ScalarKind Kind { get; }

    public NumericShape Shape { get; }

    public int Rows { get; }

    public int Columns { get; }

    public bool IsScalar => Shape == NumericShape.Scalar;

    public bool IsVector => Shape == NumericShape.Vector;

    public bool IsMatrix => Shape == NumericShape.Matrix;

    /// <summary>Component count of a scalar or vector.</summary>
    public int Size => Rows * Columns;

    public override int ComponentCount => Rows * Columns;

    public override IReadOnlyList<ScalarKind> ComponentKinds => _componentKinds ??= Enumerable.Repeat(Kind, ComponentCount).ToArray();

    public static NumericType Scalar(ScalarKind kind) => new NumericType(kind, NumericShape.Scalar, 1, 1);

    public static NumericType Vector(ScalarKind kind, int size) => new NumericType(kind, NumericShape.Vector, 1, size);

    public static NumericType Matrix(ScalarKind kind, int rows, int columns) => new NumericType(kind, NumericShape.Matrix, rows, columns);

    public static NumericType Bool => Scalar(ScalarKind.Bool);

    public static NumericType Int => Scalar(ScalarKind.Int);

    public static NumericType UInt => Scalar(ScalarKind.UInt);

    public static NumericType Float => Scalar(ScalarKind.Float);

    public NumericType WithKind(ScalarKind kind) => new NumericType(kind, Shape, Rows, Columns);

    /// <summary>Same kind and shape family, other component count (scalar for 1, vector otherwise).</summary>
    public static NumericType ScalarOrVector(ScalarKind kind, int size) => size == 1 ? Scalar(kind) : Vector(kind, size);

    public override bool Equals(ShaderType? other) =>
        other is NumericType numeric && numeric.Kind == Kind && numeric.Shape == Shape && numeric.Rows == Rows && numeric.Columns == Columns;

    public override int GetHashCode() => HashCode.Combine(Kind, Shape, Rows, Columns);

    public override string ToString() => Shape switch
    {
        NumericShape.Scalar => Kind.Keyword(),
        NumericShape.Vector => Kind.IsLiteral() ? $"vector<{Kind.Keyword()}, {Columns}>" : $"{Kind.Keyword()}{Columns}",
        _ => Kind.IsLiteral() ? $"matrix<{Kind.Keyword()}, {Rows}, {Columns}>" : $"{Kind.Keyword()}{Rows}x{Columns}",
    };
}

public sealed class ArrayType : ShaderType
{
    private ScalarKind[]? _componentKinds;

    public ArrayType(ShaderType element, int length)
    {
        Element = element;
        Length = length;
    }

    public ShaderType Element { get; }

    public int Length { get; }

    public override int ComponentCount => Element.ComponentCount * Length;

    public override IReadOnlyList<ScalarKind> ComponentKinds =>
        _componentKinds ??= Enumerable.Range(0, Length).SelectMany(_ => Element.ComponentKinds).ToArray();

    public override bool Equals(ShaderType? other) => other is ArrayType array && array.Length == Length && array.Element.Equals(Element);

    public override int GetHashCode() => HashCode.Combine(Element, Length);

    /// <summary>`float[3]`; declarations put the length after the name instead.</summary>
    public override string ToString() => $"{InnermostElement}{Dimensions}";

    internal ShaderType InnermostElement => Element is ArrayType inner ? inner.InnermostElement : Element;

    internal string Dimensions => $"[{Length}]" + (Element is ArrayType inner ? inner.Dimensions : string.Empty);
}

public sealed record StructField(string Name, ShaderType Type, int ComponentOffset);

/// <summary>A struct; two struct types are the same only if they are the same declaration.</summary>
public sealed class StructType : ShaderType
{
    private ScalarKind[]? _componentKinds;

    public StructType(string name, IReadOnlyList<(string Name, ShaderType Type)> fields)
    {
        Name = name;
        List<StructField> laidOut = new List<StructField>();
        int offset = 0;
        foreach ((string fieldName, ShaderType fieldType) in fields)
        {
            laidOut.Add(new StructField(fieldName, fieldType, offset));
            offset += fieldType.ComponentCount;
        }
        Fields = laidOut;
        ComponentCount = offset;
    }

    public string Name { get; }

    public IReadOnlyList<StructField> Fields { get; }

    public override int ComponentCount { get; }

    public override IReadOnlyList<ScalarKind> ComponentKinds => _componentKinds ??= Fields.SelectMany(field => field.Type.ComponentKinds).ToArray();

    public StructField? FindField(string name) => Fields.FirstOrDefault(field => field.Name == name);

    public override bool Equals(ShaderType? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Name;
}

public sealed class VoidType : ShaderType
{
    public static readonly VoidType Instance = new VoidType();

    private VoidType()
    {
    }

    public override int ComponentCount => 0;

    public override IReadOnlyList<ScalarKind> ComponentKinds => Array.Empty<ScalarKind>();

    public override bool Equals(ShaderType? other) => other is VoidType;

    public override int GetHashCode() => 0;

    public override string ToString() => "void";
}
