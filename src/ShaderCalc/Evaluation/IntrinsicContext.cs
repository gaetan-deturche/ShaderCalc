using ShaderCalc.Binding;

namespace ShaderCalc.Evaluation;

/// <summary>What an intrinsic implementation can see while it runs.</summary>
public sealed class IntrinsicContext
{
    private readonly Action<DiagnosticSeverity, string> _report;

    public IntrinsicContext(BoundIntrinsicCall call, SemanticsProfile profile, UnitChecker units, Action<DiagnosticSeverity, string> report)
    {
        Call = call;
        Profile = profile;
        Units = units;
        _report = report;
        Outputs = new Value?[call.Arguments.Count];
    }

    public BoundIntrinsicCall Call { get; }

    public SemanticsProfile Profile { get; }

    public UnitChecker Units { get; }

    /// <summary>Values for out arguments, by argument index.</summary>
    public Value?[] Outputs { get; }

    public void Report(DiagnosticSeverity severity, string message) => _report(severity, message);

    /// <summary>True when argument <paramref name="index"/> is a compile-time constant whose every component equals <paramref name="number"/>.</summary>
    public bool IsConstant(int index, double number)
    {
        Value? constant = Call.ConstantArguments[index];
        return constant != null && Enumerable.Range(0, constant.Bits.Length).All(component => constant.GetNumber(component) == number);
    }
}
