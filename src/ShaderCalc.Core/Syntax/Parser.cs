using System.Globalization;
using ShaderCalc.Units;

namespace ShaderCalc.Syntax;

/// <summary>
/// Recursive-descent parser for HLSL (math subset) and the C++ forms people paste: const T&amp; / T&amp; parameters,
/// std:: names, static_cast, auto, inline/constexpr, namespaces, using-aliases, brace initialisation.
/// </summary>
public sealed class Parser
{
    private sealed class ParseException : Exception
    {
        public ParseException(string message, Token token) : base(message)
        {
            Token = token;
        }

        public Token Token { get; }
    }

    private static readonly HashSet<string> SkippedModifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "inline", "constexpr", "extern", "export", "volatile", "precise", "FORCEINLINE", "FORCENOINLINE", "__forceinline",
        "linear", "centroid", "nointerpolation", "noperspective", "sample", "row_major", "column_major", "snorm", "unorm",
        "globallycoherent", "shared", "mutable",
    };

    private static readonly HashSet<string> ScalarTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "bool", "int", "uint", "dword", "half", "float", "double", "int64_t", "uint64_t", "int32_t", "uint32_t",
        "min16float", "min10float", "min16int", "min12int", "min16uint", "float32_t", "float64_t",
        "int16_t", "uint16_t", "float16_t",
    };

    private static readonly string[][] BinaryLevels =
    {
        new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "==", "!=" },
        new[] { "<", ">", "<=", ">=" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
    };

    private static readonly HashSet<string> AssignmentOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=",
    };

    private readonly List<Token> _tokens;
    private readonly string _sourceName;
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<string> _typeNames;
    private readonly Func<string, bool>? _isUnitName;
    private int _position;
    private bool _isInteractive;

    /// <summary>Worksheet: a line break ends a top-level statement.</summary>
    private bool _isLineMode;

    /// <summary>Open brackets in the current expression, and braces of the current statement block.</summary>
    private int _nesting;
    private int _blockDepth;

    /// <summary>The next token starts a new line where a worksheet statement may end.</summary>
    private bool AtLineBreak => _isLineMode && _nesting == 0 && _blockDepth == 0 && Current.StartsLine && Current.Kind != TokenKind.End;

    private Parser(List<Token> tokens, string sourceName, DiagnosticBag diagnostics, IEnumerable<string> typeNames, Func<string, bool>? isUnitName)
    {
        _tokens = tokens;
        _sourceName = sourceName;
        _diagnostics = diagnostics;
        _typeNames = new HashSet<string>(typeNames, StringComparer.Ordinal);
        _isUnitName = isUnitName;
    }

    /// <summary>Parses pasted code: functions, structs, typedefs, globals.</summary>
    public static CompilationUnitSyntax ParseProgram(string source, string sourceName, DiagnosticBag diagnostics, IEnumerable<string>? knownTypeNames = null)
    {
        List<Token> tokens = Preprocessor.Process(Lexer.Tokenize(source, sourceName, diagnostics), sourceName, diagnostics);
        Parser parser = new Parser(tokens, sourceName, diagnostics, knownTypeNames ?? Array.Empty<string>(), null);
        List<DeclarationSyntax> declarations = new List<DeclarationSyntax>();
        while (!parser.IsEnd)
        {
            parser.ParseDeclarations(declarations, insideBlock: false);
        }
        return new CompilationUnitSyntax(declarations);
    }

    /// <summary>
    /// Parses a calculator line: statements and declarations, the last expression may omit its ';', and
    /// `number unit` is a unit literal.
    /// </summary>
    public static List<SyntaxNode> ParseInteractive(string source, string sourceName, DiagnosticBag diagnostics, IEnumerable<string> knownTypeNames,
        Func<string, bool> isUnitName)
    {
        List<Token> tokens = Preprocessor.Process(Lexer.Tokenize(source, sourceName, diagnostics), sourceName, diagnostics);
        return ParseItems(tokens, sourceName, diagnostics, knownTypeNames, isUnitName, isLineMode: false);
    }

    /// <summary>
    /// Parses a worksheet file (tokens already preprocessed): declarations and calculation lines mixed, where a
    /// line break ends a top-level statement unless a bracket is still open.
    /// </summary>
    public static List<SyntaxNode> ParseWorksheet(List<Token> tokens, string sourceName, DiagnosticBag diagnostics, IEnumerable<string> knownTypeNames,
        Func<string, bool> isUnitName) =>
        ParseItems(tokens, sourceName, diagnostics, knownTypeNames, isUnitName, isLineMode: true);

    private static List<SyntaxNode> ParseItems(List<Token> tokens, string sourceName, DiagnosticBag diagnostics, IEnumerable<string> knownTypeNames,
        Func<string, bool> isUnitName, bool isLineMode)
    {
        Parser parser = new Parser(tokens, sourceName, diagnostics, knownTypeNames, isUnitName) { _isInteractive = true, _isLineMode = isLineMode };
        List<SyntaxNode> items = new List<SyntaxNode>();
        while (!parser.IsEnd)
        {
            int start = parser._position;
            parser._nesting = 0;
            parser._blockDepth = 0;
            try
            {
                if (parser.Accept(";"))
                {
                    continue;
                }
                if (parser.IsDeclarationKeyword() || parser.IsFunctionStart() || parser.IsResourceDeclaration())
                {
                    List<DeclarationSyntax> declarations = new List<DeclarationSyntax>();
                    parser.ParseDeclaration(declarations);
                    items.AddRange(declarations);
                }
                else if (parser.IsDeclarationStart() || parser.IsStatementKeyword() || parser.Is("{"))
                {
                    items.Add(parser.ParseStatement());
                }
                else
                {
                    Token first = parser.Current;
                    ExpressionSyntax expression = parser.ParseExpression();
                    parser.ExpectStatementEnd();
                    items.Add(new ExpressionStatementSyntax(expression, parser.SpanFrom(first)));
                }
            }
            catch (ParseException exception)
            {
                parser.Report(exception);
                parser.Recover(start);
            }
        }
        return items;
    }

    private static readonly string[] ResourceTypePrefixes =
    {
        "Texture", "RWTexture", "Buffer", "RWBuffer", "StructuredBuffer", "RWStructuredBuffer", "ByteAddressBuffer", "RWByteAddressBuffer",
        "AppendStructuredBuffer", "ConsumeStructuredBuffer", "ConstantBuffer", "Sampler", "RaytracingAccelerationStructure", "FeedbackTexture",
    };

    /// <summary>`Texture2D T;`, `RWStructuredBuffer&lt;float&gt; B;`: skipped with a note (resources aren't supported).</summary>
    private bool IsResourceDeclaration() =>
        Current.Kind == TokenKind.Identifier && !IsTypeStart(0)
        && ((Peek(1).Kind == TokenKind.Identifier && !Peek(2).Is("(") && !Peek(1).StartsLine)
            || (Peek(1).Is("<") && ResourceTypePrefixes.Any(prefix => Current.Text.StartsWith(prefix, StringComparison.Ordinal))));

    // ---- Declarations ----

    private void ParseDeclarations(List<DeclarationSyntax> declarations, bool insideBlock)
    {
        while (!IsEnd && !(insideBlock && Is("}")))
        {
            int start = _position;
            try
            {
                ParseDeclaration(declarations);
            }
            catch (ParseException exception)
            {
                Report(exception);
                Recover(start);
            }
        }
    }

    private bool IsDeclarationKeyword() =>
        Current.Kind == TokenKind.Identifier && Current.Text is "struct" or "typedef" or "using" or "namespace" or "cbuffer" or "tbuffer" or "template";

    private void ParseDeclaration(List<DeclarationSyntax> declarations)
    {
        Token start = Current;
        if (Accept(";"))
        {
            return;
        }
        SkipAttributes();
        if (Accept("namespace"))
        {
            if (Current.Kind == TokenKind.Identifier)
            {
                Advance();
            }
            Expect("{");
            ParseDeclarations(declarations, insideBlock: true);
            Expect("}");
            return;
        }
        if (Is("using"))
        {
            ParseUsing(declarations);
            return;
        }
        if (Accept("template"))
        {
            throw Error("Templates are not supported", start);
        }
        if (Is("struct"))
        {
            declarations.Add(ParseStruct());
            return;
        }
        if (Accept("typedef"))
        {
            SkipModifiers();
            TypeSyntax type = ParseType();
            Token name = ExpectIdentifier();
            List<ExpressionSyntax?> dimensions = ParseArrayDimensions();
            Expect(";");
            _typeNames.Add(name.Text);
            declarations.Add(new TypedefSyntax(type, name.Text, name.Span, dimensions, SpanFrom(start)));
            return;
        }
        if (Accept("cbuffer") || Accept("tbuffer"))
        {
            ExpectIdentifier();
            SkipRegister();
            Expect("{");
            while (!Accept("}"))
            {
                VariableDeclarationSyntax member = ParseVariableDeclaration(StorageModifiers.Uniform);
                declarations.Add(new GlobalVariableSyntax(member, member.Span));
            }
            Accept(";");
            return;
        }

        StorageModifiers modifiers = SkipModifiers();
        // Resources (Texture2D T; RWStructuredBuffer<float> B;) often come with a pasted function: skip them
        if (Current.Kind == TokenKind.Identifier && !IsTypeStart(0) && !Is("void") && (Peek(1).Kind == TokenKind.Identifier || Peek(1).Is("<"))
            && !(Peek(1).Kind == TokenKind.Identifier && Peek(2).Is("(")))
        {
            Token resource = Current;
            while (!IsEnd && !Accept(";"))
            {
                Advance();
            }
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, $"'{resource.Text}' declaration skipped: resources aren't supported", _sourceName,
                SpanFrom(resource)));
            return;
        }
        TypeSyntax returnOrVariableType = Is("void") ? new NamedTypeSyntax(Advance().Text, Previous.Span) : ParseType();
        if (Current.Kind == TokenKind.Identifier && Peek(1).Is("("))
        {
            declarations.Add(ParseFunction(returnOrVariableType, start));
            return;
        }
        VariableDeclarationSyntax variables = ParseDeclarators(modifiers, returnOrVariableType, start);
        declarations.Add(new GlobalVariableSyntax(variables, variables.Span));
    }

    private void ParseUsing(List<DeclarationSyntax> declarations)
    {
        Token start = Expect("using");
        if (Accept("namespace"))
        {
            while (!IsEnd && !Accept(";"))
            {
                Advance();
            }
            return;
        }
        Token name = ExpectIdentifier();
        Expect("=");
        TypeSyntax type = ParseType();
        Expect(";");
        _typeNames.Add(name.Text);
        declarations.Add(new TypedefSyntax(type, name.Text, name.Span, Array.Empty<ExpressionSyntax?>(), SpanFrom(start)));
    }

    private StructSyntax ParseStruct()
    {
        Token start = Expect("struct");
        Token name = ExpectIdentifier();
        _typeNames.Add(name.Text);
        Expect("{");
        List<VariableDeclarationSyntax> fields = new List<VariableDeclarationSyntax>();
        while (!Accept("}"))
        {
            if (Accept(";"))
            {
                continue;
            }
            int fieldStart = _position;
            SkipModifiers();
            ParseType();
            bool isMethod = Current.Kind == TokenKind.Identifier && Peek(1).Is("(");
            _position = fieldStart;
            if (isMethod)
            {
                throw Error("Struct member functions are not supported yet", Current);
            }
            fields.Add(ParseVariableDeclaration(StorageModifiers.None));
        }
        Accept(";");
        return new StructSyntax(name.Text, name.Span, fields, SpanFrom(start));
    }

    private FunctionSyntax ParseFunction(TypeSyntax returnType, Token start)
    {
        Token name = ExpectIdentifier();
        Expect("(");
        List<ParameterSyntax> parameters = new List<ParameterSyntax>();
        if (Is("void") && Peek(1).Is(")"))
        {
            Advance();
        }
        if (!Accept(")"))
        {
            do
            {
                parameters.Add(ParseParameter());
            }
            while (Accept(","));
            Expect(")");
        }
        SkipSemantic();
        // C++ trailing specifiers
        while (Current.Kind == TokenKind.Identifier && Current.Text is "noexcept" or "const")
        {
            Advance();
        }
        BlockSyntax? body = Accept(";") ? null : ParseBlock();
        return new FunctionSyntax(returnType, name.Text, name.Span, parameters, body, SpanFrom(start));
    }

    private ParameterSyntax ParseParameter()
    {
        Token start = Current;
        ParameterMode mode = ParameterMode.In;
        bool isConst = false;
        while (Current.Kind == TokenKind.Identifier)
        {
            if (Current.Text is "in" or "out" or "inout")
            {
                string qualifier = Advance().Text;
                mode = qualifier switch { "out" => ParameterMode.Out, "inout" => ParameterMode.InOut, _ => mode };
            }
            else if (Current.Text == "const")
            {
                isConst = true;
                Advance();
            }
            else if (Current.Text == "uniform" || SkippedModifiers.Contains(Current.Text))
            {
                Advance();
            }
            else
            {
                break;
            }
        }
        TypeSyntax type = ParseType();
        isConst |= Accept("const");
        // C++ references: const T& reads, T& writes back
        if (Accept("&"))
        {
            mode = isConst ? ParameterMode.In : ParameterMode.InOut;
        }
        Token name = ExpectIdentifier();
        List<ExpressionSyntax?> dimensions = ParseArrayDimensions();
        SkipSemantic();
        ExpressionSyntax? defaultValue = Accept("=") ? ParseAssignment() : null;
        return new ParameterSyntax(mode, isConst, type, name.Text, name.Span, dimensions, defaultValue, SpanFrom(start));
    }

    private VariableDeclarationSyntax ParseVariableDeclaration(StorageModifiers extraModifiers)
    {
        Token start = Current;
        StorageModifiers modifiers = SkipModifiers() | extraModifiers;
        TypeSyntax type = ParseType();
        return ParseDeclarators(modifiers, type, start);
    }

    private VariableDeclarationSyntax ParseDeclarators(StorageModifiers modifiers, TypeSyntax type, Token start)
    {
        List<DeclaratorSyntax> declarators = new List<DeclaratorSyntax>();
        do
        {
            Token name = ExpectIdentifier();
            List<ExpressionSyntax?> dimensions = ParseArrayDimensions();
            SkipSemantic();
            ExpressionSyntax? initializer = null;
            if (Accept("="))
            {
                initializer = Is("{") ? ParseInitializerList() : ParseAssignment();
            }
            else if (Is("{"))
            {
                // C++ brace initialisation: float3 v{1, 2, 3}
                initializer = ParseInitializerList();
            }
            declarators.Add(new DeclaratorSyntax(name.Text, name.Span, dimensions, initializer));
        }
        while (Accept(","));
        ExpectStatementEnd();
        return new VariableDeclarationSyntax(modifiers, type, declarators, SpanFrom(start));
    }

    private List<ExpressionSyntax?> ParseArrayDimensions()
    {
        List<ExpressionSyntax?> dimensions = new List<ExpressionSyntax?>();
        while (Accept("["))
        {
            dimensions.Add(Is("]") ? null : ParseExpression());
            Expect("]");
        }
        return dimensions;
    }

    private StorageModifiers SkipModifiers()
    {
        StorageModifiers modifiers = StorageModifiers.None;
        while (Current.Kind == TokenKind.Identifier)
        {
            switch (Current.Text)
            {
                case "const":
                    modifiers |= StorageModifiers.Const;
                    break;
                case "static":
                    modifiers |= StorageModifiers.Static;
                    break;
                case "groupshared":
                    modifiers |= StorageModifiers.GroupShared;
                    break;
                case "uniform":
                    modifiers |= StorageModifiers.Uniform;
                    break;
                default:
                    if (!SkippedModifiers.Contains(Current.Text))
                    {
                        return modifiers;
                    }
                    break;
            }
            Advance();
        }
        return modifiers;
    }

    private void SkipAttributes()
    {
        while (Is("["))
        {
            int depth = 0;
            do
            {
                depth += Current.Text == "[" ? 1 : Current.Text == "]" ? -1 : 0;
                Advance();
            }
            while (depth > 0 && !IsEnd);
        }
    }

    /// <summary>`: SV_Target`, `: register(t0)`, `: packoffset(c0)` are irrelevant here.</summary>
    private void SkipSemantic()
    {
        while (Is(":") && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance();
            Advance();
            if (Accept("("))
            {
                while (!IsEnd && !Accept(")"))
                {
                    Advance();
                }
            }
        }
    }

    private void SkipRegister() => SkipSemantic();

    // ---- Types ----

    private bool IsTypeStart(int offset)
    {
        Token token = Peek(offset);
        if (token.Kind != TokenKind.Identifier)
        {
            return false;
        }
        if (token.Text == "std" && Peek(offset + 1).Is("::"))
        {
            return IsTypeStart(offset + 2);
        }
        return token.Text is "auto" or "unsigned" or "signed" or "long" or "vector" or "matrix" or "short"
            || IsBuiltinTypeName(token.Text) || _typeNames.Contains(token.Text);
    }

    public static bool IsBuiltinTypeName(string name)
    {
        if (ScalarTypeNames.Contains(name))
        {
            return true;
        }
        string scalar = name.TrimEnd('1', '2', '3', '4', 'x');
        if (!ScalarTypeNames.Contains(scalar))
        {
            return false;
        }
        string suffix = name[scalar.Length..];
        return suffix.Length == 1 && suffix[0] is >= '1' and <= '4'
            || suffix.Length == 3 && suffix[0] is >= '1' and <= '4' && suffix[1] == 'x' && suffix[2] is >= '1' and <= '4';
    }

    /// <summary>Skips a type at the cursor (lookahead only). Returns the offset after it, or -1.</summary>
    private int ScanType(int offset)
    {
        if (!IsTypeStart(offset))
        {
            return -1;
        }
        if (Peek(offset).Text == "std")
        {
            offset += 2;
        }
        string text = Peek(offset).Text;
        if (text is "unsigned" or "signed" or "long" or "short")
        {
            while (Peek(offset).Kind == TokenKind.Identifier && Peek(offset).Text is "unsigned" or "signed" or "long" or "short" or "int" or "char")
            {
                offset++;
            }
            return offset;
        }
        offset++;
        if (text is "vector" or "matrix" && Peek(offset).Is("<"))
        {
            int depth = 0;
            do
            {
                depth += Peek(offset).Is("<") ? 1 : Peek(offset).Is(">") ? -1 : Peek(offset).Is(">>") ? -2 : 0;
                offset++;
            }
            while (depth > 0 && Peek(offset).Kind != TokenKind.End);
        }
        return offset;
    }

    private TypeSyntax ParseType()
    {
        Token start = Current;
        if (Is("std") && Peek(1).Is("::"))
        {
            Advance();
            Advance();
        }
        if (Accept("auto"))
        {
            return new AutoTypeSyntax(start.Span);
        }
        // C++ integer spellings
        if (Current.Kind == TokenKind.Identifier && Current.Text is "unsigned" or "signed" or "long" or "short")
        {
            bool isUnsigned = false;
            int longs = 0;
            while (Current.Kind == TokenKind.Identifier && Current.Text is "unsigned" or "signed" or "long" or "short" or "int" or "char")
            {
                string word = Advance().Text;
                isUnsigned |= word == "unsigned";
                longs += word == "long" ? 1 : 0;
            }
            string name = longs >= 2 ? (isUnsigned ? "uint64_t" : "int64_t") : (isUnsigned ? "uint" : "int");
            return new NamedTypeSyntax(name, SpanFrom(start));
        }
        Token token = ExpectIdentifier();
        if (token.Text is "vector" or "matrix" && Accept("<"))
        {
            TypeSyntax element = ParseType();
            List<ExpressionSyntax> dimensions = new List<ExpressionSyntax>();
            while (Accept(","))
            {
                dimensions.Add(ParseBinary(BinaryLevels.Length - 2));
            }
            ExpectClosingAngle();
            return new GenericTypeSyntax(token.Text, element, dimensions, SpanFrom(start));
        }
        if (!IsBuiltinTypeName(token.Text) && !_typeNames.Contains(token.Text) && token.Text is not ("vector" or "matrix"))
        {
            throw Error($"Unknown type '{token.Text}'", token);
        }
        return new NamedTypeSyntax(token.Text, token.Span);
    }

    /// <summary>`&gt;` closing a template argument list; splits a `&gt;&gt;`.</summary>
    private void ExpectClosingAngle()
    {
        if (Is(">>"))
        {
            Token both = Current;
            _tokens[_position] = both with { Text = ">", Span = both.Span with { Offset = both.Span.Offset + 1, Length = 1, Column = both.Span.Column + 1 } };
            return;
        }
        Expect(">");
    }

    // ---- Statements ----

    private bool IsStatementKeyword() =>
        Current.Kind == TokenKind.Identifier && Current.Text is "if" or "for" or "while" or "do" or "switch" or "return" or "break" or "continue" or "discard";

    /// <summary>A declaration starts with modifiers and a type followed by a name.</summary>
    private bool IsDeclarationStart()
    {
        int offset = 0;
        while (Peek(offset).Kind == TokenKind.Identifier && (Peek(offset).Text is "const" or "static" or "groupshared" or "uniform"
            || SkippedModifiers.Contains(Peek(offset).Text)))
        {
            offset++;
        }
        int afterType = ScanType(offset);
        if (afterType < 0)
        {
            return false;
        }
        while (Peek(afterType).Is("const"))
        {
            afterType++;
        }
        return Peek(afterType).Kind == TokenKind.Identifier;
    }

    /// <summary>A function definition in a calculator line: type name '('.</summary>
    private bool IsFunctionStart()
    {
        int offset = 0;
        while (Peek(offset).Kind == TokenKind.Identifier && (Peek(offset).Text is "static" or "const" || SkippedModifiers.Contains(Peek(offset).Text)))
        {
            offset++;
        }
        int afterType = Peek(offset).Is("void") ? offset + 1 : ScanType(offset);
        return afterType >= 0 && Peek(afterType).Kind == TokenKind.Identifier && Peek(afterType + 1).Is("(");
    }

    private BlockSyntax ParseBlock()
    {
        Token start = Expect("{");
        _blockDepth++;
        try
        {
            return ParseBlockBody(start);
        }
        finally
        {
            _blockDepth--;
        }
    }

    private BlockSyntax ParseBlockBody(Token start)
    {
        List<StatementSyntax> statements = new List<StatementSyntax>();
        while (!Is("}") && !IsEnd)
        {
            int statementStart = _position;
            try
            {
                statements.Add(ParseStatement());
            }
            catch (ParseException exception)
            {
                Report(exception);
                Recover(statementStart);
            }
        }
        Expect("}");
        return new BlockSyntax(statements, SpanFrom(start));
    }

    private StatementSyntax ParseStatement()
    {
        SkipAttributes();
        Token start = Current;
        if (Is("{"))
        {
            return ParseBlock();
        }
        if (Accept(";"))
        {
            return new EmptyStatementSyntax(start.Span);
        }
        if (Accept("if"))
        {
            ExpressionSyntax condition = ParseParenthesized();
            StatementSyntax then = ParseStatement();
            StatementSyntax? otherwise = Accept("else") ? ParseStatement() : null;
            return new IfSyntax(condition, then, otherwise, SpanFrom(start));
        }
        if (Accept("for"))
        {
            Expect("(");
            _nesting++;
            StatementSyntax? initializer = Accept(";") ? null : ParseSimpleStatement();
            ExpressionSyntax? condition = Is(";") ? null : ParseExpression();
            Expect(";");
            ExpressionSyntax? step = Is(")") ? null : ParseExpression();
            _nesting--;
            Expect(")");
            return new ForSyntax(initializer, condition, step, ParseStatement(), SpanFrom(start));
        }
        if (Accept("while"))
        {
            ExpressionSyntax condition = ParseParenthesized();
            return new WhileSyntax(condition, ParseStatement(), false, SpanFrom(start));
        }
        if (Accept("do"))
        {
            StatementSyntax body = ParseStatement();
            Expect("while");
            ExpressionSyntax condition = ParseParenthesized();
            ExpectStatementEnd();
            return new WhileSyntax(condition, body, true, SpanFrom(start));
        }
        if (Is("switch"))
        {
            return ParseSwitch();
        }
        if (Accept("return"))
        {
            ExpressionSyntax? value = Is(";") || AtLineBreak ? null : ParseExpression();
            ExpectStatementEnd();
            return new ReturnSyntax(value, SpanFrom(start));
        }
        if (Accept("break") || Accept("continue") || Accept("discard"))
        {
            ExpectStatementEnd();
            return new JumpSyntax(start.Text, start.Span);
        }
        return ParseSimpleStatement();
    }

    /// <summary>A declaration or an expression, ending with ';'.</summary>
    private StatementSyntax ParseSimpleStatement()
    {
        Token start = Current;
        if (IsDeclarationStart())
        {
            return ParseVariableDeclaration(StorageModifiers.None);
        }
        ExpressionSyntax expression = ParseExpression();
        ExpectStatementEnd();
        return new ExpressionStatementSyntax(expression, SpanFrom(start));
    }

    /// <summary>';', optional at the very end of a calculator line and at the end of a worksheet line.</summary>
    private void ExpectStatementEnd()
    {
        if (!(_isInteractive && (IsEnd || AtLineBreak)))
        {
            Expect(";");
        }
    }

    private SwitchSyntax ParseSwitch()
    {
        Token start = Expect("switch");
        ExpressionSyntax value = ParseParenthesized();
        Expect("{");
        _blockDepth++;
        try
        {
            return ParseSwitchBody(start, value);
        }
        finally
        {
            _blockDepth--;
        }
    }

    private SwitchSyntax ParseSwitchBody(Token start, ExpressionSyntax value)
    {
        List<SwitchSectionSyntax> sections = new List<SwitchSectionSyntax>();
        while (!Accept("}"))
        {
            Token sectionStart = Current;
            List<ExpressionSyntax?> labels = new List<ExpressionSyntax?>();
            while (Is("case") || Is("default"))
            {
                if (Accept("default"))
                {
                    labels.Add(null);
                }
                else
                {
                    Advance();
                    labels.Add(ParseConditional());
                }
                Expect(":");
            }
            if (labels.Count == 0)
            {
                throw Error("Expected 'case' or 'default'", Current);
            }
            List<StatementSyntax> statements = new List<StatementSyntax>();
            while (!Is("case") && !Is("default") && !Is("}") && !IsEnd)
            {
                statements.Add(ParseStatement());
            }
            sections.Add(new SwitchSectionSyntax(labels, statements, SpanFrom(sectionStart)));
        }
        return new SwitchSyntax(value, sections, SpanFrom(start));
    }

    // ---- Expressions (C precedence) ----

    private ExpressionSyntax ParseExpression()
    {
        Token start = Current;
        ExpressionSyntax expression = ParseAssignment();
        while (!AtLineBreak && Is(","))
        {
            Advance();
            expression = new BinaryExpressionSyntax(",", expression, ParseAssignment(), SpanFrom(start));
        }
        return expression;
    }

    private ExpressionSyntax ParseAssignment()
    {
        Token start = Current;
        ExpressionSyntax target = ParseConditional();
        if (!AtLineBreak && Current.Kind == TokenKind.Punctuator && AssignmentOperators.Contains(Current.Text))
        {
            string operation = Advance().Text;
            ExpressionSyntax value = Is("{") ? ParseInitializerList() : ParseAssignment();
            return new AssignmentSyntax(operation, target, value, SpanFrom(start));
        }
        return target;
    }

    private ExpressionSyntax ParseConditional()
    {
        Token start = Current;
        ExpressionSyntax condition = ParseBinary(0);
        if (!AtLineBreak && Accept("?"))
        {
            ExpressionSyntax whenTrue = ParseExpression();
            Expect(":");
            ExpressionSyntax whenFalse = ParseAssignment();
            return new ConditionalSyntax(condition, whenTrue, whenFalse, SpanFrom(start));
        }
        return condition;
    }

    private ExpressionSyntax ParseBinary(int level)
    {
        if (level == BinaryLevels.Length)
        {
            return ParseUnary();
        }
        Token start = Current;
        ExpressionSyntax left = ParseBinary(level + 1);
        while (!AtLineBreak && Current.Kind == TokenKind.Punctuator && BinaryLevels[level].Contains(Current.Text))
        {
            string operation = Advance().Text;
            left = new BinaryExpressionSyntax(operation, left, ParseBinary(level + 1), SpanFrom(start));
        }
        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        Token start = Current;
        if (Current.Kind == TokenKind.Punctuator && Current.Text is "-" or "+" or "!" or "~")
        {
            Advance();
            return new UnaryExpressionSyntax(start.Text, ParseUnary(), SpanFrom(start));
        }
        if (Current.Kind == TokenKind.Punctuator && Current.Text is "++" or "--")
        {
            Advance();
            return new IncrementSyntax(start.Text, true, ParseUnary(), SpanFrom(start));
        }
        // C cast: (float3)x, (float[2])x
        if (Is("(") && ScanType(1) is int afterType and > 0)
        {
            int close = afterType;
            while (Peek(close).Is("["))
            {
                while (!Peek(close).Is("]") && Peek(close).Kind != TokenKind.End)
                {
                    close++;
                }
                close++;
            }
            if (Peek(close).Is(")") && !Peek(close + 1).Is("."))
            {
                Advance();
                TypeSyntax type = ParseType();
                List<ExpressionSyntax?> dimensions = ParseArrayDimensions();
                Expect(")");
                ExpressionSyntax operand = ParseUnary();
                return new CastSyntax(type, dimensions.Select(dimension => dimension ?? throw Error("A cast needs array lengths", start)).ToList(),
                    operand, SpanFrom(start));
            }
        }
        return ParsePostfix(ParsePrimary());
    }

    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (!AtLineBreak)
        {
            Token token = Current;
            if (Accept("."))
            {
                Token member = ExpectIdentifier();
                expression = new MemberSyntax(expression, member.Text, member.Span, expression.Span.To(member.Span));
            }
            else if (Accept("["))
            {
                _nesting++;
                ExpressionSyntax index = ParseExpression();
                _nesting--;
                Token close = Expect("]");
                expression = new IndexSyntax(expression, index, expression.Span.To(close.Span));
            }
            else if (Current.Kind == TokenKind.Punctuator && Current.Text is "++" or "--")
            {
                Advance();
                expression = new IncrementSyntax(token.Text, false, expression, expression.Span.To(token.Span));
            }
            else
            {
                return expression;
            }
        }
        return expression;
    }

    private ExpressionSyntax ParsePrimary()
    {
        Token token = Current;
        if (token.Kind == TokenKind.Number)
        {
            Advance();
            LiteralSyntax number = ParseNumber(token);
            if (_isUnitName != null && !AtLineBreak && Current.Kind == TokenKind.Identifier && _isUnitName(Current.Text) && !Peek(1).Is("("))
            {
                return ParseUnitLiteral(number, token);
            }
            return number;
        }
        if (Is("("))
        {
            return ParseParenthesized();
        }
        if (Is("{"))
        {
            return ParseInitializerList();
        }
        if (token.Kind != TokenKind.Identifier)
        {
            throw Error(token.Kind == TokenKind.End ? "Unexpected end of input" : $"Unexpected '{token.Text}'", token);
        }

        if (token.Text is "true" or "false")
        {
            Advance();
            return new LiteralSyntax(ScalarKind.Bool, token.Text == "true" ? 1UL : 0UL, token.Text, token.Span);
        }
        if (token.Text is "static_cast" or "reinterpret_cast")
        {
            Advance();
            Expect("<");
            TypeSyntax type = ParseType();
            ExpectClosingAngle();
            Expect("(");
            ExpressionSyntax operand = ParseExpression();
            Expect(")");
            return new CastSyntax(type, Array.Empty<ExpressionSyntax>(), operand, SpanFrom(token));
        }
        // vector<float, 3>(...) and the C++ spellings unsigned(x), std::uint32_t(x)
        if (IsTypeStart(0) && (token.Text is "vector" or "matrix" or "unsigned" or "long" or "std") && !(token.Text == "std" && !IsTypeStart(2)))
        {
            TypeSyntax type = ParseType();
            Expect("(");
            return new ConstructorSyntax(type, ParseArguments(), SpanFrom(token));
        }

        Advance();
        string name = token.Text;
        // std::sqrt, std::clamp... are the HLSL intrinsics of the same name
        if (name == "std" && Accept("::"))
        {
            name = ExpectIdentifier().Text;
        }
        if (!AtLineBreak && Accept("("))
        {
            return new CallSyntax(name, token.Span, ParseArguments(), SpanFrom(token));
        }
        return new NameSyntax(name, token.Span);
    }

    /// <summary>Arguments after '(' up to and including ')'.</summary>
    private List<ExpressionSyntax> ParseArguments()
    {
        _nesting++;
        try
        {
            return ParseArgumentList();
        }
        finally
        {
            _nesting--;
        }
    }

    /// <summary>`( expression )`, the line-break rule suspended inside.</summary>
    private ExpressionSyntax ParseParenthesized()
    {
        Expect("(");
        _nesting++;
        ExpressionSyntax inner = ParseExpression();
        _nesting--;
        Expect(")");
        return inner;
    }

    private List<ExpressionSyntax> ParseArgumentList()
    {
        List<ExpressionSyntax> arguments = new List<ExpressionSyntax>();
        if (Accept(")"))
        {
            return arguments;
        }
        do
        {
            arguments.Add(ParseAssignment());
        }
        while (Accept(","));
        Expect(")");
        return arguments;
    }

    private InitializerListSyntax ParseInitializerList()
    {
        Token start = Expect("{");
        _nesting++;
        try
        {
            return ParseInitializerElements(start);
        }
        finally
        {
            _nesting--;
        }
    }

    private InitializerListSyntax ParseInitializerElements(Token start)
    {
        List<ExpressionSyntax> elements = new List<ExpressionSyntax>();
        while (!Is("}"))
        {
            elements.Add(Is("{") ? ParseInitializerList() : ParseAssignment());
            if (!Accept(","))
            {
                break;
            }
        }
        Expect("}");
        return new InitializerListSyntax(elements, SpanFrom(start));
    }

    /// <summary>`3 km/h`, `9.81 m/s^2`, `2 m^-1`: continues only into another unit, so `6 m / x` divides.</summary>
    private ExpressionSyntax ParseUnitLiteral(LiteralSyntax number, Token start)
    {
        double scale = 1;
        Dimension dimension = Dimension.None;
        List<string> text = new List<string>();
        bool isDivision = false;
        while (true)
        {
            Token unitToken = ExpectIdentifier();
            UnitDefinition unit = UnitTable.Units[unitToken.Text];
            int exponent = 1;
            string factorText = unit.Name;
            if (!AtLineBreak && Is("^") && (Peek(1).Kind == TokenKind.Number || (Peek(1).Is("-") && Peek(2).Kind == TokenKind.Number)))
            {
                Advance();
                bool isNegative = Accept("-");
                exponent = int.Parse(Advance().Text, CultureInfo.InvariantCulture) * (isNegative ? -1 : 1);
                factorText += $"^{exponent}";
            }
            double factorScale = Math.Pow(unit.ToSi, exponent);
            Dimension factorDimension = unit.Dimension.Power(exponent)!;
            scale = isDivision ? scale / factorScale : scale * factorScale;
            dimension = isDivision ? dimension.Divide(factorDimension) : dimension.Multiply(factorDimension);
            text.Add((text.Count == 0 ? string.Empty : isDivision ? "/" : "*") + factorText);

            if (!AtLineBreak && Current.Text is "/" or "*" && Peek(1).Kind == TokenKind.Identifier && _isUnitName!(Peek(1).Text) && !Peek(2).Is("("))
            {
                isDivision = Advance().Text == "/";
                continue;
            }
            break;
        }
        double numberValue = Scalars.ToNumber(number.Kind, number.Bits);
        return new UnitLiteralSyntax(numberValue * scale, dimension, string.Concat(text), SpanFrom(start));
    }

    /// <summary>
    /// HLSL literals: unsuffixed `1` / `1.5` are literal int / literal float (64-bit), `u` uint, `l`/`ll` int64, `ul` uint64,
    /// `f` float, `h` half (= float without 16-bit types), `l`/`lf` on a float double. Leading 0 is octal.
    /// </summary>
    private LiteralSyntax ParseNumber(Token token)
    {
        string text = token.Text.ToLowerInvariant();
        bool isHex = text.StartsWith("0x", StringComparison.Ordinal);
        string digits = isHex ? text[2..].TrimEnd('u', 'l') : text.TrimEnd('u', 'l', 'f', 'h');
        string suffix = text[(isHex ? 2 + digits.Length : digits.Length)..];
        bool isFloat = !isHex && (digits.Contains('.') || digits.Contains('e') || suffix is "f" or "h" or "lf");
        try
        {
            if (isFloat)
            {
                double value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                ScalarKind kind = suffix switch
                {
                    "" => ScalarKind.LiteralFloat,
                    "l" or "lf" => ScalarKind.Double,
                    _ => ScalarKind.Float,
                };
                ulong bits = kind == ScalarKind.Float ? Scalars.FromFloat((float)value) : Scalars.FromDouble(value);
                return new LiteralSyntax(kind, bits, token.Text, token.Span);
            }

            ulong integer = isHex ? ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : digits.Length > 1 && digits[0] == '0' ? System.Convert.ToUInt64(digits, 8)
                : ulong.Parse(digits, CultureInfo.InvariantCulture);
            ScalarKind integerKind = suffix switch
            {
                "" => ScalarKind.LiteralInt,
                "u" => integer > uint.MaxValue ? ScalarKind.UInt64 : ScalarKind.UInt,
                "l" or "ll" => ScalarKind.Int64,
                _ => ScalarKind.UInt64,
            };
            ulong encoded = integerKind switch
            {
                ScalarKind.UInt => (uint)integer,
                _ => integer,
            };
            return new LiteralSyntax(integerKind, encoded, token.Text, token.Span);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw Error($"Invalid number '{token.Text}'", token);
        }
    }

    // ---- Token helpers ----

    private bool IsEnd => Current.Kind == TokenKind.End;

    private Token Current => _tokens[_position];

    private Token Previous => _tokens[Math.Max(_position - 1, 0)];

    private Token Peek(int offset) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

    private Token Advance()
    {
        Token token = _tokens[_position];
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }
        return token;
    }

    private bool Is(string text) => Current.Kind != TokenKind.End && Current.Kind != TokenKind.String && Current.Text == text;

    private bool Accept(string text)
    {
        if (Is(text))
        {
            Advance();
            return true;
        }
        return false;
    }

    private Token Expect(string text) =>
        Is(text) ? Advance() : throw Error(IsEnd ? $"Expected '{text}' before the end" : $"Expected '{text}' but found '{Current.Text}'", Current);

    private Token ExpectIdentifier() =>
        Current.Kind == TokenKind.Identifier ? Advance() : throw Error(IsEnd ? "Expected a name before the end" : $"Expected a name but found '{Current.Text}'", Current);

    private SourceSpan SpanFrom(Token start) => start.Span.To(Previous.Span);

    private ParseException Error(string message, Token token) => new ParseException(message, token);

    private void Report(ParseException exception) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, exception.Message, _sourceName, exception.Token.Span));

    /// <summary>After an error: skip to the end of the statement or block, always making progress.</summary>
    private void Recover(int start)
    {
        if (_position == start)
        {
            Advance();
        }
        int depth = 0;
        while (!IsEnd)
        {
            if (Is("{"))
            {
                depth++;
            }
            else if (Is("}"))
            {
                if (depth == 0)
                {
                    return;
                }
                depth--;
                if (depth == 0)
                {
                    Advance();
                    Accept(";");
                    return;
                }
            }
            else if (Is(";") && depth == 0)
            {
                Advance();
                return;
            }
            Advance();
        }
    }
}
