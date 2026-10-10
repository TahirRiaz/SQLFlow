using System.Globalization;
using System.Text;

namespace SqlFlow.Lineage.PowerBi;

/// <summary>A column of the report's model, as a measure or a visual names it.</summary>
internal sealed record ModelColumn(string Table, string Column);

/// <summary>
/// A relationship a measure activates with <c>USERELATIONSHIP</c>: while the measure is evaluated, it replaces every
/// other relationship between the same two tables.
/// </summary>
internal sealed record RelationshipOverride(string TableA, string ColumnA, string TableB, string ColumnB)
{
    /// <summary>The same override whichever way round it was written.</summary>
    public static RelationshipOverride Normalize(ModelColumn first, ModelColumn second)
        => Order(first, second) <= 0
            ? new(first.Table, first.Column, second.Table, second.Column)
            : new(second.Table, second.Column, first.Table, first.Column);

    private static int Order(ModelColumn first, ModelColumn second)
    {
        var byTable = string.CompareOrdinal(first.Table, second.Table);
        return byTable != 0 ? byTable : string.CompareOrdinal(first.Column, second.Column);
    }

    public bool Connects(string a, string b)
        => (string.Equals(TableA, a, StringComparison.Ordinal) && string.Equals(TableB, b, StringComparison.Ordinal))
           || (string.Equals(TableA, b, StringComparison.Ordinal) && string.Equals(TableB, a, StringComparison.Ordinal));
}

/// <summary>An aggregate expression over model columns, to be rendered once the join it runs over is known.</summary>
internal abstract record AggExpr;

/// <summary>An aggregate of one model column; <see cref="Column"/> is null for a row count of <see cref="RowsOf"/>.</summary>
internal sealed record AggFunction(
    string Function, bool Distinct, ModelColumn? Column, string? RowsOf, IReadOnlySet<RelationshipOverride> Overrides) : AggExpr;

internal sealed record AggNumber(string Value) : AggExpr;

internal sealed record AggNull : AggExpr;

internal sealed record AggBinary(string Operator, AggExpr Left, AggExpr Right) : AggExpr;

internal sealed record AggNegate(AggExpr Operand) : AggExpr;

/// <summary><c>DIVIDE(numerator, denominator[, alternate])</c>.</summary>
internal sealed record AggDivide(AggExpr Numerator, AggExpr Denominator, AggExpr? Alternate) : AggExpr;

/// <summary>Raised when an expression is outside what can be translated; the message says what and why.</summary>
internal sealed class TranslationException(string message) : Exception(message);

/// <summary>
/// Translates a Power BI measure's DAX into an <see cref="AggExpr"/>: the aggregates <c>SUM</c>, <c>AVERAGE</c>,
/// <c>MIN</c>, <c>MAX</c>, <c>COUNT</c>/<c>COUNTA</c>, <c>COUNTROWS</c>, and <c>DISTINCTCOUNT</c> of a column;
/// <c>DIVIDE</c>; arithmetic; references to other measures; <c>BLANK()</c>; and <c>CALCULATE</c> whose only filters
/// are <c>USERELATIONSHIP</c>. Anything else is refused naming the function, because a measure translated partly
/// would compute a different number under the same name.
/// </summary>
internal sealed class DaxTranslator
{
    private const int MaxMeasureDepth = 16;

    private static readonly Dictionary<string, string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUM"] = "SUM",
        ["AVERAGE"] = "AVG",
        ["MIN"] = "MIN",
        ["MAX"] = "MAX",
        ["COUNT"] = "COUNT",
        ["COUNTA"] = "COUNT",
        ["DISTINCTCOUNT"] = "COUNT",
    };

    private readonly IReadOnlyDictionary<(string Table, string Name), string> _measures;
    private readonly IReadOnlyDictionary<string, string> _measureHome;

    /// <param name="measures">Every measure of the model: (home table, name) to DAX.</param>
    public DaxTranslator(IReadOnlyDictionary<(string Table, string Name), string> measures)
    {
        ArgumentNullException.ThrowIfNull(measures);
        _measures = measures;

        // A measure name is unique across a model, so a bare [Name] reference finds its home table here.
        var home = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, name) in measures.Keys)
        {
            home.TryAdd(name, table);
        }

        _measureHome = home;
    }

    public bool IsMeasure(string table, string name) => _measures.ContainsKey((table, name));

    /// <summary>Translates the measure <paramref name="name"/> defined on <paramref name="table"/>.</summary>
    /// <exception cref="TranslationException">The measure is outside the translated subset.</exception>
    public AggExpr Measure(string table, string name) => Measure(table, name, 0, new HashSet<RelationshipOverride>());

    private AggExpr Measure(string table, string name, int depth, IReadOnlySet<RelationshipOverride> overrides)
    {
        if (depth > MaxMeasureDepth)
        {
            throw new TranslationException($"measure '{name}' refers to other measures too deeply");
        }

        if (!_measures.TryGetValue((table, name), out var dax))
        {
            throw new TranslationException($"'{table}'[{name}] is not a measure of the model");
        }

        DaxNode parsed;
        try
        {
            parsed = new DaxParser(dax).ParseAll();
        }
        catch (TranslationException ex)
        {
            throw new TranslationException($"measure '{name}' does not parse ({ex.Message})");
        }

        return Translate(parsed, table, depth, overrides);
    }

    private AggExpr Translate(DaxNode node, string homeTable, int depth, IReadOnlySet<RelationshipOverride> overrides)
    {
        switch (node)
        {
            case DaxNumber number:
                return new AggNumber(number.Value);
            case DaxUnary { Operator: "-" } negate:
                return new AggNegate(Translate(negate.Operand, homeTable, depth, overrides));
            case DaxUnary { Operator: "+" } plus:
                return Translate(plus.Operand, homeTable, depth, overrides);
            case DaxBinary { Operator: "+" or "-" or "*" or "/" } binary:
                return new AggBinary(
                    binary.Operator,
                    Translate(binary.Left, homeTable, depth, overrides),
                    Translate(binary.Right, homeTable, depth, overrides));
            case DaxColumn { Table: null } bare when _measureHome.TryGetValue(bare.Name, out var home):
                return Measure(home, bare.Name, depth + 1, overrides);
            case DaxColumn { Table: { } table } qualified when IsMeasure(table, qualified.Name):
                return Measure(table, qualified.Name, depth + 1, overrides);
            case DaxColumn column:
                throw new TranslationException(
                    $"it uses the column {Describe(column, homeTable)} outside an aggregation");
            case DaxFunction function:
                return Function(function, homeTable, depth, overrides);
            case DaxBinary binary:
                throw new TranslationException($"it uses the operator {binary.Operator}, which is not translated");
            case DaxText:
                throw new TranslationException("it uses a text value, which is not translated");
            default:
                throw new TranslationException("it uses a value that is not translated");
        }
    }

    private AggExpr Function(DaxFunction function, string homeTable, int depth, IReadOnlySet<RelationshipOverride> overrides)
    {
        var name = function.Name.ToUpperInvariant();
        if (Aggregates.TryGetValue(name, out var sqlFunction))
        {
            if (function.Arguments is not [DaxColumn column])
            {
                throw new TranslationException($"{name} is used with something other than one column");
            }

            if (column.Table is null && _measureHome.ContainsKey(column.Name))
            {
                throw new TranslationException($"{name} is applied to the measure [{column.Name}]");
            }

            return new AggFunction(
                sqlFunction, name == "DISTINCTCOUNT", new ModelColumn(column.Table ?? homeTable, column.Name), null, overrides);
        }

        switch (name)
        {
            case "COUNTROWS":
                if (function.Arguments is not [DaxTable table])
                {
                    throw new TranslationException("COUNTROWS is used with something other than a table name");
                }

                return new AggFunction("COUNT", false, null, table.Name, overrides);

            case "DIVIDE":
                if (function.Arguments.Count is < 2 or > 3)
                {
                    throw new TranslationException("DIVIDE takes two or three arguments");
                }

                return new AggDivide(
                    Translate(function.Arguments[0], homeTable, depth, overrides),
                    Translate(function.Arguments[1], homeTable, depth, overrides),
                    function.Arguments.Count == 3 ? Translate(function.Arguments[2], homeTable, depth, overrides) : null);

            case "BLANK":
                return new AggNull();

            case "CALCULATE":
            {
                if (function.Arguments.Count == 0)
                {
                    throw new TranslationException("CALCULATE has no expression");
                }

                var inner = new HashSet<RelationshipOverride>(overrides);
                foreach (var filter in function.Arguments.Skip(1))
                {
                    if (filter is not DaxFunction { Arguments: [DaxColumn first, DaxColumn second] } use
                        || !string.Equals(use.Name, "USERELATIONSHIP", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TranslationException(
                            $"CALCULATE applies the filter {Describe(filter)}, and only USERELATIONSHIP is translated");
                    }

                    inner.Add(RelationshipOverride.Normalize(
                        new ModelColumn(first.Table ?? homeTable, first.Name),
                        new ModelColumn(second.Table ?? homeTable, second.Name)));
                }

                return Translate(function.Arguments[0], homeTable, depth, inner);
            }

            default:
                throw new TranslationException($"it uses the DAX function {name}, which is not translated");
        }
    }

    private static string Describe(DaxColumn column, string homeTable) => $"'{column.Table ?? homeTable}'[{column.Name}]";

    private static string Describe(DaxNode node) => node switch
    {
        DaxFunction function => function.Name.ToUpperInvariant(),
        DaxColumn column => $"[{column.Name}]",
        _ => "an expression",
    };

    // ---- DAX syntax -----------------------------------------------------------------------------------------

    private abstract record DaxNode;

    private sealed record DaxNumber(string Value) : DaxNode;

    private sealed record DaxText(string Value) : DaxNode;

    private sealed record DaxColumn(string? Table, string Name) : DaxNode;

    private sealed record DaxTable(string Name) : DaxNode;

    private sealed record DaxFunction(string Name, IReadOnlyList<DaxNode> Arguments) : DaxNode;

    private sealed record DaxBinary(string Operator, DaxNode Left, DaxNode Right) : DaxNode;

    private sealed record DaxUnary(string Operator, DaxNode Operand) : DaxNode;

    private enum TokenKind
    {
        Name,
        QuotedTable,
        Bracketed,
        Number,
        Text,
        Symbol,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private sealed class DaxParser
    {
        private const int MaxDepth = 100;
        private readonly List<Token> _tokens;
        private int _position;
        private int _depth;

        public DaxParser(string text) => _tokens = Tokenize(text);

        public DaxNode ParseAll()
        {
            var node = Expression();
            if (_tokens[_position].Kind != TokenKind.End)
            {
                throw new TranslationException($"unexpected '{_tokens[_position].Value}'");
            }

            return node;
        }

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
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/' || c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? throw new TranslationException("an unterminated comment") : close + 2;
                }
                else if (c is '\'' or '"' or '[')
                {
                    var closer = c == '[' ? ']' : c;
                    var value = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                        {
                            throw new TranslationException("an unterminated name or text");
                        }

                        if (text[i] == closer)
                        {
                            if (i + 1 < text.Length && text[i + 1] == closer)
                            {
                                value.Append(closer);
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        value.Append(text[i++]);
                    }

                    tokens.Add(new Token(
                        c switch { '\'' => TokenKind.QuotedTable, '"' => TokenKind.Text, _ => TokenKind.Bracketed },
                        value.ToString()));
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    var start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Number, text[start..i]));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Name, text[start..i]));
                }
                else if (i + 1 < text.Length && text.Substring(i, 2) is "<=" or ">=" or "<>" or "==" or "&&" or "||")
                {
                    tokens.Add(new Token(TokenKind.Symbol, text.Substring(i, 2)));
                    i += 2;
                }
                else if ("+-*/(),&=<>^".Contains(c, StringComparison.Ordinal))
                {
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                    i++;
                }
                else
                {
                    throw new TranslationException($"the character '{c}'");
                }
            }

            tokens.Add(new Token(TokenKind.End, string.Empty));
            return tokens;
        }

        private bool Is(string symbol) => _tokens[_position] is { Kind: TokenKind.Symbol } token && token.Value == symbol;

        private void Expect(string symbol)
        {
            if (!Is(symbol))
            {
                throw new TranslationException($"expected '{symbol}' but found '{_tokens[_position].Value}'");
            }

            _position++;
        }

        // Precedence, loosest first: || then && then comparison then additive (+ - &) then multiplicative.
        private DaxNode Expression()
        {
            if (++_depth > MaxDepth)
            {
                throw new TranslationException("the expression nests too deeply");
            }

            try
            {
                var left = Conjunction();
                while (Is("||"))
                {
                    _position++;
                    left = new DaxBinary("||", left, Conjunction());
                }

                return left;
            }
            finally
            {
                _depth--;
            }
        }

        private DaxNode Conjunction()
        {
            var left = Comparison();
            while (Is("&&"))
            {
                _position++;
                left = new DaxBinary("&&", left, Comparison());
            }

            return left;
        }

        private DaxNode Comparison()
        {
            var left = Additive();
            while (Is("=") || Is("==") || Is("<>") || Is("<") || Is("<=") || Is(">") || Is(">="))
            {
                var op = _tokens[_position++].Value;
                left = new DaxBinary(op, left, Additive());
            }

            return left;
        }

        private DaxNode Additive()
        {
            var left = Term();
            while (Is("+") || Is("-") || Is("&"))
            {
                var op = _tokens[_position++].Value;
                left = new DaxBinary(op, left, Term());
            }

            return left;
        }

        private DaxNode Term()
        {
            var left = Unary();
            while (Is("*") || Is("/"))
            {
                var op = _tokens[_position++].Value;
                left = new DaxBinary(op, left, Unary());
            }

            return left;
        }

        private DaxNode Unary()
        {
            if (Is("-") || Is("+"))
            {
                var op = _tokens[_position++].Value;
                return new DaxUnary(op, Unary());
            }

            return Primary();
        }

        private DaxNode Primary()
        {
            var token = _tokens[_position];
            switch (token.Kind)
            {
                case TokenKind.Number:
                    _position++;
                    return new DaxNumber(decimal.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed.ToString(CultureInfo.InvariantCulture)
                        : throw new TranslationException($"the number {token.Value}"));
                case TokenKind.Text:
                    _position++;
                    return new DaxText(token.Value);
                case TokenKind.Bracketed:
                    _position++;
                    return new DaxColumn(null, token.Value);
                case TokenKind.QuotedTable:
                    _position++;
                    return TableOrColumn(token.Value);
                case TokenKind.Name:
                    _position++;
                    if (Is("("))
                    {
                        _position++;
                        var arguments = new List<DaxNode>();
                        if (!Is(")"))
                        {
                            arguments.Add(Expression());
                            while (Is(","))
                            {
                                _position++;
                                arguments.Add(Expression());
                            }
                        }

                        Expect(")");
                        return new DaxFunction(token.Value, arguments);
                    }

                    return TableOrColumn(token.Value);
                case TokenKind.Symbol when token.Value == "(":
                {
                    _position++;
                    var inner = Expression();
                    Expect(")");
                    return inner;
                }

                default:
                    throw new TranslationException($"unexpected '{token.Value}'");
            }
        }

        private DaxNode TableOrColumn(string table)
        {
            if (_tokens[_position].Kind == TokenKind.Bracketed)
            {
                return new DaxColumn(table, _tokens[_position++].Value);
            }

            return new DaxTable(table);
        }
    }
}
