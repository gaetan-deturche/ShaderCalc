using ShaderCalc.Evaluation;
using ShaderCalc.Syntax;

namespace ShaderCalc.Binding;

/// <summary>
/// How well a backend's result can be expected to match: exact IEEE operations, or functions each GPU approximates
/// (sin, exp2, log2...), which references are compared against with a tolerance.
/// </summary>
public enum IntrinsicPrecision
{
    Exact,
    Approximate,
}

/// <summary>Resolved overload: parameter types (arguments are converted to them), modes, return type.</summary>
public sealed record IntrinsicSignature(IReadOnlyList<ShaderType> ParameterTypes, IReadOnlyList<ParameterMode> Modes, ShaderType ReturnType)
{
    public static IntrinsicSignature AllIn(IReadOnlyList<ShaderType> parameterTypes, ShaderType returnType) =>
        new IntrinsicSignature(parameterTypes, parameterTypes.Select(_ => ParameterMode.In).ToArray(), returnType);
}

public readonly record struct IntrinsicResolution(IntrinsicSignature? Signature, string? Error)
{
    public static IntrinsicResolution Fail(string error) => new IntrinsicResolution(null, error);

    public static implicit operator IntrinsicResolution(IntrinsicSignature signature) => new IntrinsicResolution(signature, null);
}

/// <summary>Computes an intrinsic. Out arguments are written to <see cref="IntrinsicContext.Outputs"/>.</summary>
public delegate Value IntrinsicImplementation(IntrinsicContext context, Value[] arguments);

public sealed class Intrinsic
{
    public Intrinsic(string name, IntrinsicPrecision precision, Func<IReadOnlyList<ShaderType>, IntrinsicResolution> resolve, IntrinsicImplementation implementation)
    {
        Name = name;
        Precision = precision;
        Resolve = resolve;
        Implementation = implementation;
    }

    public string Name { get; }

    public IntrinsicPrecision Precision { get; }

    public Func<IReadOnlyList<ShaderType>, IntrinsicResolution> Resolve { get; }

    public IntrinsicImplementation Implementation { get; }

    /// <summary>Arguments whose being a compile-time constant changes DXC's lowering (pow's exponent 2 → x * x).</summary>
    public IReadOnlyList<int> ConstantSensitiveArguments { get; init; } = Array.Empty<int>();

    public override string ToString() => Name;
}
