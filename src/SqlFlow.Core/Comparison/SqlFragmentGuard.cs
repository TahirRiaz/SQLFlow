using System.Text;

namespace SqlFlow.Core.Comparison;

/// <summary>
/// The trust boundary for the SQL FRAGMENTS a baseline comparison accepts: the logical-key expressions and the
/// optional row filter. Those cannot be parameters (they are projected and grouped, not compared to a value),
/// so they are interpolated into the comparison's generated SQL, and this guard is what makes that safe.
///
/// It is an allowlist, not a denylist: a fragment is accepted only when EVERY token in it is an identifier, a
/// bracketed identifier, a numeric or quoted literal, an operator, a parenthesis, a comma, or a word on the
/// keyword/function allowlist below. Anything else is refused by name. That refuses statement terminators,
/// comment introducers, variables, batch separators, every DML and DDL verb, and any function call that is not
/// one of the scalar functions a key expression legitimately needs, so a fragment cannot become a statement,
/// call a procedure, or read anything the comparison did not ask for.
/// </summary>
public static class SqlFragmentGuard
{
    /// <summary>The longest fragment accepted. A logical key or a period filter is short; anything approaching
    /// this length is not the thing this guard was written for.</summary>
    public const int MaxLength = 512;

    /// <summary>Deepest parenthesis nesting a fragment may use.</summary>
    private const int MaxDepth = 8;

    /// <summary>Words that may appear bare: SQL keywords a key expression or filter legitimately uses, plus the
    /// type names a CAST/CONVERT targets. Case-insensitive.</summary>
    private static readonly HashSet<string> AllowedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Expression keywords.
        "AS", "AND", "OR", "NOT", "IS", "NULL", "IN", "BETWEEN", "LIKE", "ESCAPE",
        "CASE", "WHEN", "THEN", "ELSE", "END", "COLLATE", "DISTINCT",
        // Type names a CAST or CONVERT targets.
        "BIT", "TINYINT", "SMALLINT", "INT", "INTEGER", "BIGINT",
        "DECIMAL", "NUMERIC", "FLOAT", "REAL", "MONEY", "SMALLMONEY",
        "CHAR", "VARCHAR", "NCHAR", "NVARCHAR", "TEXT", "NTEXT",
        "BINARY", "VARBINARY", "IMAGE", "MAX",
        "DATE", "TIME", "DATETIME", "DATETIME2", "SMALLDATETIME", "DATETIMEOFFSET",
        "UNIQUEIDENTIFIER", "SQL_VARIANT", "XML",
        // Date parts, which appear bare as arguments to DATEPART/DATEADD/DATEDIFF.
        "YEAR", "QUARTER", "MONTH", "DAYOFYEAR", "DAY", "WEEK", "WEEKDAY",
        "HOUR", "MINUTE", "SECOND", "MILLISECOND", "MICROSECOND", "NANOSECOND",
        "YY", "YYYY", "QQ", "Q", "MM", "M", "DY", "DD", "D", "WK", "WW", "DW", "HH", "MI", "N", "SS", "S", "MS",
    };

    /// <summary>The only functions a fragment may call: scalar, deterministic-enough, side-effect free. A word
    /// followed by "(" that is not in this set is refused, which is what keeps a fragment from calling a
    /// procedure, a metadata function, or anything that reaches outside the row.</summary>
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE", "PARSE",
        "ISNULL", "COALESCE", "NULLIF", "IIF",
        "LEFT", "RIGHT", "SUBSTRING", "LEN", "DATALENGTH", "TRIM", "LTRIM", "RTRIM",
        "UPPER", "LOWER", "REPLACE", "REVERSE", "CONCAT", "CONCAT_WS", "STR", "SPACE", "REPLICATE",
        "CHARINDEX", "PATINDEX", "STUFF", "FORMAT",
        "ABS", "ROUND", "FLOOR", "CEILING", "SIGN", "POWER", "SQRT", "LOG", "EXP",
        "YEAR", "MONTH", "DAY", "DATEPART", "DATENAME", "DATEADD", "DATEDIFF", "EOMONTH", "DATEFROMPARTS",
        "HASHBYTES", "CHECKSUM", "BINARY_CHECKSUM",
    };

    /// <summary>
    /// Validates one fragment, returning it trimmed. Throws <see cref="SqlFlowException"/> naming the offending
    /// token, so an operator learns exactly what was refused instead of getting a blanket rejection.
    /// </summary>
    /// <param name="fragment">The fragment as the caller wrote it.</param>
    /// <param name="field">The request field it came from, for the error message.</param>
    public static string Validate(string fragment, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            throw new SqlFlowException($"{field} must not be blank.");
        }

        var text = fragment.Trim();
        if (text.Length > MaxLength)
        {
            throw new SqlFlowException($"{field} is longer than {MaxLength} characters.");
        }

        var depth = 0;
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];

            if (char.IsWhiteSpace(ch))
            {
                if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r'))
                {
                    throw new SqlFlowException($"{field} contains a control character.");
                }

                index++;
                continue;
            }

            switch (ch)
            {
                case '(':
                    if (++depth > MaxDepth)
                    {
                        throw new SqlFlowException($"{field} nests parentheses more than {MaxDepth} deep.");
                    }

                    index++;
                    continue;

                case ')':
                    if (--depth < 0)
                    {
                        throw new SqlFlowException($"{field} closes a parenthesis that was never opened.");
                    }

                    index++;
                    continue;

                case ',':
                case '+':
                case '*':
                case '%':
                case '=':
                case '<':
                case '>':
                    index++;
                    continue;

                case '.':
                    index++;
                    continue;

                case '-':
                    // A single minus is subtraction or a sign; two start a line comment, which would let a
                    // fragment neutralise the rest of the generated statement.
                    if (index + 1 < text.Length && text[index + 1] == '-')
                    {
                        throw new SqlFlowException($"{field} contains a comment marker (--), which is not allowed.");
                    }

                    index++;
                    continue;

                case '/':
                    if (index + 1 < text.Length && text[index + 1] == '*')
                    {
                        throw new SqlFlowException($"{field} contains a comment marker (/*), which is not allowed.");
                    }

                    index++;
                    continue;

                case '!':
                    // Only as part of != and !<, !>.
                    if (index + 1 < text.Length && text[index + 1] is '=' or '<' or '>')
                    {
                        index += 2;
                        continue;
                    }

                    throw new SqlFlowException($"{field} contains a stray '!'.");

                case '\'':
                    index = SkipStringLiteral(text, index, field);
                    continue;

                case '[':
                    index = SkipBracketedIdentifier(text, index, field);
                    continue;

                case ';':
                    throw new SqlFlowException(
                        $"{field} contains a statement terminator (;). A fragment is an expression, not a statement.");

                case '@':
                    throw new SqlFlowException($"{field} contains a variable or parameter marker (@), which is not allowed.");

                case '"':
                    throw new SqlFlowException(
                        $"{field} contains a double quote. Quote an identifier with square brackets instead.");
            }

            if (char.IsDigit(ch))
            {
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
                {
                    index++;
                }

                // An exponent, so 1e6 is one token rather than a number followed by the identifier "e6".
                if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
                {
                    var lookahead = index + 1;
                    if (lookahead < text.Length && (text[lookahead] == '+' || text[lookahead] == '-'))
                    {
                        lookahead++;
                    }

                    if (lookahead < text.Length && char.IsDigit(text[lookahead]))
                    {
                        index = lookahead;
                        while (index < text.Length && char.IsDigit(text[index]))
                        {
                            index++;
                        }
                    }
                }

                continue;
            }

            if (IsWordStart(ch))
            {
                var start = index;
                while (index < text.Length && IsWordPart(text[index]))
                {
                    index++;
                }

                var word = text[start..index];

                // A unicode string literal (N'...') is a prefix, not an identifier.
                if (word.Length == 1 && (word[0] == 'N' || word[0] == 'n')
                    && index < text.Length && text[index] == '\'')
                {
                    index = SkipStringLiteral(text, index, field);
                    continue;
                }

                var next = NextNonSpace(text, index);
                if (next == '(')
                {
                    // A word before "(" is a call, a parameterised type (decimal(18,2), varchar(10)), or a
                    // keyword that legitimately precedes a group (IN, AND, NOT). Anything else reaching outside
                    // the row (DB_NAME, SUSER_SNAME, a procedure) is refused by name.
                    if (!AllowedFunctions.Contains(word) && !AllowedWords.Contains(word))
                    {
                        throw new SqlFlowException(
                            $"{field} calls '{word}', which is not one of the scalar functions a comparison " +
                            $"fragment may use. Allowed: {string.Join(", ", AllowedFunctions.Order(StringComparer.Ordinal))}.");
                    }

                    continue;
                }

                if (IsReservedVerb(word))
                {
                    throw new SqlFlowException(
                        $"{field} contains the keyword '{word}'. A comparison fragment is a read-only " +
                        "expression and may not contain a statement keyword.");
                }

                // Anything else that is a bare word is either an allowlisted keyword or a column reference.
                // Column references are checked by the server, which fails the task with its own precise
                // "Invalid column name" if the operator named something that does not exist.
                continue;
            }

            throw new SqlFlowException($"{field} contains the character '{ch}', which is not allowed in a fragment.");
        }

        if (depth != 0)
        {
            throw new SqlFlowException($"{field} leaves {depth} parenthesis/parentheses unclosed.");
        }

        return text;
    }

    /// <summary>
    /// Splits a comma-separated list of fragments on the commas that are at nesting depth zero and outside a
    /// string literal, so <c>CAST(x AS time), y</c> stays two expressions rather than three. Each part is then
    /// validated in its own right.
    /// </summary>
    public static IReadOnlyList<string> ValidateList(string list, string field, int maxItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (string.IsNullOrWhiteSpace(list))
        {
            throw new SqlFlowException($"{field} must not be blank.");
        }

        if (list.Length > MaxLength * maxItems)
        {
            throw new SqlFlowException($"{field} is too long.");
        }

        var parts = new List<string>();
        var buffer = new StringBuilder();
        var depth = 0;
        var inString = false;
        for (var i = 0; i < list.Length; i++)
        {
            var ch = list[i];
            if (inString)
            {
                buffer.Append(ch);
                if (ch == '\'')
                {
                    // A doubled quote is an escaped quote inside the literal, not its end.
                    if (i + 1 < list.Length && list[i + 1] == '\'')
                    {
                        buffer.Append(list[++i]);
                    }
                    else
                    {
                        inString = false;
                    }
                }

                continue;
            }

            switch (ch)
            {
                case '\'':
                    inString = true;
                    buffer.Append(ch);
                    continue;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(buffer.ToString());
                    buffer.Clear();
                    continue;
            }

            buffer.Append(ch);
        }

        parts.Add(buffer.ToString());

        var validated = parts
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (validated.Count == 0)
        {
            throw new SqlFlowException($"{field} produced no expressions.");
        }

        if (validated.Count > maxItems)
        {
            throw new SqlFlowException($"{field} names {validated.Count} expressions; at most {maxItems} are allowed.");
        }

        return validated.Select(p => Validate(p, field)).ToArray();
    }

    /// <summary>
    /// Validates a plain identifier (a schema, object, column, or linked-server name) as the comparison will
    /// quote it into SQL. Brackets are refused: the caller passes the bare name and the builder adds the
    /// quoting, so a name carrying its own bracket cannot close the quoting early.
    /// </summary>
    public static string ValidateIdentifier(string identifier, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new SqlFlowException($"{field} must not be blank.");
        }

        var text = identifier.Trim();
        if (text.Length > 128)
        {
            throw new SqlFlowException($"{field} is longer than the 128-character SQL Server identifier limit.");
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '#' or '$' or ' ' or '-')
            {
                continue;
            }

            throw new SqlFlowException(
                $"{field} contains '{ch}'. Pass the bare identifier; SQLFlow adds the quoting.");
        }

        return text;
    }

    private static int SkipStringLiteral(string text, int index, string field)
    {
        // index points at the opening quote.
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] != '\'')
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++; // an escaped quote inside the literal
                continue;
            }

            return i + 1;
        }

        throw new SqlFlowException($"{field} has an unterminated string literal.");
    }

    private static int SkipBracketedIdentifier(string text, int index, string field)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] != ']')
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == ']')
            {
                i++; // an escaped bracket inside the identifier
                continue;
            }

            if (i == index + 1)
            {
                throw new SqlFlowException($"{field} contains an empty bracketed identifier ([]).");
            }

            return i + 1;
        }

        throw new SqlFlowException($"{field} has an unterminated bracketed identifier.");
    }

    private static char NextNonSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length ? text[index] : '\0';
    }

    private static bool IsWordStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsWordPart(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '$' or '#';

    /// <summary>Statement and batch keywords a read-only expression can never legitimately contain. Refused by
    /// name so the error explains itself, rather than the fragment failing obscurely on the server.</summary>
    private static bool IsReservedVerb(string word) => word.ToUpperInvariant() switch
    {
        "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "TRUNCATE" or "DROP" or "CREATE" or "ALTER"
            or "EXEC" or "EXECUTE" or "GRANT" or "REVOKE" or "DENY" or "BACKUP" or "RESTORE" or "SHUTDOWN"
            or "GO" or "USE" or "DECLARE" or "SET" or "WAITFOR" or "OPENQUERY" or "OPENROWSET" or "OPENDATASOURCE"
            or "FROM" or "WHERE" or "JOIN" or "UNION" or "INTO" or "GROUP" or "ORDER" or "HAVING" or "WITH"
            or "BEGIN" or "COMMIT" or "ROLLBACK" or "TRAN" or "TRANSACTION" or "DBCC" or "KILL" or "RECONFIGURE"
            => true,
        _ => false,
    };
}
