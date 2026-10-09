namespace ShaderCalc.Binding;

/// <summary>Enumerates the bound tree (expressions, statements, and the bodies of called functions).</summary>
public static class BoundTreeWalker
{
    public static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundUnary unary => new[] { unary.Operand },
        BoundBinary binary => new[] { binary.Left, binary.Right },
        BoundLogical logical => new[] { logical.Left, logical.Right },
        BoundAssignment assignment => new[] { assignment.Target, assignment.Value },
        BoundCompoundAssignment compound => new[] { compound.Target, compound.Value },
        BoundIncrement increment => new[] { increment.Target },
        BoundConditional conditional => new[] { conditional.Condition, conditional.WhenTrue, conditional.WhenFalse },
        BoundCall call => call.Arguments,
        BoundIntrinsicCall call => call.Arguments,
        BoundConversion conversion => new[] { conversion.Operand },
        BoundConstruct construct => construct.Sources,
        BoundSwizzle swizzle => new[] { swizzle.Operand },
        BoundIndex index => new[] { index.Operand, index.Index },
        BoundField field => new[] { field.Operand },
        BoundComma comma => new[] { comma.Left, comma.Right },
        _ => Array.Empty<BoundExpression>(),
    };

    public static IEnumerable<BoundExpression> Expressions(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements.SelectMany(Expressions),
        BoundVariableDeclaration { Initializer: not null } declaration => new[] { declaration.Initializer },
        BoundExpressionStatement expression => new[] { expression.Expression },
        BoundIf branch => new[] { branch.Condition }.Concat(Expressions(branch.Then)).Concat(branch.Else == null ? Array.Empty<BoundExpression>() : Expressions(branch.Else)),
        BoundLoop loop => (loop.Initializer == null ? Array.Empty<BoundExpression>() : Expressions(loop.Initializer))
            .Concat(new[] { loop.Condition, loop.Step }.OfType<BoundExpression>())
            .Concat(Expressions(loop.Body)),
        BoundSwitch switchStatement => new[] { switchStatement.Value }.Concat(switchStatement.Sections.SelectMany(section => section.Statements.SelectMany(Expressions))),
        BoundReturn { Value: not null } returnStatement => new[] { returnStatement.Value },
        _ => Array.Empty<BoundExpression>(),
    };

    /// <summary>Every expression reachable from the roots, following calls into function bodies (each once).</summary>
    public static IEnumerable<BoundExpression> Reachable(IEnumerable<BoundExpression> roots)
    {
        HashSet<FunctionSymbol> visitedFunctions = new HashSet<FunctionSymbol>();
        Stack<BoundExpression> pending = new Stack<BoundExpression>(roots);
        while (pending.Count > 0)
        {
            BoundExpression expression = pending.Pop();
            yield return expression;
            foreach (BoundExpression child in Children(expression))
            {
                pending.Push(child);
            }
            if (expression is BoundCall call && call.Function.Body != null && visitedFunctions.Add(call.Function))
            {
                foreach (BoundExpression inner in Expressions(call.Function.Body))
                {
                    pending.Push(inner);
                }
            }
        }
    }
}
