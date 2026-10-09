using ShaderCalc.Evaluation;
using ShaderCalc.Syntax;
using ShaderCalc.Units;

namespace ShaderCalc.Binding;

/// <summary>
/// Type-checks syntax into the bound tree: resolves names and types, picks overloads (user functions and
/// intrinsics), inserts every implicit conversion, and reports problems for the whole text at once.
/// </summary>
public sealed class Binder
{
    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _variables = new Dictionary<string, VariableSymbol>(StringComparer.Ordinal);

        public Scope(Scope? parent)
        {
            Parent = parent;
        }

        public Scope? Parent { get; }

        public bool TryDeclare(VariableSymbol variable) => _variables.TryAdd(variable.Name, variable);

        public VariableSymbol? Lookup(string name) => _variables.TryGetValue(name, out VariableSymbol? variable) ? variable : Parent?.Lookup(name);
    }

    private string _sourceName;
    private readonly SemanticsProfile _profile;
    private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
    private readonly Dictionary<string, ShaderType> _typeNames = new Dictionary<string, ShaderType>(StringComparer.Ordinal);
    private readonly List<StructType> _structs = new List<StructType>();
    private readonly Dictionary<string, List<FunctionSymbol>> _functions = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
    private readonly List<FunctionSymbol> _functionOrder = new List<FunctionSymbol>();
    private readonly Dictionary<string, VariableSymbol> _globals = new Dictionary<string, VariableSymbol>(StringComparer.Ordinal);
    private readonly List<VariableSymbol> _globalOrder = new List<VariableSymbol>();
    private readonly Dictionary<FunctionSymbol, HashSet<FunctionSymbol>> _calls = new Dictionary<FunctionSymbol, HashSet<FunctionSymbol>>();
    private readonly IReadOnlyDictionary<string, VariableSymbol>? _sessionVariables;
    private readonly bool _isInteractive;

    private Scope _scope = new Scope(null);
    private FunctionSymbol? _function;
    private int _loopDepth;
    private int _breakableDepth;

    private Binder(string sourceName, SemanticsProfile profile, IReadOnlyDictionary<string, VariableSymbol>? sessionVariables, bool isInteractive)
    {
        _sourceName = sourceName;
        _profile = profile;
        _sessionVariables = sessionVariables;
        _isInteractive = isInteractive;
    }

    // ---------------------------------------------------------------- Entry points

    public static BoundProgram BindProgram(CompilationUnitSyntax unit, string sourceName, SemanticsProfile profile, IEnumerable<Diagnostic> parseDiagnostics)
    {
        Binder binder = new Binder(sourceName, profile, null, isInteractive: false);
        binder._diagnostics.AddRange(parseDiagnostics);
        binder.DeclareProgram(unit.Declarations);
        return binder.ToProgram();
    }

    /// <summary>Binds a calculator line against a program and the session's variables.</summary>
    public static (BoundInteractive Line, IReadOnlyList<Diagnostic> Diagnostics) BindInteractive(IReadOnlyList<StatementSyntax> statements, BoundProgram program,
        IReadOnlyDictionary<string, VariableSymbol> sessionVariables, string sourceName, SemanticsProfile profile)
    {
        Binder binder = new Binder(sourceName, profile, sessionVariables, isInteractive: true);
        binder.Import(program);
        BoundInteractive line = binder.BindLine(statements);
        return (line, binder._diagnostics.Items);
    }

    /// <summary>
    /// Binds worksheet files as one program: declarations of every file are shared (types, then function signatures),
    /// top-level lines are bound in file then line order (their variables become globals the functions can read),
    /// then function bodies.
    /// </summary>
    public static BoundWorksheet BindWorksheet(IReadOnlyList<WorksheetSource> sources, SemanticsProfile profile, IEnumerable<Diagnostic> parseDiagnostics)
    {
        Binder binder = new Binder(string.Empty, profile, null, isInteractive: true);
        binder._diagnostics.AddRange(parseDiagnostics);
        foreach (WorksheetSource source in sources)
        {
            binder._sourceName = source.Name;
            foreach (DeclarationSyntax declaration in source.Items.OfType<DeclarationSyntax>())
            {
                binder.DeclareType(declaration);
            }
        }
        foreach (WorksheetSource source in sources)
        {
            binder._sourceName = source.Name;
            foreach (FunctionSyntax function in source.Items.OfType<FunctionSyntax>())
            {
                binder.DeclareFunction(function);
            }
        }

        List<ScriptLine> lines = new List<ScriptLine>();
        foreach (WorksheetSource source in sources)
        {
            binder._sourceName = source.Name;
            foreach (SyntaxNode item in source.Items)
            {
                if (item is GlobalVariableSyntax global)
                {
                    binder.DeclareGlobal(global.Declaration);
                }
                else if (item is StatementSyntax statement)
                {
                    lines.Add(binder.BindScriptLine(source.Name, statement));
                }
            }
        }

        foreach (FunctionSymbol function in binder._functionOrder)
        {
            if (function.Syntax.Body != null)
            {
                binder.BindFunctionBody(function);
            }
            else
            {
                binder._sourceName = function.SourceName;
                binder.Error(function.Syntax.NameSpan, $"'{function.Signature}' is declared but never defined");
            }
        }
        binder.ReportRecursion();
        return new BoundWorksheet(binder.ToProgram(), lines);
    }

    /// <summary>
    /// One top-level worksheet line. Its variables are globals (functions can read them); a new name assigned with `=`
    /// declares one of the value's type.
    /// </summary>
    private ScriptLine BindScriptLine(string sourceName, StatementSyntax statement)
    {
        List<VariableSymbol> declared = new List<VariableSymbol>();
        if (statement is ExpressionStatementSyntax { Expression: AssignmentSyntax { Operator: "=", Target: NameSyntax name } assignment }
            && LookupVariable(name.Name) == null)
        {
            BoundExpression value = BindExpression(assignment.Value);
            if (value is BoundErrorExpression || value.Type is VoidType)
            {
                if (value is not BoundErrorExpression)
                {
                    Error(assignment.Value.Span, "this expression has no value");
                }
                return new ScriptLine(sourceName, statement.Span, new BoundBlock(Array.Empty<BoundStatement>(), statement.Span), null, declared);
            }
            ShaderType type = Materialize(value.Type);
            VariableSymbol variable = new VariableSymbol(name.Name, type, VariableKind.Uniform, false, name.Span);
            DeclareScriptGlobal(variable);
            declared.Add(variable);
            return new ScriptLine(sourceName, statement.Span, new BoundVariableDeclaration(variable, Convert(value, type, false, assignment.Value.Span), statement.Span),
                null, declared);
        }

        if (statement is VariableDeclarationSyntax declaration)
        {
            bool isStatic = declaration.Modifiers.HasFlag(StorageModifiers.Static);
            bool isConst = declaration.Modifiers.HasFlag(StorageModifiers.Const);
            List<BoundStatement> statements = new List<BoundStatement>();
            foreach (DeclaratorSyntax declarator in declaration.Declarators)
            {
                (VariableSymbol? variable, BoundExpression? initializer) = DeclareVariable(declaration.Type, declarator,
                    isStatic ? VariableKind.Static : VariableKind.Uniform, isConst, isConst && isStatic);
                if (variable == null)
                {
                    continue;
                }
                DeclareScriptGlobal(variable);
                variable.Initializer = initializer;
                if (initializer != null && isConst && isStatic)
                {
                    variable.ConstantValue = Evaluator.TryEvaluateConstant(initializer, _profile);
                }
                declared.Add(variable);
                statements.Add(new BoundVariableDeclaration(variable, initializer, declaration.Span));
            }
            BoundStatement bound = statements.Count == 1 ? statements[0] : new BoundBlock(statements, declaration.Span, isScope: false);
            return new ScriptLine(sourceName, statement.Span, bound, null, declared);
        }

        BoundStatement boundStatement = BindStatement(statement);
        BoundExpression? result = boundStatement is BoundExpressionStatement { Expression: var expression } && expression.Type is not VoidType
            && expression is not BoundErrorExpression ? expression : null;
        return new ScriptLine(sourceName, statement.Span, boundStatement, result, declared);
    }

    private void DeclareScriptGlobal(VariableSymbol variable)
    {
        if (!_globals.TryAdd(variable.Name, variable))
        {
            Error(variable.Span, $"'{variable.Name}' is already defined");
            return;
        }
        _globalOrder.Add(variable);
    }

    private void DeclareType(DeclarationSyntax declaration)
    {
        switch (declaration)
        {
            case StructSyntax structure:
                DeclareStruct(structure);
                break;
            case TypedefSyntax typedef:
                ShaderType? aliased = ResolveType(typedef.Type);
                if (aliased != null)
                {
                    _typeNames[typedef.Name] = WithArrayDimensions(aliased, typedef.ArrayDimensions, typedef.NameSpan) ?? aliased;
                }
                break;
        }
    }

    private void Import(BoundProgram program)
    {
        foreach (StructType structure in program.Structs)
        {
            _structs.Add(structure);
        }
        foreach ((string name, ShaderType type) in program.TypeNames)
        {
            _typeNames[name] = type;
        }
        foreach (VariableSymbol global in program.Globals)
        {
            _globals[global.Name] = global;
            _globalOrder.Add(global);
        }
        foreach (FunctionSymbol function in program.Functions)
        {
            AddFunction(function);
        }
    }

    private BoundProgram ToProgram() =>
        new BoundProgram(_structs, _typeNames, _globalOrder, _functionOrder, _diagnostics.Items);

    // ---------------------------------------------------------------- Diagnostics

    private void Error(SourceSpan span, string message) => _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, _sourceName, span, _function?.Name));

    private void Warning(SourceSpan span, string message) => _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, message, _sourceName, span, _function?.Name));

    // ---------------------------------------------------------------- Program

    private void DeclareProgram(IReadOnlyList<DeclarationSyntax> declarations)
    {
        // Types first, then signatures (so functions may be used before their definition), then globals, then bodies
        foreach (DeclarationSyntax declaration in declarations)
        {
            DeclareType(declaration);
        }

        foreach (FunctionSyntax function in declarations.OfType<FunctionSyntax>())
        {
            DeclareFunction(function);
        }

        foreach (GlobalVariableSyntax global in declarations.OfType<GlobalVariableSyntax>())
        {
            DeclareGlobal(global.Declaration);
        }

        foreach (FunctionSymbol function in _functionOrder)
        {
            if (function.Syntax.Body != null)
            {
                BindFunctionBody(function);
            }
            else
            {
                Error(function.Syntax.NameSpan, $"'{function.Signature}' is declared but never defined");
            }
        }
        ReportRecursion();
    }

    private void DeclareStruct(StructSyntax syntax)
    {
        List<(string Name, ShaderType Type)> fields = new List<(string Name, ShaderType Type)>();
        foreach (VariableDeclarationSyntax field in syntax.Fields)
        {
            ShaderType? fieldType = ResolveType(field.Type);
            if (fieldType == null)
            {
                continue;
            }
            foreach (DeclaratorSyntax declarator in field.Declarators)
            {
                if (declarator.Initializer != null)
                {
                    Warning(declarator.NameSpan, $"default value of field '{declarator.Name}' is ignored (HLSL structs have none)");
                }
                if (fields.Any(existing => existing.Name == declarator.Name))
                {
                    Error(declarator.NameSpan, $"'{syntax.Name}' already has a field '{declarator.Name}'");
                    continue;
                }
                ShaderType? declaratorType = WithArrayDimensions(fieldType, declarator.ArrayDimensions, declarator.NameSpan);
                if (declaratorType != null)
                {
                    fields.Add((declarator.Name, declaratorType));
                }
            }
        }
        StructType structType = new StructType(syntax.Name, fields);
        if (_typeNames.ContainsKey(syntax.Name))
        {
            Error(syntax.NameSpan, $"type '{syntax.Name}' is already defined");
            return;
        }
        _typeNames[syntax.Name] = structType;
        _structs.Add(structType);
    }

    private void DeclareFunction(FunctionSyntax syntax)
    {
        ShaderType? returnType = syntax.ReturnType is NamedTypeSyntax { Name: "void" } ? VoidType.Instance : ResolveType(syntax.ReturnType);
        if (returnType == null)
        {
            return;
        }
        List<ParameterSymbol> parameters = new List<ParameterSymbol>();
        foreach (ParameterSyntax parameter in syntax.Parameters)
        {
            ShaderType? parameterType = ResolveType(parameter.Type);
            ShaderType? withDimensions = parameterType == null ? null : WithArrayDimensions(parameterType, parameter.ArrayDimensions, parameter.NameSpan);
            if (withDimensions == null)
            {
                return;
            }
            ParameterSymbol symbol = new ParameterSymbol(parameter.Name, withDimensions, parameter.Mode, parameter.IsConst, parameter.NameSpan);
            if (parameter.DefaultValue != null)
            {
                symbol.DefaultValue = Convert(BindExpression(parameter.DefaultValue), withDimensions, false, parameter.DefaultValue.Span);
            }
            parameters.Add(symbol);
        }

        // A definition completes an earlier prototype with the same parameter types
        FunctionSymbol? existing = _functions.TryGetValue(syntax.Name, out List<FunctionSymbol>? overloads)
            ? overloads.FirstOrDefault(candidate => candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(parameters.Select(parameter => parameter.Type)))
            : null;
        if (existing != null)
        {
            if (existing.Syntax.Body != null && syntax.Body != null)
            {
                Error(syntax.NameSpan, $"'{existing.Signature}' is already defined");
            }
            else if (syntax.Body != null)
            {
                existing.AttachDefinition(syntax);
                existing.SourceName = _sourceName;
            }
            return;
        }
        AddFunction(new FunctionSymbol(syntax.Name, returnType, parameters, syntax) { SourceName = _sourceName });
    }

    private void AddFunction(FunctionSymbol function)
    {
        if (!_functions.TryGetValue(function.Name, out List<FunctionSymbol>? overloads))
        {
            _functions[function.Name] = overloads = new List<FunctionSymbol>();
        }
        overloads.Add(function);
        _functionOrder.Add(function);
    }

    private void DeclareGlobal(VariableDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.HasFlag(StorageModifiers.GroupShared))
        {
            Warning(declaration.Span, "groupshared is treated as an ordinary static variable (one thread)");
        }
        bool isStatic = declaration.Modifiers.HasFlag(StorageModifiers.Static) || declaration.Modifiers.HasFlag(StorageModifiers.GroupShared);
        bool isConst = declaration.Modifiers.HasFlag(StorageModifiers.Const);
        foreach (DeclaratorSyntax declarator in declaration.Declarators)
        {
            (VariableSymbol? symbol, BoundExpression? initializer) = DeclareVariable(declaration.Type, declarator,
                isStatic ? VariableKind.Static : VariableKind.Uniform, isConst || !isStatic, requireInitializer: isConst && isStatic);
            if (symbol == null)
            {
                continue;
            }
            if (!_globals.TryAdd(symbol.Name, symbol))
            {
                Error(declarator.NameSpan, $"'{symbol.Name}' is already defined");
                continue;
            }
            symbol.Initializer = initializer;
            // Only static const folds; a uniform's initializer is just its default value
            if (initializer != null && isConst && isStatic)
            {
                symbol.ConstantValue = Evaluator.TryEvaluateConstant(initializer, _profile);
            }
            _globalOrder.Add(symbol);
        }
    }

    private void BindFunctionBody(FunctionSymbol function)
    {
        _sourceName = function.SourceName;
        _function = function;
        _calls[function] = new HashSet<FunctionSymbol>();
        _scope = new Scope(null);
        foreach (ParameterSymbol parameter in function.Parameters)
        {
            if (!_scope.TryDeclare(parameter))
            {
                Error(parameter.Span, $"parameter '{parameter.Name}' is declared twice");
            }
        }
        BoundBlock body = BindBlock(function.Syntax.Body!, newScope: false);
        function.Body = body;
        if (function.ReturnType is not VoidType && !AlwaysReturns(body))
        {
            Warning(function.Syntax.NameSpan, $"not every path of '{function.Name}' returns a value");
        }
        _function = null;
    }

    private void ReportRecursion()
    {
        HashSet<FunctionSymbol> reported = new HashSet<FunctionSymbol>();
        foreach (FunctionSymbol function in _calls.Keys)
        {
            if (!reported.Contains(function) && Reaches(function, function, new HashSet<FunctionSymbol>()))
            {
                reported.Add(function);
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, $"'{function.Name}' is recursive: HLSL doesn't allow recursion", _sourceName,
                    function.Syntax.NameSpan, function.Name));
            }
        }
    }

    private bool Reaches(FunctionSymbol from, FunctionSymbol target, HashSet<FunctionSymbol> visited)
    {
        if (!_calls.TryGetValue(from, out HashSet<FunctionSymbol>? callees))
        {
            return false;
        }
        foreach (FunctionSymbol callee in callees)
        {
            if (callee == target || (visited.Add(callee) && Reaches(callee, target, visited)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool AlwaysReturns(BoundStatement statement) => statement switch
    {
        BoundReturn => true,
        BoundBlock block => block.Statements.Any(AlwaysReturns),
        BoundIf { Else: not null } branch => AlwaysReturns(branch.Then) && AlwaysReturns(branch.Else),
        BoundLoop { Condition: null } loop => !ContainsBreak(loop.Body),
        BoundSwitch switchStatement => switchStatement.Sections.Any(section => section.IsDefault)
            && switchStatement.Sections.All(section => section.Statements.Any(AlwaysReturns)),
        _ => false,
    };

    private static bool ContainsBreak(BoundStatement statement) => statement switch
    {
        BoundJump { IsBreak: true } => true,
        BoundBlock block => block.Statements.Any(ContainsBreak),
        BoundIf branch => ContainsBreak(branch.Then) || (branch.Else != null && ContainsBreak(branch.Else)),
        _ => false,
    };

    // ---------------------------------------------------------------- Calculator lines

    private BoundInteractive BindLine(IReadOnlyList<StatementSyntax> statements)
    {
        List<BoundStatement> bound = new List<BoundStatement>();
        List<VariableSymbol> declared = new List<VariableSymbol>();
        BoundExpression? result = null;
        VariableSymbol? resultVariable = null;
        foreach (StatementSyntax statement in statements)
        {
            result = null;
            resultVariable = null;
            // `x = value` with a new name declares a session variable of the value's type
            if (statement is ExpressionStatementSyntax { Expression: AssignmentSyntax { Operator: "=", Target: NameSyntax name } assignment }
                && LookupVariable(name.Name) == null)
            {
                BoundExpression value = BindExpression(assignment.Value);
                if (value is BoundErrorExpression || value.Type is VoidType)
                {
                    if (value.Type is VoidType && value is not BoundErrorExpression)
                    {
                        Error(assignment.Value.Span, "this expression has no value");
                    }
                    continue;
                }
                ShaderType type = Materialize(value.Type);
                VariableSymbol variable = new VariableSymbol(name.Name, type, VariableKind.Session, false, name.Span);
                _scope.TryDeclare(variable);
                declared.Add(variable);
                bound.Add(new BoundVariableDeclaration(variable, Convert(value, type, false, assignment.Value.Span), statement.Span));
                resultVariable = variable;
                continue;
            }
            if (statement is VariableDeclarationSyntax declaration)
            {
                foreach (DeclaratorSyntax declarator in declaration.Declarators)
                {
                    (VariableSymbol? variable, BoundExpression? initializer) = DeclareVariable(declaration.Type, declarator, VariableKind.Session,
                        declaration.Modifiers.HasFlag(StorageModifiers.Const), declaration.Modifiers.HasFlag(StorageModifiers.Const));
                    if (variable == null)
                    {
                        continue;
                    }
                    _scope.TryDeclare(variable);
                    declared.Add(variable);
                    bound.Add(new BoundVariableDeclaration(variable, initializer, declaration.Span));
                    resultVariable = variable;
                }
                continue;
            }
            BoundStatement boundStatement = BindStatement(statement);
            bound.Add(boundStatement);
            if (boundStatement is BoundExpressionStatement expressionStatement && expressionStatement.Expression.Type is not VoidType
                && expressionStatement.Expression is not BoundErrorExpression)
            {
                result = expressionStatement.Expression;
            }
        }
        return new BoundInteractive(bound, result, resultVariable, declared);
    }

    /// <summary>The type a value gets when nothing else decides: literal int → int, literal float → float.</summary>
    public static ShaderType Materialize(ShaderType type) => type is NumericType numeric ? numeric.WithKind(numeric.Kind.Materialized()) : type;

    // ---------------------------------------------------------------- Types

    private ShaderType? ResolveType(TypeSyntax syntax)
    {
        switch (syntax)
        {
            case NamedTypeSyntax named:
                if (TryParseBuiltinType(named.Name, out ShaderType? builtin, out string? error))
                {
                    return builtin;
                }
                if (error != null)
                {
                    Error(named.Span, error);
                    return null;
                }
                if (_typeNames.TryGetValue(named.Name, out ShaderType? declared))
                {
                    return declared;
                }
                Error(named.Span, $"unknown type '{named.Name}'");
                return null;
            case GenericTypeSyntax generic:
                ShaderType? element = ResolveType(generic.Element);
                if (element is not NumericType { IsScalar: true } scalar)
                {
                    if (element != null)
                    {
                        Error(generic.Element.Span, $"{generic.Name}<> needs a scalar type, got {element}");
                    }
                    return null;
                }
                int expected = generic.Name == "vector" ? 1 : 2;
                if (generic.Dimensions.Count != expected)
                {
                    Error(generic.Span, $"{generic.Name}<> takes {expected + 1} arguments");
                    return null;
                }
                int[] dimensions = generic.Dimensions.Select(dimension => ConstantInt(dimension, 1, 4) ?? 0).ToArray();
                if (dimensions.Any(dimension => dimension == 0))
                {
                    return null;
                }
                return generic.Name == "vector" ? NumericType.Vector(scalar.Kind, dimensions[0]) : NumericType.Matrix(scalar.Kind, dimensions[0], dimensions[1]);
            case AutoTypeSyntax:
                Error(syntax.Span, "'auto' needs an initializer to take its type from");
                return null;
            default:
                return null;
        }
    }

    /// <summary>float, uint3, half2x2, dword, min16float... (half and min-precision types are 32-bit, as DXC compiles them by default).</summary>
    public static bool TryParseBuiltinType(string name, out ShaderType? type, out string? error)
    {
        type = null;
        error = null;
        if (name == "vector")
        {
            type = NumericType.Vector(ScalarKind.Float, 4);
            return true;
        }
        if (name == "matrix")
        {
            type = NumericType.Matrix(ScalarKind.Float, 4, 4);
            return true;
        }
        if (!Parser.IsBuiltinTypeName(name))
        {
            return false;
        }
        string scalarName = name.TrimEnd('1', '2', '3', '4', 'x');
        ScalarKind? kind = scalarName switch
        {
            "bool" => ScalarKind.Bool,
            "int" or "int32_t" or "min16int" or "min12int" => ScalarKind.Int,
            "uint" or "dword" or "uint32_t" or "min16uint" => ScalarKind.UInt,
            "int64_t" => ScalarKind.Int64,
            "uint64_t" => ScalarKind.UInt64,
            "float" or "float32_t" or "half" or "min16float" or "min10float" => ScalarKind.Float,
            "double" or "float64_t" => ScalarKind.Double,
            _ => null,
        };
        if (kind == null)
        {
            error = $"'{name}' needs 16-bit types (-enable-16bit-types), which aren't supported";
            return false;
        }
        string suffix = name[scalarName.Length..];
        type = suffix.Length switch
        {
            0 => NumericType.Scalar(kind.Value),
            1 => NumericType.Vector(kind.Value, suffix[0] - '0'),
            _ => NumericType.Matrix(kind.Value, suffix[0] - '0', suffix[2] - '0'),
        };
        return true;
    }

    private ShaderType? WithArrayDimensions(ShaderType element, IReadOnlyList<ExpressionSyntax?> dimensions, SourceSpan span, int? unsizedLength = null)
    {
        ShaderType result = element;
        for (int index = dimensions.Count - 1; index >= 0; index--)
        {
            ExpressionSyntax? dimension = dimensions[index];
            int? length = dimension == null ? (index == 0 ? unsizedLength : null) : ConstantInt(dimension, 1, 1 << 20);
            if (length == null)
            {
                if (dimension == null)
                {
                    Error(span, "an array without a length needs an initializer list");
                }
                return null;
            }
            result = new ArrayType(result, length.Value);
        }
        return result;
    }

    /// <summary>A compile-time integer in [minimum, maximum] (array lengths, vector sizes).</summary>
    private int? ConstantInt(ExpressionSyntax syntax, int minimum, int maximum)
    {
        BoundExpression bound = BindExpression(syntax);
        if (bound is BoundErrorExpression)
        {
            return null;
        }
        Value? value = bound.Type is NumericType { IsScalar: true, Kind: var kind } && (kind.IsInteger() || kind == ScalarKind.Bool)
            ? Evaluator.TryEvaluateConstant(bound, _profile)
            : null;
        if (value == null)
        {
            Error(syntax.Span, "needs a constant integer");
            return null;
        }
        long number = (long)value.GetNumber(0);
        if (number < minimum || number > maximum)
        {
            Error(syntax.Span, $"{number} is out of range [{minimum}, {maximum}]");
            return null;
        }
        return (int)number;
    }

    // ---------------------------------------------------------------- Variables

    /// <summary>Declares one declarator: type (auto, array lengths from the initializer) and converted initializer.</summary>
    private (VariableSymbol? Variable, BoundExpression? Initializer) DeclareVariable(TypeSyntax typeSyntax, DeclaratorSyntax declarator, VariableKind kind,
        bool isConst, bool requireInitializer)
    {
        ShaderType? type;
        BoundExpression? initializer = null;
        if (typeSyntax is AutoTypeSyntax)
        {
            if (declarator.Initializer is null or InitializerListSyntax)
            {
                Error(declarator.NameSpan, "'auto' needs an initializer expression");
                return (null, null);
            }
            initializer = BindExpression(declarator.Initializer);
            if (initializer is BoundErrorExpression)
            {
                return (null, null);
            }
            type = Materialize(initializer.Type);
            type = WithArrayDimensions(type, declarator.ArrayDimensions, declarator.NameSpan);
            if (type == null)
            {
                return (null, null);
            }
            initializer = Convert(initializer, type, false, declarator.Initializer.Span);
        }
        else
        {
            ShaderType? element = ResolveType(typeSyntax);
            if (element == null)
            {
                return (null, null);
            }
            int? unsizedLength = null;
            if (declarator.ArrayDimensions.Count > 0 && declarator.ArrayDimensions[0] == null && declarator.Initializer is InitializerListSyntax list)
            {
                int perElement = WithArrayDimensions(element, declarator.ArrayDimensions.Skip(1).ToList(), declarator.NameSpan)?.ComponentCount ?? 1;
                int components = CountInitializerComponents(list);
                if (components % perElement != 0)
                {
                    Error(list.Span, $"{components} values don't fill whole elements of {perElement} components");
                    return (null, null);
                }
                unsizedLength = components / perElement;
            }
            type = WithArrayDimensions(element, declarator.ArrayDimensions, declarator.NameSpan, unsizedLength);
            if (type == null)
            {
                return (null, null);
            }
            if (declarator.Initializer is InitializerListSyntax initializerList)
            {
                initializer = BindInitializerList(initializerList, type);
            }
            else if (declarator.Initializer != null)
            {
                initializer = Convert(BindExpression(declarator.Initializer), type, false, declarator.Initializer.Span);
            }
        }

        if (initializer == null && requireInitializer)
        {
            Error(declarator.NameSpan, $"const '{declarator.Name}' needs an initializer");
        }
        return (new VariableSymbol(declarator.Name, type, kind, isConst, declarator.NameSpan), initializer);
    }

    private int CountInitializerComponents(InitializerListSyntax list)
    {
        int count = 0;
        foreach (ExpressionSyntax element in list.Elements)
        {
            if (element is InitializerListSyntax inner)
            {
                count += CountInitializerComponents(inner);
                continue;
            }
            // Binding twice is harmless: expressions in initializers have no binder side effects
            BoundExpression bound = BindExpression(element);
            count += bound.Type.ComponentCount;
        }
        return count;
    }

    /// <summary>`{ a, { b, c } }`: HLSL flattens every level; the component count must match the type.</summary>
    private BoundExpression BindInitializerList(InitializerListSyntax list, ShaderType type)
    {
        List<BoundExpression> sources = new List<BoundExpression>();
        void Flatten(InitializerListSyntax current)
        {
            foreach (ExpressionSyntax element in current.Elements)
            {
                if (element is InitializerListSyntax inner)
                {
                    Flatten(inner);
                }
                else
                {
                    sources.Add(BindExpression(element));
                }
            }
        }
        Flatten(list);
        if (sources.Any(source => source is BoundErrorExpression))
        {
            return new BoundErrorExpression(list.Span);
        }
        int count = sources.Sum(source => source.Type.ComponentCount);
        if (count != type.ComponentCount)
        {
            Error(list.Span, $"{type} needs {type.ComponentCount} values, the list has {count}");
            return new BoundErrorExpression(list.Span);
        }
        return new BoundConstruct(type, sources, isInitializerList: true, list.Span);
    }

    private VariableSymbol? LookupVariable(string name) =>
        _scope.Lookup(name)
        ?? (_sessionVariables != null && _sessionVariables.TryGetValue(name, out VariableSymbol? session) ? session : null)
        ?? (_globals.TryGetValue(name, out VariableSymbol? global) ? global : null);

    // ---------------------------------------------------------------- Statements

    private BoundBlock BindBlock(BlockSyntax block, bool newScope = true)
    {
        Scope saved = _scope;
        if (newScope)
        {
            _scope = new Scope(_scope);
        }
        List<BoundStatement> statements = block.Statements.Select(BindStatement).ToList();
        _scope = saved;
        return new BoundBlock(statements, block.Span);
    }

    private BoundStatement BindStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockSyntax block:
                return BindBlock(block);
            case EmptyStatementSyntax empty:
                return new BoundBlock(Array.Empty<BoundStatement>(), empty.Span);
            case VariableDeclarationSyntax declaration:
                return BindLocalDeclaration(declaration);
            case ExpressionStatementSyntax expression:
                return new BoundExpressionStatement(BindExpression(expression.Expression), expression.Span);
            case IfSyntax ifStatement:
            {
                BoundExpression condition = BindCondition(ifStatement.Condition);
                BoundStatement then = BindStatement(ifStatement.Then);
                BoundStatement? otherwise = ifStatement.Else == null ? null : BindStatement(ifStatement.Else);
                return new BoundIf(condition, then, otherwise, ifStatement.Span);
            }
            case ForSyntax forStatement:
            {
                Scope saved = _scope;
                _scope = new Scope(_scope);
                BoundStatement? initializer = forStatement.Initializer == null ? null : BindStatement(forStatement.Initializer);
                BoundExpression? condition = forStatement.Condition == null ? null : BindCondition(forStatement.Condition);
                BoundExpression? step = forStatement.Step == null ? null : BindExpression(forStatement.Step);
                BoundStatement body = BindLoopBody(forStatement.Body);
                _scope = saved;
                return new BoundLoop(initializer, condition, step, body, false, forStatement.Span);
            }
            case WhileSyntax whileStatement:
            {
                BoundExpression condition = BindCondition(whileStatement.Condition);
                BoundStatement body = BindLoopBody(whileStatement.Body);
                return new BoundLoop(null, condition, null, body, whileStatement.IsDoWhile, whileStatement.Span);
            }
            case SwitchSyntax switchStatement:
                return BindSwitch(switchStatement);
            case ReturnSyntax returnStatement:
                return BindReturn(returnStatement);
            case JumpSyntax jump:
                if (jump.Keyword == "discard")
                {
                    Error(jump.Span, "discard only exists in pixel shaders");
                }
                else if (jump.Keyword == "break" && _breakableDepth == 0)
                {
                    Error(jump.Span, "break outside a loop or switch");
                }
                else if (jump.Keyword == "continue" && _loopDepth == 0)
                {
                    Error(jump.Span, "continue outside a loop");
                }
                return new BoundJump(jump.Keyword == "break", jump.Span);
            default:
                Error(statement.Span, $"unsupported statement {statement.GetType().Name}");
                return new BoundBlock(Array.Empty<BoundStatement>(), statement.Span);
        }
    }

    private BoundStatement BindLoopBody(StatementSyntax body)
    {
        _loopDepth++;
        _breakableDepth++;
        BoundStatement bound = BindStatement(body);
        _loopDepth--;
        _breakableDepth--;
        return bound;
    }

    private BoundStatement BindLocalDeclaration(VariableDeclarationSyntax declaration)
    {
        List<BoundStatement> declarations = new List<BoundStatement>();
        bool isConst = declaration.Modifiers.HasFlag(StorageModifiers.Const);
        foreach (DeclaratorSyntax declarator in declaration.Declarators)
        {
            (VariableSymbol? variable, BoundExpression? initializer) = DeclareVariable(declaration.Type, declarator,
                _isInteractive && _function == null ? VariableKind.Session : VariableKind.Local, isConst, isConst);
            if (variable == null)
            {
                continue;
            }
            if (!_scope.TryDeclare(variable))
            {
                Error(declarator.NameSpan, $"'{variable.Name}' is already declared in this scope");
            }
            if (initializer != null && isConst)
            {
                variable.ConstantValue = Evaluator.TryEvaluateConstant(initializer, _profile);
            }
            declarations.Add(new BoundVariableDeclaration(variable, initializer, declaration.Span));
        }
        return declarations.Count == 1 ? declarations[0] : new BoundBlock(declarations, declaration.Span, isScope: false);
    }

    private BoundExpression BindCondition(ExpressionSyntax syntax)
    {
        BoundExpression condition = BindExpression(syntax);
        if (condition is BoundErrorExpression)
        {
            return condition;
        }
        if (condition.Type is not NumericType { IsScalar: true } && !(condition.Type is NumericType { ComponentCount: 1 }))
        {
            Error(syntax.Span, $"a condition must be a scalar, got {condition.Type} (use any(), all() or select())");
            return new BoundErrorExpression(syntax.Span);
        }
        return Convert(condition, NumericType.Bool, false, syntax.Span);
    }

    private BoundStatement BindSwitch(SwitchSyntax syntax)
    {
        BoundExpression value = BindExpression(syntax.Value);
        if (value is not BoundErrorExpression)
        {
            if (value.Type is not NumericType { IsScalar: true } numeric || !(numeric.Kind.IsInteger() || numeric.Kind == ScalarKind.Bool))
            {
                Error(syntax.Value.Span, $"switch needs an integer, got {value.Type}");
            }
            else
            {
                value = Convert(value, Materialize(numeric), false, syntax.Value.Span);
            }
        }
        HashSet<long> seen = new HashSet<long>();
        List<BoundSwitchSection> sections = new List<BoundSwitchSection>();
        _breakableDepth++;
        Scope saved = _scope;
        _scope = new Scope(_scope);
        bool hasDefault = false;
        foreach (SwitchSectionSyntax section in syntax.Sections)
        {
            List<long> labels = new List<long>();
            bool isDefault = false;
            foreach (ExpressionSyntax? label in section.Labels)
            {
                if (label == null)
                {
                    if (hasDefault)
                    {
                        Error(section.Span, "switch has two default labels");
                    }
                    isDefault = hasDefault = true;
                    continue;
                }
                int? constant = ConstantInt(label, int.MinValue, int.MaxValue);
                if (constant != null && !seen.Add(constant.Value))
                {
                    Error(label.Span, $"duplicate case {constant}");
                }
                if (constant != null)
                {
                    labels.Add(constant.Value);
                }
            }
            sections.Add(new BoundSwitchSection(labels, isDefault, section.Statements.Select(BindStatement).ToList()));
        }
        _scope = saved;
        _breakableDepth--;
        return new BoundSwitch(value, sections, syntax.Span);
    }

    private BoundStatement BindReturn(ReturnSyntax syntax)
    {
        if (_function == null)
        {
            Error(syntax.Span, "return outside a function");
            return new BoundReturn(null, syntax.Span);
        }
        if (syntax.Value == null)
        {
            if (_function.ReturnType is not VoidType)
            {
                Error(syntax.Span, $"'{_function.Name}' must return a {_function.ReturnType}");
            }
            return new BoundReturn(null, syntax.Span);
        }
        BoundExpression value = BindExpression(syntax.Value);
        if (_function.ReturnType is VoidType)
        {
            Error(syntax.Value.Span, $"'{_function.Name}' returns void");
            return new BoundReturn(null, syntax.Span);
        }
        return new BoundReturn(Convert(value, _function.ReturnType, false, syntax.Value.Span), syntax.Span);
    }

    // ---------------------------------------------------------------- Conversions

    /// <summary>Converts to a type (implicit or cast), reporting impossible conversions and truncation warnings.</summary>
    private BoundExpression Convert(BoundExpression expression, ShaderType type, bool isExplicit, SourceSpan span)
    {
        if (expression is BoundErrorExpression)
        {
            return expression;
        }
        ConversionInfo conversion = TypeRules.Classify(expression.Type, type, isExplicit);
        if (!conversion.IsPossible)
        {
            Error(span, expression.Type is VoidType ? "this expression has no value" : $"can't convert {expression.Type} to {type}");
            return new BoundErrorExpression(span);
        }
        if (conversion.Kind == ConversionKind.Identity)
        {
            return expression;
        }
        if (conversion.Warning != null && !isExplicit)
        {
            Warning(span, conversion.Warning);
        }
        return new BoundConversion(expression, type, conversion.Kind, isExplicit, span);
    }

    // ---------------------------------------------------------------- Expressions

    private BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        switch (syntax)
        {
            case LiteralSyntax literal:
                return new BoundLiteral(Value.Scalar(literal.Kind, literal.Bits, UnitTag.Bare), literal.Span);
            case UnitLiteralSyntax unitLiteral:
                return new BoundUnitLiteral(unitLiteral.SiValue, unitLiteral.Dimension, unitLiteral.UnitText, unitLiteral.Span);
            case NameSyntax name:
                return BindName(name);
            case UnaryExpressionSyntax unary:
                return BindUnary(unary);
            case IncrementSyntax increment:
                return BindIncrement(increment);
            case BinaryExpressionSyntax binary:
                return BindBinary(binary);
            case AssignmentSyntax assignment:
                return BindAssignment(assignment);
            case ConditionalSyntax conditional:
                return BindConditional(conditional);
            case CallSyntax call:
                return BindCall(call);
            case ConstructorSyntax constructor:
            {
                ShaderType? type = ResolveType(constructor.Type);
                return type == null ? new BoundErrorExpression(constructor.Span) : BindConstructor(type, constructor.Arguments, constructor.Span);
            }
            case CastSyntax cast:
                return BindCast(cast);
            case MemberSyntax member:
                return BindMember(member);
            case IndexSyntax index:
                return BindIndex(index);
            case InitializerListSyntax list:
                Error(list.Span, "an initializer list can only initialize a declaration");
                return new BoundErrorExpression(list.Span);
            default:
                Error(syntax.Span, $"unsupported expression {syntax.GetType().Name}");
                return new BoundErrorExpression(syntax.Span);
        }
    }

    private BoundExpression BindName(NameSyntax name)
    {
        VariableSymbol? variable = LookupVariable(name.Name);
        if (variable != null)
        {
            return new BoundVariable(variable, name.Span);
        }
        if (_functions.ContainsKey(name.Name) || Intrinsics.TryGet(name.Name, out _))
        {
            Error(name.Span, $"'{name.Name}' is a function: call it with (...)");
        }
        else
        {
            Error(name.Span, $"unknown name '{name.Name}'");
        }
        return new BoundErrorExpression(name.Span);
    }

    private BoundExpression BindUnary(UnaryExpressionSyntax syntax)
    {
        BoundExpression operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression)
        {
            return operand;
        }
        if (operand.Type is not NumericType numeric)
        {
            Error(syntax.Span, $"'{syntax.Operator}' needs a number, got {operand.Type}");
            return new BoundErrorExpression(syntax.Span);
        }
        switch (syntax.Operator)
        {
            case "!":
                return new BoundUnary(UnaryOperator.LogicalNot, operand, numeric.WithKind(ScalarKind.Bool), syntax.Span);
            case "~":
                if (numeric.Kind.IsFloat())
                {
                    Error(syntax.Span, $"'~' needs integers, got {numeric}");
                    return new BoundErrorExpression(syntax.Span);
                }
                NumericType integer = numeric.Kind == ScalarKind.Bool ? numeric.WithKind(ScalarKind.Int) : numeric;
                return new BoundUnary(UnaryOperator.BitwiseNot, Convert(operand, integer, false, syntax.Span), integer, syntax.Span);
            default:
                NumericType arithmetic = numeric.Kind == ScalarKind.Bool ? numeric.WithKind(ScalarKind.Int) : numeric;
                return new BoundUnary(syntax.Operator == "-" ? UnaryOperator.Negate : UnaryOperator.Plus, Convert(operand, arithmetic, false, syntax.Span),
                    arithmetic, syntax.Span);
        }
    }

    private BoundExpression BindIncrement(IncrementSyntax syntax)
    {
        BoundExpression target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression || !CheckAssignable(target, syntax.Target.Span))
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (target.Type is not NumericType { Kind: not ScalarKind.Bool })
        {
            Error(syntax.Span, $"'{syntax.Operator}' needs a number, got {target.Type}");
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundIncrement(target, syntax.Operator == "++", syntax.IsPrefix, syntax.Span);
    }

    private BoundExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        if (syntax.Operator == ",")
        {
            return new BoundComma(BindExpression(syntax.Left), BindExpression(syntax.Right), syntax.Span);
        }
        BoundExpression left = BindExpression(syntax.Left);
        BoundExpression right = BindExpression(syntax.Right);
        if (left is BoundErrorExpression || right is BoundErrorExpression)
        {
            return new BoundErrorExpression(syntax.Span);
        }

        if (syntax.Operator is "&&" or "||")
        {
            if (left.Type is not NumericType { ComponentCount: 1 } || right.Type is not NumericType { ComponentCount: 1 })
            {
                Error(syntax.Span, $"'{syntax.Operator}' needs scalars in HLSL 2021; use {(syntax.Operator == "&&" ? "and()" : "or()")} for vectors");
                return new BoundErrorExpression(syntax.Span);
            }
            return new BoundLogical(syntax.Operator == "&&", Convert(left, NumericType.Bool, false, syntax.Left.Span),
                Convert(right, NumericType.Bool, false, syntax.Right.Span), syntax.Span);
        }

        BinaryOperator? operation = TypeRules.ParseBinaryOperator(syntax.Operator);
        if (operation == null)
        {
            Error(syntax.Span, $"unknown operator '{syntax.Operator}'");
            return new BoundErrorExpression(syntax.Span);
        }
        return MakeBinary(operation.Value, left, right, syntax.Span);
    }

    private BoundExpression MakeBinary(BinaryOperator operation, BoundExpression left, BoundExpression right, SourceSpan span)
    {
        if (left.Type is not NumericType leftType || right.Type is not NumericType rightType)
        {
            Error(span, $"'{TypeRules.Symbol(operation)}' can't combine {left.Type} and {right.Type}");
            return new BoundErrorExpression(span);
        }
        var resolved = TypeRules.ResolveBinary(operation, leftType, rightType, out string? error, out string? warning);
        if (resolved == null)
        {
            Error(span, error!);
            return new BoundErrorExpression(span);
        }
        if (warning != null)
        {
            Warning(span, warning);
        }
        (NumericType operandType, NumericType rightOperandType, NumericType resultType) = resolved.Value;
        return new BoundBinary(operation, ConvertQuietly(left, operandType), ConvertQuietly(right, rightOperandType), resultType, span);
    }

    /// <summary>Operand conversion whose truncation warning was already reported for the whole operation.</summary>
    private BoundExpression ConvertQuietly(BoundExpression expression, ShaderType type)
    {
        ConversionInfo conversion = TypeRules.Classify(expression.Type, type, isExplicit: true);
        return conversion.Kind == ConversionKind.Identity ? expression : new BoundConversion(expression, type, conversion.Kind, false, expression.Span);
    }

    private BoundExpression BindAssignment(AssignmentSyntax syntax)
    {
        BoundExpression target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression || !CheckAssignable(target, syntax.Target.Span))
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (syntax.Value is InitializerListSyntax list)
        {
            return new BoundAssignment(target, BindInitializerList(list, target.Type), syntax.Span);
        }
        BoundExpression value = BindExpression(syntax.Value);
        if (syntax.Operator == "=")
        {
            return new BoundAssignment(target, Convert(value, target.Type, false, syntax.Value.Span), syntax.Span);
        }

        BinaryOperator? operation = TypeRules.ParseBinaryOperator(syntax.Operator[..^1]);
        if (value is BoundErrorExpression || operation == null)
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (target.Type is not NumericType targetType || value.Type is not NumericType valueType)
        {
            Error(syntax.Span, $"'{syntax.Operator}' can't combine {target.Type} and {value.Type}");
            return new BoundErrorExpression(syntax.Span);
        }
        var resolved = TypeRules.ResolveBinary(operation.Value, targetType, valueType, out string? error, out string? warning);
        if (resolved == null)
        {
            Error(syntax.Span, error!);
            return new BoundErrorExpression(syntax.Span);
        }
        if (warning != null)
        {
            Warning(syntax.Span, warning);
        }
        NumericType operationType = resolved.Value.OperandType;
        if (!TypeRules.Classify(resolved.Value.ResultType, targetType, false).IsPossible)
        {
            Error(syntax.Span, $"can't store {resolved.Value.ResultType} in {targetType}");
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundCompoundAssignment(operation.Value, target, ConvertQuietly(value, resolved.Value.RightType), operationType, syntax.Span);
    }

    private bool CheckAssignable(BoundExpression target, SourceSpan span)
    {
        switch (target)
        {
            case BoundVariable variable:
                if (variable.Variable.IsConst && !(variable.Variable.Kind == VariableKind.Uniform && _isInteractive && _function == null))
                {
                    Error(span, variable.Variable.Kind == VariableKind.Uniform
                        ? $"'{variable.Variable.Name}' is a uniform: read-only in functions (make it static to write it)"
                        : $"'{variable.Variable.Name}' is const");
                    return false;
                }
                return true;
            case BoundSwizzle swizzle:
                if (swizzle.Indices.Distinct().Count() != swizzle.Indices.Length)
                {
                    Error(span, $"can't assign to '.{swizzle.Text}': it repeats a component");
                    return false;
                }
                return CheckAssignable(swizzle.Operand, span);
            case BoundIndex index:
                return CheckAssignable(index.Operand, span);
            case BoundField field:
                return CheckAssignable(field.Operand, span);
            default:
                Error(span, "this expression can't be assigned");
                return false;
        }
    }

    private BoundExpression BindConditional(ConditionalSyntax syntax)
    {
        BoundExpression condition = BindExpression(syntax.Condition);
        BoundExpression whenTrue = BindExpression(syntax.WhenTrue);
        BoundExpression whenFalse = BindExpression(syntax.WhenFalse);
        if (condition is BoundErrorExpression || whenTrue is BoundErrorExpression || whenFalse is BoundErrorExpression)
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (condition.Type is not NumericType { ComponentCount: 1 })
        {
            Error(syntax.Condition.Span, $"?: needs a scalar condition in HLSL 2021, got {condition.Type}; use select() for vectors");
            return new BoundErrorExpression(syntax.Span);
        }
        BoundExpression boolCondition = Convert(condition, NumericType.Bool, false, syntax.Condition.Span);
        ShaderType? common = whenTrue.Type.Equals(whenFalse.Type) ? whenTrue.Type : null;
        if (common == null && whenTrue.Type is NumericType trueType && whenFalse.Type is NumericType falseType)
        {
            common = TypeRules.CombineShapes(trueType, falseType, TypeRules.CommonKind(trueType.Kind, falseType.Kind), out string? warning);
            if (warning != null)
            {
                Warning(syntax.Span, warning);
            }
        }
        if (common == null)
        {
            Error(syntax.Span, $"?: branches have incompatible types {whenTrue.Type} and {whenFalse.Type}");
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundConditional(boolCondition, ConvertQuietly(whenTrue, common), ConvertQuietly(whenFalse, common), syntax.Span);
    }

    private BoundExpression BindCast(CastSyntax syntax)
    {
        ShaderType? type = ResolveType(syntax.Type);
        if (type == null)
        {
            return new BoundErrorExpression(syntax.Span);
        }
        type = WithArrayDimensions(type, syntax.ArrayDimensions.Cast<ExpressionSyntax?>().ToList(), syntax.Span);
        BoundExpression operand = BindExpression(syntax.Operand);
        return type == null ? new BoundErrorExpression(syntax.Span) : Convert(operand, type, true, syntax.Span);
    }

    // ---- Calls ----

    private BoundExpression BindCall(CallSyntax syntax)
    {
        // Constructors: float3(...), a typedef'd vector type...
        if (TryParseBuiltinType(syntax.Name, out ShaderType? builtin, out _) || (_typeNames.TryGetValue(syntax.Name, out builtin) && builtin is not StructType))
        {
            return BindConstructor(builtin!, syntax.Arguments, syntax.Span);
        }
        if (_typeNames.TryGetValue(syntax.Name, out ShaderType? structType) && structType is StructType)
        {
            Error(syntax.NameSpan, $"HLSL structs have no constructors: use {{ ... }} in a declaration, or ({syntax.Name})0");
            return new BoundErrorExpression(syntax.Span);
        }

        List<BoundExpression> arguments = syntax.Arguments.Select(BindExpression).ToList();
        if (arguments.Any(argument => argument is BoundErrorExpression))
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (_functions.TryGetValue(syntax.Name, out List<FunctionSymbol>? overloads))
        {
            return BindUserCall(syntax, overloads, arguments);
        }
        if (Intrinsics.TryGet(syntax.Name, out Intrinsic intrinsic))
        {
            return BindIntrinsicCall(syntax, intrinsic, arguments);
        }
        Error(syntax.NameSpan, Intrinsics.IsUnsupported(syntax.Name)
            ? $"'{syntax.Name}' only exists inside a GPU pipeline and isn't supported here"
            : $"unknown function '{syntax.Name}'");
        return new BoundErrorExpression(syntax.Span);
    }

    private BoundExpression BindConstructor(ShaderType type, IReadOnlyList<ExpressionSyntax> argumentSyntax, SourceSpan span)
    {
        if (type is not NumericType numeric)
        {
            Error(span, $"{type} can't be constructed with (...)");
            return new BoundErrorExpression(span);
        }
        List<BoundExpression> arguments = argumentSyntax.Select(BindExpression).ToList();
        if (arguments.Any(argument => argument is BoundErrorExpression))
        {
            return new BoundErrorExpression(span);
        }
        foreach (BoundExpression argument in arguments)
        {
            if (argument.Type is not NumericType)
            {
                Error(argument.Span, $"{numeric}(...) takes numbers, got {argument.Type}");
                return new BoundErrorExpression(span);
            }
        }
        int count = arguments.Sum(argument => argument.Type.ComponentCount);
        if (count != numeric.ComponentCount && !(arguments.Count == 1 && count == 1))
        {
            Error(span, $"{numeric} needs {numeric.ComponentCount} components, got {count}");
            return new BoundErrorExpression(span);
        }
        return new BoundConstruct(numeric, arguments, isInitializerList: false, span);
    }

    private BoundExpression BindUserCall(CallSyntax syntax, List<FunctionSymbol> overloads, List<BoundExpression> arguments)
    {
        List<(FunctionSymbol Function, int Cost)> viable = new List<(FunctionSymbol Function, int Cost)>();
        foreach (FunctionSymbol candidate in overloads)
        {
            if (arguments.Count > candidate.Parameters.Count || arguments.Count < candidate.RequiredParameterCount)
            {
                continue;
            }
            int cost = 0;
            for (int index = 0; index < arguments.Count && cost != int.MaxValue; index++)
            {
                ParameterSymbol parameter = candidate.Parameters[index];
                ConversionInfo inward = TypeRules.Classify(arguments[index].Type, parameter.Type, false);
                ConversionInfo outward = TypeRules.Classify(parameter.Type, arguments[index].Type, false);
                bool isPossible = parameter.Mode switch
                {
                    ParameterMode.In => inward.IsPossible,
                    ParameterMode.Out => outward.IsPossible,
                    _ => inward.IsPossible && outward.IsPossible,
                };
                cost = isPossible ? cost + (parameter.Mode == ParameterMode.Out ? outward.Cost : inward.Cost) : int.MaxValue;
            }
            if (cost != int.MaxValue)
            {
                viable.Add((candidate, cost));
            }
        }
        string argumentList = string.Join(", ", arguments.Select(argument => argument.Type));
        if (viable.Count == 0)
        {
            Error(syntax.NameSpan, $"no '{syntax.Name}' takes ({argumentList}); candidates: {string.Join("; ", overloads.Select(overload => overload.Signature))}");
            return new BoundErrorExpression(syntax.Span);
        }
        int best = viable.Min(candidate => candidate.Cost);
        List<FunctionSymbol> bestOverloads = viable.Where(candidate => candidate.Cost == best).Select(candidate => candidate.Function).ToList();
        if (bestOverloads.Count > 1)
        {
            Error(syntax.NameSpan, $"call of '{syntax.Name}({argumentList})' is ambiguous: {string.Join("; ", bestOverloads.Select(overload => overload.Signature))}");
            return new BoundErrorExpression(syntax.Span);
        }

        FunctionSymbol function = bestOverloads[0];
        List<BoundExpression> converted = new List<BoundExpression>();
        for (int index = 0; index < function.Parameters.Count; index++)
        {
            ParameterSymbol parameter = function.Parameters[index];
            if (index >= arguments.Count)
            {
                converted.Add(parameter.DefaultValue!);
                continue;
            }
            if (parameter.Mode == ParameterMode.In)
            {
                converted.Add(Convert(arguments[index], parameter.Type, false, arguments[index].Span));
            }
            else
            {
                if (!CheckAssignable(arguments[index], arguments[index].Span))
                {
                    return new BoundErrorExpression(syntax.Span);
                }
                converted.Add(arguments[index]);
            }
        }
        if (_function != null)
        {
            _calls[_function].Add(function);
        }
        return new BoundCall(function, converted, syntax.Span);
    }

    private BoundExpression BindIntrinsicCall(CallSyntax syntax, Intrinsic intrinsic, List<BoundExpression> arguments)
    {
        IntrinsicResolution resolution = intrinsic.Resolve(arguments.Select(argument => argument.Type).ToList());
        if (resolution.Signature == null)
        {
            Error(syntax.NameSpan, $"{syntax.Name}({string.Join(", ", arguments.Select(argument => argument.Type))}): {resolution.Error}");
            return new BoundErrorExpression(syntax.Span);
        }
        IntrinsicSignature signature = resolution.Signature;
        List<BoundExpression> converted = new List<BoundExpression>();
        List<Value?> constants = new List<Value?>();
        for (int index = 0; index < arguments.Count; index++)
        {
            if (signature.Modes[index] == ParameterMode.In)
            {
                BoundExpression argument = Convert(arguments[index], signature.ParameterTypes[index], false, arguments[index].Span);
                converted.Add(argument);
                constants.Add(argument is BoundErrorExpression ? null : Evaluator.TryEvaluateConstant(argument, _profile));
            }
            else
            {
                if (!CheckAssignable(arguments[index], arguments[index].Span)
                    || !TypeRules.Classify(signature.ParameterTypes[index], arguments[index].Type, false).IsPossible)
                {
                    if (TypeRules.Classify(signature.ParameterTypes[index], arguments[index].Type, false).IsPossible == false)
                    {
                        Error(arguments[index].Span, $"{syntax.Name} writes a {signature.ParameterTypes[index]} here, which can't go into {arguments[index].Type}");
                    }
                    return new BoundErrorExpression(syntax.Span);
                }
                converted.Add(arguments[index]);
                constants.Add(null);
            }
        }
        if (converted.Any(argument => argument is BoundErrorExpression))
        {
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundIntrinsicCall(intrinsic, signature, converted, constants, syntax.Span);
    }

    // ---- Members and indexing ----

    private BoundExpression BindMember(MemberSyntax syntax)
    {
        BoundExpression target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression)
        {
            return target;
        }
        switch (target.Type)
        {
            case StructType structType:
                StructField? field = structType.FindField(syntax.Member);
                if (field == null)
                {
                    Error(syntax.MemberSpan, $"'{structType.Name}' has no field '{syntax.Member}'");
                    return new BoundErrorExpression(syntax.Span);
                }
                return new BoundField(target, field, syntax.Span);
            case NumericType { IsMatrix: true } matrix:
                return BindMatrixSwizzle(target, matrix, syntax);
            case NumericType numeric:
                return BindSwizzle(target, numeric, syntax);
            default:
                Error(syntax.MemberSpan, $"{target.Type} has no member '{syntax.Member}'");
                return new BoundErrorExpression(syntax.Span);
        }
    }

    private BoundExpression BindSwizzle(BoundExpression target, NumericType numeric, MemberSyntax syntax)
    {
        string text = syntax.Member;
        bool isColor = text.IndexOfAny(new[] { 'r', 'g', 'b', 'a' }) >= 0;
        string set = isColor ? "rgba" : "xyzw";
        int[] indices = text.Select(character => set.IndexOf(character)).ToArray();
        if (text.Length > 4 || indices.Any(index => index < 0 || index >= numeric.Size))
        {
            Error(syntax.MemberSpan, indices.Any(index => index < 0)
                ? $"invalid swizzle '.{text}' (use xyzw or rgba, not both)"
                : $"'.{text}' reads past the {numeric.Size} components of {numeric}");
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundSwizzle(target, indices, text, NumericType.ScalarOrVector(numeric.Kind, indices.Length), syntax.Span);
    }

    /// <summary>m._m01 (0-based) or m._12 (1-based), chained: m._m00_m11.</summary>
    private BoundExpression BindMatrixSwizzle(BoundExpression target, NumericType matrix, MemberSyntax syntax)
    {
        string text = syntax.Member;
        List<int> indices = new List<int>();
        int position = 0;
        while (position < text.Length)
        {
            bool isZeroBased = position + 1 < text.Length && text[position] == '_' && text[position + 1] == 'm';
            int start = position + (isZeroBased ? 2 : 1);
            if (text[position] != '_' || start + 2 > text.Length || !char.IsAsciiDigit(text[start]) || !char.IsAsciiDigit(text[start + 1]))
            {
                Error(syntax.MemberSpan, $"invalid matrix member '.{text}' (use ._m01 or ._12)");
                return new BoundErrorExpression(syntax.Span);
            }
            int row = text[start] - '0' - (isZeroBased ? 0 : 1);
            int column = text[start + 1] - '0' - (isZeroBased ? 0 : 1);
            if (row < 0 || column < 0 || row >= matrix.Rows || column >= matrix.Columns)
            {
                Error(syntax.MemberSpan, $"'.{text}' is outside {matrix}");
                return new BoundErrorExpression(syntax.Span);
            }
            indices.Add(row * matrix.Columns + column);
            position = start + 2;
        }
        if (indices.Count > 4)
        {
            Error(syntax.MemberSpan, $"'.{text}' selects more than 4 elements");
            return new BoundErrorExpression(syntax.Span);
        }
        return new BoundSwizzle(target, indices.ToArray(), text, NumericType.ScalarOrVector(matrix.Kind, indices.Count), syntax.Span);
    }

    private BoundExpression BindIndex(IndexSyntax syntax)
    {
        BoundExpression target = BindExpression(syntax.Target);
        BoundExpression index = BindExpression(syntax.Index);
        if (target is BoundErrorExpression || index is BoundErrorExpression)
        {
            return new BoundErrorExpression(syntax.Span);
        }
        if (index.Type is not NumericType { ComponentCount: 1 } indexType || indexType.Kind.IsFloat())
        {
            Error(syntax.Index.Span, $"an index must be an integer, got {index.Type}");
            return new BoundErrorExpression(syntax.Span);
        }
        BoundExpression convertedIndex = Convert(index, NumericType.Scalar(indexType.Kind is ScalarKind.UInt ? ScalarKind.UInt : ScalarKind.Int), false, syntax.Index.Span);
        (ShaderType elementType, int length) = target.Type switch
        {
            ArrayType array => (array.Element, array.Length),
            NumericType { IsMatrix: true } matrix => ((ShaderType)NumericType.Vector(matrix.Kind, matrix.Columns), matrix.Rows),
            NumericType { IsVector: true } vector => (NumericType.Scalar(vector.Kind), vector.Size),
            _ => (VoidType.Instance, 0),
        };
        if (length == 0)
        {
            Error(syntax.Span, $"{target.Type} can't be indexed");
            return new BoundErrorExpression(syntax.Span);
        }
        Value? constant = Evaluator.TryEvaluateConstant(convertedIndex, _profile);
        if (constant != null)
        {
            long number = (long)constant.GetNumber(0);
            if (number < 0 || number >= length)
            {
                Error(syntax.Index.Span, $"index {number} is out of range [0, {length - 1}]");
                return new BoundErrorExpression(syntax.Span);
            }
        }
        return new BoundIndex(target, convertedIndex, length, elementType, syntax.Span);
    }
}
