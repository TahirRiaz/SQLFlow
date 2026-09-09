using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Query;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The statement to prepare, and where it would run. No token exists yet; this is the ask that mints
/// one.</summary>
public sealed record PrepareQueryRequest(
    string? Sql, string? Reference, string? Kind = null, string? Database = null, string? Pool = null,
    int? MaxRows = null, int? TimeoutSeconds = null);

/// <summary>
/// A prepared query awaiting approval: the exact statement that would run, the one-time token that would run
/// it, and when that token lapses. <see cref="Sql"/> is what a client MUST show a person before redeeming the
/// token; showing something else and redeeming this would be showing one query and running another.
/// </summary>
public sealed record PreparedQueryDto(
    Guid PlanId, string Sql, string Reference, string? Database, int MaxRows, int TimeoutSeconds,
    DateTime ExpiresUtc, string Instruction);

/// <summary>
/// The query surface: a two-step, human-in-the-loop path for running ad-hoc read-only questions against the
/// warehouse.
///
/// The two steps exist to make the confirmation ENFORCED rather than merely requested. A client that could
/// post SQL and rows would come back could always skip asking; here the run endpoint takes a token and never a
/// statement, so the only executable SQL is SQL that was first prepared, returned to the caller, and shown.
/// The plan row is single-use and short-lived, so an approval cannot be replayed, and it records who prepared
/// what, so an executed query is attributable after the fact.
///
/// Both endpoints sit behind <c>ControlPlane:DataOps:Enabled</c> and the <c>operate</c> scope. Everything they
/// run is proved read-only by parsing it, twice: here at prepare, and again on the node before execution.
/// </summary>
public static class QueryEndpoints
{
    /// <summary>How long an approval stays redeemable. Long enough for a person to read the statement and
    /// decide, short enough that a token left in a transcript is not a standing permission.</summary>
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Lapsed plans are swept opportunistically on prepare, so the table cannot grow without bound
    /// and a stale approval cannot be redeemed even if the expiry check were somehow missed.</summary>
    private static readonly TimeSpan PlanRetention = TimeSpan.FromHours(24);

    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/dataops/queries/prepare", PrepareAsync)
            .WithTags("Query").WithName("PrepareQuery");
        group.MapPost("/dataops/queries/{planId:guid}/run", RunAsync)
            .WithTags("Query").WithName("RunPreparedQuery");

        return group;
    }

    /// <summary>
    /// Validates a statement and mints a one-time token for it. Nothing is executed and nothing touches the
    /// datasource: this proves the statement is a single read-only SELECT, records it, and hands back the
    /// exact text for a person to read.
    /// </summary>
    private static async Task<Results<Ok<PreparedQueryDto>, ProblemHttpResult>> PrepareAsync(
        PrepareQueryRequest request, CatalogDbContext db, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (request is null)
        {
            return Problem("A prepare requires a request body.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        if (!options.Value.DataOps.Enabled)
        {
            return Problem(
                "The query surface is part of the data-operations surface, which is not enabled in this " +
                "deployment. Set ControlPlane__DataOps__Enabled=true to turn it on.",
                StatusCodes.Status403Forbidden, "Not enabled");
        }

        DataSourceKind? kind = null;
        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            if (!Enum.TryParse<DataSourceKind>(request.Kind.Trim(), ignoreCase: true, out var parsed))
            {
                return Problem(
                    $"Unknown provider kind '{request.Kind}'. Valid values: MSSQL, AZDB, MySQL, PostgreSQL, Oracle.",
                    StatusCodes.Status400BadRequest, "Invalid request");
            }

            kind = parsed;
        }

        var reference = request.Reference?.Trim() ?? string.Empty;
        if (!ComputeTaskPayload.IsWholeReference(reference))
        {
            return Problem(
                "The datasource must be a whole ${env:...} / ${keyvault:...} reference or an @alias. Inline " +
                "connection strings are not accepted here.",
                StatusCodes.Status400BadRequest, "Invalid request");
        }

        // The same reference gate the compute path uses: a query may only reach a datasource the reviewed git
        // estate already declares, so this surface can never be pointed at a novel connection.
        if (!reference.StartsWith('@'))
        {
            var known = await db.Pipelines.AsNoTracking()
                .AnyAsync(p => p.SourceServer == reference || p.TargetServer == reference, ct)
                .ConfigureAwait(false);
            if (!known)
            {
                return Problem(
                    $"No pipeline in the catalog declares the datasource reference '{reference}'.",
                    StatusCodes.Status404NotFound, "Not found");
            }
        }

        string sql;
        QueryRunRequest bounds;
        try
        {
            // Parsed, not pattern-matched. This is the check that decides the statement is read-only.
            sql = ReadOnlyQueryGuard.Validate(request.Sql ?? string.Empty);
            bounds = new QueryRunRequest
            {
                Sql = sql,
                Database = string.IsNullOrWhiteSpace(request.Database) ? null : request.Database.Trim(),
                MaxRows = request.MaxRows ?? QueryRunRequest.DefaultMaxRows,
                TimeoutSeconds = request.TimeoutSeconds ?? QueryRunRequest.DefaultTimeoutSeconds,
            }.Validate();
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Query refused");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await db.QueryPlans.Where(p => p.PreparedUtc < now - PlanRetention).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var plan = new CatalogQueryPlan
        {
            PlanId = Guid.NewGuid(),
            Sql = sql,
            SourceRef = reference,
            ProviderKind = kind?.ToString(),
            Database = bounds.Database,
            TargetPool = string.IsNullOrWhiteSpace(request.Pool) ? null : request.Pool.Trim(),
            MaxRows = bounds.MaxRows,
            TimeoutSeconds = bounds.TimeoutSeconds,
            PreparedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name,
            PreparedUtc = now,
            ExpiresUtc = now + PlanLifetime,
        };

        db.QueryPlans.Add(plan);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new PreparedQueryDto(
            plan.PlanId, sql, reference, plan.Database, plan.MaxRows, plan.TimeoutSeconds, plan.ExpiresUtc,
            "This query has NOT run. Show the SQL above to the user verbatim and get their agreement, then " +
            "redeem this planId to run it. The token is single-use and expires."));
    }

    /// <summary>
    /// Redeems a plan token and queues the approved query. The token is consumed atomically, so a replay
    /// finds nothing to redeem rather than running the query a second time.
    /// </summary>
    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> RunAsync(
        Guid planId, CatalogDbContext db, IRunDispatcher dispatcher, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.DataOps.Enabled)
        {
            return Problem(
                "The query surface is not enabled in this deployment. Set ControlPlane__DataOps__Enabled=true.",
                StatusCodes.Status403Forbidden, "Not enabled");
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Consume atomically: the UPDATE only matches a plan that is unconsumed and unexpired, so two
        // concurrent redemptions cannot both win and a replay cannot run the query again.
        var consumed = await db.QueryPlans
            .Where(p => p.PlanId == planId && p.ConsumedUtc == null && p.ExpiresUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ConsumedUtc, now), ct)
            .ConfigureAwait(false);

        if (consumed == 0)
        {
            var existing = await db.QueryPlans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == planId, ct).ConfigureAwait(false);

            return existing switch
            {
                null => Problem(
                    $"No prepared query has the id {planId}. Prepare the query first, show it to the user, " +
                    "then redeem the token this returns.",
                    StatusCodes.Status404NotFound, "Not found"),
                { ConsumedUtc: not null } => Problem(
                    "That prepared query has already been run. A plan is single-use: prepare it again to run " +
                    "it a second time, so each execution is separately approved.",
                    StatusCodes.Status409Conflict, "Already used"),
                _ => Problem(
                    "That prepared query has expired. An approval is a decision about a moment, not a " +
                    "standing permission: prepare it again.",
                    StatusCodes.Status410Gone, "Expired"),
            };
        }

        var plan = await db.QueryPlans.AsNoTracking()
            .FirstAsync(p => p.PlanId == planId, ct).ConfigureAwait(false);

        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.RunQuery,
            SourceRef = plan.SourceRef,
            ProviderKind = plan.ProviderKind is null ? null : Enum.Parse<DataSourceKind>(plan.ProviderKind),
            Database = plan.Database,
            Sql = plan.Sql,
            MaxRows = plan.MaxRows,
            TimeoutSeconds = plan.TimeoutSeconds,
        };

        try
        {
            payload.Validate();
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Invalid compute task");
        }

        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(
                payload.Operation, payload.SourceRef, plan.ProviderKind, payload.ToJson(),
                plan.TargetPool, user.FindFirst("sub")?.Value ?? user.Identity?.Name),
            ct).ConfigureAwait(false);

        await db.QueryPlans.Where(p => p.PlanId == planId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.TaskId, taskId), ct)
            .ConfigureAwait(false);

        return TypedResults.Accepted(
            $"/api/v1/datasources/tasks/{taskId}", new ComputeTaskAccepted(taskId, RunStatuses.Queued));
    }

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
