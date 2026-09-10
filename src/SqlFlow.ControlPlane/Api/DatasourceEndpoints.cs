using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One datasource the estate declares: the connection REFERENCE (a whole <c>${...}</c> / <c>@alias</c>
/// token, or the hashed identity of an inline literal), its best-effort provider kind read from the flow
/// definitions, whether a worker can resolve it for ad-hoc compute (only whole references can travel), and how
/// many active pipelines read from / write to it.</summary>
public sealed record DatasourceDto(
    string Reference, string? Kind, bool Resolvable, int SourcePipelines, int TargetPipelines);

/// <summary>The body that requests an ad-hoc compute task against a datasource. References only, exactly like a
/// run trigger: the executing node resolves the credential from its own environment, and a secret is never
/// accepted here. <c>Reference</c> must be a whole <c>${...}</c> reference the estate already declares, or an
/// <c>@alias</c> from the full-mode registry; <c>Kind</c> names the provider for a <c>${...}</c> reference
/// (default SQL Server); <c>Pool</c> routes the task to a node that can reach the source. The remaining fields
/// are the operation's arguments, validated per operation at this trust boundary.</summary>
public sealed record ComputeTaskRequest(
    string? Reference, string? Operation, string? Kind = null, string? Pool = null,
    string? Database = null, string? Schema = null, string? ObjectName = null, string? NameLike = null,
    string? SearchTerm = null, bool IncludeTables = true, bool IncludeViews = true, bool IncludeSystem = false,
    int Offset = 0, int Limit = 200, int? SampleSize = null, int MaxKeyColumns = 4, int MaxCandidates = 5,
    bool VerifyCandidates = true, bool TrustDeclaredKeys = true,
    IReadOnlyList<string>? Columns = null, string? CompareMode = null, string? LinkedServer = null, string? BaselineDatabase = null,
    string? BaselineSchema = null, string? BaselineObjectName = null,
    IReadOnlyList<string>? KeyExpressions = null, IReadOnlyList<string>? CompareColumns = null,
    string? ExcludeColumnPattern = null, string? Where = null, int SampleRows = 5);

/// <summary>The accepted-task acknowledgement: the minted task id and its queued status. The task executes
/// asynchronously; poll <c>GET /api/v1/datasources/tasks/{taskId}</c> (the <c>Location</c> header) for the
/// result, optionally long-polling with <c>waitMs</c>.</summary>
public sealed record ComputeTaskAccepted(Guid TaskId, string Status);

/// <summary>A compute task as the task list shows it: everything but the (possibly large) result body.
/// <see cref="Target"/> is the object an object-scoped task (introspect, detect a unique key, check for
/// duplicates) ran against, as <c>[db.]schema.name</c>, so a task history names WHAT was inspected; null for
/// list/search/test tasks.</summary>
public sealed record ComputeTaskSummaryDto(
    Guid TaskId, string Operation, string SourceRef, string? ProviderKind, string? Pool, string Status,
    string? RequestedBy, DateTime EnqueuedUtc, DateTime? StartUtc, DateTime? EndUtc, string? ClaimedByNode,
    DateTime? CancelRequestedUtc, string? Error, bool HasResult, string? Target = null);

/// <summary>A single compute task with its result: <see cref="Result"/> is the operation's JSON document
/// (shape depends on the operation), present once the task succeeded. <see cref="Target"/> as on the summary,
/// so a failed task (no result to read a name from) still says what it ran against.</summary>
public sealed record ComputeTaskDto(
    Guid TaskId, string Operation, string SourceRef, string? ProviderKind, string? Pool, string Status,
    string? RequestedBy, DateTime EnqueuedUtc, DateTime? StartUtc, DateTime? EndUtc, string? ClaimedByNode,
    DateTime? CancelRequestedUtc, string? Error, JsonElement? Result, string? Target = null);

/// <summary>
/// What the data-operations surface offers in THIS deployment: whether the feature switch is on, the
/// read-only checks available, and the linked servers a baseline comparison may name. A client reads this
/// before offering the surface, so a deployment with the switch off shows an explanation rather than a
/// failing button.
/// </summary>
public sealed record DataOpsCapabilitiesDto(
    bool Enabled, string DisabledReason, IReadOnlyList<string> Operations,
    IReadOnlyList<string> ComparisonLinkedServers, string? DefaultLinkedServer,
    IReadOnlyList<string> CompareModes, string QuerySurface);

/// <summary>
/// The datasource surface: the estate's datasources as the catalog knows them, and the ad-hoc compute queue
/// that runs live inspections against them (list databases/schemas/tables, search, introspect, test the
/// connection, detect a unique key). Reads are catalog-only; the compute POST is privileged ("operate") and
/// enqueues onto the durable compute queue, where whichever worker node can reach the source executes it: the
/// control plane itself NEVER opens a connection to a datasource here, preserving the product's credential
/// model (references travel, nodes resolve).
/// </summary>
public static class DatasourceEndpoints
{
    /// <summary>The longest a GET may long-poll for a result before answering with the current state.</summary>
    private const int MaxWaitMs = 20_000;

    /// <summary>How often the long-poll rechecks the task row.</summary>
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The most distinct references the list endpoint resolves a provider kind for by parsing one flow
    /// definition each; beyond this (a pathological estate) the kind is simply reported unknown.</summary>
    private const int MaxKindLookups = 100;

    public static RouteGroupBuilder MapDatasourceReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/datasources", ListDatasourcesAsync)
            .WithTags("Datasources").WithName("ListDatasources");
        group.MapGet("/datasources/tasks", ListTasksAsync)
            .WithTags("Datasources").WithName("ListComputeTasks");
        group.MapGet("/datasources/tasks/{taskId:guid}", GetTaskAsync)
            .WithTags("Datasources").WithName("GetComputeTask");
        group.MapGet("/dataops/capabilities", GetDataOpsCapabilities)
            .WithTags("Datasources").WithName("GetDataOpsCapabilities");

        return group;
    }

    public static RouteGroupBuilder MapDatasourceComputeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/datasources/tasks", TriggerTaskAsync)
            .WithTags("Datasources").WithName("TriggerComputeTask");
        group.MapPost("/datasources/tasks/{taskId:guid}/cancel", CancelTaskAsync)
            .WithTags("Datasources").WithName("CancelComputeTask");

        return group;
    }

    /// <summary>
    /// What the data-operations surface offers here. Always answers, switch on or off: a client needs to know
    /// the feature is disabled in order to say so.
    /// </summary>
    private static Ok<DataOpsCapabilitiesDto> GetDataOpsCapabilities(IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dataOps = options.Value.DataOps;

        var reason = dataOps.Enabled
            ? ""
            : "The data-operations surface is off. Set ControlPlane__DataOps__Enabled=true to enable ad-hoc " +
              "queries, the duplicate-key check, and the baseline comparison.";

        // DERIVED from the operation set, never a hand-kept copy of it. A literal list here silently goes
        // stale the moment an operation is added, and the failure is invisible from the server: the endpoint
        // answers 200, and a reading client concludes the missing capability does not exist and tells the user
        // the product cannot do it. That is exactly how runQuery shipped, deployed, and stayed unusable.
        var operations = ComputeOperations.All.Where(ComputeOperations.IsDataOps).ToArray();

        return TypedResults.Ok(new DataOpsCapabilitiesDto(
            dataOps.Enabled, reason, operations,
            dataOps.Comparison.AllowedLinkedServers().ToArray(),
            string.IsNullOrWhiteSpace(dataOps.Comparison.DefaultLinkedServer)
                ? null
                : dataOps.Comparison.DefaultLinkedServer.Trim(),
            Enum.GetNames<BaselineCompareMode>(),
            dataOps.Enabled
                ? "Ad-hoc queries run in two steps: prepare_query validates a SELECT and returns it with a " +
                  "one-time token WITHOUT running anything, then run_query redeems that token after a person " +
                  "has seen and approved the exact statement."
                : ""));
    }

    /// <summary>
    /// The estate's datasources: every distinct connection reference the active pipelines declare as a source
    /// or target, with usage counts and a best-effort provider kind read from one flow definition per
    /// reference. The file-system pseudo-endpoint and inline-literal identities are included for completeness
    /// but flagged unresolvable (no worker can turn them back into a connection), so the GUI can show them
    /// grayed out rather than hiding them.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DatasourceDto>>> ListDatasourcesAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Select(p => new { p.SourceServer, p.TargetServer, p.LastSeenUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var references = new Dictionary<string, (int Source, int Target)>(StringComparer.Ordinal);
        foreach (var pipeline in pipelines)
        {
            if (!string.IsNullOrWhiteSpace(pipeline.SourceServer) && pipeline.SourceServer != ServerIdentity.FileSystem)
            {
                var counts = references.GetValueOrDefault(pipeline.SourceServer);
                references[pipeline.SourceServer] = (counts.Source + 1, counts.Target);
            }

            if (!string.IsNullOrWhiteSpace(pipeline.TargetServer) && pipeline.TargetServer != ServerIdentity.FileSystem)
            {
                var counts = references.GetValueOrDefault(pipeline.TargetServer);
                references[pipeline.TargetServer] = (counts.Source, counts.Target + 1);
            }
        }

        var items = new List<DatasourceDto>(references.Count);
        var kindLookups = 0;
        foreach (var (reference, counts) in references.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? kind = null;
            if (kindLookups < MaxKindLookups)
            {
                kindLookups++;
                kind = await ResolveKindAsync(db, reference, ct).ConfigureAwait(false);
            }

            items.Add(new DatasourceDto(
                reference, kind, ComputeTaskPayload.IsWholeReference(reference), counts.Source, counts.Target));
        }

        return TypedResults.Ok<IReadOnlyList<DatasourceDto>>(items);
    }

    /// <summary>
    /// Best-effort provider kind for a reference: parse the newest active flow definition that declares it and
    /// find the connection whose reference maps to the same server identity. The definitions serialize each
    /// connection as an object carrying <c>connectionRef</c> and <c>kind</c> (camelCase, enum as name), so a
    /// recursive scan finds it regardless of the flow kind's document shape. Null when nothing matches (the
    /// caller then lets the task request name the kind explicitly, defaulting to SQL Server).
    /// </summary>
    private static async Task<string?> ResolveKindAsync(CatalogDbContext db, string reference, CancellationToken ct)
    {
        // A handful of the newest definitions, not just one: the newest flow using the reference may be a kind
        // whose document carries no connection object (an sp flow), while an older sibling names the provider.
        var definitions = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && (p.SourceServer == reference || p.TargetServer == reference)
                && p.DefinitionJson != "")
            .OrderByDescending(p => p.LastSeenUtc)
            .Select(p => p.DefinitionJson)
            .Take(5)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var definitionJson in definitions)
        {
            try
            {
                using var document = JsonDocument.Parse(definitionJson);
                if (FindKindForReference(document.RootElement, reference) is { } kind)
                {
                    return kind;
                }
            }
            catch (JsonException)
            {
                // A definition that fails to parse (a sync predating the JSON projection) simply yields no kind
                // from this pipeline; the datasource itself is still listed.
            }
        }

        return null;
    }

    private static string? FindKindForReference(JsonElement element, string reference)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("connectionRef", out var connectionRef)
                    && connectionRef.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && ServerIdentity.From(connectionRef.GetString()!) == reference)
                {
                    return kind.GetString();
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (FindKindForReference(property.Value, reference) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindKindForReference(item, reference) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> TriggerTaskAsync(
        ComputeTaskRequest request, CatalogDbContext db, IRunDispatcher dispatcher,
        IOptions<ControlPlaneOptions> options, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null)
        {
            return Problem("A compute task requires a request body.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        ArgumentNullException.ThrowIfNull(options);
        var dataOps = options.Value.DataOps;
        var operation = request.Operation?.Trim() ?? string.Empty;

        // The feature switch is checked BEFORE anything else about the request, so a deployment with the
        // surface off answers the same way for a well-formed ask and a malformed one: not enabled here.
        if (ComputeOperations.IsDataOps(operation) && !dataOps.Enabled)
        {
            return Problem(
                $"The '{operation}' operation is part of the data-operations surface, which is not enabled in " +
                "this deployment. Set ControlPlane__DataOps__Enabled=true to turn it on.",
                StatusCodes.Status403Forbidden, "Not enabled");
        }

        BaselineCompareMode? compareMode = null;
        if (!string.IsNullOrWhiteSpace(request.CompareMode))
        {
            if (!Enum.TryParse<BaselineCompareMode>(request.CompareMode.Trim(), ignoreCase: true, out var parsedMode))
            {
                return Problem(
                    $"Unknown compareMode '{request.CompareMode}'. Valid values: " +
                    $"{string.Join(", ", Enum.GetNames<BaselineCompareMode>())}.",
                    StatusCodes.Status400BadRequest, "Invalid request");
            }

            compareMode = parsedMode;
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

        if (request.Pool is { Length: > 128 } || (request.Pool?.Any(char.IsControl) ?? false))
        {
            return Problem("pool must be at most 128 characters with no control characters.",
                StatusCodes.Status400BadRequest, "Invalid request");
        }

        var payload = new ComputeTaskPayload
        {
            Operation = request.Operation?.Trim() ?? string.Empty,
            SourceRef = request.Reference?.Trim() ?? string.Empty,
            ProviderKind = kind,
            Database = Trimmed(request.Database),
            Schema = Trimmed(request.Schema),
            ObjectName = Trimmed(request.ObjectName),
            NameLike = Trimmed(request.NameLike),
            SearchTerm = Trimmed(request.SearchTerm),
            IncludeTables = request.IncludeTables,
            IncludeViews = request.IncludeViews,
            IncludeSystem = request.IncludeSystem,
            Offset = request.Offset,
            Limit = request.Limit,
            SampleSize = request.SampleSize,
            MaxKeyColumns = request.MaxKeyColumns,
            MaxCandidates = request.MaxCandidates,
            VerifyCandidates = request.VerifyCandidates,
            TrustDeclaredKeys = request.TrustDeclaredKeys,
            Columns = request.Columns,
            CompareMode = compareMode,
            // A comparison that names no linked server takes the deployment's default, so the common case is
            // one field shorter and a client never has to know the estate's linked-server name.
            LinkedServer = Trimmed(request.LinkedServer)
                ?? (compareMode is null ? null : Trimmed(dataOps.Comparison.DefaultLinkedServer)),
            BaselineDatabase = Trimmed(request.BaselineDatabase),
            BaselineSchema = Trimmed(request.BaselineSchema),
            BaselineObjectName = Trimmed(request.BaselineObjectName),
            KeyExpressions = request.KeyExpressions,
            CompareColumns = request.CompareColumns,
            ExcludeColumnPattern = Trimmed(request.ExcludeColumnPattern),
            Where = Trimmed(request.Where),
            SampleRows = request.SampleRows,
        };

        try
        {
            payload.Validate();

            // The comparison's linked server is a POLICY decision the control plane owns, so it is checked
            // here against the configured allowlist rather than on the node, which cannot see the policy.
            if (payload.Operation == ComputeOperations.CompareBaseline)
            {
                payload.ToComparisonRequest(dataOps.Comparison.AllowedLinkedServers());
            }

            if (payload.Operation == ComputeOperations.CompareBaseline
                && dataOps.Comparison.Databases.Count > 0
                && !dataOps.Comparison.Databases.Contains(payload.BaselineDatabase!, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(
                    $"The baseline database '{payload.BaselineDatabase}' is not allowlisted for comparison. " +
                    $"Configured: {string.Join(", ", dataOps.Comparison.Databases)}.",
                    StatusCodes.Status400BadRequest, "Invalid compute task");
            }
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Invalid compute task");
        }

        // The reference gate: a ${...} reference must already be declared by the estate (some pipeline reads or
        // writes through it), so this surface can only inspect datasources the reviewed git estate names; it can
        // never point a worker at a novel connection. An @alias is exempt: it resolves only against the
        // full-mode registry (flw.DataSource) on the node, which is itself a curated allowlist.
        if (!payload.SourceRef.StartsWith('@'))
        {
            var known = await db.Pipelines.AsNoTracking()
                .AnyAsync(p => p.SourceServer == payload.SourceRef || p.TargetServer == payload.SourceRef, ct)
                .ConfigureAwait(false);
            if (!known)
            {
                return Problem(
                    $"No pipeline in the catalog declares the datasource reference '{payload.SourceRef}'. Ad-hoc " +
                    "compute is limited to datasources the estate already uses (or an @alias from the registry).",
                    StatusCodes.Status404NotFound, "Not found");
            }
        }

        var requestedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(
                payload.Operation, payload.SourceRef, kind?.ToString(), payload.ToJson(),
                Trimmed(request.Pool), requestedBy),
            ct).ConfigureAwait(false);

        return TypedResults.Accepted(
            $"/api/v1/datasources/tasks/{taskId}", new ComputeTaskAccepted(taskId, RunStatuses.Queued));
    }

    /// <summary>
    /// One task, optionally long-polled: <c>waitMs</c> (capped at 20s) holds the request open until the task
    /// reaches a terminal state, so the GUI gets an interactive answer in one round trip instead of hammering
    /// the endpoint. Stale tasks are expired lazily here (a queued task nobody claims, a running task whose
    /// node is lost), so an ask always terminates in bounded time.
    /// </summary>
    private static async Task<Results<Ok<ComputeTaskDto>, ProblemHttpResult>> GetTaskAsync(
        Guid taskId, CatalogDbContext db, TimeProvider clock, int? waitMs, CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(Math.Clamp(waitMs ?? 0, 0, MaxWaitMs));
        var deadline = clock.GetUtcNow() + wait;

        while (true)
        {
            var task = await db.ComputeTasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TaskId == taskId, ct).ConfigureAwait(false);
            if (task is null)
            {
                return Problem($"No compute task '{taskId}'.", StatusCodes.Status404NotFound, "Not found");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (IsStale(task, now))
            {
                await ComputeTaskStore.ExpireAsync(db, now, ct).ConfigureAwait(false);
                continue; // reload the (now terminal) row
            }

            if (RunStatuses.IsTerminal(task.Status) || clock.GetUtcNow() >= deadline)
            {
                return TypedResults.Ok(ToDetailDto(task));
            }

            await Task.Delay(WaitPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<Ok<PagedResult<ComputeTaskSummaryDto>>> ListTasksAsync(
        CatalogDbContext db, TimeProvider clock, int? page, int? pageSize, string? status, string? reference,
        string? operation, CancellationToken ct)
    {
        // The list is the operator's task history; sweep stale tasks first so it never shows a zombie.
        await ComputeTaskStore.ExpireAsync(db, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.ComputeTasks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(reference))
        {
            query = query.Where(t => t.SourceRef == reference);
        }

        if (!string.IsNullOrWhiteSpace(operation))
        {
            query = query.Where(t => t.Operation == operation);
        }

        var ordered = query.OrderByDescending(t => t.EnqueuedUtc).ThenByDescending(t => t.TaskId);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        // Projected without ResultJson (a result can be megabytes); ArgumentsJson rides along so the target
        // object can be named in memory, page-bounded.
        var rows = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(t => new
            {
                t.TaskId, t.Operation, t.SourceRef, t.ProviderKind, t.TargetPool, t.Status, t.RequestedBy,
                t.EnqueuedUtc, t.StartUtc, t.EndUtc, t.ClaimedByNode, t.CancelRequestedUtc, t.Error,
                HasResult = t.ResultJson != null, t.ArgumentsJson,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var items = rows
            .Select(t => new ComputeTaskSummaryDto(
                t.TaskId, t.Operation, t.SourceRef, t.ProviderKind, t.TargetPool, t.Status, t.RequestedBy,
                t.EnqueuedUtc, t.StartUtc, t.EndUtc, t.ClaimedByNode, t.CancelRequestedUtc, t.Error,
                t.HasResult, TargetFromArguments(t.Operation, t.ArgumentsJson)))
            .ToList();
        return TypedResults.Ok(new PagedResult<ComputeTaskSummaryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ComputeTaskAccepted>, Accepted<ComputeTaskAccepted>, ProblemHttpResult>> CancelTaskAsync(
        Guid taskId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var outcome = await dispatcher.CancelComputeTaskAsync(db, taskId, ct).ConfigureAwait(false);
        return outcome switch
        {
            // A still-queued task is cancelled synchronously (it never ran): 200 with the terminal status.
            CancelOutcome.Cancelled => TypedResults.Ok(new ComputeTaskAccepted(taskId, RunStatuses.Cancelled)),
            // A running task's cancel is asynchronous: the owning node aborts the in-flight query. 202 with a
            // transitional status; poll the task for the terminal outcome.
            CancelOutcome.CancelRequested => TypedResults.Accepted(
                $"/api/v1/datasources/tasks/{taskId}", new ComputeTaskAccepted(taskId, "cancelling")),
            CancelOutcome.NotFound => Problem(
                $"No compute task '{taskId}'.", StatusCodes.Status404NotFound, "Not found"),
            _ => Problem(
                $"Compute task '{taskId}' has already finished and cannot be cancelled.",
                StatusCodes.Status409Conflict, "Conflict"),
        };
    }

    private static bool IsStale(CatalogComputeTask task, DateTime nowUtc)
        => (task.Status == RunStatuses.Queued && task.EnqueuedUtc < nowUtc - ComputeTaskStore.QueuedExpiry)
           || (task.Status == RunStatuses.Running && task.StartUtc is { } start
               && start < nowUtc - ComputeTaskStore.RunningExpiry);

    private static ComputeTaskDto ToDetailDto(CatalogComputeTask task)
    {
        JsonElement? result = null;
        if (task.ResultJson is not null)
        {
            // The result was serialized by the executor and stored verbatim; it re-parses here so the API
            // answers with a JSON document, not a double-encoded string.
            result = JsonSerializer.Deserialize<JsonElement>(task.ResultJson);
        }

        return new ComputeTaskDto(
            task.TaskId, task.Operation, task.SourceRef, task.ProviderKind, task.TargetPool, task.Status,
            task.RequestedBy, task.EnqueuedUtc, task.StartUtc, task.EndUtc, task.ClaimedByNode,
            task.CancelRequestedUtc, task.Error, result, TargetFromArguments(task.Operation, task.ArgumentsJson));
    }

    /// <summary>The <c>[db.]schema.name</c> an object-scoped task targets, parsed from its stored arguments
    /// through the payload's own (validating) reader; null for non-object operations and for a row whose
    /// arguments no longer validate (a legacy or hand-edited row must not break the listing).</summary>
    private static string? TargetFromArguments(string operation, string argumentsJson)
    {
        if (operation is not (ComputeOperations.IntrospectObject or ComputeOperations.DetectUniqueKey))
        {
            return null;
        }

        try
        {
            var payload = ComputeTaskPayload.FromJson(argumentsJson);
            if (string.IsNullOrWhiteSpace(payload.Schema) || string.IsNullOrWhiteSpace(payload.ObjectName))
            {
                return null;
            }

            return string.Join(".", new[] { payload.Database, payload.Schema, payload.ObjectName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        catch (SqlFlowException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
