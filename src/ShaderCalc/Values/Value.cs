using System.Globalization;
using System.Text;
using ShaderCalc.Units;

namespace ShaderCalc;

/// <summary>
/// A typed value: every scalar component flattened (matrices row by row, then arrays, then struct fields), each with
/// its exact bits (see <see cref="Scalars"/>) and its unit.
/// </summary>
public sealed class Value
{
    public Value(ShaderType type, ulong[] bits, UnitTag[] units)
    {
        if (bits.Length != type.ComponentCount || units.Length != bits.Length)
        {
            throw new ArgumentException($"{type} needs {type.ComponentCount} components, got {bits.Length} bits and {units.Length} units");
        }
        Type = type;
        Bits = bits;
        Units = units;
    }

    public ShaderType Type { get; }

    public ulong[] Bits { get; }

    public UnitTag[] Units { get; }

    public static readonly Value Void = new Value(VoidType.Instance, Array.Empty<ulong>(), Array.Empty<UnitTag>());

    public static Value Zero(ShaderType type, UnitTag unit) =>
        new Value(type, new ulong[type.ComponentCount], Enumerable.Repeat(unit, type.ComponentCount).ToArray());

    public static Value Scalar(ScalarKind kind, ulong bits, UnitTag unit) => new Value(NumericType.Scalar(kind), new[] { bits }, new[] { unit });

    public static Value FromBool(bool value) => Scalar(ScalarKind.Bool, Scalars.FromBool(value), UnitTag.Bare);

    public static Value FromInt(int value) => Scalar(ScalarKind.Int, Scalars.FromInt(value), UnitTag.Bare);

    public static Value FromUInt(uint value) => Scalar(ScalarKind.UInt, Scalars.FromUInt(value), UnitTag.Bare);

    public static Value FromFloat(float value) => Scalar(ScalarKind.Float, Scalars.FromFloat(value), UnitTag.Bare);

    public static Value FromDouble(double value) => Scalar(ScalarKind.Double, Scalars.FromDouble(value), UnitTag.Bare);

    public Value Clone() => new Value(Type, (ulong[])Bits.Clone(), (UnitTag[])Units.Clone());

    public ScalarKind KindAt(int index) => Type.ComponentKinds[index];

    public float GetFloat(int index) => Scalars.ToFloat(Bits[index]);

    public bool GetBool(int index) => Scalars.IsTrue(KindAt(index), Bits[index]);

    /// <summary>Component as a double, whatever its kind (display, unit maths).</summary>
    public double GetNumber(int index) => Scalars.ToNumber(KindAt(index), Bits[index]);

    /// <summary>The value's components as 32-bit words, the way a shader stores them (64-bit kinds: low word first).</summary>
    public IEnumerable<uint> ToWords()
    {
        for (int index = 0; index < Bits.Length; index++)
        {
            ScalarKind kind = KindAt(index);
            yield return (uint)Bits[index];
            if (kind.Is64Bit())
            {
                yield return (uint)(Bits[index] >> 32);
            }
        }
    }

    public override string ToString() => Format(this, includeUnits: true);

    // ---- Display ----

    public static string FormatComponent(ScalarKind kind, ulong bits) => kind switch
    {
        ScalarKind.Bool => bits != 0 ? "true" : "false",
        ScalarKind.Int => ((int)bits).ToString(CultureInfo.InvariantCulture),
        ScalarKind.UInt => ((uint)bits).ToString(CultureInfo.InvariantCulture),
        ScalarKind.Int64 or ScalarKind.LiteralInt => ((long)bits).ToString(CultureInfo.InvariantCulture),
        ScalarKind.UInt64 => bits.ToString(CultureInfo.InvariantCulture),
        ScalarKind.Half => FormatReal((double)Scalars.ToHalf(bits), ((double)Scalars.ToHalf(bits)).ToString("R", CultureInfo.InvariantCulture)),
        ScalarKind.Float => FormatReal(Scalars.ToFloat(bits), Scalars.ToFloat(bits).ToString("R", CultureInfo.InvariantCulture)),
        _ => FormatReal(Scalars.ToDouble(bits), Scalars.ToDouble(bits).ToString("R", CultureInfo.InvariantCulture)),
    };

    private static string FormatReal(double value, string text) => double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "+INF"
        : double.IsNegativeInfinity(value) ? "-INF"
        : text;

    public static string Format(Value value, bool includeUnits)
    {
        StringBuilder builder = new StringBuilder();
        int index = 0;
        AppendValue(builder, value, value.Type, ref index);
        if (includeUnits)
        {
            builder.Append(FormatUnits(value));
        }
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, Value value, ShaderType type, ref int index)
    {
        switch (type)
        {
            case NumericType { IsScalar: true } scalar:
                builder.Append(FormatComponent(scalar.Kind, value.Bits[index++]));
                break;
            case NumericType numeric:
                builder.Append(numeric).Append('(');
                for (int component = 0; component < numeric.ComponentCount; component++)
                {
                    builder.Append(component == 0 ? string.Empty : ", ").Append(FormatComponent(numeric.Kind, value.Bits[index++]));
                }
                builder.Append(')');
                break;
            case ArrayType array:
                builder.Append('{');
                for (int element = 0; element < array.Length; element++)
                {
                    builder.Append(element == 0 ? string.Empty : ", ");
                    AppendValue(builder, value, array.Element, ref index);
                }
                builder.Append('}');
                break;
            case StructType structure:
                builder.Append("{ ");
                for (int field = 0; field < structure.Fields.Count; field++)
                {
                    builder.Append(field == 0 ? string.Empty : ", ").Append(structure.Fields[field].Name).Append(" = ");
                    AppendValue(builder, value, structure.Fields[field].Type, ref index);
                }
                builder.Append(" }");
                break;
        }
    }

    /// <summary>" m/s" when every component shares one unit, one unit per component otherwise.</summary>
    private static string FormatUnits(Value value)
    {
        if (value.Units.Length == 0 || value.Units.All(unit => unit.Dimension.IsNone))
        {
            return string.Empty;
        }
        if (value.Units.All(unit => unit.Dimension.Equals(value.Units[0].Dimension)))
        {
            return " " + DescribeDimension(value.Units[0].Dimension);
        }
        return " [" + string.Join(", ", value.Units.Select(unit => unit.Dimension.IsNone ? "1" : DescribeDimension(unit.Dimension))) + "]";
    }

    public static string DescribeDimension(Dimension dimension) =>
        UnitTable.DerivedNames.TryGetValue(dimension, out string? name) ? $"{dimension} ({name})" : dimension.ToString();
}
