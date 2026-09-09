using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFlow.Lineage.Extraction;

/// <summary>
/// A table/view/procedure name as the extractor saw it: up to four parts, raw spellings preserved,
/// case-folded for identity. The DeltaForge TableReference equivalent. Immutable.
/// </summary>
public sealed record TableName
{
    /// <summary>The linked-server part of a four-part name, when present.</summary>
    public string? Server { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    /// <summary>How many parts the author wrote (1 to 4): resolution confidence, DeltaForge-style.</summary>
    public int PartCount =>
        (Server is null ? 0 : 1) + (Database is null ? 0 : 1) + (Schema is null ? 0 : 1) + 1;

    /// <summary>A #temp or ##temp object: script-local, resolved through local deps, never a graph node.</summary>
    public bool IsTemp => Name.StartsWith('#');

    /// <summary>The case-folded identity used for dedup and local-dependency keys. Interior gaps render as
    /// empty slots ('db..name' for a database-qualified name with no schema), so a USE-qualified one-part
    /// reference can never collide with a schema-qualified two-part one.</summary>
    public string Key
    {
        get
        {
            var slots = new[] { Server, Database, Schema };
            var highest = Array.FindIndex(slots, s => s is not null);
            var parts = new List<string>(4);
            if (highest >= 0)
            {
                for (var i = highest; i < slots.Length; i++)
                {
                    parts.Add(slots[i]?.ToLowerInvariant() ?? string.Empty);
                }
            }

            parts.Add(Name.ToLowerInvariant());
            return string.Join('.', parts);
        }
    }

    public override string ToString()
        => string.Join('.', new[] { Server, Database, Schema, Name }.Where(p => p is not null));

    /// <summary>Builds a name from a parsed <see cref="SchemaObjectName"/>, applying the scope's current
    /// database to 1/2-part names when one is known (a preceding USE statement); the author's own database
    /// part always wins.</summary>
    public static TableName From(SchemaObjectName name, string? currentDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var database = name.DatabaseIdentifier?.Value;
        var schema = name.SchemaIdentifier?.Value;

        // USE db context only qualifies names that carry a schema or none at all; it never overrides an
        // explicit database part.
        if (database is null && currentDatabase is not null)
        {
            database = currentDatabase;
        }

        return new TableName
        {
            Server = name.ServerIdentifier?.Value,
            Database = database,
            Schema = schema,
            Name = name.BaseIdentifier?.Value ?? string.Empty,
        };
    }
}
