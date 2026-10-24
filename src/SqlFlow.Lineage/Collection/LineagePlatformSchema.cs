namespace SqlFlow.Lineage.Collection;

/// <summary>
/// A schema the platform keeps its own tables in: SQLFlow's catalog, or the database of a module the host migrates. The
/// derived tier's live inventory lists every object of each database a flow names, and a flow can name the database the
/// platform itself lives in (a module publishing views beside its own tables, say); this tells the inventory which of those
/// objects are the platform's rather than data a flow publishes. A database holds the schema when the inventory finds the
/// schema's migrations history table in it, so a same-named schema in another database is never mistaken for it.
/// </summary>
public sealed record LineagePlatformSchema
{
    /// <summary>The schema the platform's tables live in.</summary>
    public required string Schema { get; init; }

    /// <summary>The migrations history table the platform keeps in <see cref="Schema"/>: its presence is what marks a
    /// database as holding this schema.</summary>
    public required string HistoryTable { get; init; }

    /// <summary>The names of the platform's tables and views in <see cref="Schema"/>, or null when every object in the
    /// schema is the platform's (nothing else may live there). Compared without regard to case, as SQL Server names are.</summary>
    public IReadOnlyCollection<string>? Objects { get; init; }

    /// <summary>
    /// Which of a database's objects are the platform's: those of every schema in <paramref name="schemas"/> whose history
    /// table <paramref name="inventory"/> holds. An empty list, or a database holding none of them, names no object.
    /// </summary>
    /// <param name="schemas">The platform's schemas.</param>
    /// <param name="inventory">Every (schema, name) the database's inventory holds.</param>
    /// <returns>A test of one (schema, name) of the same database.</returns>
    public static Func<string, string, bool> OwnedIn(
        IReadOnlyList<LineagePlatformSchema> schemas, IEnumerable<(string Schema, string Name)> inventory)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(inventory);
        if (schemas.Count == 0)
        {
            return static (_, _) => false;
        }

        var present = inventory
            .Select(o => (Schema: o.Schema.ToUpperInvariant(), Name: o.Name.ToUpperInvariant()))
            .ToHashSet();
        var held = schemas
            .Where(s => present.Contains((s.Schema.ToUpperInvariant(), s.HistoryTable.ToUpperInvariant())))
            .Select(s => (
                s.Schema,
                s.HistoryTable,
                Objects: s.Objects is null ? null : new HashSet<string>(s.Objects, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (held.Count == 0)
        {
            return static (_, _) => false;
        }

        return (schema, name) => held.Exists(s =>
            string.Equals(schema, s.Schema, StringComparison.OrdinalIgnoreCase)
            && (s.Objects is null
                || string.Equals(name, s.HistoryTable, StringComparison.OrdinalIgnoreCase)
                || s.Objects.Contains(name)));
    }
}
