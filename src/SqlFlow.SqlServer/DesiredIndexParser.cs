using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Core;

namespace SqlFlow.SqlServer;

/// <summary>One <c>CREATE INDEX</c> statement parsed from a desired-index script.</summary>
public sealed record ParsedIndex(string Name, string? Schema, string Table, string StatementText);

/// <summary>
/// Parses a desired-index T-SQL script into its individual <c>CREATE INDEX</c> statements using the
/// ScriptDom parser (a standalone parser, not SMO). The original statement text of each index is
/// preserved verbatim so it can be executed exactly as the author wrote it, with all options, included
/// columns, and filters intact. Only <c>CREATE INDEX</c> statements are collected; anything else in the
/// script (comments, batch separators) is ignored.
/// </summary>
public static class DesiredIndexParser
{
    public static IReadOnlyList<ParsedIndex> Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (string.IsNullOrWhiteSpace(script))
        {
            return [];
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(script), out var errors);
        if (errors.Count > 0)
        {
            var detail = string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}"));
            throw new SqlFlowException($"Invalid desired-index script: {detail}");
        }

        var visitor = new CreateIndexCollector();
        fragment.Accept(visitor);

        var result = new List<ParsedIndex>(visitor.Statements.Count);
        foreach (var node in visitor.Statements)
        {
            var name = node.Name?.Value;
            var baseName = node.OnName?.BaseIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseName))
            {
                throw new SqlFlowException("Desired-index script contains a CREATE INDEX without a resolvable index or table name.");
            }

            result.Add(new ParsedIndex(
                Name: name,
                Schema: node.OnName?.SchemaIdentifier?.Value,
                Table: baseName,
                StatementText: ExtractText(node)));
        }

        return result;
    }

    private static string ExtractText(TSqlFragment node)
    {
        var tokens = node.ScriptTokenStream;
        var builder = new StringBuilder();
        for (var i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
        {
            builder.Append(tokens[i].Text);
        }

        return builder.ToString().Trim();
    }

    private sealed class CreateIndexCollector : TSqlFragmentVisitor
    {
        public List<CreateIndexStatement> Statements { get; } = [];

        public override void Visit(CreateIndexStatement node) => Statements.Add(node);
    }
}
