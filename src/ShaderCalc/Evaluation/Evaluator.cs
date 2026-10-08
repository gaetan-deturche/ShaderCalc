using ShaderCalc.Binding;
using ShaderCalc.Syntax;
using ShaderCalc.Units;

namespace ShaderCalc.Evaluation;

public sealed class EvaluationException : Exception
{
    public EvaluationException(string message, SourceSpan span) : base(message)
    {
        Span = span;
    }

    public SourceSpan Span { get; }
}

public sealed record EvaluationOptions
{
    /// <summary>Report every bare literal that adopts a unit.</summary>
    public bool StrictUnits { get; init; }

    /// <summary>Statements + expressions evaluated before giving up (infinite loops).</summary>
    public long MaxSteps { get; init; } = 50_000_000;

    public CancellationToken Cancellation { get; init; }
}

/// <summary>
/// Executes the bound tree. Variables live in per-call frames keyed by symbol; globals, uniforms and session
/// variables in a storage the caller owns. Every write copies, so values are never shared between variables.
/// </summary>
public sealed class Evaluator
{
    private sealed class NotConstantException : Exception
    {
    }

    private sealed class Frame
    {
        public Frame(FunctionSymbol? function)
        {
            Function = function;
        }

        public FunctionSymbol? Function { get; }

        public Dictionary<VariableSymbol, Value> Locals { get; } = new Dictionary<VariableSymbol, Value>();
    }

    private enum Flow
    {
        Normal,
        Break,
        Continue,
        Return,
    }

    /// <summary>Where a write goes: some components of a variable's storage.</summary>
    private readonly record struct Place(Value Storage, int[] Components, ShaderType Type);

    private const int MaxCallDepth = 64;

    private readonly SemanticsProfile _profile;
    private readonly EvaluationOptions _options;
    private readonly Dictionary<VariableSymbol, Value> _storage;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _programSourceName;
    private readonly string _lineSourceName;
    private readonly bool _isConstantMode;
    private long _steps;
    private int _callDepth;
    private Value? _returnValue;
    private Frame _frame = new Frame(null);

    public Evaluator(SemanticsProfile profile, EvaluationOptions options, Dictionary<VariableSymbol, Value> storage, DiagnosticBag diagnostics,
        string programSourceName, string lineSourceName)
        : this(profile, options, storage, diagnostics, programSourceName, lineSourceName, isConstantMode: false)
    {
    }

    private Evaluator(SemanticsProfile profile, EvaluationOptions options, Dictionary<VariableSymbol, Value> storage, DiagnosticBag diagnostics,
        string programSourceName, string lineSourceName, bool isConstantMode)
    {
        _profile = profile;
        _options = options;
        _storage = storage;
        _diagnostics = diagnostics;
        _programSourceName = programSourceName;
        _lineSourceName = lineSourceName;
        _isConstantMode = isConstantMode;
    }

    public SemanticsProfile Profile => _profile;

    /// <summary>The value of a constant expression (literals, static consts, pure operations), or null.</summary>
    public static Value? TryEvaluateConstant(BoundExpression expression, SemanticsProfile profile)
    {
        Evaluator evaluator = new Evaluator(profile, new EvaluationOptions { MaxSteps = 100_000 }, new Dictionary<VariableSymbol, Value>(),
            new DiagnosticBag(), string.Empty, string.Empty, isConstantMode: true);
        try
        {
            return evaluator.Evaluate(expression);
        }
        catch (Exception exception) when (exception is NotConstantException or EvaluationException)
        {
            return null;
        }
    }

    /// <summary>Initializes static globals (once per evaluation, like a shader invocation) and uniforms not set yet.</summary>
    public void InitializeGlobals(BoundProgram program)
    {
        foreach (VariableSymbol global in program.Globals)
        {
            if (global.Kind == VariableKind.Uniform && _storage.ContainsKey(global))
            {
                continue;
            }
            _storage[global] = global.Initializer == null ? Value.Zero(global.Type, UnitTag.Bare) : Evaluate(global.Initializer).Clone();
        }
    }

    /// <summary>Runs a calculator line; returns the value it shows (null for none).</summary>
    public Value? Run(BoundInteractive line)
    {
        _frame = new Frame(null);
        foreach (BoundStatement statement in line.Statements)
        {
            Flow flow = Execute(statement);
            if (flow != Flow.Normal)
            {
                break;
            }
        }
        if (line.ResultVariable != null)
        {
            return ReadVariable(line.ResultVariable, line.ResultVariable.Span);
        }
        return line.Result == null ? null : _lastResult;
    }

    private Value? _lastResult;

    /// <summary>Calls a function directly with argument values (already of the parameter types).</summary>
    public Value Call(FunctionSymbol function, IReadOnlyList<Value> arguments)
    {
        Frame frame = new Frame(function);
        for (int index = 0; index < function.Parameters.Count; index++)
        {
            frame.Locals[function.Parameters[index]] = index < arguments.Count ? arguments[index].Clone() : Value.Zero(function.Parameters[index].Type, UnitTag.Bare);
        }
        return Invoke(function, frame, function.Syntax.NameSpan);
    }

    // ---------------------------------------------------------------- Diagnostics

    private string SourceName => _frame.Function != null ? _programSourceName : _lineSourceName;

    private void Report(DiagnosticSeverity severity, string message, SourceSpan span)
    {
        if (!_isConstantMode)
        {
            _diagnostics.Add(new Diagnostic(severity, message, SourceName, span, _frame.Function?.Name));
        }
    }

    private UnitChecker UnitsAt(SourceSpan span) => new UnitChecker((severity, message) => Report(severity, message, span), _options.StrictUnits);

    private void Step(SourceSpan span)
    {
        _steps++;
        if (_steps > _options.MaxSteps)
        {
            throw new EvaluationException($"stopped after {_options.MaxSteps:N0} steps (infinite loop?)", span);
        }
        if ((_steps & 0xFFF) == 0)
        {
            _options.Cancellation.ThrowIfCancellationRequested();
        }
    }

    // ---------------------------------------------------------------- Statements

    private Flow Execute(BoundStatement statement)
    {
        Step(statement.Span);
        switch (statement)
        {
            case BoundBlock block:
                foreach (BoundStatement inner in block.Statements)
                {
                    Flow flow = Execute(inner);
                    if (flow != Flow.Normal)
                    {
                        return flow;
                    }
                }
                return Flow.Normal;

            case BoundVariableDeclaration declaration:
            {
                Value value = declaration.Initializer == null ? Value.Zero(declaration.Variable.Type, UnitTag.Bare) : Evaluate(declaration.Initializer).Clone();
                if (declaration.Variable.Kind == VariableKind.Session)
                {
                    _storage[declaration.Variable] = value;
                }
                else
                {
                    _frame.Locals[declaration.Variable] = value;
                }
                return Flow.Normal;
            }

            case BoundExpressionStatement expression:
                _lastResult = Evaluate(expression.Expression);
                return Flow.Normal;

            case BoundIf ifStatement:
                if (IsTrue(Evaluate(ifStatement.Condition)))
                {
                    return Execute(ifStatement.Then);
                }
                return ifStatement.Else == null ? Flow.Normal : Execute(ifStatement.Else);

            case BoundLoop loop:
                return ExecuteLoop(loop);

            case BoundSwitch switchStatement:
                return ExecuteSwitch(switchStatement);

            case BoundReturn returnStatement:
                _returnValue = returnStatement.Value == null ? null : Evaluate(returnStatement.Value);
                return Flow.Return;

            case BoundJump jump:
                return jump.IsBreak ? Flow.Break : Flow.Continue;

            default:
                throw new EvaluationException($"unsupported statement {statement.GetType().Name}", statement.Span);
        }
    }

    private Flow ExecuteLoop(BoundLoop loop)
    {
        if (loop.Initializer != null)
        {
            Execute(loop.Initializer);
        }
        bool isFirst = true;
        while (true)
        {
            if (!(loop.IsDoWhile && isFirst) && loop.Condition != null && !IsTrue(Evaluate(loop.Condition)))
            {
                return Flow.Normal;
            }
            isFirst = false;
            Flow flow = Execute(loop.Body);
            if (flow == Flow.Break)
            {
                return Flow.Normal;
            }
            if (flow == Flow.Return)
            {
                return flow;
            }
            if (loop.Step != null)
            {
                Evaluate(loop.Step);
            }
        }
    }

    private Flow ExecuteSwitch(BoundSwitch switchStatement)
    {
        long value = (long)Evaluate(switchStatement.Value).GetNumber(0);
        int start = switchStatement.Sections.ToList().FindIndex(section => section.Labels.Contains(value));
        if (start < 0)
        {
            start = switchStatement.Sections.ToList().FindIndex(section => section.IsDefault);
        }
        if (start < 0)
        {
            return Flow.Normal;
        }
        // Fall through the following sections until a break
        for (int index = start; index < switchStatement.Sections.Count; index++)
        {
            foreach (BoundStatement statement in switchStatement.Sections[index].Statements)
            {
                Flow flow = Execute(statement);
                if (flow == Flow.Break)
                {
                    return Flow.Normal;
                }
                if (flow != Flow.Normal)
                {
                    return flow;
                }
            }
        }
        return Flow.Normal;
    }

    private static bool IsTrue(Value condition) => condition.GetBool(0);

    // ---------------------------------------------------------------- Expressions

    public Value Evaluate(BoundExpression expression)
    {
        Step(expression.Span);
        switch (expression)
        {
            case BoundLiteral literal:
                return literal.Value;
            case BoundUnitLiteral unitLiteral:
                return Value.Scalar(ScalarKind.LiteralFloat, Scalars.FromDouble(unitLiteral.SiValue), UnitTag.Of(unitLiteral.Dimension));
            case BoundVariable variable:
                return ReadVariable(variable.Variable, variable.Span);
            case BoundUnary unary:
                return EvaluateUnary(unary);
            case BoundBinary binary:
                return EvaluateBinary(binary.Operator, Evaluate(binary.Left), Evaluate(binary.Right), binary.Type, binary.Span);
            case BoundLogical logical:
            {
                bool left = IsTrue(Evaluate(logical.Left));
                if (left != logical.IsAnd)
                {
                    return Value.FromBool(left);
                }
                return Value.FromBool(IsTrue(Evaluate(logical.Right)));
            }
            case BoundAssignment assignment:
            {
                NotInConstantMode();
                Place place = ResolvePlace(assignment.Target);
                Value value = Evaluate(assignment.Value);
                Write(place, value);
                return Read(place);
            }
            case BoundCompoundAssignment compound:
                return EvaluateCompoundAssignment(compound);
            case BoundIncrement increment:
                return EvaluateIncrement(increment);
            case BoundConditional conditional:
                return IsTrue(Evaluate(conditional.Condition)) ? Evaluate(conditional.WhenTrue) : Evaluate(conditional.WhenFalse);
            case BoundCall call:
                NotInConstantMode();
                return EvaluateCall(call);
            case BoundIntrinsicCall intrinsicCall:
                return EvaluateIntrinsic(intrinsicCall);
            case BoundConversion conversion:
                return ConvertValue(Evaluate(conversion.Operand), conversion.Type, conversion.Kind);
            case BoundConstruct construct:
                return EvaluateConstruct(construct);
            case BoundSwizzle swizzle:
                return Select(Evaluate(swizzle.Operand), swizzle.Indices, swizzle.Type);
            case BoundIndex index:
            {
                Value operand = Evaluate(index.Operand);
                int element = CheckedIndex(Evaluate(index.Index), index.Length, index.Span);
                int size = index.Type.ComponentCount;
                return Select(operand, Enumerable.Range(element * size, size).ToArray(), index.Type);
            }
            case BoundField field:
                return Select(Evaluate(field.Operand), Enumerable.Range(field.Field.ComponentOffset, field.Field.Type.ComponentCount).ToArray(), field.Type);
            case BoundComma comma:
                Evaluate(comma.Left);
                return Evaluate(comma.Right);
            case BoundErrorExpression:
                throw new EvaluationException("can't evaluate an expression that has errors", expression.Span);
            default:
                throw new EvaluationException($"unsupported expression {expression.GetType().Name}", expression.Span);
        }
    }

    private void NotInConstantMode()
    {
        if (_isConstantMode)
        {
            throw new NotConstantException();
        }
    }

    private Value ReadVariable(VariableSymbol variable, SourceSpan span)
    {
        if (_isConstantMode)
        {
            return variable.ConstantValue ?? throw new NotConstantException();
        }
        // A copy: the storage is written in place, and an earlier read must not change with it
        if (_frame.Locals.TryGetValue(variable, out Value? local) || _storage.TryGetValue(variable, out local))
        {
            return local.Clone();
        }
        if (variable.ConstantValue != null)
        {
            return variable.ConstantValue;
        }
        throw new EvaluationException($"'{variable.Name}' has no value yet", span);
    }

    private static Value Select(Value value, int[] indices, ShaderType type) =>
        new Value(type, indices.Select(index => value.Bits[index]).ToArray(), indices.Select(index => value.Units[index]).ToArray());

    private int CheckedIndex(Value index, int length, SourceSpan span)
    {
        long number = index.KindAt(0) == ScalarKind.UInt ? (uint)index.Bits[0] : (long)index.GetNumber(0);
        if (number < 0 || number >= length)
        {
            throw new EvaluationException($"index {number} is out of range [0, {length - 1}] (undefined on a GPU)", span);
        }
        return (int)number;
    }

    private Value EvaluateUnary(BoundUnary unary)
    {
        Value operand = Evaluate(unary.Operand);
        UnitChecker units = UnitsAt(unary.Span);
        ulong[] bits = new ulong[operand.Bits.Length];
        UnitTag[] tags = new UnitTag[bits.Length];
        for (int index = 0; index < bits.Length; index++)
        {
            bits[index] = ScalarArithmetic.Unary(unary.Operator, operand.KindAt(index), operand.Bits[index], _profile);
            tags[index] = unary.Operator switch
            {
                UnaryOperator.LogicalNot => new UnitTag(Dimension.None, operand.Units[index].IsAdoptable),
                UnaryOperator.BitwiseNot => units.Drop(operand.Units[index], "'~'"),
                _ => operand.Units[index],
            };
        }
        return new Value(unary.Type, bits, tags);
    }

    /// <summary>Component-wise binary operation on two values already converted to the operation's shape and kind.</summary>
    private Value EvaluateBinary(BinaryOperator operation, Value left, Value right, ShaderType resultType, SourceSpan span)
    {
        UnitChecker units = UnitsAt(span);
        string context = $"'{TypeRules.Symbol(operation)}'";
        int count = resultType.ComponentCount;
        ulong[] bits = new ulong[count];
        UnitTag[] tags = new UnitTag[count];
        for (int index = 0; index < count; index++)
        {
            int leftIndex = left.Bits.Length == 1 ? 0 : index;
            int rightIndex = right.Bits.Length == 1 ? 0 : index;
            bits[index] = ScalarArithmetic.Binary(operation, left.KindAt(leftIndex), left.Bits[leftIndex], right.Bits[rightIndex], _profile, out string? fault);
            if (fault != null)
            {
                Report(DiagnosticSeverity.Warning, fault, span);
            }
            UnitTag leftUnit = left.Units[leftIndex];
            UnitTag rightUnit = right.Units[rightIndex];
            tags[index] = operation switch
            {
                BinaryOperator.Multiply => UnitChecker.Multiply(leftUnit, rightUnit),
                BinaryOperator.Divide => UnitChecker.Divide(leftUnit, rightUnit),
                BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor =>
                    new UnitTag(Dimension.None, units.Drop(leftUnit, context).IsAdoptable && units.Drop(rightUnit, context).IsAdoptable),
                BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight => units.Drop(leftUnit, context),
                _ when TypeRules.IsComparison(operation) => new UnitTag(Dimension.None, units.Same(leftUnit, rightUnit, context).IsAdoptable),
                _ => units.Same(leftUnit, rightUnit, context),
            };
        }
        return new Value(resultType, bits, tags);
    }

    private Value EvaluateCompoundAssignment(BoundCompoundAssignment compound)
    {
        NotInConstantMode();
        Place place = ResolvePlace(compound.Target);
        Value current = ConvertImplicitly(Read(place), compound.OperationType);
        Value value = Evaluate(compound.Value);
        Value result = EvaluateBinary(compound.Operator, current, value, compound.OperationType, compound.Span);
        Write(place, ConvertImplicitly(result, place.Type));
        return Read(place);
    }

    private Value EvaluateIncrement(BoundIncrement increment)
    {
        NotInConstantMode();
        Place place = ResolvePlace(increment.Target);
        Value before = Read(place);
        Value one = ConvertValue(Value.Scalar(ScalarKind.LiteralInt, 1, UnitTag.Bare), before.Type, ConversionKind.Splat);
        // The 1 takes the variable's unit
        Value unitOne = new Value(one.Type, one.Bits, before.Units.ToArray());
        Value after = EvaluateBinary(increment.IsIncrement ? BinaryOperator.Add : BinaryOperator.Subtract, before, unitOne, before.Type, increment.Span);
        Write(place, after);
        return increment.IsPrefix ? after : before;
    }

    private Value EvaluateConstruct(BoundConstruct construct)
    {
        List<(ulong Bits, ScalarKind Kind, UnitTag Unit)> components = new List<(ulong Bits, ScalarKind Kind, UnitTag Unit)>();
        foreach (BoundExpression source in construct.Sources)
        {
            Value value = Evaluate(source);
            for (int index = 0; index < value.Bits.Length; index++)
            {
                components.Add((value.Bits[index], value.KindAt(index), value.Units[index]));
            }
        }
        int count = construct.Type.ComponentCount;
        IReadOnlyList<ScalarKind> kinds = construct.Type.ComponentKinds;
        ulong[] bits = new ulong[count];
        UnitTag[] units = new UnitTag[count];
        for (int index = 0; index < count; index++)
        {
            // A single scalar fills every component
            (ulong sourceBits, ScalarKind sourceKind, UnitTag unit) = components[components.Count == 1 ? 0 : index];
            bits[index] = Scalars.Convert(sourceKind, kinds[index], sourceBits, _profile);
            units[index] = unit;
        }
        return new Value(construct.Type, bits, units);
    }

    // ---------------------------------------------------------------- Conversions

    private Value ConvertImplicitly(Value value, ShaderType type)
    {
        ConversionInfo conversion = TypeRules.Classify(value.Type, type, isExplicit: true);
        return conversion.Kind == ConversionKind.Identity ? value : ConvertValue(value, type, conversion.Kind);
    }

    public Value ConvertValue(Value value, ShaderType type, ConversionKind kind)
    {
        if (kind == ConversionKind.Identity && value.Type.Equals(type))
        {
            return value;
        }
        int count = type.ComponentCount;
        IReadOnlyList<ScalarKind> kinds = type.ComponentKinds;
        int[] sources = new int[count];
        switch (kind)
        {
            case ConversionKind.Splat:
                // all zeros: every component comes from the scalar
                break;
            case ConversionKind.Truncation when value.Type is NumericType { IsMatrix: true } from && type is NumericType { IsMatrix: true } to:
                for (int row = 0; row < to.Rows; row++)
                {
                    for (int column = 0; column < to.Columns; column++)
                    {
                        sources[row * to.Columns + column] = row * from.Columns + column;
                    }
                }
                break;
            default:
                // Numeric, flat and vector truncation keep components in order
                for (int index = 0; index < count; index++)
                {
                    sources[index] = index;
                }
                break;
        }
        ulong[] bits = new ulong[count];
        UnitTag[] units = new UnitTag[count];
        for (int index = 0; index < count; index++)
        {
            bits[index] = Scalars.Convert(value.KindAt(sources[index]), kinds[index], value.Bits[sources[index]], _profile);
            units[index] = value.Units[sources[index]];
        }
        return new Value(type, bits, units);
    }

    // ---------------------------------------------------------------- Places (assignment targets)

    private Place ResolvePlace(BoundExpression target)
    {
        switch (target)
        {
            case BoundVariable variable:
            {
                Value storage = Storage(variable.Variable, variable.Span);
                return new Place(storage, Enumerable.Range(0, storage.Bits.Length).ToArray(), variable.Type);
            }
            case BoundSwizzle swizzle:
            {
                Place place = ResolvePlace(swizzle.Operand);
                return new Place(place.Storage, swizzle.Indices.Select(index => place.Components[index]).ToArray(), swizzle.Type);
            }
            case BoundIndex index:
            {
                Place place = ResolvePlace(index.Operand);
                int element = CheckedIndex(Evaluate(index.Index), index.Length, index.Span);
                int size = index.Type.ComponentCount;
                return new Place(place.Storage, place.Components.Skip(element * size).Take(size).ToArray(), index.Type);
            }
            case BoundField field:
            {
                Place place = ResolvePlace(field.Operand);
                return new Place(place.Storage, place.Components.Skip(field.Field.ComponentOffset).Take(field.Field.Type.ComponentCount).ToArray(), field.Type);
            }
            default:
                throw new EvaluationException("this expression can't be assigned", target.Span);
        }
    }

    private Value Storage(VariableSymbol variable, SourceSpan span)
    {
        if (_frame.Locals.TryGetValue(variable, out Value? local))
        {
            return local;
        }
        if (_storage.TryGetValue(variable, out Value? stored))
        {
            return stored;
        }
        throw new EvaluationException($"'{variable.Name}' has no storage", span);
    }

    private static Value Read(Place place) =>
        new Value(place.Type, place.Components.Select(index => place.Storage.Bits[index]).ToArray(),
            place.Components.Select(index => place.Storage.Units[index]).ToArray());

    /// <summary>Writes a value of the place's type. The written components take the value's units.</summary>
    private static void Write(Place place, Value value)
    {
        for (int index = 0; index < place.Components.Length; index++)
        {
            place.Storage.Bits[place.Components[index]] = value.Bits[index];
            place.Storage.Units[place.Components[index]] = value.Units[index];
        }
    }

    // ---------------------------------------------------------------- Calls

    private Value EvaluateCall(BoundCall call)
    {
        FunctionSymbol function = call.Function;
        Frame frame = new Frame(function);
        List<(Place Place, ParameterSymbol Parameter)> writeBacks = new List<(Place Place, ParameterSymbol Parameter)>();
        for (int index = 0; index < function.Parameters.Count; index++)
        {
            ParameterSymbol parameter = function.Parameters[index];
            BoundExpression argument = call.Arguments[index];
            if (parameter.Mode == ParameterMode.In)
            {
                frame.Locals[parameter] = Evaluate(argument).Clone();
                continue;
            }
            // out / inout: copy in (inout) now, copy out after the call, in parameter order
            Place place = ResolvePlace(argument);
            writeBacks.Add((place, parameter));
            frame.Locals[parameter] = parameter.Mode == ParameterMode.InOut
                ? ConvertImplicitly(Read(place), parameter.Type).Clone()
                : Value.Zero(parameter.Type, UnitTag.Bare);
        }

        Value result = Invoke(function, frame, call.Span);
        foreach ((Place place, ParameterSymbol parameter) in writeBacks)
        {
            Write(place, ConvertImplicitly(frame.Locals[parameter], place.Type));
        }
        return result;
    }

    private Value Invoke(FunctionSymbol function, Frame frame, SourceSpan span)
    {
        if (function.Body == null)
        {
            throw new EvaluationException($"'{function.Name}' has no body", span);
        }
        if (_callDepth >= MaxCallDepth)
        {
            throw new EvaluationException("calls nested too deep (recursion isn't allowed in HLSL)", span);
        }
        Frame caller = _frame;
        _frame = frame;
        _callDepth++;
        try
        {
            _returnValue = null;
            Flow flow = Execute(function.Body);
            if (function.ReturnType is VoidType)
            {
                return Value.Void;
            }
            if (flow != Flow.Return || _returnValue == null)
            {
                throw new EvaluationException($"'{function.Name}' ended without returning a value", function.Syntax.NameSpan);
            }
            return _returnValue;
        }
        finally
        {
            _callDepth--;
            _frame = caller;
        }
    }

    private Value EvaluateIntrinsic(BoundIntrinsicCall call)
    {
        Value[] arguments = new Value[call.Arguments.Count];
        Place?[] outputs = new Place?[call.Arguments.Count];
        for (int index = 0; index < arguments.Length; index++)
        {
            if (call.Signature.Modes[index] == ParameterMode.In)
            {
                arguments[index] = Evaluate(call.Arguments[index]);
            }
            else
            {
                NotInConstantMode();
                outputs[index] = ResolvePlace(call.Arguments[index]);
                arguments[index] = Value.Zero(call.Signature.ParameterTypes[index], UnitTag.Bare);
            }
        }
        IntrinsicContext context = new IntrinsicContext(call, _profile, UnitsAt(call.Span), (severity, message) => Report(severity, message, call.Span));
        Value result = call.Intrinsic.Implementation(context, arguments);
        for (int index = 0; index < outputs.Length; index++)
        {
            if (outputs[index] is Place place && context.Outputs[index] is Value output)
            {
                Write(place, ConvertImplicitly(output, place.Type));
            }
        }
        return result;
    }
}
