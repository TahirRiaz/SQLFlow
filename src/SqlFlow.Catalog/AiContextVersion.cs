using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.Catalog;

/// <summary>
/// One version of an AI context, resolved against the catalog: every table and join target as the catalog object it
/// names, every column spelt as the catalog holds it, every example with the objects it reads. It is what a sync
/// records as the version a file proposes (<see cref="CatalogAiContext.CurrentJson"/>), what an admin approves
/// (<see cref="CatalogAiContext.ApprovedJson"/>), and, narrowed by the file as it stands, what is served.
/// </summary>
/// <param name="Name">The context's name.</param>
/// <param name="Description">What the context covers.</param>
/// <param name="Instructions">Guidance for every query over the context's tables.</param>
/// <param name="Tables">The tables the context opens.</param>
/// <param name="Examples">The example questions it gives.</param>
public sealed record AiContextVersion(
    string Name,
    string? Description,
    string? Instructions,
    IReadOnlyList<AiContextVersionTable> Tables,
    IReadOnlyList<AiContextVersionExample> Examples)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>The version as stored: compact JSON in declaration order, so the same version always reads the same.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The version a stored <see cref="ToJson"/> holds.</summary>
    public static AiContextVersion FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<AiContextVersion>(json, Json)
            ?? throw new JsonException("An AI context version is stored as a JSON object, not null.");
    }

    /// <summary>The lowercase-hex SHA-256 of <paramref name="json"/>: the version's identity.</summary>
    public static string HashOf(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// What of this (approved) version is still served while the file proposes <paramref name="current"/>: everything
    /// the current version still gives, as this version gives it. A table, column, measure, join or example the file
    /// dropped is no longer served; what it added or changed waits for the next approval. Instructions are kept while
    /// the file still gives some.
    /// </summary>
    public AiContextVersion NarrowedTo(AiContextVersion current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var currentTables = current.Tables.ToDictionary(t => t.ObjectKey, StringComparer.Ordinal);
        var tables = new List<AiContextVersionTable>();
        foreach (var table in Tables)
        {
            if (!currentTables.TryGetValue(table.ObjectKey, out var now))
            {
                continue;
            }

            var columns = now.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var measures = now.Measures.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var joins = now.Joins.Select(j => j.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keptColumns = table.Columns.Where(c => columns.Contains(c.Name)).ToList();
            if (keptColumns.Count == 0)
            {
                continue;
            }

            tables.Add(table with
            {
                Columns = keptColumns,
                Denied = table.Denied
                    .Union(table.Columns.Where(c => !columns.Contains(c.Name)).Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Measures = table.Measures.Where(m => measures.Contains(m.Name)).ToList(),
                Joins = table.Joins.Where(j => joins.Contains(j.Identity)).ToList(),
            });
        }

        var questions = current.Examples.Select(e => e.Question).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return this with
        {
            Instructions = current.Instructions is null ? null : Instructions,
            Tables = tables,
            Examples = Examples.Where(e => questions.Contains(e.Question)).ToList(),
        };
    }
}

/// <summary>One table of a context version.</summary>
/// <param name="ObjectKey">The catalog object's key.</param>
/// <param name="Display">The table as its file named it, for messages and the GUI.</param>
/// <param name="BusinessName">The name a business reader knows it by.</param>
/// <param name="Description">What one row represents.</param>
/// <param name="Synonyms">Other words for it.</param>
/// <param name="Key">The columns identifying one row.</param>
/// <param name="Columns">The columns the context opens: every column the catalog held for the table when the version was
/// read, but the denied ones, each with what the file says it means.</param>
/// <param name="Denied">The columns the file denies, spelt as the catalog holds them.</param>
/// <param name="Measures">Named expressions over the open columns.</param>
/// <param name="Joins">How the table joins to others.</param>
public sealed record AiContextVersionTable(
    string ObjectKey,
    string Display,
    string? BusinessName,
    string? Description,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> Key,
    IReadOnlyList<AiContextVersionColumn> Columns,
    IReadOnlyList<string> Denied,
    IReadOnlyList<AiContextVersionMeasure> Measures,
    IReadOnlyList<AiContextVersionJoin> Joins);

/// <summary>One column a context opens, with what its file says it means.</summary>
public sealed record AiContextVersionColumn(string Name, string? Description, IReadOnlyList<string> Synonyms);

/// <summary>One measure of a context's table.</summary>
public sealed record AiContextVersionMeasure(string Name, string Expression, string? Description);

/// <summary>One join from a context's table, with the columns paired by position.</summary>
/// <param name="ToObjectKey">The catalog object joined to.</param>
/// <param name="ToDisplay">That object as the file named it.</param>
/// <param name="Columns">The declaring table's columns.</param>
/// <param name="ToColumns">The other table's columns.</param>
/// <param name="JoinType"><see cref="SemanticJoinType.Inner"/> or <see cref="SemanticJoinType.Left"/>.</param>
/// <param name="Description">When to use it.</param>
public sealed record AiContextVersionJoin(
    string ToObjectKey,
    string ToDisplay,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ToColumns,
    string JoinType,
    string? Description)
{
    /// <summary>What makes two joins the same join: the target and both column lists.</summary>
    [JsonIgnore]
    public string Identity => $"{ToObjectKey}\n{string.Join(',', Columns)}\n{string.Join(',', ToColumns)}";
}

/// <summary>One example question of a context, with the SQL that answers it.</summary>
/// <param name="Question">The question, as the saved answers store it.</param>
/// <param name="Sql">The query that answers it.</param>
/// <param name="SourceRef">The datasource it runs on, given or worked out from the tables it reads; null when neither.</param>
/// <param name="ObjectKeys">The catalog objects the query reads.</param>
public sealed record AiContextVersionExample(
    string Question, string Sql, string? SourceRef, IReadOnlyList<string> ObjectKeys);

/// <summary>The identity of a curated join (<see cref="CatalogSemanticRelationship.IdentityHash"/>): both endpoints and
/// their column lists, case-normalized. Declared once here so every writer of a join computes the same identity.</summary>
public static class SemanticRelationshipIdentity
{
    /// <summary>The identity hash of the join from <paramref name="fromKey"/> to <paramref name="toKey"/>.</summary>
    public static string Compute(
        string fromKey, IReadOnlyList<string> fromColumns, string toKey, IReadOnlyList<string> toColumns)
    {
        ArgumentNullException.ThrowIfNull(fromColumns);
        ArgumentNullException.ThrowIfNull(toColumns);
        return CatalogProjection.Hash(string.Join(
            '\n',
            fromKey,
            string.Join(',', fromColumns).ToUpperInvariant(),
            toKey,
            string.Join(',', toColumns).ToUpperInvariant()));
    }
}
