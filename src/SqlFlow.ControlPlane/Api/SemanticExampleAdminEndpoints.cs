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
/// An edit to a saved answer.
/// </summary>
/// <param name="Question">The question, as a person would type it.</param>
/// <param name="Sql">The query that answers it; refused unless it is a single read-only SELECT over allow-listed
/// columns, exactly as a confirmation is.</param>
/// <param name="SourceRef">The datasource to run it against, or blank to work it out from the tables the query reads.</param>
public sealed record UpdateSemanticExampleRequest(string? Question, string? Sql, string? SourceRef);

/// <summary>
/// Curating the semantic layer's example queries: the saved answers the PowerAI assistant reuses. List and search them,
/// correct one, or delete one that should no longer be precedent. Confirming stays the only way an answer is ADDED
/// (<see cref="QuestionExampleEndpoints"/>); this surface, part of the semantic layer editor, only curates what is stored.
/// <para>
/// It lives under the "admin" scope, not "operate" like confirming. Confirming adds a pair a person just checked in
/// their own conversation; editing or deleting rewrites precedent other people's answers are grounded in, and a
/// trusted example auto-runs with no approval, so changing one is curation of the layer rather than a user's own verdict.
/// </para>
/// <para>
/// An edit goes through the same validation a confirmation does (<see cref="QuestionExampleEndpoints.QuestionProblem"/>,
/// <see cref="QuestionExampleEndpoints.ValidateSqlAsync"/>, <see cref="QuestionExampleEndpoints.SourceRefProblemAsync"/>),
/// so the store cannot come to hold anything through this door that the confirm door would refuse.
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
        examples.MapPut("/{id:long}", UpdateAsync).WithName("UpdateSemanticLayerExample");
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
    private static async Task<Results<Ok<SemanticExampleAdminDto>, ProblemHttpResult>> UpdateAsync(
        long id, UpdateSemanticExampleRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        var question = QuestionExampleEndpoints.NormalizeQuestion(request.Question);
        if (QuestionExampleEndpoints.QuestionProblem(question) is { } questionProblem)
        {
            return Invalid(questionProblem);
        }

        string sql;
        try
        {
            sql = await QuestionExampleEndpoints.ValidateSqlAsync(db, request.Sql, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Query refused");
        }

        var sourceRef = SemanticLayer.TrimToNull(request.SourceRef);
        if (sourceRef is not null
            && await QuestionExampleEndpoints.SourceRefProblemAsync(db, sourceRef, ct).ConfigureAwait(false) is { } sourceRefProblem)
        {
            return sourceRefProblem;
        }

        var example = await db.SemanticExamples.AsTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (example is null)
        {
            return NotFound(id);
        }

        var hash = SemanticExampleHash.Compute(question, sql);
        if (await IsDuplicateAsync(db, hash, id, ct).ConfigureAwait(false))
        {
            return Duplicate();
        }

        // Compared through the hash's own normalization (case and whitespace folded), so reformatting the SQL is not
        // mistaken for a different query whose objects need resolving again.
        var sqlChanged = !string.Equals(
            SemanticExampleHash.Compute(string.Empty, example.Sql),
            SemanticExampleHash.Compute(string.Empty, sql),
            StringComparison.Ordinal);
        if (sqlChanged)
        {
            example.ObjectKeys = QuestionExampleEndpoints.JoinObjectKeys(
                await DatasourceInference.ObjectKeysFromSqlAsync(db, sql, ct).ConfigureAwait(false));
        }

        example.SourceRef = sourceRef
            ?? (await DatasourceInference.InferAsync(
                db, sql, QuestionExampleEndpoints.SplitObjectKeys(example.ObjectKeys), ct).ConfigureAwait(false)).Reference;
        example.Question = question;
        example.Sql = sql;
        example.ContentHash = hash;
        example.Provenance = SemanticExampleProvenance.UserConfirmed;
        example.ConfirmedBy = SemanticLayer.Actor(user);
        example.ConfirmedUtc = clock.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The duplicate check above and the save are not atomic: the unique index on the hash is what finally
            // decides, and a loss to a concurrent write of the same pair is reported as the conflict it is.
            db.ChangeTracker.Clear();
            if (await IsDuplicateAsync(db, hash, id, ct).ConfigureAwait(false))
            {
                return Duplicate();
            }

            throw;
        }

        return TypedResults.Ok((await SemanticLayer.ExampleEvaluation.OfAsync(db, example, ct).ConfigureAwait(false)).ToAdminDto());
    }

    /// <summary>Deletes a saved answer. Later similar questions stop finding it, and it can no longer be auto-run.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var deleted = await db.SemanticExamples.Where(e => e.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0 ? NotFound(id) : TypedResults.NoContent();
    }

    private static Task<bool> IsDuplicateAsync(CatalogDbContext db, string hash, long id, CancellationToken ct)
        => db.SemanticExamples.AsNoTracking().AnyAsync(e => e.ContentHash == hash && e.Id != id, ct);

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    private static ProblemHttpResult NotFound(long id)
        => TypedResults.Problem(
            detail: $"No saved answer with id '{id.ToString(CultureInfo.InvariantCulture)}'.",
            statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Duplicate()
        => TypedResults.Problem(
            detail: "Another saved answer already has this question and query. Delete one of them instead of making "
                + "them identical.",
            statusCode: StatusCodes.Status409Conflict, title: "Duplicate saved answer");
}
