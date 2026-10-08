using System.Globalization;
using System.Text;
using ShaderCalc.Binding;
using ShaderCalc.Evaluation;
using ShaderCalc.Units;

namespace ShaderCalc.Reference;

public enum ReferenceVerdict
{
    /// <summary>Bit-identical.</summary>
    Match,

    /// <summary>Differs only through functions the GPU approximates (sin, exp2...), within the tolerance.</summary>
    WithinTolerance,

    Mismatch,

    /// <summary>The line can't be checked (no value, errors, DXC rejected the code...).</summary>
    NotChecked,
}

public sealed record ReferenceOutcome(ReferenceVerdict Verdict, Value? ReferenceValue, long MaxUlps, bool UsesApproximations, string? Message, string Hlsl,
    ComputeTimings? Timings)
{
    public override string ToString() => Verdict switch
    {
        ReferenceVerdict.Match => "= reference",
        ReferenceVerdict.WithinTolerance => $"≈ reference ({MaxUlps} ulp)",
        ReferenceVerdict.Mismatch => $"≠ reference: {ReferenceValue}{(UsesApproximations ? $" ({MaxUlps} ulp, approximate functions involved)" : string.Empty)}",
        _ => $"not checked: {Message}",
    };
}

/// <summary>
/// Runs a calculator line on the HLSL reference (DXC + WARP) and compares the bits. Literal call arguments,
/// uniforms and session variables are fed through the input buffer, already converted to their concrete types,
/// so WARP executes the math instead of DXC folding it, while DXC's literal rules stay intact.
/// </summary>
public static class ReferenceChecker
{
    /// <summary>Largest float difference, in units in the last place, accepted when approximate functions are involved.</summary>
    public const long ApproximateToleranceUlps = 64;

    /// <summary>
    /// Absolute difference also accepted then: D3D allows sin/cos 0.0008 and log2 2^-21 near 1, where ulps explode
    /// because the result is tiny.
    /// </summary>
    public const double ApproximateToleranceAbsolute = 0.0008;

    public static ReferenceOutcome Check(LineResult line, ReferenceMode mode = ReferenceMode.Strict)
    {
        // Unit errors don't stop the numeric check; only a missing value does
        if (line.Value == null || line.Line == null || line.Program == null || line.Value.Type is VoidType)
        {
            return NotChecked(line.HasErrors ? "the line has errors" : "the line has no value");
        }
        if (line.Program.HasErrors)
        {
            return NotChecked("the code has errors");
        }

        HarnessEmitter emitter = new HarnessEmitter(line.Inputs);
        string hlsl;
        try
        {
            hlsl = emitter.Build(line.Program, line.Line, line.Value.Type);
        }
        catch (InvalidOperationException exception)
        {
            return NotChecked(exception.Message);
        }

        int outputWords = line.Value.ToWords().Count();
        uint[] words;
        ComputeTimings timings;
        try
        {
            (words, timings) = WarpDevice.Shared.Run(hlsl, emitter.Inputs, outputWords, mode);
        }
        catch (ReferenceCompileException exception)
        {
            return new ReferenceOutcome(ReferenceVerdict.NotChecked, null, 0, false, exception.Message, hlsl, null);
        }

        Value reference = FromWords(line.Value.Type, words);
        bool usesApproximations = UsesApproximations(line.Line);
        uint[] ours = line.Value.ToWords().ToArray();
        if (ours.SequenceEqual(words) || BothNaN(line.Value, reference))
        {
            return new ReferenceOutcome(ReferenceVerdict.Match, reference, 0, usesApproximations, null, hlsl, timings);
        }
        long maxUlps = MaxUlps(line.Value, reference);
        bool isClose = maxUlps <= ApproximateToleranceUlps || MaxAbsoluteDifference(line.Value, reference) <= ApproximateToleranceAbsolute;
        ReferenceVerdict verdict = usesApproximations && isClose ? ReferenceVerdict.WithinTolerance : ReferenceVerdict.Mismatch;
        return new ReferenceOutcome(verdict, reference, maxUlps, usesApproximations, null, hlsl, timings);
    }

    private static ReferenceOutcome NotChecked(string message) => new ReferenceOutcome(ReferenceVerdict.NotChecked, null, 0, false, message, string.Empty, null);

    private static bool UsesApproximations(BoundInteractive line) =>
        BoundTreeWalker.Reachable(line.Statements.SelectMany(BoundTreeWalker.Expressions))
            .Any(expression => expression is BoundIntrinsicCall { Intrinsic.Precision: IntrinsicPrecision.Approximate });

    /// <summary>Rebuilds a value from the shader's output words (64-bit kinds: low word first).</summary>
    public static Value FromWords(ShaderType type, IReadOnlyList<uint> words)
    {
        IReadOnlyList<ScalarKind> kinds = type.ComponentKinds;
        ulong[] bits = new ulong[kinds.Count];
        int word = 0;
        for (int index = 0; index < kinds.Count; index++)
        {
            ulong low = words[word++];
            bits[index] = kinds[index] switch
            {
                ScalarKind.Int => Scalars.FromInt((int)low),
                _ when kinds[index].Is64Bit() => low | ((ulong)words[word++] << 32),
                _ => low,
            };
        }
        return new Value(type, bits, Enumerable.Repeat(UnitTag.Bare, kinds.Count).ToArray());
    }

    private static bool BothNaN(Value ours, Value reference) =>
        Enumerable.Range(0, ours.Bits.Length).All(index =>
            ours.Bits[index] == reference.Bits[index] || (ours.KindAt(index).IsFloat() && double.IsNaN(ours.GetNumber(index)) && double.IsNaN(reference.GetNumber(index))));

    /// <summary>Largest distance between float components, in representable values (0 for integers that match).</summary>
    public static long MaxUlps(Value ours, Value reference)
    {
        long worst = 0;
        for (int index = 0; index < ours.Bits.Length; index++)
        {
            ScalarKind kind = ours.KindAt(index);
            if (ours.Bits[index] == reference.Bits[index])
            {
                continue;
            }
            if (kind == ScalarKind.Float)
            {
                worst = Math.Max(worst, Math.Abs(Ordered((uint)ours.Bits[index]) - Ordered((uint)reference.Bits[index])));
            }
            else if (kind == ScalarKind.Double)
            {
                worst = Math.Max(worst, (long)Math.Min(long.MaxValue, Math.Abs((decimal)Ordered64(ours.Bits[index]) - Ordered64(reference.Bits[index]))));
            }
            else
            {
                return long.MaxValue;
            }
        }
        return worst;
    }

    /// <summary>Largest absolute difference between float components (infinity when a NaN or integer differs).</summary>
    public static double MaxAbsoluteDifference(Value ours, Value reference)
    {
        double worst = 0;
        for (int index = 0; index < ours.Bits.Length; index++)
        {
            if (ours.Bits[index] == reference.Bits[index])
            {
                continue;
            }
            double difference = ours.KindAt(index).IsFloat() ? Math.Abs(ours.GetNumber(index) - reference.GetNumber(index)) : double.PositiveInfinity;
            worst = Math.Max(worst, double.IsNaN(difference) ? double.PositiveInfinity : difference);
        }
        return worst;
    }

    private static long Ordered(uint bits) => (bits & 0x80000000) != 0 ? -(long)(bits & 0x7FFFFFFF) : bits;

    private static long Ordered64(ulong bits) => (bits & 0x8000000000000000) != 0 ? -(long)(bits & 0x7FFFFFFFFFFFFFFF) : (long)bits;

    /// <summary>Builds the compute shader for one line, collecting the input words it reads.</summary>
    private sealed class HarnessEmitter : BoundHlslEmitter
    {
        private readonly IReadOnlyDictionary<VariableSymbol, Value> _inputValues;

        /// <summary>Only the line's own code gets its literals fed through the buffer, never function bodies.</summary>
        private bool _isEmittingLine;

        public HarnessEmitter(IReadOnlyDictionary<VariableSymbol, Value> inputValues)
        {
            _inputValues = inputValues;
        }

        public List<uint> Inputs { get; } = new List<uint>();

        public string Build(BoundProgram program, BoundInteractive line, ShaderType resultType)
        {
            Builder.Append("RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n\n");
            EmitProgram(program, uniformsAsStatics: true);
            Builder.Append($"[RootSignature(\"{WarpDevice.RootSignature}\")]\n[numthreads(1, 1, 1)]\nvoid {WarpDevice.EntryPoint}()\n{{\n");

            // Uniforms and calculator variables hold runtime data: load them
            foreach ((VariableSymbol variable, Value value) in _inputValues)
            {
                if (variable.Kind == VariableKind.Session)
                {
                    Builder.Append("    ").Append(Declaration(variable.Type, variable.Name)).Append(";\n");
                }
                foreach ((string path, int component) in ComponentPaths(variable.Type, variable.Name))
                {
                    Builder.Append($"    {path} = {LoadScalar(value.KindAt(component), value.Bits[component])};\n");
                }
            }

            _isEmittingLine = true;
            IReadOnlyList<BoundStatement> statements = line.Statements;
            BoundExpression? result = line.Result;
            int bodyCount = result != null ? statements.Count - 1 : statements.Count;
            for (int index = 0; index < bodyCount; index++)
            {
                EmitStatement(statements[index], 1);
            }
            string resultExpression = result != null ? Expression(result) : line.ResultVariable!.Name;
            Builder.Append("    ").Append(Declaration(resultType, "result")).Append(";\n");
            Builder.Append($"    result = {resultExpression};\n");

            int word = 0;
            foreach ((string path, int component) in ComponentPaths(resultType, "result"))
            {
                ScalarKind kind = resultType.ComponentKinds[component];
                switch (kind)
                {
                    case ScalarKind.Bool:
                        Builder.Append($"    RefOutput[{word++}] = {path} ? 1u : 0u;\n");
                        break;
                    case ScalarKind.UInt:
                        Builder.Append($"    RefOutput[{word++}] = {path};\n");
                        break;
                    case ScalarKind.Double:
                        Builder.Append($"    {{ uint low, high; asuint({path}, low, high); RefOutput[{word}] = low; RefOutput[{word + 1}] = high; }}\n");
                        word += 2;
                        break;
                    case ScalarKind.Int64:
                    case ScalarKind.UInt64:
                        Builder.Append($"    RefOutput[{word}] = (uint)({path}); RefOutput[{word + 1}] = (uint)((uint64_t)({path}) >> 32);\n");
                        word += 2;
                        break;
                    default:
                        Builder.Append($"    RefOutput[{word++}] = asuint({path});\n");
                        break;
                }
            }
            Builder.Append("}\n");
            return ToString();
        }

        /// <summary>Literal arguments become buffer loads once converted to a concrete type, so DXC can't fold them.</summary>
        public override string Expression(BoundExpression expression)
        {
            if (!_isEmittingLine)
            {
                return base.Expression(expression);
            }
            // pow(x, 2.0) must keep its literal 2, or DXC would no longer turn it into x * x
            if (expression is BoundIntrinsicCall { Intrinsic.ConstantSensitiveArguments.Count: > 0 } call)
            {
                IEnumerable<string> arguments = call.Arguments.Select((argument, index) =>
                    call.Intrinsic.ConstantSensitiveArguments.Contains(index) ? base.Expression(argument) : Expression(argument));
                return $"{call.Intrinsic.Name}({string.Join(", ", arguments)})";
            }
            if (expression is BoundConversion conversion && conversion.Type is NumericType target && !target.Kind.IsLiteral()
                && conversion.Operand.Type is NumericType { Kind: var sourceKind } && sourceKind.IsLiteral())
            {
                Value? constant = Evaluator.TryEvaluateConstant(conversion, SemanticsProfile.Hlsl);
                if (constant != null)
                {
                    return LoadValue(constant);
                }
            }
            return base.Expression(expression);
        }

        private string LoadValue(Value value)
        {
            NumericType type = (NumericType)value.Type;
            string[] components = Enumerable.Range(0, value.Bits.Length).Select(index => LoadScalar(value.KindAt(index), value.Bits[index])).ToArray();
            return type.IsScalar ? components[0] : $"{type}({string.Join(", ", components)})";
        }

        private string LoadScalar(ScalarKind kind, ulong bits)
        {
            int word = Inputs.Count;
            string Read(int offset) => $"RefInput[{(word + offset).ToString(CultureInfo.InvariantCulture)}]";
            Inputs.Add((uint)bits);
            if (kind.Is64Bit())
            {
                Inputs.Add((uint)(bits >> 32));
            }
            return kind switch
            {
                ScalarKind.Bool => $"({Read(0)} != 0u)",
                ScalarKind.Int => $"asint({Read(0)})",
                ScalarKind.UInt => Read(0),
                ScalarKind.Float or ScalarKind.Half => $"asfloat({Read(0)})",
                ScalarKind.Double => $"asdouble({Read(0)}, {Read(1)})",
                ScalarKind.Int64 => $"((int64_t)(((uint64_t){Read(1)} << 32) | (uint64_t){Read(0)}))",
                ScalarKind.UInt64 => $"(((uint64_t){Read(1)} << 32) | (uint64_t){Read(0)})",
                _ => throw new InvalidOperationException($"can't load a {kind}"),
            };
        }

        /// <summary>HLSL access path of every flattened component: v[2], m[1][0], s.Field[3].x...</summary>
        private static IEnumerable<(string Path, int Component)> ComponentPaths(ShaderType type, string root)
        {
            int component = 0;
            foreach (string path in Paths(type, root))
            {
                yield return (path, component++);
            }
        }

        private static IEnumerable<string> Paths(ShaderType type, string root)
        {
            switch (type)
            {
                case NumericType { IsScalar: true }:
                    yield return root;
                    break;
                case NumericType { IsVector: true } vector:
                    for (int index = 0; index < vector.Size; index++)
                    {
                        yield return $"{root}[{index}]";
                    }
                    break;
                case NumericType matrix:
                    for (int row = 0; row < matrix.Rows; row++)
                    {
                        for (int column = 0; column < matrix.Columns; column++)
                        {
                            yield return $"{root}[{row}][{column}]";
                        }
                    }
                    break;
                case ArrayType array:
                    for (int index = 0; index < array.Length; index++)
                    {
                        foreach (string path in Paths(array.Element, $"{root}[{index}]"))
                        {
                            yield return path;
                        }
                    }
                    break;
                case StructType structure:
                    foreach (StructField field in structure.Fields)
                    {
                        foreach (string path in Paths(field.Type, $"{root}.{field.Name}"))
                        {
                            yield return path;
                        }
                    }
                    break;
            }
        }
    }
}
