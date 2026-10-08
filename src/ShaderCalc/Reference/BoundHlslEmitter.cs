using System.Globalization;
using System.Text;
using ShaderCalc.Binding;
using ShaderCalc.Syntax;

namespace ShaderCalc.Reference;

/// <summary>
/// Writes the bound tree back as plain HLSL that DXC accepts: C++ forms come out normalised (references become
/// in/inout, auto its type, std:: gone), macros expanded, unit literals as SI numbers. Implicit conversions are
/// left to DXC, so the reference also checks the interpreter's typing.
/// </summary>
public class BoundHlslEmitter
{
    private readonly StringBuilder _builder = new StringBuilder();

    public override string ToString() => _builder.ToString();

    protected StringBuilder Builder => _builder;

    // ---- Program ----

    public void EmitProgram(BoundProgram program, bool uniformsAsStatics)
    {
        foreach (StructType structure in program.Structs)
        {
            _builder.Append("struct ").Append(structure.Name).Append("\n{\n");
            foreach (StructField field in structure.Fields)
            {
                _builder.Append("    ").Append(Declaration(field.Type, field.Name)).Append(";\n");
            }
            _builder.Append("};\n\n");
        }

        // Prototypes first: functions may call each other in any order (default values go here, once)
        foreach (FunctionSymbol function in program.Functions)
        {
            _builder.Append(Signature(function, withDefaults: true)).Append(";\n");
        }
        _builder.Append('\n');

        foreach (VariableSymbol global in program.Globals)
        {
            string prefix = global.Kind == VariableKind.Uniform && uniformsAsStatics ? "static " : global.Kind == VariableKind.Static ? "static " : string.Empty;
            prefix += global.IsConst && global.Kind == VariableKind.Static ? "const " : string.Empty;
            _builder.Append(prefix).Append(Declaration(global.Type, global.Name));
            if (global.Initializer != null && !(global.Kind == VariableKind.Uniform && uniformsAsStatics))
            {
                _builder.Append(" = ").Append(Initializer(global.Initializer));
            }
            _builder.Append(";\n");
        }
        _builder.Append('\n');

        foreach (FunctionSymbol function in program.Functions)
        {
            if (function.Body == null)
            {
                continue;
            }
            _builder.Append(Signature(function)).Append('\n');
            EmitStatement(function.Body, 0);
            _builder.Append('\n');
        }
    }

    private string Signature(FunctionSymbol function, bool withDefaults = false)
    {
        string parameters = string.Join(", ", function.Parameters.Select(parameter =>
            (parameter.Mode switch { ParameterMode.Out => "out ", ParameterMode.InOut => "inout ", _ => string.Empty }) + Declaration(parameter.Type, parameter.Name)
            + (withDefaults && parameter.DefaultValue != null ? $" = {Expression(parameter.DefaultValue)}" : string.Empty)));
        return $"{function.ReturnType} {function.Name}({parameters})";
    }

    /// <summary>`float3 name`, `float name[2][3]`.</summary>
    public static string Declaration(ShaderType type, string name) =>
        type is ArrayType array ? $"{array.InnermostElement} {name}{array.Dimensions}" : $"{type} {name}";

    // ---- Statements ----

    public void EmitStatement(BoundStatement statement, int depth)
    {
        string indent = new string(' ', depth * 4);
        switch (statement)
        {
            case BoundBlock { IsScope: false } group:
                foreach (BoundStatement inner in group.Statements)
                {
                    EmitStatement(inner, depth);
                }
                break;
            case BoundBlock block:
                _builder.Append(indent).Append("{\n");
                foreach (BoundStatement inner in block.Statements)
                {
                    EmitStatement(inner, depth + 1);
                }
                _builder.Append(indent).Append("}\n");
                break;
            case BoundIf ifStatement:
                _builder.Append(indent).Append($"if ({Expression(ifStatement.Condition)})\n");
                EmitNested(ifStatement.Then, depth);
                if (ifStatement.Else != null)
                {
                    _builder.Append(indent).Append("else\n");
                    EmitNested(ifStatement.Else, depth);
                }
                break;
            case BoundLoop { IsDoWhile: true } doWhile:
                _builder.Append(indent).Append("do\n");
                EmitNested(doWhile.Body, depth);
                _builder.Append(indent).Append($"while ({Expression(doWhile.Condition!)});\n");
                break;
            case BoundLoop loop:
                _builder.Append(indent).Append("{\n");
                if (loop.Initializer != null)
                {
                    EmitStatement(loop.Initializer, depth + 1);
                }
                _builder.Append(indent).Append($"    for (; {(loop.Condition == null ? string.Empty : Expression(loop.Condition))}; " +
                    $"{(loop.Step == null ? string.Empty : Expression(loop.Step))})\n");
                EmitNested(loop.Body, depth + 1);
                _builder.Append(indent).Append("}\n");
                break;
            case BoundSwitch switchStatement:
                _builder.Append(indent).Append($"switch ({Expression(switchStatement.Value)})\n").Append(indent).Append("{\n");
                foreach (BoundSwitchSection section in switchStatement.Sections)
                {
                    foreach (long label in section.Labels)
                    {
                        _builder.Append(indent).Append($"case {label.ToString(CultureInfo.InvariantCulture)}:\n");
                    }
                    if (section.IsDefault)
                    {
                        _builder.Append(indent).Append("default:\n");
                    }
                    _builder.Append(indent).Append("{\n");
                    foreach (BoundStatement inner in section.Statements)
                    {
                        EmitStatement(inner, depth + 2);
                    }
                    _builder.Append(indent).Append("}\n");
                }
                _builder.Append(indent).Append("}\n");
                break;
            case BoundReturn returnStatement:
                _builder.Append(indent).Append(returnStatement.Value == null ? "return;\n" : $"return {Expression(returnStatement.Value)};\n");
                break;
            case BoundJump jump:
                _builder.Append(indent).Append(jump.IsBreak ? "break;\n" : "continue;\n");
                break;
            case BoundVariableDeclaration declaration:
                _builder.Append(indent).Append(declaration.Variable.IsConst ? "const " : string.Empty).Append(Declaration(declaration.Variable.Type, declaration.Variable.Name));
                if (declaration.Initializer != null)
                {
                    _builder.Append(" = ").Append(Initializer(declaration.Initializer));
                }
                _builder.Append(";\n");
                break;
            case BoundExpressionStatement expression:
                _builder.Append(indent).Append(Expression(expression.Expression)).Append(";\n");
                break;
            default:
                throw new InvalidOperationException($"can't emit {statement.GetType().Name}");
        }
    }

    private void EmitNested(BoundStatement statement, int depth) => EmitStatement(statement, statement is BoundBlock ? depth : depth + 1);

    private string Initializer(BoundExpression initializer) => initializer switch
    {
        BoundConstruct { IsInitializerList: true } list => "{ " + string.Join(", ", list.Sources.Select(Expression)) + " }",
        BoundConversion { IsExplicit: false } conversion when conversion.Operand is BoundConstruct { IsInitializerList: true } => Initializer(conversion.Operand),
        _ => Expression(initializer),
    };

    // ---- Expressions ----

    public virtual string Expression(BoundExpression expression) => expression switch
    {
        BoundLiteral literal => Literal(literal.Value),
        BoundUnitLiteral unitLiteral => RealLiteral(unitLiteral.SiValue),
        BoundVariable variable => variable.Variable.Name,
        BoundUnary unary => $"({UnarySymbol(unary.Operator)}{Expression(unary.Operand)})",
        BoundBinary binary => $"({Expression(binary.Left)} {TypeRules.Symbol(binary.Operator)} {Expression(binary.Right)})",
        BoundLogical logical => $"({Expression(logical.Left)} {(logical.IsAnd ? "&&" : "||")} {Expression(logical.Right)})",
        BoundAssignment assignment => $"({Expression(assignment.Target)} = {Expression(assignment.Value)})",
        BoundCompoundAssignment compound => $"({Expression(compound.Target)} {TypeRules.Symbol(compound.Operator)}= {Expression(compound.Value)})",
        BoundIncrement { IsPrefix: true } increment => $"({(increment.IsIncrement ? "++" : "--")}{Expression(increment.Target)})",
        BoundIncrement increment => $"({Expression(increment.Target)}{(increment.IsIncrement ? "++" : "--")})",
        BoundConditional conditional => $"({Expression(conditional.Condition)} ? {Expression(conditional.WhenTrue)} : {Expression(conditional.WhenFalse)})",
        BoundCall call => $"{call.Function.Name}({string.Join(", ", call.Arguments.Take(ExplicitArgumentCount(call)).Select(Expression))})",
        BoundIntrinsicCall call => $"{call.Intrinsic.Name}({string.Join(", ", call.Arguments.Select(Expression))})",
        BoundConversion { IsExplicit: true } conversion => $"(({CastType(conversion.Type)})({Expression(conversion.Operand)}))",
        BoundConversion conversion => Expression(conversion.Operand),
        BoundConstruct construct => $"{construct.Type}({string.Join(", ", construct.Sources.Select(Expression))})",
        BoundSwizzle swizzle => $"{Expression(swizzle.Operand)}.{swizzle.Text}",
        BoundIndex index => $"{Expression(index.Operand)}[{Expression(index.Index)}]",
        BoundField field => $"{Expression(field.Operand)}.{field.Field.Name}",
        BoundComma comma => $"({Expression(comma.Left)}, {Expression(comma.Right)})",
        _ => throw new InvalidOperationException($"can't emit {expression.GetType().Name}"),
    };

    /// <summary>Default arguments are filled in by the binder; DXC fills them in itself.</summary>
    private static int ExplicitArgumentCount(BoundCall call)
    {
        int count = call.Arguments.Count;
        while (count > 0 && call.Function.Parameters[count - 1].DefaultValue == call.Arguments[count - 1])
        {
            count--;
        }
        return count;
    }

    private static string CastType(ShaderType type) => type is ArrayType array ? $"{array.InnermostElement}{array.Dimensions}" : type.ToString()!;

    private static string UnarySymbol(UnaryOperator operation) => operation switch
    {
        UnaryOperator.Negate => "-",
        UnaryOperator.LogicalNot => "!",
        UnaryOperator.BitwiseNot => "~",
        _ => "+",
    };

    public static string Literal(Value value)
    {
        ulong bits = value.Bits[0];
        return value.KindAt(0) switch
        {
            ScalarKind.Bool => bits != 0 ? "true" : "false",
            ScalarKind.LiteralInt => ((long)bits).ToString(CultureInfo.InvariantCulture),
            ScalarKind.Int => ((int)bits).ToString(CultureInfo.InvariantCulture),
            ScalarKind.UInt => ((uint)bits).ToString(CultureInfo.InvariantCulture) + "u",
            ScalarKind.Int64 => ((long)bits).ToString(CultureInfo.InvariantCulture) + "ll",
            ScalarKind.UInt64 => bits.ToString(CultureInfo.InvariantCulture) + "ull",
            ScalarKind.LiteralFloat => RealLiteral(Scalars.ToDouble(bits)),
            ScalarKind.Double => RealLiteral(Scalars.ToDouble(bits)) + "L",
            _ => FloatLiteral(Scalars.ToFloat(bits)),
        };
    }

    private static string RealLiteral(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static string FloatLiteral(float value)
    {
        if (!float.IsFinite(value))
        {
            return $"asfloat(0x{BitConverter.SingleToUInt32Bits(value):X8}u)";
        }
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return (text.Contains('.') || text.Contains('E') ? text : text + ".0") + "f";
    }
}
