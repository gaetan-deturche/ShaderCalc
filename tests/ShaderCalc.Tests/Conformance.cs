using System.Text;
using ShaderCalc;
using ShaderCalc.Binding;
using ShaderCalc.Evaluation;
using ShaderCalc.Reference;
using ShaderCalc.Units;

namespace ShaderCalc.Tests;

public sealed record ConformanceSample(Value[] Inputs, Value Ours, uint[] Reference, long Ulps);

public sealed record ConformanceReport(string Expression, int Samples, int Exact, long MaxUlps, IReadOnlyList<ConformanceSample> Mismatches)
{
    public override string ToString()
    {
        StringBuilder builder = new StringBuilder($"{Expression,-40} {Exact,5}/{Samples,-5} exact, max {(MaxUlps == long.MaxValue ? "∞" : MaxUlps.ToString())} ulp");
        foreach (ConformanceSample sample in Mismatches.Take(4))
        {
            Value reference = ReferenceChecker.FromWords(sample.Ours.Type, sample.Reference);
            builder.Append($"\n    ({string.Join(", ", sample.Inputs.Select(input => input.ToString()))}) → ours {sample.Ours}, WARP {reference}");
        }
        return builder.ToString();
    }
}

/// <summary>
/// Runs `T F(params) { return expression; }` over many inputs, in the interpreter and in one WARP dispatch (inputs
/// read from a buffer in a loop, so nothing is constant-folded), and compares every sample.
/// </summary>
public static class Conformance
{
    private static readonly float[] SpecialFloats =
    {
        0f, -0f, 1f, -1f, 0.5f, -0.5f, 1.5f, 2.5f, -2.5f, 3.5f, 2f, 3f, 0.1f, 0.25f, 0.75f, 0.999999f, 1.000001f, 100f, -100f,
        MathF.PI, -MathF.PI / 2, 1e-7f, 1e-20f, 1e-38f, 1e-40f, -1e-40f, 1e10f, 1e30f, -1e30f, 3.4e38f, 255.5f, 16777217f, 2147483648f,
        -2147483904f, 4294967296f, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
    };

    public static IEnumerable<float> Floats(int randomCount, int seed)
    {
        Random random = new Random(seed);
        foreach (float special in SpecialFloats)
        {
            yield return special;
        }
        for (int index = 0; index < randomCount; index++)
        {
            float mantissa = (float)(random.NextDouble() * 2 - 1);
            yield return mantissa * MathF.Pow(2, random.Next(-24, 25));
        }
    }

    public static IEnumerable<int> Ints(int randomCount, int seed)
    {
        Random random = new Random(seed);
        foreach (int special in new[] { 0, 1, -1, 2, -2, 7, -7, 31, 32, 33, 63, int.MaxValue, int.MinValue, 255, -256, 65536 })
        {
            yield return special;
        }
        for (int index = 0; index < randomCount; index++)
        {
            yield return random.Next(int.MinValue, int.MaxValue);
        }
    }

    /// <summary>Cartesian product of the special values, then random tuples.</summary>
    public static List<Value[]> FloatTuples(int arity, int randomCount, int seed)
    {
        List<float> values = Floats(randomCount, seed).ToList();
        List<Value[]> tuples = new List<Value[]>();
        Random random = new Random(seed + 1);
        if (arity == 1)
        {
            return values.Select(value => new[] { Value.FromFloat(value) }).ToList();
        }
        int specials = arity == 2 ? SpecialFloats.Length : 12;
        IEnumerable<IEnumerable<float>> product = new[] { Enumerable.Empty<float>() };
        for (int position = 0; position < arity; position++)
        {
            product = product.SelectMany(prefix => SpecialFloats.Take(specials).Select(value => prefix.Append(value)));
        }
        tuples.AddRange(product.Select(tuple => tuple.Select(Value.FromFloat).ToArray()));
        for (int index = 0; index < randomCount; index++)
        {
            tuples.Add(Enumerable.Range(0, arity).Select(_ => Value.FromFloat(values[random.Next(values.Count)])).ToArray());
        }
        return tuples;
    }

    public static ConformanceReport Run(string parameters, string returnType, string expression, IReadOnlyList<Value[]> samples, ReferenceMode mode = ReferenceMode.Strict)
    {
        string program = $"{returnType} F({parameters}) {{ return {expression}; }}";
        ShaderSession session = new ShaderSession();
        IReadOnlyList<Diagnostic> diagnostics = session.SetProgram(program);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            throw new InvalidOperationException(string.Join("\n", diagnostics));
        }
        FunctionSymbol function = session.Program.Functions.Single();

        // Interpreter
        DiagnosticBag bag = new DiagnosticBag();
        Evaluator evaluator = new Evaluator(session.Profile, new EvaluationOptions(), new Dictionary<VariableSymbol, Value>(), bag, "input");
        List<Value> ours = samples.Select(sample => evaluator.Call(function, sample.Select((input, index) =>
            evaluator.ConvertValue(input, function.Parameters[index].Type, ConversionKind.Numeric)).ToArray())).ToList();

        // WARP: one loop over the input buffer
        int inputWords = samples[0].Sum(input => input.ToWords().Count());
        int outputWords = ours[0].ToWords().Count();
        StringBuilder hlsl = new StringBuilder();
        hlsl.Append("RWStructuredBuffer<uint> RefOutput : register(u0);\nStructuredBuffer<uint> RefInput : register(t0);\n");
        hlsl.Append(program).Append('\n');
        hlsl.Append($"[RootSignature(\"{WarpDevice.RootSignature}\")]\n[numthreads(1, 1, 1)]\nvoid {WarpDevice.EntryPoint}()\n{{\n");
        hlsl.Append($"    for (uint sample = 0; sample < {samples.Count}u; sample++)\n    {{\n        uint input = sample * {inputWords}u;\n");
        List<string> arguments = new List<string>();
        int word = 0;
        foreach (ParameterSymbol parameter in function.Parameters)
        {
            NumericType type = (NumericType)parameter.Type;
            string[] components = Enumerable.Range(0, type.ComponentCount).Select(_ =>
            {
                string low = $"RefInput[input + {word++}u]";
                string high = type.Kind.Is64Bit() ? $"RefInput[input + {word++}u]" : string.Empty;
                return Load(type.Kind, low, high);
            }).ToArray();
            arguments.Add(type.IsScalar ? components[0] : $"{type}({string.Join(", ", components)})");
        }
        hlsl.Append($"        {returnType} result = F({string.Join(", ", arguments)});\n");
        NumericType resultType = (NumericType)function.ReturnType;
        int outputWord = 0;
        for (int component = 0; component < resultType.ComponentCount; component++)
        {
            string access = resultType.IsScalar ? "result" : resultType.IsMatrix
                ? $"result[{component / resultType.Columns}][{component % resultType.Columns}]"
                : $"result[{component}]";
            string target = $"RefOutput[sample * {outputWords}u + {outputWord++}u]";
            switch (resultType.Kind)
            {
                case ScalarKind.Bool:
                    hlsl.Append($"        {target} = {access} ? 1u : 0u;\n");
                    break;
                case ScalarKind.UInt:
                    hlsl.Append($"        {target} = {access};\n");
                    break;
                case ScalarKind.Double:
                    hlsl.Append($"        {{ uint low, high; asuint({access}, low, high); {target} = low; RefOutput[sample * {outputWords}u + {outputWord++}u] = high; }}\n");
                    break;
                case ScalarKind.Int64:
                case ScalarKind.UInt64:
                    hlsl.Append($"        {target} = (uint)({access}); RefOutput[sample * {outputWords}u + {outputWord++}u] = (uint)((uint64_t)({access}) >> 32);\n");
                    break;
                default:
                    hlsl.Append($"        {target} = asuint({access});\n");
                    break;
            }
        }
        hlsl.Append("    }\n}\n");

        List<uint> inputs = samples.SelectMany(sample => sample.SelectMany(input => input.ToWords())).ToList();
        (uint[] output, _) = WarpDevice.Shared.Run(hlsl.ToString(), inputs, outputWords * samples.Count, mode);

        int exact = 0;
        long maxUlps = 0;
        List<ConformanceSample> mismatches = new List<ConformanceSample>();
        for (int index = 0; index < samples.Count; index++)
        {
            uint[] reference = output[(index * outputWords)..((index + 1) * outputWords)];
            Value referenceValue = ReferenceChecker.FromWords(ours[index].Type, reference);
            bool isNaNPair = Enumerable.Range(0, ours[index].Bits.Length).All(component =>
                ours[index].Bits[component] == referenceValue.Bits[component]
                || (ours[index].KindAt(component).IsFloat() && double.IsNaN(ours[index].GetNumber(component)) && double.IsNaN(referenceValue.GetNumber(component))));
            if (isNaNPair)
            {
                exact++;
                continue;
            }
            long ulps = ReferenceChecker.MaxUlps(ours[index], referenceValue);
            maxUlps = Math.Max(maxUlps, ulps);
            mismatches.Add(new ConformanceSample(samples[index], ours[index], reference, ulps));
        }
        mismatches.Sort((left, right) => right.Ulps.CompareTo(left.Ulps));

        // Investigation aid: every mismatch with exact bits, one line each
        string? dumpPath = Environment.GetEnvironmentVariable("SHADERCALC_CONFORMANCE_DUMP");
        if (dumpPath != null)
        {
            File.AppendAllLines(dumpPath, mismatches.Select(sample =>
                $"{expression}\t{string.Join(",", sample.Inputs.SelectMany(input => input.ToWords()).Select(bits => $"{bits:X8}"))}" +
                $"\t{string.Join(",", sample.Ours.ToWords().Select(bits => $"{bits:X8}"))}\t{string.Join(",", sample.Reference.Select(bits => $"{bits:X8}"))}"));
        }
        return new ConformanceReport(expression, samples.Count, exact, maxUlps, mismatches);
    }

    private static string Load(ScalarKind kind, string word, string high) => kind switch
    {
        ScalarKind.Int => $"asint({word})",
        ScalarKind.UInt => word,
        ScalarKind.Bool => $"({word} != 0u)",
        ScalarKind.Double => $"asdouble({word}, {high})",
        ScalarKind.Int64 => $"((int64_t)(((uint64_t){high} << 32) | (uint64_t){word}))",
        ScalarKind.UInt64 => $"(((uint64_t){high} << 32) | (uint64_t){word})",
        _ => $"asfloat({word})",
    };

    public static Value[] Doubles(IEnumerable<double> values) => values.Select(Value.FromDouble).ToArray();

    public static Value Int64(long value) => Value.Scalar(ScalarKind.Int64, (ulong)value, ShaderCalc.Units.UnitTag.Bare);
}
