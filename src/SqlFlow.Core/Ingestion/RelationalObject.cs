using SqlFlow.Core;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// A three-part SQL object name <c>[Database].[Schema].[Name]</c> (legacy srcDBSchTbl / trgDBSchTbl). The
/// legacy CHECK requires <c>PARSENAME(...,3)</c> to be non-null (at least three parts); this parser is
/// bracket-aware (an inner ']' is escaped as ']]') and, like the legacy GetValidSrcTrgName, uses the
/// rightmost three parts so a stray leading server part is dropped rather than failing a port.
/// </summary>
public sealed record RelationalObject
{
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>The fully bracketed, ']'-escaped name: <c>[Database].[Schema].[Name]</c>.</summary>
    public string QualifiedName => $"[{Escape(Database)}].[{Escape(Schema)}].[{Escape(Name)}]";

    public static RelationalObject Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SqlFlowException("A source or target object name must not be empty.");
        }

        var parts = IngestionText.SplitRespectingBrackets(name, '.')
            .Select(IngestionText.Unbracket)
            .ToList();

        if (parts.Count < 3)
        {
            throw new SqlFlowException(
                $"Object name '{name}' must be a three-part [Database].[Schema].[Object] name; found {parts.Count} part(s).");
        }

        var database = parts[^3];
        var schema = parts[^2];
        var objectName = parts[^1];

        if (database.Length == 0 || schema.Length == 0 || objectName.Length == 0)
        {
            throw new SqlFlowException($"Object name '{name}' has an empty database, schema, or object part.");
        }

        return new RelationalObject { Database = database, Schema = schema, Name = objectName };
    }

    private static string Escape(string part) => part.Replace("]", "]]", StringComparison.Ordinal);
}
