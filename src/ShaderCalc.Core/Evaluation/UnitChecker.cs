using ShaderCalc.Units;

namespace ShaderCalc.Evaluation;

/// <summary>
/// Unit rules for one operation: sums and comparisons need one unit (a bare literal adopts the other's), products
/// combine them, transcendental functions need dimensionless input. Violations are diagnostics, never failures.
/// </summary>
public sealed class UnitChecker
{
    private readonly Action<DiagnosticSeverity, string> _report;
    private readonly bool _isStrict;

    public UnitChecker(Action<DiagnosticSeverity, string> report, bool isStrict)
    {
        _report = report;
        _isStrict = isStrict;
    }

    public UnitTag Same(UnitTag left, UnitTag right, string context)
    {
        if (left.Dimension.Equals(right.Dimension))
        {
            return new UnitTag(left.Dimension, left.IsAdoptable && right.IsAdoptable);
        }
        if (right.IsAdoptable && !left.IsAdoptable)
        {
            NoteAdoption(left.Dimension, context);
            return UnitTag.Of(left.Dimension);
        }
        if (left.IsAdoptable && !right.IsAdoptable)
        {
            NoteAdoption(right.Dimension, context);
            return UnitTag.Of(right.Dimension);
        }
        _report(DiagnosticSeverity.Error, $"{context} mixes units: {left.Dimension} and {right.Dimension}");
        return UnitTag.Of(left.Dimension);
    }

    public static UnitTag Multiply(UnitTag left, UnitTag right) =>
        new UnitTag(left.Dimension.Multiply(right.Dimension), left.IsAdoptable && right.IsAdoptable);

    public static UnitTag Divide(UnitTag left, UnitTag right) =>
        new UnitTag(left.Dimension.Divide(right.Dimension), left.IsAdoptable && right.IsAdoptable);

    /// <summary>Input of sin, exp, saturate...: must be a pure number.</summary>
    public UnitTag Dimensionless(UnitTag value, string context)
    {
        if (!value.Dimension.IsNone && !value.IsAdoptable)
        {
            _report(DiagnosticSeverity.Error, $"{context} needs a dimensionless value, got {value.Dimension}");
        }
        return new UnitTag(Dimension.None, value.IsAdoptable);
    }

    public UnitTag Power(UnitTag value, double exponent, string context)
    {
        Dimension? raised = value.Dimension.Power(exponent);
        if (raised == null)
        {
            _report(DiagnosticSeverity.Error, $"{context} of {value.Dimension} has no unit (exponent {exponent})");
            return new UnitTag(Dimension.None, value.IsAdoptable);
        }
        return new UnitTag(raised, value.IsAdoptable);
    }

    /// <summary>Bit operations and reinterpretations: the result is raw bits.</summary>
    public UnitTag Drop(UnitTag value, string context)
    {
        if (!value.Dimension.IsNone)
        {
            _report(DiagnosticSeverity.Warning, $"{context} drops the unit {value.Dimension}");
        }
        return new UnitTag(Dimension.None, value.IsAdoptable);
    }

    private void NoteAdoption(Dimension adopted, string context)
    {
        if (_isStrict && !adopted.IsNone)
        {
            _report(DiagnosticSeverity.Warning, $"number literal in {context} taken as {adopted}");
        }
    }
}
