using System.Globalization;
using System.Text;

namespace SqlFlow.Lineage.PowerQuery;

/// <summary>A Power Query (M) expression, as far as <see cref="MParser"/> reads the language.</summary>
internal abstract record MExpr;

/// <summary><c>let name = expr, ... in body</c>.</summary>
internal sealed record MLet(IReadOnlyList<(string Name, MExpr Value)> Steps, MExpr Body) : MExpr;

/// <summary>An identifier (<c>Source</c>, <c>#"Renamed Columns"</c>, <c>Table.SelectColumns</c>).</summary>
internal sealed record MIdentifier(string Name) : MExpr;

/// <summary>A text literal, unescaped.</summary>
internal sealed record MText(string Value) : MExpr;

/// <summary>A number literal, as written.</summary>
internal sealed record MNumber(string Value) : MExpr;

/// <summary><c>true</c>, <c>false</c>, or <c>null</c>.</summary>
internal sealed record MKeywordLiteral(string Keyword) : MExpr;

/// <summary><c>{a, b}</c>.</summary>
internal sealed record MList(IReadOnlyList<MExpr> Items) : MExpr;

/// <summary><c>[a = x, b = y]</c> (a record), or <c>[Column]</c> (a field reference inside <c>each</c>).</summary>
internal sealed record MRecord(IReadOnlyList<(string Name, MExpr Value)> Fields) : MExpr;

/// <summary><c>[Column]</c> on its own: a field of the implicit <c>_</c> row.</summary>
internal sealed record MFieldReference(string Field) : MExpr;

/// <summary><c>target(args)</c>.</summary>
internal sealed record MCall(MExpr Target, IReadOnlyList<MExpr> Arguments) : MExpr;

/// <summary><c>target{selector}</c>: an item lookup.</summary>
internal sealed record MItemAccess(MExpr Target, MExpr Selector) : MExpr;

/// <summary><c>target[field]</c>: a field access on a value.</summary>
internal sealed record MFieldAccess(MExpr Target, string Field) : MExpr;

/// <summary><c>each body</c>.</summary>
internal sealed record MEach(MExpr Body) : MExpr;

/// <summary>A binary operator (<c>&amp;</c>, <c>+</c>, <c>=</c>, <c>and</c>, ...).</summary>
internal sealed record MBinary(string Operator, MExpr Left, MExpr Right) : MExpr;

/// <summary>A unary operator (<c>-</c>, <c>not</c>).</summary>
internal sealed record MUnary(string Operator, MExpr Operand) : MExpr;

/// <summary><c>if c then a else b</c>.</summary>
internal sealed record MIf(MExpr Condition, MExpr Then, MExpr Else) : MExpr;

/// <summary>A type expression (<c>type text</c>, <c>Int64.Type</c> is an identifier instead); kept as its text.</summary>
internal sealed record MType(string Text) : MExpr;

/// <summary>A function value (<c>(x) =&gt; body</c>): parsed so a query containing one still reads.</summary>
internal sealed record MFunction(IReadOnlyList<string> Parameters, MExpr Body) : MExpr;

/// <summary>Raised when M text uses a form this reader does not cover. The message names the form.</summary>
internal sealed class MParseException(string message) : Exception(message);

/// <summary>
/// A reader for the part of the Power Query formula language a table's query uses: <c>let</c> step chains,
/// identifiers (including <c>#"quoted"</c> ones), literals, lists, records, item and field access, function calls,
/// <c>each</c>, operators, <c>if</c>, and type expressions. It builds a syntax tree and nothing else: what the steps
/// mean is <see cref="PowerQueryLineage"/>'s concern. A form outside that set is refused with its position rather
/// than guessed at.
/// </summary>
internal static class MParser
{
    /// <summary>The deepest nesting accepted, so a pathological expression cannot exhaust the stack.</summary>
    private const int MaxDepth = 200;

    public static MExpr Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(Tokenize(text));
        var expression = parser.ParseExpression(0);
        parser.Expect(TokenKind.End);
        return expression;
    }

    private enum TokenKind
    {
        Identifier,
        Text,
        Number,
        Symbol,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Value, int Position);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new MParseException($"an unterminated comment starts at {i}");
                }

                i = close + 2;
                continue;
            }

            if (c == '"' || (c == '#' && i + 1 < text.Length && text[i + 1] == '"'))
            {
                var start = i;
                var quoted = c == '#';
                i += quoted ? 2 : 1;
                var value = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new MParseException($"an unterminated text literal starts at {start}");
                    }

                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(text[i]);
                    i++;
                }

                tokens.Add(new Token(quoted ? TokenKind.Identifier : TokenKind.Text, value.ToString(), start));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    i++;
                    if (i < text.Length && (text[i] == '+' || text[i] == '-'))
                    {
                        i++;
                    }

                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i], start));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                // An identifier may be dotted (Table.SelectColumns, JoinKind.LeftOuter): each part joins the name.
                var start = i;
                i++;
                while (i < text.Length
                       && (char.IsLetterOrDigit(text[i]) || text[i] == '_'
                           || (text[i] == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            if (two is "=>" or "<=" or ">=" or "<>" or "..")
            {
                tokens.Add(new Token(TokenKind.Symbol, two, i));
                i += 2;
                continue;
            }

            if ("=,(){}[]&+-*/<>;@?!".Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), i));
                i++;
                continue;
            }

            throw new MParseException($"the character '{c}' at {i} is not part of the Power Query this reads");
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private int _depth;

        private Token Current => tokens[_position];

        private bool IsSymbol(string symbol) => Current.Kind == TokenKind.Symbol && Current.Value == symbol;

        private bool IsKeyword(string keyword) => Current.Kind == TokenKind.Identifier && Current.Value == keyword;

        public void Expect(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                throw new MParseException($"expected {kind} at {Current.Position} but found '{Current.Value}'");
            }
        }

        private void ExpectSymbol(string symbol)
        {
            if (!IsSymbol(symbol))
            {
                throw new MParseException($"expected '{symbol}' at {Current.Position} but found '{Current.Value}'");
            }

            _position++;
        }

        private void ExpectKeyword(string keyword)
        {
            if (!IsKeyword(keyword))
            {
                throw new MParseException($"expected '{keyword}' at {Current.Position} but found '{Current.Value}'");
            }

            _position++;
        }

        private string ExpectName()
        {
            if (Current.Kind != TokenKind.Identifier)
            {
                throw new MParseException($"expected a name at {Current.Position} but found '{Current.Value}'");
            }

            return tokens[_position++].Value;
        }

        // Operator precedence, loosest first: or, and, comparison, additive (+ - &), multiplicative (* /).
        private static readonly string[][] Levels =
        [
            ["or"],
            ["and"],
            ["=", "<>", "<", "<=", ">", ">="],
            ["+", "-", "&"],
            ["*", "/"],
        ];

        public MExpr ParseExpression(int level)
        {
            if (++_depth > MaxDepth)
            {
                throw new MParseException("the expression nests too deeply");
            }

            try
            {
                if (level == 0)
                {
                    if (IsKeyword("let"))
                    {
                        return ParseLet();
                    }

                    if (IsKeyword("each"))
                    {
                        _position++;
                        return new MEach(ParseExpression(0));
                    }

                    if (IsKeyword("if"))
                    {
                        _position++;
                        var condition = ParseExpression(0);
                        ExpectKeyword("then");
                        var then = ParseExpression(0);
                        ExpectKeyword("else");
                        return new MIf(condition, then, ParseExpression(0));
                    }

                    if (TryParseFunction(out var function))
                    {
                        return function;
                    }
                }

                if (level == Levels.Length)
                {
                    return ParseUnary();
                }

                var left = ParseExpression(level + 1);
                while (Levels[level].Contains(Current.Value) && Current.Kind is TokenKind.Symbol or TokenKind.Identifier)
                {
                    var op = tokens[_position++].Value;
                    var right = ParseExpression(level + 1);
                    left = new MBinary(op, left, right);
                }

                return left;
            }
            finally
            {
                _depth--;
            }
        }

        private MLet ParseLet()
        {
            ExpectKeyword("let");
            var steps = new List<(string, MExpr)>();
            while (true)
            {
                var name = ExpectName();
                ExpectSymbol("=");
                steps.Add((name, ParseExpression(0)));
                if (IsSymbol(","))
                {
                    _position++;
                    continue;
                }

                break;
            }

            ExpectKeyword("in");
            return new MLet(steps, ParseExpression(0));
        }

        // (a, b) => body. Recognized by looking ahead for the arrow after a parenthesized name list.
        private bool TryParseFunction(out MExpr function)
        {
            function = null!;
            if (!IsSymbol("("))
            {
                return false;
            }

            var probe = _position + 1;
            var parameters = new List<string>();
            while (probe < tokens.Count && tokens[probe].Kind == TokenKind.Identifier)
            {
                parameters.Add(tokens[probe].Value);
                probe++;
                if (probe < tokens.Count && tokens[probe].Kind == TokenKind.Identifier && tokens[probe].Value == "as")
                {
                    probe += 2;
                }

                if (probe < tokens.Count && tokens[probe].Kind == TokenKind.Symbol && tokens[probe].Value == ",")
                {
                    probe++;
                    continue;
                }

                break;
            }

            if (probe + 1 >= tokens.Count
                || tokens[probe].Value != ")"
                || tokens[probe + 1].Value != "=>")
            {
                return false;
            }

            _position = probe + 2;
            function = new MFunction(parameters, ParseExpression(0));
            return true;
        }

        private MExpr ParseUnary()
        {
            if (IsSymbol("-") || IsSymbol("+"))
            {
                var op = tokens[_position++].Value;
                return new MUnary(op, ParseUnary());
            }

            if (IsKeyword("not"))
            {
                _position++;
                return new MUnary("not", ParseUnary());
            }

            return ParsePostfix(ParsePrimary());
        }

        private MExpr ParsePostfix(MExpr expression)
        {
            while (true)
            {
                if (IsSymbol("("))
                {
                    _position++;
                    var arguments = new List<MExpr>();
                    if (!IsSymbol(")"))
                    {
                        arguments.Add(ParseExpression(0));
                        while (IsSymbol(","))
                        {
                            _position++;
                            arguments.Add(ParseExpression(0));
                        }
                    }

                    ExpectSymbol(")");
                    expression = new MCall(expression, arguments);
                }
                else if (IsSymbol("{"))
                {
                    _position++;
                    var selector = ParseExpression(0);
                    ExpectSymbol("}");
                    expression = new MItemAccess(expression, selector);
                }
                else if (IsSymbol("["))
                {
                    _position++;
                    var field = ExpectName();
                    ExpectSymbol("]");
                    expression = new MFieldAccess(expression, field);
                }
                else
                {
                    return expression;
                }
            }
        }

        private MExpr ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Text:
                    _position++;
                    return new MText(token.Value);
                case TokenKind.Number:
                    _position++;
                    return new MNumber(token.Value);
                case TokenKind.Identifier:
                    _position++;
                    return token.Value switch
                    {
                        "true" or "false" or "null" => new MKeywordLiteral(token.Value),
                        "type" => ParseType(),
                        _ => new MIdentifier(token.Value),
                    };
                case TokenKind.Symbol when token.Value == "(":
                {
                    _position++;
                    var inner = ParseExpression(0);
                    ExpectSymbol(")");
                    return inner;
                }

                case TokenKind.Symbol when token.Value == "{":
                {
                    _position++;
                    var items = new List<MExpr>();
                    if (!IsSymbol("}"))
                    {
                        items.Add(ParseExpression(0));
                        while (IsSymbol(","))
                        {
                            _position++;
                            items.Add(ParseExpression(0));
                        }
                    }

                    ExpectSymbol("}");
                    return new MList(items);
                }

                case TokenKind.Symbol when token.Value == "[":
                    return ParseRecordOrField();
                default:
                    throw new MParseException($"unexpected '{token.Value}' at {token.Position}");
            }
        }

        // "[Name]" is a field of the implicit row; "[a = x, ...]" is a record; "[]" is an empty record.
        private MExpr ParseRecordOrField()
        {
            ExpectSymbol("[");
            if (IsSymbol("]"))
            {
                _position++;
                return new MRecord([]);
            }

            var name = ExpectName();
            if (IsSymbol("]"))
            {
                _position++;
                return new MFieldReference(name);
            }

            var fields = new List<(string, MExpr)>();
            ExpectSymbol("=");
            fields.Add((name, ParseExpression(0)));
            while (IsSymbol(","))
            {
                _position++;
                var next = ExpectName();
                ExpectSymbol("=");
                fields.Add((next, ParseExpression(0)));
            }

            ExpectSymbol("]");
            return new MRecord(fields);
        }

        // type text / type nullable number / type table [A = _t, B = text] / type {text}: kept as text.
        private MType ParseType()
        {
            var text = new StringBuilder("type");
            if (IsKeyword("nullable"))
            {
                text.Append(" nullable");
                _position++;
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                var name = tokens[_position++].Value;
                text.Append(' ').Append(name);
                if (name == "table" && IsSymbol("["))
                {
                    AppendBalanced(text);
                }
            }
            else if (IsSymbol("[") || IsSymbol("{"))
            {
                AppendBalanced(text);
            }
            else
            {
                throw new MParseException($"an unrecognized type expression at {Current.Position}");
            }

            return new MType(text.ToString());
        }

        // Consumes a bracketed group, from its opening bracket to the one that closes it, into the text.
        private void AppendBalanced(StringBuilder text)
        {
            var depth = 0;
            do
            {
                if (Current.Kind == TokenKind.End)
                {
                    throw new MParseException("a type expression is not closed");
                }

                if (IsSymbol("[") || IsSymbol("{") || IsSymbol("("))
                {
                    depth++;
                }
                else if (IsSymbol("]") || IsSymbol("}") || IsSymbol(")"))
                {
                    depth--;
                }

                text.Append(' ').Append(tokens[_position++].Value);
            }
            while (depth > 0);
        }
    }

    /// <summary>Formats a number literal for T-SQL, or null when it is not a plain number.</summary>
    public static string? NumberLiteral(string value)
        => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;
}
