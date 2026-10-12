using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The semantic layer's example queries, the saved answers the assistant reuses: list and search them, read one, and
/// remove one that should no longer be precedent. Answers are added by a person confirming one in chat
/// (<see cref="QuestionExampleEndpoints"/>), by a report, or by an approved AI context's file; none is edited here.
/// <para>
/// It lives under the "admin" scope: removing an answer changes precedent other people's answers are grounded in, and a
/// trusted example auto-runs with no approval. An example an AI context gives is removed from its file instead, so the
/// file and what is served never disagree.
/// </para>
/// </summary>
public static class SemanticExampleAdminEndpoints
{
    public static RouteGroupBuilder MapSemanticExampleAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var examples = group.MapGroup("/powerai/semantic-layer/examples").WithTags("PowerAI");
        examples.MapGet(string.Empty, ListAsync).WithName("ListSemanticLayerExamples");
        examples.MapGet("/{id:long}", GetAsync).WithName("GetSemanticLayerExample");
        examples.MapDelete("/{id:long}", DeleteAsync).WithName("DeleteSemanticLayerExample");

        return group;
    }

    /// <summary>Saved answers, newest first, optionally narrowed to those whose question or SQL contains every word of
    /// <paramref name="search"/>.</summary>
    private static async Task<Ok<PagedResult<SemanticExampleAdminDto>>> ListAsync(
        CatalogDbContext db, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.SemanticExamples.AsNoTracking();

        var term = SearchQuery.Parse(search);
        if (term is not null)
        {
            foreach (var token in term.Tokens)
            {
                var t = token;
                query = query.Where(e => e.Question.Contains(t) || e.Sql.Contains(t));
            }
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(e => e.ConfirmedUtc).ThenByDescending(e => e.Id)
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        // Evaluated one at a time on the page's own rows (at most the page size): each check reads the column
        // policy, and the whole point of showing it is that a column denied after an answer was saved withholds it.
        var items = new List<SemanticExampleAdminDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add((await SemanticLayer.ExampleEvaluation.OfAsync(db, row, ct).ConfigureAwait(false)).ToAdminDto());
        }

        return TypedResults.Ok(new PagedResult<SemanticExampleAdminDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<SemanticExampleAdminDto>, ProblemHttpResult>> GetAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var example = await db.SemanticExamples.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        return example is null
            ? NotFound(id)
            : TypedResults.Ok((await SemanticLayer.ExampleEvaluation.OfAsync(db, example, ct).ConfigureAwait(false)).ToAdminDto());
    }

    /// <summary>
    /// Corrects a saved answer. The question and SQL are what the duplicate hash is OF, so it is recomputed, and an
    /// edit that would make this answer identical to another saved one is refused rather than silently merged. When
    /// the SQL changes, the objects it reads are resolved again from its tables (the old keys described a different
    /// query), which also moves it to the Examples tab of the tables it now reads; an unchanged SQL keeps the keys it
    /// was stored with, which may have come from lineage the SQL alone cannot reproduce. A blank datasource is worked
    /// out from those objects, the same inference confirming uses. The editor becomes the person standing behind the
    /// answer, because a trusted example auto-runs on the strength of someone having checked exactly this SQL.
    /// </summary>
    /// <summary>Deletes a saved answer a person confirmed or a report gave. Later similar questions stop finding it, and
    /// it can no longer be auto-run. One an AI context gives is refused: removing it from the context's file is what
    /// removes it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var context = await db.SemanticExamples.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.AiContext })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (context is null)
        {
            return NotFound(id);
        }

        if (context.AiContext is not null)
        {
            return TypedResults.Problem(
                detail: $"This answer is given by AI context '{context.AiContext}'. Remove it from that context's file; the "
                    + "next sync stops serving it.",
                statusCode: StatusCodes.Status409Conflict, title: "Given by an AI context");
        }

        var deleted = await db.SemanticExamples.Where(e => e.Id == id && e.AiContext == null)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0 ? NotFound(id) : TypedResults.NoContent();
    }

    private static ProblemHttpResult NotFound(long id)
        => TypedResults.Problem(
            detail: $"No saved answer with id '{id.ToString(CultureInfo.InvariantCulture)}'.",
            statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
