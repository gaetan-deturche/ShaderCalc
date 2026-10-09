using System.Globalization;

namespace ShaderCalc.Syntax;

/// <summary>
/// C preprocessor subset: #define (object-like and function-like, # and ##), #undef, #if/#ifdef/#ifndef/#elif/#else/
/// #endif, #error. #include, #pragma and #line are ignored. Expanded tokens keep the invocation's position.
/// </summary>
public sealed class Preprocessor
{
    private sealed record Macro(string Name, List<string>? Parameters, bool IsVariadic, List<Token> Body);

    private readonly Dictionary<string, Macro> _macros = new Dictionary<string, Macro>(StringComparer.Ordinal);
    private DiagnosticBag _diagnostics;
    private string _sourceName;

    private Preprocessor(string sourceName, DiagnosticBag diagnostics)
    {
        _sourceName = sourceName;
        _diagnostics = diagnostics;
        foreach ((string name, string value) in new[]
        {
            ("__HLSL_VERSION", "2021"), ("__SHADER_TARGET_MAJOR", "6"), ("__SHADER_TARGET_MINOR", "0"), ("__SHADER_TARGET_STAGE", "5"),
        })
        {
            _macros[name] = new Macro(name, null, false, new List<Token> { new Token(TokenKind.Number, value, SourceSpan.None, false, true) });
        }
    }

    public static List<Token> Process(List<Token> tokens, string sourceName, DiagnosticBag diagnostics) =>
        new Preprocessor(sourceName, diagnostics).Run(tokens);

    /// <summary>A preprocessor whose macros carry over from one file to the next (worksheet tabs, like headers).</summary>
    public static Preprocessor CreateShared() => new Preprocessor(string.Empty, new DiagnosticBag());

    public List<Token> ProcessFile(List<Token> tokens, string sourceName, DiagnosticBag diagnostics)
    {
        _sourceName = sourceName;
        _diagnostics = diagnostics;
        return Run(tokens);
    }

    private List<Token> Run(List<Token> tokens)
    {
        List<Token> output = new List<Token>();
        List<Token> text = new List<Token>();
        // Each entry: is this region active, has any branch of the #if been taken, was the parent active
        Stack<(bool Active, bool Taken, bool ParentActive)> conditions = new Stack<(bool Active, bool Taken, bool ParentActive)>();
        bool active = true;

        int index = 0;
        while (index < tokens.Count && tokens[index].Kind != TokenKind.End)
        {
            Token token = tokens[index];
            if (!(token.StartsLine && token.Is("#")))
            {
                if (active)
                {
                    text.Add(token);
                }
                index++;
                continue;
            }

            // A directive runs to the end of its line
            int end = index + 1;
            while (!tokens[end].StartsLine)
            {
                end++;
            }
            List<Token> line = tokens.GetRange(index + 1, end - index - 1);
            index = end;
            if (line.Count == 0)
            {
                continue;
            }

            output.AddRange(Expand(text));
            text.Clear();
            string directive = line[0].Text;
            switch (directive)
            {
                case "if":
                case "ifdef":
                case "ifndef":
                {
                    bool condition = active && (directive == "if" ? EvaluateCondition(line) : _macros.ContainsKey(NameAt(line, 1)) == (directive == "ifdef"));
                    conditions.Push((condition, condition, active));
                    active = condition;
                    break;
                }
                case "elif":
                case "else":
                {
                    if (conditions.Count == 0)
                    {
                        Report(line[0], $"#{directive} without #if");
                        break;
                    }
                    (bool _, bool taken, bool parentActive) = conditions.Pop();
                    bool condition = parentActive && !taken && (directive == "else" || EvaluateCondition(line));
                    conditions.Push((condition, taken || condition, parentActive));
                    active = condition;
                    break;
                }
                case "endif":
                    if (conditions.Count == 0)
                    {
                        Report(line[0], "#endif without #if");
                        break;
                    }
                    active = conditions.Pop().ParentActive;
                    break;
                case "define" when active:
                    Define(line);
                    break;
                case "undef" when active:
                    _macros.Remove(NameAt(line, 1));
                    break;
                case "error" when active:
                    Report(line[0], "#error " + string.Join(" ", line.Skip(1).Select(part => part.Text)));
                    break;
                case "include" when active:
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "#include is ignored: paste the code it needs", _sourceName, line[0].Span));
                    break;
                case "pragma":
                case "line":
                case "define":
                case "undef":
                case "error":
                case "include":
                    break;
                default:
                    if (active)
                    {
                        Report(line[0], $"Unknown directive #{directive}");
                    }
                    break;
            }
        }

        if (conditions.Count > 0)
        {
            Report(tokens[^1], "Missing #endif");
        }
        output.AddRange(Expand(text));
        output.Add(tokens[^1]);
        return output;
    }

    private string NameAt(List<Token> line, int position)
    {
        if (position < line.Count && line[position].Kind == TokenKind.Identifier)
        {
            return line[position].Text;
        }
        Report(line[0], $"#{line[0].Text} needs a macro name");
        return string.Empty;
    }

    private void Define(List<Token> line)
    {
        string name = NameAt(line, 1);
        if (name.Length == 0)
        {
            return;
        }
        int bodyStart = 2;
        List<string>? parameters = null;
        bool isVariadic = false;
        // Function-like only when '(' touches the name
        if (line.Count > 2 && line[2].Is("(") && !line[2].HasLeadingSpace)
        {
            parameters = new List<string>();
            int position = 3;
            while (position < line.Count && !line[position].Is(")"))
            {
                if (line[position].Is("..."))
                {
                    isVariadic = true;
                    parameters.Add("__VA_ARGS__");
                }
                else if (line[position].Kind == TokenKind.Identifier)
                {
                    parameters.Add(line[position].Text);
                }
                position++;
            }
            bodyStart = position + 1;
        }
        _macros[name] = new Macro(name, parameters, isVariadic, line.Skip(bodyStart).ToList());
    }

    // ---- Expansion ----

    private List<Token> Expand(List<Token> input)
    {
        List<Token> output = new List<Token>();
        // Pending tokens, last one first: expansions are pushed back and rescanned
        List<Token> pending = new List<Token>(input);
        pending.Reverse();
        while (pending.Count > 0)
        {
            Token token = Pop(pending);
            if (token.Kind != TokenKind.Identifier || !_macros.TryGetValue(token.Text, out Macro? macro) || (token.Hidden?.Contains(macro.Name) ?? false))
            {
                output.Add(token);
                continue;
            }

            List<List<Token>>? arguments = null;
            if (macro.Parameters != null)
            {
                if (pending.Count == 0 || !pending[^1].Is("("))
                {
                    output.Add(token);
                    continue;
                }
                Pop(pending);
                arguments = CollectArguments(pending, token);
                if (arguments.Count == 1 && arguments[0].Count == 0 && macro.Parameters.Count == 0)
                {
                    arguments.Clear();
                }
                if (arguments.Count != macro.Parameters.Count && !(macro.IsVariadic && arguments.Count >= macro.Parameters.Count - 1))
                {
                    Report(token, $"Macro '{macro.Name}' takes {macro.Parameters.Count} arguments, got {arguments.Count}");
                    continue;
                }
            }

            HashSet<string> hidden = new HashSet<string>(token.Hidden ?? new HashSet<string>(), StringComparer.Ordinal) { macro.Name };
            // The expansion starts where the invocation did (worksheet lines end at line starts)
            List<Token> replacement = Substitute(macro, arguments, token)
                .Select((part, index) => part with { Span = token.Span, Hidden = hidden, StartsLine = index == 0 && token.StartsLine })
                .ToList();
            for (int position = replacement.Count - 1; position >= 0; position--)
            {
                pending.Add(replacement[position]);
            }
        }
        return output;
    }

    private static Token Pop(List<Token> pending)
    {
        Token token = pending[^1];
        pending.RemoveAt(pending.Count - 1);
        return token;
    }

    private List<List<Token>> CollectArguments(List<Token> pending, Token invocation)
    {
        List<List<Token>> arguments = new List<List<Token>> { new List<Token>() };
        int depth = 0;
        while (true)
        {
            if (pending.Count == 0)
            {
                Report(invocation, $"Unterminated call of macro '{invocation.Text}'");
                return arguments;
            }
            Token token = Pop(pending);
            if (token.Is("(") || token.Is("[") || token.Is("{"))
            {
                depth++;
            }
            else if (token.Is(")") || token.Is("]") || token.Is("}"))
            {
                if (depth == 0 && token.Is(")"))
                {
                    return arguments;
                }
                depth--;
            }
            else if (depth == 0 && token.Is(","))
            {
                arguments.Add(new List<Token>());
                continue;
            }
            arguments[^1].Add(token);
        }
    }

    private List<Token> Substitute(Macro macro, List<List<Token>>? arguments, Token invocation)
    {
        List<Token> body = macro.Body;
        List<Token> result = new List<Token>();
        for (int position = 0; position < body.Count; position++)
        {
            Token token = body[position];
            int parameter = ParameterIndex(macro, token);
            // #param: the argument as a string
            if (token.Is("#") && arguments != null && position + 1 < body.Count && ParameterIndex(macro, body[position + 1]) >= 0)
            {
                List<Token> argument = Argument(macro, arguments, ParameterIndex(macro, body[++position]));
                string text = string.Join(" ", argument.Select(part => part.Text));
                result.Add(new Token(TokenKind.String, "\"" + text.Replace("\"", "\\\"") + "\"", invocation.Span, false, true));
                continue;
            }
            if (parameter >= 0 && arguments != null)
            {
                bool isPasted = (position > 0 && body[position - 1].Is("##")) || (position + 1 < body.Count && body[position + 1].Is("##"));
                List<Token> argument = Argument(macro, arguments, parameter);
                result.AddRange(isPasted ? argument : Expand(argument));
                continue;
            }
            result.Add(token);
        }

        // a ## b: glue the neighbours into one token
        for (int position = 0; position < result.Count; position++)
        {
            if (!result[position].Is("##") || result[position].Kind == TokenKind.String)
            {
                continue;
            }
            if (position == 0 || position + 1 >= result.Count)
            {
                result.RemoveAt(position--);
                continue;
            }
            string glued = result[position - 1].Text + result[position + 1].Text;
            List<Token> relexed = Lexer.Tokenize(glued, _sourceName, new DiagnosticBag());
            Token pasted = relexed.Count == 2 ? relexed[0] with { Span = invocation.Span } : result[position - 1];
            result.RemoveRange(position - 1, 3);
            result.Insert(position - 1, pasted);
            position--;
        }
        return result;
    }

    private static int ParameterIndex(Macro macro, Token token) =>
        macro.Parameters != null && token.Kind == TokenKind.Identifier ? macro.Parameters.IndexOf(token.Text) : -1;

    private static List<Token> Argument(Macro macro, List<List<Token>> arguments, int parameter)
    {
        if (macro.IsVariadic && parameter == macro.Parameters!.Count - 1)
        {
            // __VA_ARGS__: the remaining arguments, commas included
            List<Token> rest = new List<Token>();
            for (int index = parameter; index < arguments.Count; index++)
            {
                if (index > parameter)
                {
                    rest.Add(new Token(TokenKind.Punctuator, ",", SourceSpan.None, false, false));
                }
                rest.AddRange(arguments[index]);
            }
            return rest;
        }
        return parameter < arguments.Count ? arguments[parameter] : new List<Token>();
    }

    // ---- #if expressions ----

    private bool EvaluateCondition(List<Token> line)
    {
        // defined(X) / defined X are resolved before expansion
        List<Token> resolved = new List<Token>();
        for (int position = 1; position < line.Count; position++)
        {
            if (line[position].Is("defined"))
            {
                bool hasParenthesis = position + 1 < line.Count && line[position + 1].Is("(");
                int namePosition = position + (hasParenthesis ? 2 : 1);
                string name = namePosition < line.Count ? line[namePosition].Text : string.Empty;
                resolved.Add(new Token(TokenKind.Number, _macros.ContainsKey(name) ? "1" : "0", line[position].Span, false, true));
                position = namePosition + (hasParenthesis ? 1 : 0);
                continue;
            }
            resolved.Add(line[position]);
        }

        List<Token> expanded = Expand(resolved);
        expanded.Add(new Token(TokenKind.End, string.Empty, line[0].Span, true, true));
        int cursor = 0;
        try
        {
            long value = ConditionExpression(expanded, ref cursor, 0);
            return value != 0;
        }
        catch (FormatException exception)
        {
            Report(line[0], $"Invalid #{line[0].Text} expression: {exception.Message}");
            return false;
        }
    }

    private static readonly string[][] ConditionLevels =
    {
        new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "==", "!=" },
        new[] { "<", ">", "<=", ">=" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
    };

    private static long ConditionExpression(List<Token> tokens, ref int cursor, int level)
    {
        if (level == 0)
        {
            long condition = ConditionExpression(tokens, ref cursor, 1);
            if (tokens[cursor].Is("?"))
            {
                cursor++;
                long whenTrue = ConditionExpression(tokens, ref cursor, 0);
                Expect(tokens, ref cursor, ":");
                long whenFalse = ConditionExpression(tokens, ref cursor, 0);
                return condition != 0 ? whenTrue : whenFalse;
            }
            return condition;
        }
        if (level > ConditionLevels.Length)
        {
            return ConditionUnary(tokens, ref cursor);
        }
        long left = ConditionExpression(tokens, ref cursor, level + 1);
        while (tokens[cursor].Kind == TokenKind.Punctuator && ConditionLevels[level - 1].Contains(tokens[cursor].Text))
        {
            string operation = tokens[cursor++].Text;
            long right = ConditionExpression(tokens, ref cursor, level + 1);
            left = operation switch
            {
                "||" => left != 0 || right != 0 ? 1 : 0,
                "&&" => left != 0 && right != 0 ? 1 : 0,
                "|" => left | right,
                "^" => left ^ right,
                "&" => left & right,
                "==" => left == right ? 1 : 0,
                "!=" => left != right ? 1 : 0,
                "<" => left < right ? 1 : 0,
                ">" => left > right ? 1 : 0,
                "<=" => left <= right ? 1 : 0,
                ">=" => left >= right ? 1 : 0,
                "<<" => left << (int)right,
                ">>" => left >> (int)right,
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                "/" => right == 0 ? throw new FormatException("division by zero") : left / right,
                _ => right == 0 ? throw new FormatException("division by zero") : left % right,
            };
        }
        return left;
    }

    private static long ConditionUnary(List<Token> tokens, ref int cursor)
    {
        Token token = tokens[cursor++];
        switch (token.Text)
        {
            case "!":
                return ConditionUnary(tokens, ref cursor) == 0 ? 1 : 0;
            case "~":
                return ~ConditionUnary(tokens, ref cursor);
            case "-":
                return -ConditionUnary(tokens, ref cursor);
            case "+":
                return ConditionUnary(tokens, ref cursor);
            case "(":
                long inner = ConditionExpression(tokens, ref cursor, 0);
                Expect(tokens, ref cursor, ")");
                return inner;
        }
        if (token.Kind == TokenKind.Number)
        {
            string digits = token.Text.TrimEnd('u', 'U', 'l', 'L');
            return digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(digits, CultureInfo.InvariantCulture);
        }
        // An identifier left after expansion counts as 0
        if (token.Kind == TokenKind.Identifier)
        {
            return 0;
        }
        throw new FormatException($"unexpected '{token.Text}'");
    }

    private static void Expect(List<Token> tokens, ref int cursor, string text)
    {
        if (!tokens[cursor].Is(text))
        {
            throw new FormatException($"expected '{text}'");
        }
        cursor++;
    }

    private void Report(Token token, string message) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, _sourceName, token.Span));
}
