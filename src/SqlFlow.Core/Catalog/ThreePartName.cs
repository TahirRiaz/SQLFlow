namespace SqlFlow.Core.Catalog;

/// <summary>
/// A catalog-layer object name: an optional database, a schema, and the object. Bracket-escaped on render.
/// The two-part <see cref="SchemaQualified"/> form is used for <c>OBJECT_ID</c> in the connected database;
/// <see cref="QualifiedName"/> includes the database for display.
/// </summary>
public sealed record ThreePartName
{
    /// <summary>The database; null or empty means the connection's current database.</summary>
    public string? Database { get; init; }

    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>Two-part <c>[schema].[name]</c>, valid for OBJECT_ID in the connected database.</summary>
    public string SchemaQualified => $"[{Escape(Schema)}].[{Escape(Name)}]";

    /// <summary>Full display name, including the database when known.</summary>
    public string QualifiedName => string.IsNullOrEmpty(Database)
        ? SchemaQualified
        : $"[{Escape(Database)}].{SchemaQualified}";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
