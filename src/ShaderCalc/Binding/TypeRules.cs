namespace ShaderCalc.Binding;

/// <summary>How one type converts to another: whether it is allowed, how, how good a match it is, and any warning.</summary>
public readonly record struct ConversionInfo(bool IsPossible, ConversionKind Kind, int Cost, string? Warning)
{
    public static readonly ConversionInfo Impossible = new ConversionInfo(false, ConversionKind.Identity, int.MaxValue, null);
}

/// <summary>HLSL type rules as DXC applies them: literal adoption, promotion, conversions, operator result types.</summary>
public static class TypeRules
{
    public const int TruncationCost = 1000;

    public static ConversionInfo Classify(ShaderType from, ShaderType to, bool isExplicit)
    {
        if (from.Equals(to))
        {
            return new ConversionInfo(true, ConversionKind.Identity, 0, null);
        }
        if (from is VoidType || to is VoidType)
        {
            return ConversionInfo.Impossible;
        }

        if (from is NumericType source && to is NumericType target)
        {
            int kindCost = KindCost(source.Kind, target.Kind);
            if (source.Shape == target.Shape && source.Rows == target.Rows && source.Columns == target.Columns)
            {
                return new ConversionInfo(true, ConversionKind.Numeric, kindCost, null);
            }
            if (source.IsScalar)
            {
                return new ConversionInfo(true, ConversionKind.Splat, 10 + kindCost, null);
            }
            if (target.IsScalar)
            {
                return new ConversionInfo(true, ConversionKind.Truncation, TruncationCost + kindCost, $"implicit truncation of {source} to {target}");
            }
            if (source.IsVector && target.IsVector && target.Size < source.Size)
            {
                return new ConversionInfo(true, ConversionKind.Truncation, TruncationCost + kindCost, $"implicit truncation of {source} to {target}");
            }
            if (source.IsMatrix && target.IsMatrix && target.Rows <= source.Rows && target.Columns <= source.Columns)
            {
                return new ConversionInfo(true, ConversionKind.Truncation, TruncationCost + kindCost, $"implicit truncation of {source} to {target}");
            }
            // float4 <-> float1x4 / float4x1 implicitly; other same-size layouts by cast
            bool isRowOrColumn = (source.IsVector && target.IsMatrix && (target.Rows == 1 || target.Columns == 1))
                || (source.IsMatrix && target.IsVector && (source.Rows == 1 || source.Columns == 1));
            if (isRowOrColumn && source.ComponentCount == target.ComponentCount)
            {
                return new ConversionInfo(true, ConversionKind.Flat, 20 + kindCost, null);
            }
            if (isExplicit && target.ComponentCount <= source.ComponentCount)
            {
                return new ConversionInfo(true, ConversionKind.Flat, 50 + kindCost, null);
            }
            return ConversionInfo.Impossible;
        }

        if (!isExplicit)
        {
            return ConversionInfo.Impossible;
        }
        // (S)0, (float[4])0
        if (from is NumericType { IsScalar: true })
        {
            return new ConversionInfo(true, ConversionKind.Splat, 50, null);
        }
        return to.ComponentCount <= from.ComponentCount ? new ConversionInfo(true, ConversionKind.Flat, 50, null) : ConversionInfo.Impossible;
    }

    /// <summary>Ranking used by overload resolution: 0 exact, small for literal adoption and promotions.</summary>
    public static int KindCost(ScalarKind from, ScalarKind to)
    {
        if (from == to)
        {
            return 0;
        }
        if (from == ScalarKind.LiteralInt)
        {
            return to switch
            {
                ScalarKind.Int => 1,
                ScalarKind.UInt or ScalarKind.Int64 or ScalarKind.UInt64 => 2,
                ScalarKind.LiteralFloat => 2,
                _ => 3,
            };
        }
        if (from == ScalarKind.LiteralFloat)
        {
            return to switch
            {
                ScalarKind.Float => 1,
                ScalarKind.Half or ScalarKind.Double => 2,
                _ => 4,
            };
        }
        if (from == ScalarKind.Bool || to == ScalarKind.Bool)
        {
            return 4;
        }
        // Widening (int -> uint/int64/float, float -> double) beats narrowing
        return to.Rank() > from.Rank() ? 2 : 3;
    }

    /// <summary>The kind both operands of an arithmetic operation take.</summary>
    public static ScalarKind PromoteKinds(ScalarKind left, ScalarKind right)
    {
        if (left == right)
        {
            return left == ScalarKind.Bool ? ScalarKind.Int : left;
        }
        if (left.IsLiteral() && right.IsLiteral())
        {
            return left == ScalarKind.LiteralFloat || right == ScalarKind.LiteralFloat ? ScalarKind.LiteralFloat : ScalarKind.LiteralInt;
        }
        if (left.IsLiteral())
        {
            return AdaptLiteral(left, right);
        }
        if (right.IsLiteral())
        {
            return AdaptLiteral(right, left);
        }
        ScalarKind leftKind = left == ScalarKind.Bool ? ScalarKind.Int : left;
        ScalarKind rightKind = right == ScalarKind.Bool ? ScalarKind.Int : right;
        return leftKind.Rank() >= rightKind.Rank() ? leftKind : rightKind;
    }

    /// <summary>A literal meeting a concrete kind: it adopts it, except that a float literal makes integers float.</summary>
    private static ScalarKind AdaptLiteral(ScalarKind literal, ScalarKind other)
    {
        if (other == ScalarKind.Bool)
        {
            return literal == ScalarKind.LiteralFloat ? ScalarKind.Float : ScalarKind.Int;
        }
        return literal == ScalarKind.LiteralFloat && other.IsInteger() ? ScalarKind.Float : other;
    }

    /// <summary>Common kind that keeps bools (for ?:, select, comparisons of bools).</summary>
    public static ScalarKind CommonKind(ScalarKind left, ScalarKind right) =>
        left == ScalarKind.Bool && right == ScalarKind.Bool ? ScalarKind.Bool : PromoteKinds(left, right);

    /// <summary>
    /// Shape of a component-wise operation between two numeric types: a scalar takes the other's shape; vectors and
    /// matrices of different sizes truncate to the smaller one (with a warning). Null when they can't combine.
    /// </summary>
    public static NumericType? CombineShapes(NumericType left, NumericType right, ScalarKind kind, out string? warning)
    {
        warning = null;
        if (left.IsScalar)
        {
            return right.WithKind(kind);
        }
        if (right.IsScalar)
        {
            return left.WithKind(kind);
        }
        if (left.IsVector && right.IsVector)
        {
            if (left.Size != right.Size)
            {
                warning = $"implicit truncation of vector type ({left} and {right})";
            }
            return NumericType.Vector(kind, Math.Min(left.Size, right.Size));
        }
        if (left.IsMatrix && right.IsMatrix)
        {
            if (left.Rows != right.Rows || left.Columns != right.Columns)
            {
                warning = $"implicit truncation of matrix type ({left} and {right})";
            }
            return NumericType.Matrix(kind, Math.Min(left.Rows, right.Rows), Math.Min(left.Columns, right.Columns));
        }
        // vector with a 1xN / Nx1 matrix of the same size behaves as the vector
        NumericType vector = left.IsVector ? left : right;
        NumericType matrix = left.IsMatrix ? left : right;
        if ((matrix.Rows == 1 || matrix.Columns == 1) && matrix.ComponentCount == vector.Size)
        {
            return vector.WithKind(kind);
        }
        return null;
    }

    public static bool IsComparison(BinaryOperator operation) => operation is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
        or BinaryOperator.LessOrEqual or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual;

    public static bool IsBitwise(BinaryOperator operation) => operation is BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor;

    public static bool IsShift(BinaryOperator operation) => operation is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight;

    /// <summary>
    /// Operand and result types of a binary operator, or an error message.
    /// </summary>
    public static (NumericType OperandType, NumericType RightType, NumericType ResultType)? ResolveBinary(BinaryOperator operation, NumericType left,
        NumericType right, out string? error, out string? warning)
    {
        error = null;
        ScalarKind kind;
        if (IsShift(operation))
        {
            // The left operand decides the type; a literal on the left adopts the right one's kind
            kind = left.Kind == ScalarKind.Bool ? ScalarKind.Int
                : left.Kind.IsLiteral() && !right.Kind.IsLiteral() ? PromoteKinds(left.Kind, right.Kind)
                : left.Kind;
            if (!kind.IsInteger() || !(right.Kind.IsInteger() || right.Kind == ScalarKind.Bool))
            {
                error = $"'{Symbol(operation)}' needs integer operands, got {left} and {right}";
                warning = null;
                return null;
            }
        }
        else if (IsBitwise(operation))
        {
            kind = left.Kind == ScalarKind.Bool && right.Kind == ScalarKind.Bool ? ScalarKind.Bool : PromoteKinds(left.Kind, right.Kind);
            if (kind.IsFloat())
            {
                error = $"'{Symbol(operation)}' needs integer operands, got {left} and {right}";
                warning = null;
                return null;
            }
        }
        else if (operation is BinaryOperator.Equal or BinaryOperator.NotEqual)
        {
            kind = CommonKind(left.Kind, right.Kind);
        }
        else
        {
            kind = PromoteKinds(left.Kind, right.Kind);
        }

        NumericType? shape = CombineShapes(left, right, kind, out warning);
        if (shape == null)
        {
            error = $"'{Symbol(operation)}' can't combine {left} and {right}";
            return null;
        }
        NumericType result = IsComparison(operation) ? shape.WithKind(ScalarKind.Bool) : shape;
        return (shape, shape, result);
    }

    public static string Symbol(BinaryOperator operation) => operation switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Remainder => "%",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        BinaryOperator.BitwiseXor => "^",
        BinaryOperator.ShiftLeft => "<<",
        BinaryOperator.ShiftRight => ">>",
        BinaryOperator.Equal => "==",
        BinaryOperator.NotEqual => "!=",
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        _ => ">=",
    };

    public static BinaryOperator? ParseBinaryOperator(string text) => text switch
    {
        "+" => BinaryOperator.Add,
        "-" => BinaryOperator.Subtract,
        "*" => BinaryOperator.Multiply,
        "/" => BinaryOperator.Divide,
        "%" => BinaryOperator.Remainder,
        "&" => BinaryOperator.BitwiseAnd,
        "|" => BinaryOperator.BitwiseOr,
        "^" => BinaryOperator.BitwiseXor,
        "<<" => BinaryOperator.ShiftLeft,
        ">>" => BinaryOperator.ShiftRight,
        "==" => BinaryOperator.Equal,
        "!=" => BinaryOperator.NotEqual,
        "<" => BinaryOperator.Less,
        "<=" => BinaryOperator.LessOrEqual,
        ">" => BinaryOperator.Greater,
        ">=" => BinaryOperator.GreaterOrEqual,
        _ => null,
    };
}
