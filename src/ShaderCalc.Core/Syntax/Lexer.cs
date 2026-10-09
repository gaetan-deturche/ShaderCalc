namespace ShaderCalc.Syntax;

public enum TokenKind
{
    Identifier,
    Number,
    String,
    Punctuator,
    End,
}

/// <summary>
/// A token. <see cref="StartsLine"/> marks the first token of a source line (preprocessor directives);
/// <see cref="Hidden"/> holds the macros that must not expand it again.
/// </summary>
public sealed record Token(TokenKind Kind, string Text, SourceSpan Span, bool StartsLine, bool HasLeadingSpace)
{
    public IReadOnlySet<string>? Hidden { get; init; }

    public bool Is(string text) => Kind is TokenKind.Punctuator or TokenKind.Identifier && Text == text;

    public override string ToString() => Text;
}

public static class Lexer
{
    private static readonly string[] Punctuators =
    {
        "<<=", ">>=", "...", "::", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "+=", "-=", "*=", "/=",
        "%=", "&=", "|=", "^=", "##", "+", "-", "*", "/", "%", "&", "|", "^", "~", "!", "<", ">", "=", "?", ":", ";", ",",
        ".", "(", ")", "[", "]", "{", "}", "#",
    };

    /// <summary>Whole-word literal suffixes; anything else after a number is a separate word (a unit in calculator lines).</summary>
    private static readonly HashSet<string> IntegerSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "u", "l", "ul", "lu", "ll", "ull", "llu",
    };

    private static readonly HashSet<string> FloatSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "f", "h", "l", "lf" };

    public static List<Token> Tokenize(string source, string sourceName, DiagnosticBag diagnostics)
    {
        List<Token> tokens = new List<Token>();
        int index = 0;
        int line = 1;
        int lineStart = 0;
        bool startsLine = true;
        bool hasLeadingSpace = false;

        while (index < source.Length)
        {
            char character = source[index];
            if (character == '\n')
            {
                index++;
                line++;
                lineStart = index;
                startsLine = true;
                hasLeadingSpace = false;
                continue;
            }
            // Line continuation: the next line belongs to this one
            if (character == '\\' && NextIsNewline(source, index + 1, out int newlineLength))
            {
                index += 1 + newlineLength;
                line++;
                lineStart = index;
                hasLeadingSpace = true;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                index++;
                hasLeadingSpace = true;
                continue;
            }
            if (character == '/' && Peek(source, index + 1) == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }
                hasLeadingSpace = true;
                continue;
            }
            if (character == '/' && Peek(source, index + 1) == '*')
            {
                int commentLine = line;
                int commentColumn = index - lineStart + 1;
                index += 2;
                while (index < source.Length && !(source[index] == '*' && Peek(source, index + 1) == '/'))
                {
                    if (source[index] == '\n')
                    {
                        line++;
                        lineStart = index + 1;
                    }
                    index++;
                }
                if (index >= source.Length)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "Unterminated /* comment", sourceName, new SourceSpan(index, 0, commentLine, commentColumn)));
                }
                index = Math.Min(index + 2, source.Length);
                hasLeadingSpace = true;
                continue;
            }

            int start = index;
            int column = index - lineStart + 1;
            TokenKind kind;
            if (char.IsAsciiDigit(character) || (character == '.' && char.IsAsciiDigit(Peek(source, index + 1))))
            {
                index = ScanNumber(source, index);
                kind = TokenKind.Number;
            }
            else if (char.IsLetter(character) || character == '_')
            {
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                {
                    index++;
                }
                kind = TokenKind.Identifier;
            }
            else if (character is '"' or '\'')
            {
                index++;
                while (index < source.Length && source[index] != character && source[index] != '\n')
                {
                    index += source[index] == '\\' ? 2 : 1;
                }
                index = Math.Min(index + 1, source.Length);
                kind = TokenKind.String;
            }
            else
            {
                string? punctuator = Punctuators.FirstOrDefault(candidate => string.CompareOrdinal(source, index, candidate, 0, candidate.Length) == 0);
                if (punctuator == null)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, $"Unexpected character '{character}'", sourceName, new SourceSpan(index, 1, line, column)));
                    index++;
                    continue;
                }
                index += punctuator.Length;
                kind = TokenKind.Punctuator;
            }

            tokens.Add(new Token(kind, source[start..index], new SourceSpan(start, index - start, line, column), startsLine, hasLeadingSpace));
            startsLine = false;
            hasLeadingSpace = false;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, new SourceSpan(index, 0, line, index - lineStart + 1), true, true));
        return tokens;
    }

    private static int ScanNumber(string source, int index)
    {
        bool isHex = source[index] == '0' && Peek(source, index + 1) is 'x' or 'X';
        if (isHex)
        {
            index += 2;
            while (index < source.Length && char.IsAsciiHexDigit(source[index]))
            {
                index++;
            }
            return ScanSuffix(source, index, IntegerSuffixes);
        }

        bool isFloat = false;
        while (index < source.Length && (char.IsAsciiDigit(source[index]) || source[index] == '.'))
        {
            isFloat |= source[index] == '.';
            index++;
        }
        // Exponent only when digits follow, so `2 em` stays a number and a word
        if (index < source.Length && source[index] is 'e' or 'E')
        {
            int digits = Peek(source, index + 1) is '+' or '-' ? index + 2 : index + 1;
            if (char.IsAsciiDigit(Peek(source, digits)))
            {
                isFloat = true;
                index = digits;
                while (index < source.Length && char.IsAsciiDigit(source[index]))
                {
                    index++;
                }
            }
        }
        // `1f` is a float in HLSL; `2km` is the number 2 then the unit km
        int afterSuffix = ScanSuffix(source, index, FloatSuffixes);
        if (afterSuffix == index && !isFloat)
        {
            afterSuffix = ScanSuffix(source, index, IntegerSuffixes);
        }
        return afterSuffix;
    }

    private static int ScanSuffix(string source, int index, HashSet<string> suffixes)
    {
        int wordEnd = index;
        while (wordEnd < source.Length && (char.IsLetterOrDigit(source[wordEnd]) || source[wordEnd] == '_'))
        {
            wordEnd++;
        }
        return wordEnd > index && suffixes.Contains(source[index..wordEnd]) ? wordEnd : index;
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    private static bool NextIsNewline(string source, int index, out int length)
    {
        length = Peek(source, index) == '\r' && Peek(source, index + 1) == '\n' ? 2 : Peek(source, index) == '\n' ? 1 : 0;
        return length > 0;
    }
}
