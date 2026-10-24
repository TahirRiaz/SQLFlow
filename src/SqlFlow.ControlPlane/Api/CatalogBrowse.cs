using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One stored spelling of a (server, database, schema, kind) grouping of catalog objects, with how many objects
/// carry it; after <see cref="CatalogBrowse.FoldSpellings"/>, one grouping in its shown spelling.</summary>
internal sealed record SpelledGroup(string ServerRef, string? Database, string? Schema, string Kind, int Count);

/// <summary>
/// How the catalog's database objects are browsed by server, database and schema, for every endpoint that groups or lists
/// them that way (the lineage schema browse and the semantic layer's coverage): one filter and one grouping rule, so a
/// group's count and the objects listed under it can never disagree. Names compare as the node key folds them, without
/// regard to case, whatever collation the catalog database was created with.
/// </summary>
internal static class CatalogBrowse
{
    /// <summary>The collation the groupings read stored names under: binary, so every spelling arrives on its own and
    /// <see cref="FoldSpellings"/>, not the catalog database's collation, decides what one group is.</summary>
    private const string ExactCollation = "Latin1_General_100_BIN2";

    /// <summary>The collation the filters compare names under: without regard to case, as the node key folds them.</summary>
    private const string CaseInsensitiveCollation = "Latin1_General_100_CI_AS";

    /// <summary>
    /// Narrows <paramref name="query"/> to the server reference, the database and the schema given, each compared without
    /// regard to case, or to the objects whose database or schema a sync could not work out (<paramref name="databaseUnknown"/>,
    /// <paramref name="schemaUnknown"/>). A request naming a database and asking for an unknown one at once is refused, as is
    /// one doing so for a schema.
    /// </summary>
    public static (IQueryable<CatalogObject>? Query, ProblemHttpResult? Problem) Filter(
        IQueryable<CatalogObject> query, string? serverRef, string? database, string? schema, bool? databaseUnknown, bool? schemaUnknown)
    {
        ArgumentNullException.ThrowIfNull(query);
        var withoutDatabase = databaseUnknown == true;
        var withoutSchema = schemaUnknown == true;
        if ((withoutDatabase && !string.IsNullOrWhiteSpace(database)) || (withoutSchema && !string.IsNullOrWhiteSpace(schema)))
        {
            return (null, TypedResults.Problem(
                detail: "Name a database or ask for the objects whose database is unknown (databaseUnknown=true), not both; the same goes for schema and schemaUnknown.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
        }

        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            var server = serverRef.Trim();
            query = query.Where(o => EF.Functions.Collate(o.ServerRef, CaseInsensitiveCollation) == server);
        }

        if (withoutDatabase)
        {
            query = query.Where(o => o.Database == null);
        }
        else if (!string.IsNullOrWhiteSpace(database))
        {
            var name = database.Trim();
            query = query.Where(o => EF.Functions.Collate(o.Database!, CaseInsensitiveCollation) == name);
        }

        if (withoutSchema)
        {
            query = query.Where(o => o.Schema == null);
        }
        else if (!string.IsNullOrWhiteSpace(schema))
        {
            var name = schema.Trim();
            query = query.Where(o => EF.Functions.Collate(o.Schema!, CaseInsensitiveCollation) == name);
        }

        return (query, null);
    }

    /// <summary>
    /// The (server, database, schema, kind) groupings of the objects <paramref name="query"/> selects, each stored spelling
    /// read on its own and then folded by <see cref="FoldSpellings"/>. Unpaged: the distinct groupings are bounded by the
    /// estate's schema count times the handful of object kinds, not by its object count.
    /// </summary>
    public static async Task<List<SpelledGroup>> GroupsAsync(IQueryable<CatalogObject> query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stored = await query
            .GroupBy(o => new
            {
                ServerRef = EF.Functions.Collate(o.ServerRef, ExactCollation),
                Database = EF.Functions.Collate(o.Database, ExactCollation),
                Schema = EF.Functions.Collate(o.Schema, ExactCollation),
                o.Kind,
            })
            .Select(g => new SpelledGroup(g.Key.ServerRef, g.Key.Database, g.Key.Schema, g.Key.Kind, g.Count()))
            .ToListAsync(ct).ConfigureAwait(false);
        return FoldSpellings(stored);
    }

    /// <summary>
    /// Folds stored spellings into groupings as the node key does: a server reference, a database or a schema spelled two
    /// ways that differ only in case is one, its counts summed. Each part is shown in the spelling the most objects carry
    /// (the ordinal-first on a tie), chosen across every grouping it appears in, so one database reads the same in all of
    /// its schemas. Ordered so the hierarchy reads top-down and deterministically.
    /// </summary>
    public static List<SpelledGroup> FoldSpellings(IReadOnlyList<SpelledGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var servers = Majority(groups.Select(g => (Fold(g.ServerRef)!, g.ServerRef, g.Count)));
        var databases = Majority(groups
            .Where(g => g.Database is not null)
            .Select(g => ((Fold(g.ServerRef)!, Fold(g.Database)!), g.Database!, g.Count)));
        var schemas = Majority(groups
            .Where(g => g.Schema is not null)
            .Select(g => ((Fold(g.ServerRef)!, Fold(g.Database), Fold(g.Schema)!), g.Schema!, g.Count)));

        return groups
            .GroupBy(g => (Server: Fold(g.ServerRef)!, Database: Fold(g.Database), Schema: Fold(g.Schema), g.Kind))
            .Select(g => new SpelledGroup(
                servers[g.Key.Server],
                g.Key.Database is null ? null : databases[(g.Key.Server, g.Key.Database)],
                g.Key.Schema is null ? null : schemas[(g.Key.Server, g.Key.Database, g.Key.Schema)],
                g.Key.Kind,
                g.Sum(s => s.Count)))
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal)
            .ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A name as groupings compare it: case-folded, as the node key folds it.</summary>
    public static string? Fold(string? value) => value?.ToUpperInvariant();

    /// <summary>Per key, the spelling with the most objects, the ordinal-first on a tie.</summary>
    private static Dictionary<TKey, string> Majority<TKey>(IEnumerable<(TKey Key, string Spelling, int Count)> spellings)
        where TKey : notnull
        => spellings
            .GroupBy(s => s.Key)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.Spelling, StringComparer.Ordinal)
                    .Select(v => (Spelling: v.Key, Count: v.Sum(s => s.Count)))
                    .OrderByDescending(v => v.Count)
                    .ThenBy(v => v.Spelling, StringComparer.Ordinal)
                    .First().Spelling);
}
