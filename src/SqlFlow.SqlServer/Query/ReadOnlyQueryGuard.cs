using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Core;

namespace SqlFlow.SqlServer.Query;

/// <summary>
/// The trust boundary for an ad-hoc business query: decides whether a statement is genuinely a single
/// read-only SELECT before anything is allowed to run it.
///
/// This PARSES the statement with the same T-SQL parser the lineage extractor uses, rather than matching
/// strings. That distinction is the whole point: a denylist over text loses to comments, casing, whitespace,
/// unicode escapes, and nesting, whereas a parse tree either contains a write node or it does not. The rule is
/// an allowlist at the top (exactly one batch holding exactly one SELECT) plus a refusal of every construct
/// that can reach outside the query from INSIDE a select: a SELECT ... INTO, a procedure call, a remote
/// OPENQUERY, and the rest.
///
/// It is deliberately strict. A refused query that was actually safe costs someone a rewrite; an accepted
/// query that was not costs data.
/// </summary>
public static class ReadOnlyQueryGuard
{
    /// <summary>The longest statement accepted, so a pathological input cannot be parsed at unbounded cost.</summary>
    public const int MaxLength = 20_000;

    /// <summary>
    /// Validates the statement and returns it trimmed. Throws <see cref="SqlFlowException"/> naming what was
    /// refused and why, so the caller can rewrite rather than guess.
    /// </summary>
    public static string Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlFlowException("The query is empty.");
        }

        var text = sql.Trim();
        if (text.Length > MaxLength)
        {
            throw new SqlFlowException(
                $"The query is {text.Length} characters, over the {MaxLength}-character limit.");
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            var first = errors[0];
            throw new SqlFlowException(
                $"The query does not parse as T-SQL (line {first.Line}): {first.Message}");
        }

        if (fragment is not TSqlScript script)
        {
            throw new SqlFlowException("The query did not parse into a statement.");
        }

        // Exactly one batch holding exactly one statement. A second statement is how "SELECT 1; DROP TABLE x"
        // gets in, and a GO separator is how it gets in without a semicolon.
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (script.Batches.Count > 1 || statements.Count != 1)
        {
            throw new SqlFlowException(
                $"A query must be exactly ONE statement; this parses as {statements.Count} statement(s) in " +
                $"{script.Batches.Count} batch(es). Run one SELECT at a time.");
        }

        if (statements[0] is not SelectStatement select)
        {
            throw new SqlFlowException(
                $"Only a SELECT may be run here; this is a {Describe(statements[0])}. The query surface is " +
                "read-only, and nothing that changes data or schema is accepted.");
        }

        // WITH ... AS (...) SELECT is a SelectStatement, so common table expressions are allowed by
        // construction; a CTE that hides an INSERT still trips the visitor below.
        var visitor = new RefusalVisitor();
        select.Accept(visitor);
        if (visitor.Refusal is { } refusal)
        {
            throw new SqlFlowException(refusal);
        }

        return text;
    }

    /// <summary>True when the statement passes, for a caller that wants a boolean rather than an exception.</summary>
    public static bool IsReadOnly(string sql, out string? refusal)
    {
        try
        {
            Validate(sql);
            refusal = null;
            return true;
        }
        catch (SqlFlowException ex)
        {
            refusal = ex.Message;
            return false;
        }
    }

    private static string Describe(TSqlStatement statement) => statement switch
    {
        InsertStatement => "INSERT",
        UpdateStatement => "UPDATE",
        DeleteStatement => "DELETE",
        MergeStatement => "MERGE",
        TruncateTableStatement => "TRUNCATE TABLE",
        ExecuteStatement => "EXEC",
        DropObjectsStatement or DropTableStatement or DropIndexStatement => "DROP",
        CreateTableStatement or CreateIndexStatement or CreateViewStatement or CreateProcedureStatement
            => "CREATE",
        AlterTableStatement or AlterIndexStatement => "ALTER",
        DeclareVariableStatement => "DECLARE",
        SetVariableStatement or SetCommandStatement or PredicateSetStatement => "SET",
        BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement
            => "transaction control statement",
        WaitForStatement => "WAITFOR",
        UseStatement => "USE",
        _ => statement.GetType().Name.Replace("Statement", string.Empty, StringComparison.Ordinal) + " statement",
    };

    /// <summary>
    /// Walks the parsed SELECT and refuses the constructs that can reach outside it. Everything here is
    /// reachable from INSIDE a select, which is why parsing the top-level statement is not on its own enough:
    /// <c>SELECT * INTO t FROM x</c> is a SelectStatement that creates a table, and
    /// <c>SELECT * FROM OPENQUERY(srv, 'DELETE ...')</c> is a SelectStatement that deletes rows on another
    /// server.
    /// </summary>
    private sealed class RefusalVisitor : TSqlFragmentVisitor
    {
        public string? Refusal { get; private set; }

        private void Refuse(string message) => Refusal ??= message;

        public override void Visit(SelectStatement node)
        {
            // SELECT ... INTO creates and populates a table. It is the one write a SELECT can perform.
            if (node?.Into is not null)
            {
                Refuse("SELECT ... INTO creates a table, so it is not read-only. Remove the INTO clause.");
            }
        }

        public override void Visit(ExecutableProcedureReference node)
            => Refuse("A query may not call a stored procedure.");

        public override void Visit(ExecuteSpecification node)
            => Refuse("A query may not EXEC anything.");

        public override void Visit(OpenQueryTableReference node)
            => Refuse(
                "OPENQUERY runs a statement on another server, which this cannot inspect, so it is refused. " +
                "Use the baseline comparison for cross-estate work.");

        public override void Visit(OpenRowsetTableReference node)
            => Refuse("OPENROWSET reaches outside this connection and is refused.");

        public override void Visit(OpenXmlTableReference node)
            => Refuse("OPENXML is refused.");

        public override void Visit(AdHocTableReference node)
            => Refuse("An ad-hoc remote table reference reaches outside this connection and is refused.");

        // A function call cannot be traced to its body from here, and a table-valued function can be
        // side-effecting. Only the extended-procedure-style names that are unambiguously dangerous are
        // refused, because refusing every function would make the surface useless.
        public override void Visit(FunctionCall node)
        {
            var name = node?.FunctionName?.Value;
            if (name is not null && IsRefusedFunction(name))
            {
                Refuse($"The function {name} is not allowed in a query here.");
            }
        }

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            var name = node?.SchemaObject?.BaseIdentifier?.Value;
            if (name is not null && IsRefusedFunction(name))
            {
                Refuse($"The function {name} is not allowed in a query here.");
            }
        }

        private static bool IsRefusedFunction(string name)
            => name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("fn_trace", StringComparison.OrdinalIgnoreCase);
    }
}
