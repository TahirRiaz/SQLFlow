using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The semantic layer's rules, shared by the assistant-facing read endpoints (<see cref="SemanticLayerEndpoints"/>),
/// the read-only AI context pages (<see cref="SemanticLayerAdminEndpoints"/>, <see cref="AiContextEndpoints"/>) and the
/// sync that checks what a context file proposes (<see cref="AiContextSync"/>), so what an admin is told is servable and
/// what an assistant is actually served are decided by the same code.
///
/// The layer is what the approved AI contexts serve (<see cref="CatalogAiContext"/>, written only by
/// <see cref="AiContextMaterializer"/>): an object is in the layer exactly when at least one of its catalogued columns is
/// allowed, and only allowed columns are ever served. Everything else the layer holds (table and column descriptions,
/// synonyms, keys and joins, measures, example queries) annotates that set and is re-checked against it every time it is
/// served: a key, join, measure, or example that would name a column no longer allowed is withheld rather than served
/// stale.
/// </summary>
internal static partial class SemanticLayer
{
    /// <summary>The most example queries one table's bundle carries: enough precedent to mirror, few enough to
    /// leave the context for the question.</summary>
    internal const int MaxServedExamples = 10;

    /// <summary>The most example queries the admin editor lists for one table.</summary>
    internal const int MaxAdminExamples = 25;

    /// <summary>How many of a table's newest examples are ranked against a question to choose the ones served. Only
    /// their questions are read to rank them, and the bound keeps that read small however many accumulate.</summary>
    internal const int MaxRankedExamples = 500;

    /// <summary>The longest question a table's bundle ranks its examples against: the width of a stored question.</summary>
    internal const int MaxQuestionLength = 1000;

    /// <summary>The most measures one evaluation loads. Each is validated through the query guards, so the cap
    /// bounds what one request can cost on an estate that has accumulated many.</summary>
    internal const int MaxMeasures = 500;

    /// <summary>What a curated key's origin reads as, beside the codebase-interpreted origins
    /// (Constraint / Declared / Merge).</summary>
    internal const string CuratedOrigin = "Curated";

    /// <summary>How many object keys one IN-list lookup carries.</summary>
    private const int KeyChunk = 1000;

    private const string MeasureShapeProblem =
        "A measure must be one SQL scalar expression over its anchor table's own columns, such as " +
        "SUM(Amount) - SUM(RefundAmount): no FROM, WHERE, subquery, comment, or statement separator.";

    /// <summary>One allow-listed column of a layer object, with the semantic annotations its policy row carries.</summary>
    internal sealed record AllowedColumn(
        int Ordinal, string Name, string? DataType, bool Nullable, string? Description, IReadOnlyList<string> Synonyms);

    /// <summary>A join an object takes part in, with why it cannot be served when it cannot: exactly one of
    /// <see cref="Curated"/> and <see cref="Discovered"/> is set.</summary>
    internal sealed record JoinEvaluation(
        SemanticJoinDto Join, CatalogSemanticRelationship? Curated, ObjectRelationshipDto? Discovered, string? Problem);

    /// <summary>A measure with its anchor's name and why it cannot be served when it cannot.</summary>
    internal sealed record MeasureEvaluation(CatalogSemanticMeasure Measure, string ObjectName, string? Problem)
    {
        public SemanticMeasureDto ToDto()
            => new(Measure.Id, Measure.Name, Measure.ObjectKey, ObjectName, Measure.Expression, Measure.Description);
    }

    /// <summary>A stored example query (a saved answer), with why it cannot be served when it cannot.</summary>
    internal sealed record ExampleEvaluation(
        long Id, string Question, string Sql, string? SourceRef, IReadOnlyList<string> ObjectKeys, string Provenance,
        int? Confidence, Guid? RepoId, string? ConfirmedBy, DateTime ConfirmedUtc, string? Problem)
    {
        /// <summary>The evaluation as the editor shows it: the table's Examples tab and the Saved answers tab alike.</summary>
        public SemanticExampleAdminDto ToAdminDto()
            => new(Id, Question, Sql, SourceRef, ObjectKeys, Provenance, Confidence, RepoId, ConfirmedBy, ConfirmedUtc, Problem);

        /// <summary>Evaluates one stored example against the current query guards.</summary>
        public static async Task<ExampleEvaluation> OfAsync(CatalogDbContext db, CatalogSemanticExample example, CancellationToken ct)
            => new(
                example.Id, example.Question, example.Sql, example.SourceRef,
                QuestionExampleEndpoints.SplitObjectKeys(example.ObjectKeys), example.Provenance, example.Confidence,
                example.RepoId, example.ConfirmedBy, example.ConfirmedUtc,
                await QueryProblemAsync(db, example.Sql, ct).ConfigureAwait(false));
    }

    /// <summary>The identity recorded as a row's last editor: the subject claim, falling back to the principal's name.</summary>
    internal static string? Actor(ClaimsPrincipal user) => user.FindFirst("sub")?.Value ?? user.Identity?.Name;

    /// <summary>Trims a free-text field, mapping blank to null.</summary>
    internal static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The keys of every object in the layer: those with at least one catalogued column on the allow-list.
    /// A policy row for a column the catalog no longer reports does not count, since there is nothing to serve.</summary>
    internal static IQueryable<string> LayerObjectKeys(CatalogDbContext db)
        => db.ColumnPolicies.AsNoTracking()
            .Where(p => p.IsAllowed && db.ObjectColumns.Any(c => c.ObjectKey == p.ObjectKey && c.Name == p.ColumnName))
            .Select(p => p.ObjectKey)
            .Distinct();

    /// <summary>Every object in the layer.</summary>
    internal static IQueryable<CatalogObject> LayerObjects(CatalogDbContext db)
    {
        var layerKeys = LayerObjectKeys(db);
        return db.Objects.AsNoTracking().Where(o => layerKeys.Contains(o.Key));
    }

    /// <summary>The allow-listed columns of each of <paramref name="keys"/>, in column order. A key with no allowed
    /// column is absent from the result, which is exactly "not in the layer".</summary>
    internal static async Task<Dictionary<string, List<AllowedColumn>>> LoadAllowedColumnsAsync(
        CatalogDbContext db, IEnumerable<string> keys, CancellationToken ct)
    {
        var result = new Dictionary<string, List<AllowedColumn>>(StringComparer.Ordinal);
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(KeyChunk))
        {
            var rows = await (from c in db.ObjectColumns.AsNoTracking()
                              join p in db.ColumnPolicies.AsNoTracking()
                                  on new { c.ObjectKey, ColumnName = c.Name } equals new { p.ObjectKey, p.ColumnName }
                              where p.IsAllowed && chunk.Contains(c.ObjectKey)
                              orderby c.ObjectKey, c.Ordinal, c.Name
                              select new { c.ObjectKey, c.Ordinal, c.Name, c.DataType, c.Nullable, p.Description, p.Synonyms })
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.ObjectKey, out var list))
                {
                    list = [];
                    result[row.ObjectKey] = list;
                }

                list.Add(new AllowedColumn(
                    row.Ordinal, row.Name, row.DataType, row.Nullable, row.Description, SplitSynonyms(row.Synonyms)));
            }
        }

        return result;
    }

    /// <summary>A stored newline-joined synonym list as its entries.</summary>
    internal static IReadOnlyList<string> SplitSynonyms(string? stored)
        => string.IsNullOrEmpty(stored)
            ? []
            : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Null when every one of <paramref name="columns"/> is an allow-listed column of the object, otherwise
    /// the first offending column, explained.</summary>
    internal static string? ColumnsProblem(
        string objectName, IReadOnlyList<string> columns, IReadOnlyCollection<AllowedColumn>? allowed)
    {
        if (allowed is null || allowed.Count == 0)
        {
            return $"'{objectName}' is not in the semantic layer: no approved AI context opens it.";
        }

        foreach (var column in columns)
        {
            if (!allowed.Any(a => string.Equals(a.Name, column, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Column '{objectName}.{column}' is not open to the assistant: no approved AI context lists it.";
            }
        }

        return null;
    }

    /// <summary>The catalog's own spelling of each of <paramref name="columns"/> (already checked to be allowed), so a
    /// stored key or join reads exactly as the warehouse names its columns.</summary>
    internal static IReadOnlyList<string> Canonical(IReadOnlyList<string> columns, IReadOnlyCollection<AllowedColumn> allowed)
        => columns
            .Select(c => allowed.FirstOrDefault(a => string.Equals(a.Name, c, StringComparison.OrdinalIgnoreCase))?.Name ?? c)
            .ToList();

    /// <summary>The key served for a layer table: the curated key when every column of it is still allowed, else the
    /// codebase-interpreted key under the same condition, else none. A key naming a denied column is withheld whole,
    /// since a partial key identifies nothing.</summary>
    internal static (IReadOnlyList<string> Columns, string? Origin) ResolveKey(
        string? curated, string? interpreted, string? interpretedOrigin, IReadOnlyCollection<AllowedColumn> allowed)
    {
        var curatedColumns = LineageEndpoints.SplitColumns(curated ?? string.Empty);
        if (curatedColumns.Count > 0 && ColumnsProblem(string.Empty, curatedColumns, allowed) is null)
        {
            return (Canonical(curatedColumns, allowed), CuratedOrigin);
        }

        var interpretedColumns = LineageEndpoints.SplitColumns(interpreted ?? string.Empty);
        if (interpretedColumns.Count > 0 && ColumnsProblem(string.Empty, interpretedColumns, allowed) is null)
        {
            return (Canonical(interpretedColumns, allowed), interpretedOrigin);
        }

        return ([], null);
    }

    /// <summary>Every approved context's served instructions, each headed by its context's name, in name order; null when
    /// no context serves any.</summary>
    internal static async Task<string?> LoadInstructionsAsync(CatalogDbContext db, CancellationToken ct)
    {
        var served = await db.AiContexts.AsNoTracking()
            .Where(c => c.ApprovedHash != null && c.Instructions != null)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.Instructions })
            .ToListAsync(ct).ConfigureAwait(false);
        return served.Count == 0
            ? null
            : string.Join("\n\n", served.Select(c => $"Context '{c.Name}':\n{c.Instructions}"));
    }

    /// <summary>The instructions context <paramref name="context"/> serves, or null.</summary>
    internal static Task<string?> LoadContextInstructionsAsync(CatalogDbContext db, string? context, CancellationToken ct)
        => context is null
            ? Task.FromResult<string?>(null)
            : db.AiContexts.AsNoTracking()
                .Where(c => c.Name == context && c.ApprovedHash != null)
                .Select(c => c.Instructions)
                .FirstOrDefaultAsync(ct);

    /// <summary>Null when <paramref name="sql"/> is a single read-only SELECT reading only allow-listed columns of
    /// catalogued objects; otherwise the refusal the query surface itself would give. <paramref name="allowedOverride"/>
    /// checks it as if those columns were the allowed ones (<see cref="ColumnPolicyGuard.EnsureAllowedAsync"/>).</summary>
    internal static async Task<string?> QueryProblemAsync(
        CatalogDbContext db, string sql, CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? allowedOverride = null)
    {
        try
        {
            var validated = ReadOnlyQueryGuard.Validate(sql);
            await ColumnPolicyGuard.EnsureAllowedAsync(db, validated, ct, allowedOverride).ConfigureAwait(false);
            return null;
        }
        catch (SqlFlowException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Null when <paramref name="expression"/> is a servable measure over the object <paramref name="objectKey"/>:
    /// one scalar expression that, composed as <c>SELECT expression FROM anchor</c>, parses to exactly that shape
    /// (no second table, filter, grouping, or subquery smuggled in) and passes the read-only and column allow-list
    /// guards. Comments and statement separators are refused before parsing, since a trailing comment could swallow
    /// the composed FROM clause and let the expression bring its own.
    /// </summary>
    internal static async Task<string?> MeasureProblemAsync(
        CatalogDbContext db, string objectKey, string expression, CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? allowedOverride = null)
    {
        var anchor = await db.Objects.AsNoTracking()
            .Where(o => o.Key == objectKey)
            .Select(o => new { o.Schema, o.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (anchor is null)
        {
            return $"No catalog object has the key '{objectKey}'.";
        }

        if (expression.Contains("--", StringComparison.Ordinal)
            || expression.Contains("/*", StringComparison.Ordinal)
            || expression.Contains(';', StringComparison.Ordinal))
        {
            return MeasureShapeProblem;
        }

        var from = anchor.Schema is null
            ? QuoteIdentifier(anchor.Name)
            : $"{QuoteIdentifier(anchor.Schema)}.{QuoteIdentifier(anchor.Name)}";
        var sql = $"SELECT {expression} AS [measure_value] FROM {from}";

        var select = ColumnPolicyGuard.ParseSingleSelect(sql);
        if (select is null || !IsBareExpressionOver(select, anchor.Schema, anchor.Name))
        {
            return MeasureShapeProblem;
        }

        return await QueryProblemAsync(db, sql, ct, allowedOverride).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="select"/> is exactly <c>SELECT scalar FROM schema.name</c> with nothing else.</summary>
    private static bool IsBareExpressionOver(SelectStatement select, string? schema, string name)
    {
        if (select.Into is not null
            || select.On is not null
            || select.WithCtesAndXmlNamespaces is not null
            || select.ComputeClauses.Count > 0
            || select.OptimizerHints.Count > 0
            || select.QueryExpression is not QuerySpecification spec)
        {
            return false;
        }

        if (spec.SelectElements.Count != 1
            || spec.SelectElements[0] is not SelectScalarExpression
            || spec.WhereClause is not null
            || spec.GroupByClause is not null
            || spec.HavingClause is not null
            || spec.WindowClause is not null
            || spec.TopRowFilter is not null
            || spec.OffsetClause is not null
            || spec.OrderByClause is not null
            || spec.ForClause is not null
            || spec.UniqueRowFilter != UniqueRowFilter.NotSpecified
            || spec.FromClause is null
            || spec.FromClause.TableReferences.Count != 1
            || spec.FromClause.TableReferences[0] is not NamedTableReference table)
        {
            return false;
        }

        var target = table.SchemaObject;
        if (target.DatabaseIdentifier is not null
            || target.ServerIdentifier is not null
            || !string.Equals(target.BaseIdentifier?.Value, name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.SchemaIdentifier?.Value, schema, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var probe = new SubqueryProbe();
        spec.SelectElements[0].Accept(probe);
        return !probe.Found;
    }

    /// <summary>A bracket-quoted T-SQL identifier, with any closing bracket escaped.</summary>
    internal static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Finds any subquery inside an expression: a measure reads its anchor table and nothing else.</summary>
    private sealed class SubqueryProbe : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(ScalarSubquery node) => Found = true;

        public override void Visit(QueryDerivedTable node) => Found = true;
    }

    /// <summary>
    /// Every join <paramref name="key"/> takes part in, curated first and then discovered from the codebase, each
    /// with the reason it cannot be served when it cannot: the other table is gone or outside the layer, or a column
    /// on either side is not allow-listed. A discovered join repeating a curated one's columns is reported as a
    /// duplicate rather than served twice.
    /// </summary>
    internal static async Task<IReadOnlyList<JoinEvaluation>> EvaluateJoinsAsync(
        CatalogDbContext db, string key, string name, CancellationToken ct)
    {
        var curated = await db.SemanticRelationships.AsNoTracking()
            .Where(r => r.FromObjectKey == key || r.ToObjectKey == key)
            .OrderBy(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var (references, referencedBy) = await LineageEndpoints.LoadRelationshipsAsync(db, key, ct).ConfigureAwait(false);

        var otherKeys = curated.Select(r => r.FromObjectKey == key ? r.ToObjectKey : r.FromObjectKey)
            .Concat(references.Select(r => r.OtherObjectKey))
            .Concat(referencedBy.Select(r => r.OtherObjectKey))
            .Append(key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var allowed = await LoadAllowedColumnsAsync(db, otherKeys, ct).ConfigureAwait(false);
        var locations = new Dictionary<string, (string? Database, string? Schema, string Name)>(StringComparer.Ordinal);
        foreach (var chunk in otherKeys.Chunk(KeyChunk))
        {
            var rows = await db.Objects.AsNoTracking()
                .Where(o => chunk.Contains(o.Key))
                .Select(o => new { o.Key, o.Database, o.Schema, o.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                locations[row.Key] = (row.Database, row.Schema, row.Name);
            }
        }

        allowed.TryGetValue(key, out var ownAllowed);
        string? Problem(string otherKey, string otherName, IReadOnlyList<string> own, IReadOnlyList<string> other)
        {
            if (!locations.ContainsKey(otherKey))
            {
                return $"The other table ('{otherKey}') is no longer in the catalog.";
            }

            allowed.TryGetValue(otherKey, out var otherAllowed);
            return ColumnsProblem(name, own, ownAllowed) ?? ColumnsProblem(otherName, other, otherAllowed);
        }

        var result = new List<JoinEvaluation>(curated.Count + references.Count + referencedBy.Count);
        var servedSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relationship in curated)
        {
            var outgoing = string.Equals(relationship.FromObjectKey, key, StringComparison.Ordinal);
            var otherKey = outgoing ? relationship.ToObjectKey : relationship.FromObjectKey;
            var own = LineageEndpoints.SplitColumns(outgoing ? relationship.FromColumns : relationship.ToColumns);
            var other = LineageEndpoints.SplitColumns(outgoing ? relationship.ToColumns : relationship.FromColumns);
            var found = locations.TryGetValue(otherKey, out var location);
            var otherName = found ? location.Name : otherKey;
            var problem = Problem(otherKey, otherName, own, other);
            if (problem is null)
            {
                servedSignatures.Add(JoinSignature(otherKey, own, other));
            }

            var join = new SemanticJoinDto(
                CuratedOrigin, otherKey, location.Database, location.Schema, otherName, own, other,
                LineageEndpoints.RenderOn(name, own, otherName, other, []),
                [relationship.JoinType], IsRangeJoin: false, Occurrences: null, relationship.Description);
            result.Add(new JoinEvaluation(join, relationship, null, problem));
        }

        foreach (var relationship in references.Concat(referencedBy))
        {
            var own = LineageEndpoints.SplitColumns(relationship.OwnColumns);
            var other = LineageEndpoints.SplitColumns(relationship.OtherColumns);
            var problem = Problem(relationship.OtherObjectKey, relationship.OtherName, own, other);
            if (problem is null && !servedSignatures.Add(JoinSignature(relationship.OtherObjectKey, own, other)))
            {
                problem = "The same join is already declared as a curated relationship.";
            }

            var join = new SemanticJoinDto(
                "Discovered", relationship.OtherObjectKey, relationship.OtherDatabase, relationship.OtherSchema,
                relationship.OtherName, own, other,
                LineageEndpoints.RenderOn(name, own, relationship.OtherName, other, relationship.Operators),
                relationship.JoinTypes, relationship.IsRangeJoin, relationship.Occurrences, Description: null);
            result.Add(new JoinEvaluation(join, null, relationship, problem));
        }

        return result;
    }

    private static string JoinSignature(string otherKey, IReadOnlyList<string> own, IReadOnlyList<string> other)
        => $"{otherKey}\n{string.Join(',', own)}\n{string.Join(',', other)}";

    /// <summary>The measures <paramref name="source"/> selects (ordered by name, capped at <see cref="MaxMeasures"/>),
    /// each validated against the current allow-list.</summary>
    internal static async Task<IReadOnlyList<MeasureEvaluation>> EvaluateMeasuresAsync(
        CatalogDbContext db, IQueryable<CatalogSemanticMeasure> source, CancellationToken ct)
    {
        var measures = await source.AsNoTracking()
            .OrderBy(m => m.Name)
            .Take(MaxMeasures)
            .ToListAsync(ct).ConfigureAwait(false);
        var keys = measures.Select(m => m.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        var names = await db.Objects.AsNoTracking()
            .Where(o => keys.Contains(o.Key))
            .Select(o => new { o.Key, o.Name })
            .ToDictionaryAsync(o => o.Key, o => o.Name, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var result = new List<MeasureEvaluation>(measures.Count);
        foreach (var measure in measures)
        {
            var problem = await MeasureProblemAsync(db, measure.ObjectKey, measure.Expression, ct).ConfigureAwait(false);
            result.Add(new MeasureEvaluation(measure, names.GetValueOrDefault(measure.ObjectKey, measure.ObjectKey), problem));
        }

        return result;
    }

    /// <summary>
    /// The question an assistant is answering, which a table's examples are chosen against: <see cref="Question"/>'s
    /// own words and any <see cref="Terms"/> a search expanded it into, the share of an example's question they must
    /// cover (<see cref="QuestionSearch.CoverageOf"/>), and the examples already sent beside it, which are not sent twice.
    /// </summary>
    /// <param name="Question">The question as typed.</param>
    /// <param name="Terms">Terms a search expanded the question into, beyond its own words; empty when none were.</param>
    /// <param name="MinCoverage">How much of an example's question must be covered for it to be served, from 0 to 1.</param>
    /// <param name="AlreadySent">The ids of the examples already sent with the same answer.</param>
    internal sealed record ExampleQuestion(
        string Question, IReadOnlyList<string> Terms, double MinCoverage, IReadOnlySet<long> AlreadySent);

    /// <summary>The stored example queries that read <paramref name="objectKey"/> (at most <paramref name="take"/>),
    /// each checked through the query guards: the newest, or with a question the ones close to it. When
    /// <paramref name="servableOnly"/> is set, examples that would be refused are skipped rather than counted, so the
    /// bundle still fills with usable ones.</summary>
    /// <param name="db">The catalog.</param>
    /// <param name="objectKey">The table the examples read.</param>
    /// <param name="take">The most examples returned.</param>
    /// <param name="servableOnly">Whether to skip, rather than return with their problem, examples the guards refuse.</param>
    /// <param name="closestTo">The question an assistant is answering, or null for the newest examples. With one, the
    /// table's newest <see cref="MaxRankedExamples"/> examples are scored and cut as the stored-question search scores
    /// and cuts them: an example sharing no word with the question, or covering less of its own question than the floor,
    /// is not served, so a table whose examples answer other questions is served without any rather than with ten
    /// queries the assistant reads for nothing. The rest are ranked by score, then coverage, the newest first among
    /// equals: the precedent an assistant mirrors is then the closest it has, not whichever was confirmed last.</param>
    /// <param name="ct">Cancellation.</param>
    internal static async Task<IReadOnlyList<ExampleEvaluation>> EvaluateExamplesAsync(
        CatalogDbContext db, string objectKey, int take, bool servableOnly, ExampleQuestion? closestTo,
        CancellationToken ct)
    {
        // The stored key list is newline-joined, so a substring match narrows in SQL and an exact split confirms it.
        var newest = db.SemanticExamples.AsNoTracking()
            .Where(e => e.ObjectKeys.Contains(objectKey))
            .OrderByDescending(e => e.ConfirmedUtc).ThenByDescending(e => e.Id);

        List<CatalogSemanticExample> candidates;
        if (closestTo is null)
        {
            candidates = await newest.Take(take * 4).ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            QuestionSearch.CheckCoverage(closestTo.MinCoverage);
            var terms = closestTo.Terms.Concat(QuestionSearch.TypedTerms(closestTo.Question))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var typedWords = QuestionSearch.MeaningfulStems(closestTo.Question);

            // Ranked on the questions alone, so the SQL of every example is not read to choose ten; OrderBy is
            // stable, so equal scores keep the newest-first order they were read in.
            var questions = await newest.Take(MaxRankedExamples)
                .Select(e => new { e.Id, e.Question })
                .ToListAsync(ct).ConfigureAwait(false);
            var ranked = questions
                .Where(e => !closestTo.AlreadySent.Contains(e.Id))
                .Select(e => (
                    e.Id,
                    Score: QuestionSearch.ScoreOf(QuestionSearch.MatchedTerms(e.Question, terms)),
                    Coverage: QuestionSearch.CoverageOf(e.Question, terms),
                    Same: typedWords.Count > 0 && typedWords.SetEquals(QuestionSearch.MeaningfulStems(e.Question))))
                .Where(e => e.Score > 0 && (e.Same || e.Coverage >= closestTo.MinCoverage))
                .OrderByDescending(e => e.Score)
                .ThenByDescending(e => e.Coverage)
                .Take(take * 4)
                .Select(e => e.Id)
                .ToList();
            var rows = await db.SemanticExamples.AsNoTracking()
                .Where(e => ranked.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);
            candidates = ranked.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        }

        var result = new List<ExampleEvaluation>(take);
        foreach (var example in candidates)
        {
            if (!QuestionExampleEndpoints.SplitObjectKeys(example.ObjectKeys).Contains(objectKey, StringComparer.Ordinal))
            {
                continue;
            }

            var evaluation = await ExampleEvaluation.OfAsync(db, example, ct).ConfigureAwait(false);
            if (servableOnly && evaluation.Problem is not null)
            {
                continue;
            }

            result.Add(evaluation);
            if (result.Count == take)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The most Power BI model tables one table's bundle carries (one per report model table loading from it).</summary>
    internal const int MaxReportModels = 20;

    /// <summary>A Power BI measure or calculated column defined on a model table, with why it cannot be served when it
    /// cannot.</summary>
    internal sealed record ReportFieldEvaluation(CatalogSubscriberModelField Field, string? Problem);

    /// <summary>A Power BI model relationship between a table's model table and another model table, read from the
    /// table's own side, with the warehouse columns and object it maps to and why it cannot be served when it cannot.</summary>
    internal sealed record ReportRelationshipEvaluation(
        CatalogSubscriberModelRelationship Relationship, string ModelTable, string? OwnColumn, string OtherModelTable,
        string? OtherColumn, string? OtherObjectKey, string? OtherName, string? Problem);

    /// <summary>One report's model table that loads from a warehouse table, with its evaluated measures, calculated
    /// columns, and relationships.</summary>
    internal sealed record ReportModelEvaluation(
        CatalogSubscriberModelTable Table, string SubscriberName,
        IReadOnlyList<ReportFieldEvaluation> Fields, IReadOnlyList<ReportRelationshipEvaluation> Relationships)
    {
        /// <summary>Only what may be served: definitions whose every column is allowed.</summary>
        public SemanticReportModelDto ToServedDto() => new(
            Table.SubscriberKey, SubscriberName, Table.ReportFile, Table.Name,
            Fields.Where(f => f.Problem is null && f.Field.Kind == MeasureKind).Select(f => ToFieldDto(f.Field)).ToList(),
            Fields.Where(f => f.Problem is null && f.Field.Kind == CalculatedColumnKind).Select(f => ToFieldDto(f.Field)).ToList(),
            Relationships
                .Where(r => r.Problem is null)
                .Select(r => new SemanticReportRelationshipDto(
                    r.OwnColumn!, r.OtherObjectKey!, r.OtherName!, r.OtherColumn!, r.Relationship.Cardinality,
                    r.Relationship.IsActive, r.ModelTable, r.OtherModelTable))
                .ToList());

        /// <summary>Everything, with its state, for the editor.</summary>
        public SemanticReportModelAdminDto ToAdminDto() => new(
            Table.SubscriberKey, SubscriberName, Table.ReportFile, Table.Name,
            Fields
                .Select(f => new SemanticReportFieldAdminDto(
                    f.Field.Name, f.Field.Kind, f.Field.Expression ?? string.Empty, f.Field.Description, f.Problem))
                .ToList(),
            Relationships
                .Select(r => new SemanticReportRelationshipAdminDto(
                    r.ModelTable, r.OwnColumn, r.OtherModelTable, r.OtherColumn, r.OtherObjectKey,
                    r.Relationship.Cardinality, r.Relationship.IsActive, r.Problem))
                .ToList());

        private static SemanticReportFieldDto ToFieldDto(CatalogSubscriberModelField field)
            => new(field.Name, field.Expression ?? string.Empty, field.Description);
    }

    /// <summary>The model field kind a Power BI measure is stored under.</summary>
    internal const string MeasureKind = "measure";

    /// <summary>The model field kind a Power BI calculated column is stored under.</summary>
    internal const string CalculatedColumnKind = "calculatedColumn";

    /// <summary>A column or measure reference in DAX: <c>Table[Name]</c>, <c>'Quoted Table'[Name]</c>, or a bare
    /// <c>[Name]</c>. A quote inside a quoted table name is doubled, and a closing bracket inside a name is doubled.</summary>
    [GeneratedRegex(@"(?:'(?<quoted>(?:[^']|'')+)'|(?<bare>[A-Za-z_][A-Za-z0-9_]*))?\[(?<name>(?:[^\]]|\]\])+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex DaxReferencePattern();

    /// <summary>Every column or measure reference in a DAX expression, with its table when the reference names one. A
    /// bracketed name inside a string literal is read as a reference too, which can only withhold a definition, never
    /// serve one that names a denied column.</summary>
    internal static IEnumerable<(string? Table, string Name)> DaxReferences(string expression)
    {
        foreach (Match match in DaxReferencePattern().Matches(expression))
        {
            string? table = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value.Replace("''", "'", StringComparison.Ordinal)
                : match.Groups["bare"].Success ? match.Groups["bare"].Value : null;
            yield return (table, match.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The Power BI semantic models built on <paramref name="objectKey"/>: every report model table that loads from it
    /// (resolved at sync, <see cref="CatalogSubscriberModelTable.ObjectKey"/>), with that table's measures and calculated
    /// columns and its relationships to other model tables, each evaluated against the column allow-list.
    /// <para>
    /// A report names MODEL columns, and a model column only counts as an allowed warehouse column when its name is one:
    /// a column the report renamed is treated as not allowed, since nothing maps it back to the warehouse column it came
    /// from. So a measure or calculated column is served only when every column its DAX reads is an allowed column of
    /// the warehouse table its model table loads from, and every measure or calculated column it uses is itself
    /// servable. A relationship is served only when both model tables load from layer tables and both columns are
    /// allowed there. Everything is still returned, with the reason, for the editor.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<ReportModelEvaluation>> EvaluateReportModelsAsync(
        CatalogDbContext db, string objectKey, CancellationToken ct)
    {
        var anchors = await db.SubscriberModelTables.AsNoTracking()
            .Where(t => t.ObjectKey == objectKey)
            .OrderBy(t => t.SubscriberKey).ThenBy(t => t.ReportFile).ThenBy(t => t.Name)
            .Take(MaxReportModels)
            .ToListAsync(ct).ConfigureAwait(false);
        if (anchors.Count == 0)
        {
            return [];
        }

        // Everything the anchors' reports declare: every model table (to resolve a reference to another table) and
        // every field (to tell a column from a measure or calculated column), plus the relationships.
        var subscriberKeys = anchors.Select(t => t.SubscriberKey).Distinct(StringComparer.Ordinal).ToList();
        var reportTables = await db.SubscriberModelTables.AsNoTracking()
            .Where(t => subscriberKeys.Contains(t.SubscriberKey))
            .ToListAsync(ct).ConfigureAwait(false);
        var reportFields = await db.SubscriberModelFields.AsNoTracking()
            .Where(f => subscriberKeys.Contains(f.SubscriberKey))
            .OrderBy(f => f.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var reportRelationships = await db.SubscriberModelRelationships.AsNoTracking()
            .Where(r => subscriberKeys.Contains(r.SubscriberKey))
            .OrderBy(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var subscriberNames = (await db.Subscribers.AsNoTracking()
                .Where(s => subscriberKeys.Contains(s.ObjectKey))
                .Select(s => new { s.ObjectKey, s.Name })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(s => s.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var objectKeys = reportTables.Select(t => t.ObjectKey).OfType<string>().Append(objectKey)
            .Distinct(StringComparer.Ordinal).ToList();
        var allowed = await LoadAllowedColumnsAsync(db, objectKeys, ct).ConfigureAwait(false);
        var objectNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in objectKeys.Chunk(KeyChunk))
        {
            var rows = await db.Objects.AsNoTracking()
                .Where(o => chunk.Contains(o.Key))
                .Select(o => new { o.Key, o.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                objectNames[row.Key] = row.Name;
            }
        }

        var result = new List<ReportModelEvaluation>(anchors.Count);
        foreach (var anchor in anchors)
        {
            bool SameReport(string subscriberKey, string reportFile)
                => string.Equals(subscriberKey, anchor.SubscriberKey, StringComparison.Ordinal)
                    && string.Equals(reportFile, anchor.ReportFile, StringComparison.Ordinal);

            var tableKeys = reportTables
                .Where(t => SameReport(t.SubscriberKey, t.ReportFile))
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ObjectKey, StringComparer.OrdinalIgnoreCase);
            var fields = reportFields.Where(f => SameReport(f.SubscriberKey, f.ReportFile)).ToList();

            // The warehouse column a model column name maps to, or the reason it does not.
            string? ColumnProblem(string modelTable, string column, out string? canonical, out string? key)
            {
                canonical = null;
                key = null;
                if (!tableKeys.TryGetValue(modelTable, out key) || key is null)
                {
                    return $"The model table '{modelTable}' is not loaded from a warehouse table the catalog knows.";
                }

                var warehouseName = objectNames.GetValueOrDefault(key, key);
                if (!allowed.TryGetValue(key, out var columns))
                {
                    return $"The model table '{modelTable}' loads from '{warehouseName}', which is not in the semantic layer.";
                }

                canonical = columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Name;
                return canonical is null
                    ? $"'{modelTable}[{column}]' is not an allow-listed column of '{warehouseName}'. A column the report " +
                        "renamed counts as not allowed, since nothing maps it back to its warehouse column."
                    : null;
            }

            var memo = new Dictionary<CatalogSubscriberModelField, string?>();
            string? FieldProblem(CatalogSubscriberModelField field, HashSet<CatalogSubscriberModelField> visiting)
            {
                if (memo.TryGetValue(field, out var known))
                {
                    return known;
                }

                if (string.IsNullOrWhiteSpace(field.Expression))
                {
                    return memo[field] = "It has no DAX expression.";
                }

                // A cycle between definitions names no column the walk has not already checked on its way round.
                if (!visiting.Add(field))
                {
                    return null;
                }

                string? problem = null;
                foreach (var (table, name) in DaxReferences(field.Expression))
                {
                    var owner = table ?? field.TableName;
                    var definition = fields.FirstOrDefault(f =>
                        f.Kind != "column"
                        && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                        && (table is null
                            ? f.Kind == MeasureKind || string.Equals(f.TableName, owner, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(f.TableName, table, StringComparison.OrdinalIgnoreCase)));
                    if (definition is not null)
                    {
                        if (!ReferenceEquals(definition, field) && FieldProblem(definition, visiting) is { } nested)
                        {
                            problem = $"It uses '{definition.Name}', which is withheld. {nested}";
                            break;
                        }

                        continue;
                    }

                    if (ColumnProblem(owner, name, out _, out _) is { } columnProblem)
                    {
                        problem = columnProblem;
                        break;
                    }
                }

                visiting.Remove(field);
                return memo[field] = problem;
            }

            var evaluatedFields = fields
                .Where(f => f.Kind != "column" && string.Equals(f.TableName, anchor.Name, StringComparison.OrdinalIgnoreCase))
                .Select(f => new ReportFieldEvaluation(f, FieldProblem(f, [])))
                .ToList();

            var evaluatedRelationships = new List<ReportRelationshipEvaluation>();
            foreach (var relationship in reportRelationships.Where(r => SameReport(r.SubscriberKey, r.ReportFile)))
            {
                var outgoing = string.Equals(relationship.FromTable, anchor.Name, StringComparison.OrdinalIgnoreCase);
                if (!outgoing && !string.Equals(relationship.ToTable, anchor.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ownColumn = outgoing ? relationship.FromColumn : relationship.ToColumn;
                var otherTable = outgoing ? relationship.ToTable : relationship.FromTable;
                var otherColumn = outgoing ? relationship.ToColumn : relationship.FromColumn;

                string? ownCanonical = null;
                string? otherCanonical = null;
                string? otherKey = null;
                string? problem;
                if (ownColumn is null || otherColumn is null)
                {
                    problem = "The model does not name both of the relationship's columns.";
                }
                else
                {
                    problem = ColumnProblem(anchor.Name, ownColumn, out ownCanonical, out _)
                        ?? ColumnProblem(otherTable, otherColumn, out otherCanonical, out otherKey);
                }

                tableKeys.TryGetValue(otherTable, out var resolvedOther);
                otherKey ??= resolvedOther;
                evaluatedRelationships.Add(new ReportRelationshipEvaluation(
                    relationship, anchor.Name, ownCanonical ?? ownColumn, otherTable, otherCanonical ?? otherColumn,
                    otherKey, otherKey is null ? null : objectNames.GetValueOrDefault(otherKey, otherKey), problem));
            }

            result.Add(new ReportModelEvaluation(
                anchor, subscriberNames.GetValueOrDefault(anchor.SubscriberKey, anchor.SubscriberKey),
                evaluatedFields, evaluatedRelationships));
        }

        return result;
    }

    /// <summary>
    /// One layer table's grounding bundle as describe_semantic_table serves it, or null when the object is unknown or
    /// not in the layer: the table as <see cref="ServeTableAsync"/> serves it, the reports and dashboards that consume
    /// it, and the instructions of the AI context that serves it, so a single call is enough to write SQL against the
    /// table. With <paramref name="closestTo"/>, the examples are the ones close to that question (see
    /// <see cref="EvaluateExamplesAsync"/>), and none when none is.
    /// </summary>
    internal static async Task<SemanticTableDto?> DescribeTableAsync(
        CatalogDbContext db, string key, ExampleQuestion? closestTo, CancellationToken ct)
    {
        if (await ServeTableAsync(db, key, closestTo, ct).ConfigureAwait(false) is not { } table)
        {
            return null;
        }

        // Who consumes the table is object-level (report names and owners), so it names no column and is served
        // whole: it is how an assistant answers "who uses this table" without a raw catalog reader.
        var consumers = await LineageEndpoints.LoadObjectSubscribersAsync(db, key, ct).ConfigureAwait(false);
        var instructions = await LoadContextInstructionsAsync(db, table.Context, ct).ConfigureAwait(false);

        return new SemanticTableDto(
            table.Key, table.ServerRef, table.Database, table.Schema, table.Name, table.Kind,
            table.BusinessName, table.Description, table.Synonyms, table.KeyColumns, table.KeyOrigin, table.Columns,
            table.Joins, table.Measures, table.Examples, table.ReportModels, consumers, table.Context, instructions);
    }

    /// <summary>
    /// One layer table as an assistant writes SQL against it, or null when the object is unknown or not in the layer:
    /// its identity and business context, only its allowed columns, the servable key, joins, measures and examples, the
    /// Power BI models built on it, and the AI context that serves it. A table's description and a question's grounding
    /// (<see cref="GroundingAsync"/>) are both built from it, so what either serves is decided once.
    /// </summary>
    internal static async Task<SemanticGroundingTableDto?> ServeTableAsync(
        CatalogDbContext db, string key, ExampleQuestion? closestTo, CancellationToken ct)
    {
        var obj = await db.Objects.AsNoTracking()
            .Where(o => o.Key == key)
            .Select(o => new { o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.KeyColumns, o.KeyOrigin })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (obj is null)
        {
            return null;
        }

        var allowedByKey = await LoadAllowedColumnsAsync(db, [key], ct).ConfigureAwait(false);
        if (!allowedByKey.TryGetValue(key, out var allowed) || allowed.Count == 0)
        {
            return null;
        }

        var annotation = await db.SemanticObjects.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ObjectKey == key, ct).ConfigureAwait(false);
        var (keyColumns, keyOrigin) = ResolveKey(annotation?.KeyColumns, obj.KeyColumns, obj.KeyOrigin, allowed);
        var keySet = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var columns = allowed
            .Select(c => new SemanticColumnDto(
                c.Ordinal, c.Name, c.DataType, c.Nullable, keySet.Contains(c.Name), c.Description, c.Synonyms))
            .ToList();

        var joins = (await EvaluateJoinsAsync(db, key, obj.Name, ct).ConfigureAwait(false))
            .Where(j => j.Problem is null)
            .Select(j => j.Join)
            .ToList();

        var measures = (await EvaluateMeasuresAsync(db, db.SemanticMeasures.Where(m => m.ObjectKey == key), ct)
                .ConfigureAwait(false))
            .Where(m => m.Problem is null)
            .Select(m => m.ToDto())
            .ToList();

        var examples = (await EvaluateExamplesAsync(db, key, MaxServedExamples, servableOnly: true, closestTo, ct).ConfigureAwait(false))
            .Select(e => new SemanticExampleDto(e.Id, e.Question, e.Sql, e.Provenance, e.ConfirmedUtc))
            .ToList();

        // The Power BI models built on the table, reduced to what may be served; a report whose every definition is
        // withheld is left out entirely rather than listed empty.
        var reportModels = (await EvaluateReportModelsAsync(db, key, ct).ConfigureAwait(false))
            .Select(m => m.ToServedDto())
            .Where(m => m.Measures.Count > 0 || m.CalculatedColumns.Count > 0 || m.Relationships.Count > 0)
            .ToList();

        return new SemanticGroundingTableDto(
            obj.Key, obj.ServerRef, obj.Database, obj.Schema, obj.Name, obj.Kind,
            annotation?.BusinessName, annotation?.Description, SplitSynonyms(annotation?.Synonyms),
            keyColumns, keyOrigin, columns, joins, measures, examples, reportModels, annotation?.AiContext);
    }
}
