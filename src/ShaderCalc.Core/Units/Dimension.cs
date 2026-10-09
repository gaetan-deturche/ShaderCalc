using System.Text;

namespace ShaderCalc.Units;

/// <summary>
/// An SI dimension: exponents of the 7 base units, stored doubled so `sqrt` of m² (or of m) stays exact.
/// </summary>
public sealed class Dimension : IEquatable<Dimension>
{
    public static readonly string[] BaseSymbols = { "m", "kg", "s", "A", "K", "mol", "cd" };
    public static readonly Dimension None = new Dimension(new int[7]);

    private readonly int[] _doubledExponents;

    private Dimension(int[] doubledExponents)
    {
        _doubledExponents = doubledExponents;
    }

    public static Dimension Base(int index, int exponent = 1)
    {
        int[] exponents = new int[7];
        exponents[index] = 2 * exponent;
        return new Dimension(exponents);
    }

    public bool IsNone => _doubledExponents.All(exponent => exponent == 0);

    public Dimension Multiply(Dimension other) => IsNone ? other : other.IsNone ? this : Combine(other, (left, right) => left + right);

    public Dimension Divide(Dimension other) => other.IsNone ? this : Combine(other, (left, right) => left - right);

    /// <summary>Null when the result would need a non-half-integer exponent (e.g. the cube root of m).</summary>
    public Dimension? Power(double exponent)
    {
        if (IsNone)
        {
            return this;
        }
        int[] exponents = new int[7];
        for (int index = 0; index < 7; index++)
        {
            double doubled = _doubledExponents[index] * exponent;
            if (double.IsNaN(doubled) || Math.Abs(doubled - Math.Round(doubled)) > 1e-9)
            {
                return null;
            }
            exponents[index] = (int)Math.Round(doubled);
        }
        return new Dimension(exponents);
    }

    private Dimension Combine(Dimension other, Func<int, int, int> combine)
    {
        int[] exponents = new int[7];
        for (int index = 0; index < 7; index++)
        {
            exponents[index] = combine(_doubledExponents[index], other._doubledExponents[index]);
        }
        return new Dimension(exponents);
    }

    public bool Equals(Dimension? other) => other != null && _doubledExponents.AsSpan().SequenceEqual(other._doubledExponents);

    public override bool Equals(object? obj) => Equals(obj as Dimension);

    public override int GetHashCode() => _doubledExponents.Aggregate(17, (hash, exponent) => hash * 31 + exponent);

    /// <summary>"kg·m²/s²", or "1" when dimensionless.</summary>
    public override string ToString()
    {
        StringBuilder numerator = new StringBuilder();
        StringBuilder denominator = new StringBuilder();
        // Conventional order: kg m s A K mol cd
        foreach (int index in new[] { 1, 0, 2, 3, 4, 5, 6 })
        {
            int doubled = _doubledExponents[index];
            if (doubled == 0)
            {
                continue;
            }
            StringBuilder target = doubled > 0 ? numerator : denominator;
            if (target.Length > 0)
            {
                target.Append('·');
            }
            target.Append(BaseSymbols[index]).Append(FormatExponent(Math.Abs(doubled)));
        }

        if (numerator.Length == 0 && denominator.Length == 0)
        {
            return "1";
        }
        string top = numerator.Length > 0 ? numerator.ToString() : "1";
        return denominator.Length > 0 ? $"{top}/{denominator}" : top;
    }

    private static string FormatExponent(int doubled)
    {
        if (doubled == 2)
        {
            return string.Empty;
        }
        if (doubled % 2 == 1)
        {
            return $"^({doubled}/2)";
        }
        const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        return string.Concat((doubled / 2).ToString().Select(digit => Superscripts[digit - '0']));
    }
}

/// <summary>
/// The unit side of one value component. A bare number literal is adoptable: in sums and comparisons it takes the
/// other operand's unit instead of being an error.
/// </summary>
public readonly record struct UnitTag(Dimension Dimension, bool IsAdoptable)
{
    public static readonly UnitTag Bare = new UnitTag(Dimension.None, true);

    public static readonly UnitTag Dimensionless = new UnitTag(Dimension.None, false);

    public static UnitTag Of(Dimension dimension) => new UnitTag(dimension, false);
}

/// <summary>A unit usable in calculator literals (`3 km`): its factor to SI and its dimension.</summary>
public sealed record UnitDefinition(string Name, double ToSi, Dimension Dimension);

/// <summary>Units known to calculator literals. A small built-in set until the full table arrives (phase 2).</summary>
public static class UnitTable
{
    private static readonly Dimension Length = Dimension.Base(0);
    private static readonly Dimension Mass = Dimension.Base(1);
    private static readonly Dimension Time = Dimension.Base(2);
    private static readonly Dimension Current = Dimension.Base(3);
    private static readonly Dimension Temperature = Dimension.Base(4);
    private static readonly Dimension Amount = Dimension.Base(5);
    private static readonly Dimension Luminous = Dimension.Base(6);
    private static readonly Dimension Force = Mass.Multiply(Length).Divide(Time).Divide(Time);
    private static readonly Dimension Energy = Force.Multiply(Length);

    public static readonly IReadOnlyDictionary<string, UnitDefinition> Units = new UnitDefinition[]
    {
        new("m", 1, Length), new("km", 1000, Length), new("cm", 0.01, Length), new("mm", 0.001, Length),
        new("um", 1e-6, Length), new("nm", 1e-9, Length),
        new("s", 1, Time), new("ms", 0.001, Time), new("us", 1e-6, Time), new("ns", 1e-9, Time), new("min", 60, Time), new("h", 3600, Time),
        new("kg", 1, Mass), new("g", 0.001, Mass),
        new("A", 1, Current), new("K", 1, Temperature), new("mol", 1, Amount),
        new("cd", 1, Luminous), new("lm", 1, Luminous), new("lx", 1, Luminous.Divide(Length).Divide(Length)),
        new("N", 1, Force), new("J", 1, Energy), new("W", 1, Energy.Divide(Time)),
        new("Hz", 1, Dimension.None.Divide(Time)), new("rad", 1, Dimension.None), new("sr", 1, Dimension.None),
    }.ToDictionary(unit => unit.Name, StringComparer.Ordinal);

    /// <summary>Named SI units shown next to a result's dimension.</summary>
    public static readonly IReadOnlyDictionary<Dimension, string> DerivedNames = new Dictionary<Dimension, string>
    {
        [Force] = "N",
        [Energy] = "J",
        [Energy.Divide(Time)] = "W",
        [Luminous.Divide(Length).Divide(Length)] = "lx",
        [Dimension.None.Divide(Time)] = "Hz",
    };
}
