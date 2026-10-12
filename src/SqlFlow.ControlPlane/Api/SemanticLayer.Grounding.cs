using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The layer read as a question needs it: which tables a word mentions (the rule the layer's search and a question's
/// grounding share), and the grounding itself, the tables a question is answered from, served beside its saved
/// answers so an assistant writes the query from one answer rather than searching and describing table by table.
/// </summary>
internal static partial class SemanticLayer
{
    /// <summary>The most tables one question's grounding serves. A layer this small or smaller is served whole.</summary>
    internal const int MaxGroundingTables = 3;

    /// <summary>The most tables a question's words are scored against when the layer is too large to serve whole: the
    /// first this many the words mention, in key order, each read once with its columns to rank it. It bounds what one
    /// question costs when its words are common ones a large layer mentions everywhere.</summary>
    private const int MaxGroundingCandidates = 200;

    /// <summary>One layer table with its annotation, the row the layer's searches filter.</summary>
    internal sealed class LayerRow
    {
        public required CatalogObject Object { get; init; }

        public CatalogSemanticObject? Annotation { get; init; }
    }

    /// <summary>Every table in the layer with its annotation, when it has one.</summary>
    internal static IQueryable<LayerRow> LayerRows(CatalogDbContext db)
        => from o in LayerObjects(db)
           join s in db.SemanticObjects.AsNoTracking() on o.Key equals s.ObjectKey into annotations
           from s in annotations.DefaultIfEmpty()
           select new LayerRow { Object = o, Annotation = s };

    /// <summary>Whether a layer table mentions <paramref name="token"/>: in its name, schema, business name,
    /// description, or synonyms, or in an allowed column's name, description, or synonyms. A column counts only while
    /// the catalog still reports it, as it does everywhere in the layer.</summary>
    internal static Expression<Func<LayerRow, bool>> Mentions(CatalogDbContext db, string token)
        => x => x.Object.Name.Contains(token)
            || (x.Object.Schema != null && x.Object.Schema.Contains(token))
            || (x.Annotation != null
                && ((x.Annotation.BusinessName != null && x.Annotation.BusinessName.Contains(token))
                    || (x.Annotation.Description != null && x.Annotation.Description.Contains(token))
                    || (x.Annotation.Synonyms != null && x.Annotation.Synonyms.Contains(token))))
            || db.ColumnPolicies.Any(pol => pol.IsAllowed && pol.ObjectKey == x.Object.Key
                && (pol.ColumnName.Contains(token)
                    || (pol.Description != null && pol.Description.Contains(token))
                    || (pol.Synonyms != null && pol.Synonyms.Contains(token)))
                && db.ObjectColumns.Any(c => c.ObjectKey == pol.ObjectKey && c.Name == pol.ColumnName));

    /// <summary>The summary and allowed columns of each of <paramref name="keys"/> that is in the layer.</summary>
    internal static async Task<Dictionary<string, (SemanticTableSummaryDto Summary, IReadOnlyList<AllowedColumn> Columns)>>
        LoadSummariesAsync(CatalogDbContext db, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var result = new Dictionary<string, (SemanticTableSummaryDto, IReadOnlyList<AllowedColumn>)>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var objects = await (from o in db.Objects.AsNoTracking()
                             where keys.Contains(o.Key)
                             join s in db.SemanticObjects.AsNoTracking() on o.Key equals s.ObjectKey into annotations
                             from s in annotations.DefaultIfEmpty()
                             select new
                             {
                                 o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind,
                                 BusinessName = s == null ? null : s.BusinessName,
                                 Description = s == null ? null : s.Description,
                                 Synonyms = s == null ? null : s.Synonyms,
                             })
            .ToListAsync(ct).ConfigureAwait(false);
        var allowed = await LoadAllowedColumnsAsync(db, keys, ct).ConfigureAwait(false);

        foreach (var o in objects)
        {
            if (!allowed.TryGetValue(o.Key, out var columns))
            {
                continue;
            }

            result[o.Key] = (
                new SemanticTableSummaryDto(
                    o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.BusinessName, o.Description,
                    SplitSynonyms(o.Synonyms), columns.Count),
                columns);
        }

        return result;
    }

    /// <summary>
    /// The layer a question is answered from: the tables it needs, each as <see cref="ServeTableAsync"/> serves it with
    /// the examples close to the question, and each AI context's instructions once. A layer of
    /// <see cref="MaxGroundingTables"/> tables or fewer is served whole, since choosing among so few saves nothing and
    /// risks leaving out the one a question words differently. A larger one is served the tables its words mention
    /// most, at most <see cref="MaxGroundingTables"/>, and none when no word mentions any; the layer's size travels with
    /// the answer, so a caller knows to search it for a table the grounding does not hold.
    /// </summary>
    /// <param name="db">The catalog.</param>
    /// <param name="question">The question, the terms it was expanded into, the examples' coverage floor, and the
    /// examples already sent as its saved answers.</param>
    /// <param name="ct">Cancellation.</param>
    internal static async Task<SemanticGroundingDto> GroundingAsync(
        CatalogDbContext db, ExampleQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var layerKeys = await LayerObjectKeys(db).ToListAsync(ct).ConfigureAwait(false);
        if (layerKeys.Count == 0)
        {
            return new SemanticGroundingDto(0, [], []);
        }

        var keys = layerKeys.Count <= MaxGroundingTables
            ? layerKeys.Order(StringComparer.Ordinal).ToList()
            : await MentionedTablesAsync(db, question, ct).ConfigureAwait(false);

        var tables = new List<SemanticGroundingTableDto>(keys.Count);
        foreach (var key in keys)
        {
            // A table can leave the layer between the two reads (a context narrowed or withdrawn); it is then skipped.
            if (await ServeTableAsync(db, key, question, ct).ConfigureAwait(false) is { } table)
            {
                tables.Add(table);
            }
        }

        var contexts = new List<SemanticContextInstructionsDto>();
        foreach (var name in tables.Select(t => t.Context).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (await LoadContextInstructionsAsync(db, name, ct).ConfigureAwait(false) is { Length: > 0 } instructions)
            {
                contexts.Add(new SemanticContextInstructionsDto(name, instructions));
            }
        }

        return new SemanticGroundingDto(layerKeys.Count, contexts, tables);
    }

    /// <summary>
    /// The tables of a large layer a question's words mention most, best first: one query finds the tables any word
    /// mentions (<see cref="Mentions"/>), and each is then scored by how many of the words, each form counted once, its
    /// name, description, synonyms and allowed columns carry. A word is looked for by its stem, so "wellbores" finds the
    /// column WellboreID; a phrase or a word too short to stem is looked for as written.
    /// </summary>
    private static async Task<List<string>> MentionedTablesAsync(
        CatalogDbContext db, ExampleQuestion question, CancellationToken ct)
    {
        var tokens = question.Terms.Concat(QuestionSearch.TypedTerms(question.Question))
            .Select(SearchToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SqlFlow.Assistant.QuestionExpander.MaxTerms)
            .ToList();
        if (tokens.Count == 0)
        {
            return [];
        }

        var candidates = await LayerRows(db)
            .Where(QuestionSearch.AnyTerm<LayerRow>(tokens, token => Mentions(db, token)))
            .OrderBy(x => x.Object.Key)
            .Select(x => x.Object.Key)
            .Take(MaxGroundingCandidates)
            .ToListAsync(ct).ConfigureAwait(false);
        var loaded = await LoadSummariesAsync(db, candidates, ct).ConfigureAwait(false);

        return loaded
            .Select(entry => (Key: entry.Key, Score: tokens.Count(token => MentionsLoaded(entry.Value, token))))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxGroundingTables)
            .Select(entry => entry.Key)
            .ToList();
    }

    /// <summary>What a word is looked for as in the layer's text: its stem, so every form of it and the identifiers
    /// built on it are found, or the word as written when it is a phrase or too short to stem safely.</summary>
    private static string SearchToken(string term)
    {
        if (term.Contains(' ', StringComparison.Ordinal))
        {
            return term;
        }

        var stem = QuestionSearch.Stem(term);
        return stem.Length >= 3 ? stem : term;
    }

    /// <summary>The same test as <see cref="Mentions"/>, over a table already read, to score it.</summary>
    private static bool MentionsLoaded(
        (SemanticTableSummaryDto Summary, IReadOnlyList<AllowedColumn> Columns) table, string token)
    {
        bool Has(string? text) => text is not null && text.Contains(token, StringComparison.OrdinalIgnoreCase);
        var summary = table.Summary;
        return Has(summary.Name) || Has(summary.Schema) || Has(summary.BusinessName) || Has(summary.Description)
            || summary.Synonyms.Any(Has)
            || table.Columns.Any(c => Has(c.Name) || Has(c.Description) || c.Synonyms.Any(Has));
    }
}
