using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The GitHub half of a trigger: the repository, its pinned id, the ref, the workflow and the environment a job's
/// token must state.</summary>
public sealed record ExternalTriggerGitHubDto(string? Repository, string? RepositoryId, string? Ref, string? Workflow, string? Environment);

/// <summary>The latest request a trigger made: when, where it stands and how it ended.</summary>
public sealed record ExternalTriggerLastRequestDto(Guid Id, string State, string? Conclusion, DateTime CreatedUtc, DateTime? CompletedUtc);

/// <summary>
/// One external trigger as the External triggers page shows it: what it is and may do, how it proves who it is (for a key
/// trigger only the key's prefix and expiry), whether the configuration sets it, when and by whom it was last used, and its
/// latest request.
/// </summary>
public sealed record ExternalTriggerDto(
    Guid Id, string Name, string? Description, string Kind, string RepoName, bool AllSchedules, IReadOnlyList<string> Schedules,
    ExternalTriggerGitHubDto? GitHub, string? KeyPrefix, DateTime? KeyExpiresUtc, bool Enabled, string? ConfiguredBy, string? CreatedBy,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? LastUsedUtc, string? LastUsedDetail, ExternalTriggerLastRequestDto? LastRequest);

/// <summary>Body of creating or changing a trigger. <see cref="KeyExpiresInDays"/> applies to a key trigger's new key only
/// (null or 0: it never expires).</summary>
public sealed record ExternalTriggerWriteRequest(
    string? Name, string? Kind, string? RepoName, string? Description, bool? AllSchedules, IReadOnlyList<string>? Schedules,
    ExternalTriggerGitHubDto? GitHub, int? KeyExpiresInDays = null);

/// <summary>A trigger as written, with what is worth knowing about it (<see cref="Warnings"/>: a schedule the repository
/// does not have yet, a GitHub trigger that trusts every workflow or ref), and for a new key trigger or a rotated key the
/// key itself, shown exactly once.</summary>
public sealed record ExternalTriggerWritten(ExternalTriggerDto Trigger, IReadOnlyList<string> Warnings, string? Key = null);

/// <summary>Body of rotating a key: how long the new key lives (null or 0: it never expires).</summary>
public sealed record RotateTriggerKeyRequest(int? ExpiresInDays = null);

/// <summary>What a caller needs to set a trigger up: whether triggers are on, the GitHub issuer, audience and server this
/// control plane checks tokens against, where a workflow finds the action, and how long a credential lives.</summary>
public sealed record ExternalTriggerSetupDto(
    bool Enabled, string GitHubIssuer, string GitHubAudience, string GitHubServerUrl, string GitHubActionReference, int TokenMinutes);

/// <summary>Body of <c>POST /auth/trigger/github</c>: the job's GitHub token, and the trigger to act as when several trust the job.</summary>
public sealed record GitHubTriggerExchangeRequest(string? Token, string? Trigger = null);

/// <summary>Body of <c>POST /auth/trigger/key</c>: the trigger's key.</summary>
public sealed record KeyTriggerExchangeRequest(string? Key);

/// <summary>The trigger a credential acts as, and what it may do.</summary>
public sealed record TriggerIdentityDto(Guid Id, string Name, string RepoName, bool AllSchedules, IReadOnlyList<string> Schedules);

/// <summary>An exchange's answer: the credential, its type and lifetime, and the trigger it acts as.</summary>
public sealed record TriggerSessionResponse(string AccessToken, string TokenType, int ExpiresIn, DateTime ExpiresUtc, TriggerIdentityDto Trigger);

/// <summary>
/// External triggers: the callers outside SQLFlow that may sync a repository and fire a selection of its schedules. An
/// administrator manages them here (list, create, change, enable and disable, rotate a key, remove); a trigger the
/// configuration names is listed and refused every change, naming its setting. The two exchanges are where a trigger
/// proves who it is (a GitHub job's OpenID Connect token, or its key) and gets the short-lived credential its requests use.
/// </summary>
public static class ExternalTriggerEndpoints
{
    /// <summary>The longest a minted key may live: ten years.</summary>
    public const int MaxKeyDays = 3650;

    /// <summary>The admin surface. Map it under the admin policy.</summary>
    public static RouteGroupBuilder MapExternalTriggerEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var triggers = group.MapGroup("/external-triggers").WithTags("External triggers");
        triggers.MapGet("/", ListAsync).WithName("ListExternalTriggers");
        triggers.MapGet("/setup", Setup).WithName("GetExternalTriggerSetup");
        triggers.MapGet("/{id:guid}", GetAsync).WithName("GetExternalTrigger");
        triggers.MapPost("/", CreateAsync).WithName("CreateExternalTrigger");
        triggers.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateExternalTrigger");
        triggers.MapPost("/{id:guid}/enable", EnableAsync).WithName("EnableExternalTrigger");
        triggers.MapPost("/{id:guid}/disable", DisableAsync).WithName("DisableExternalTrigger");
        triggers.MapPost("/{id:guid}/key", RotateKeyAsync).WithName("RotateExternalTriggerKey");
        triggers.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteExternalTrigger");
        return group;
    }

    /// <summary>The two anonymous exchanges. Map them beside the other sign-in endpoints.</summary>
    public static RouteGroupBuilder MapExternalTriggerAuthEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/auth/trigger/github", ExchangeGitHubAsync).AllowAnonymous().WithTags("Authentication").WithName("ExchangeGitHubTriggerToken");
        group.MapPost("/auth/trigger/key", ExchangeKeyAsync).AllowAnonymous().WithTags("Authentication").WithName("ExchangeTriggerKey");
        return group;
    }

    // ---- Administration ---------------------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<ExternalTriggerDto>>> ListAsync(CatalogDbContext db, CancellationToken ct)
    {
        var triggers = await ExternalTriggerStore.ListAsync(db, ct).ConfigureAwait(false);
        var latest = await LatestRequestsAsync(db, triggers.Select(t => t.Id).ToList(), ct).ConfigureAwait(false);
        IReadOnlyList<ExternalTriggerDto> dtos = triggers.Select(t => ToDto(t, latest.GetValueOrDefault(t.Id))).ToList();
        return TypedResults.Ok(dtos);
    }

    private static Ok<ExternalTriggerSetupDto> Setup(IOptions<ControlPlaneOptions> options)
    {
        var triggers = options.Value.ExternalTriggers;
        return TypedResults.Ok(new ExternalTriggerSetupDto(
            triggers.Enabled, triggers.GitHub.Issuer, triggers.GitHub.Audience, triggers.GitHub.ServerUrl, triggers.GitHub.ActionReference,
            triggers.TokenMinutes));
    }

    private static async Task<Results<Ok<ExternalTriggerDto>, ProblemHttpResult>> GetAsync(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var trigger = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return NotFound(id);
        }

        var latest = await LatestRequestsAsync(db, [id], ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(trigger, latest.GetValueOrDefault(id)));
    }

    /// <summary>Creates a trigger. A key trigger's key is minted here and answered once; only its hash is kept. 201; 400 for
    /// a definition no trigger may have or an unknown repository; 409 for a name another trigger has.</summary>
    private static async Task<Results<Created<ExternalTriggerWritten>, ProblemHttpResult>> CreateAsync(
        ExternalTriggerWriteRequest body, ClaimsPrincipal user, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (definition, problem) = await CheckAsync(db, body, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (body.KeyExpiresInDays is < 0 or > MaxKeyDays)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", $"keyExpiresInDays is 0 (never) to {MaxKeyDays}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        AccessTokenGenerator.MintedToken? key = definition!.Kind == ExternalTriggerKinds.Key ? AccessTokenGenerator.MintTriggerKey() : null;
        var expires = key is not null && body.KeyExpiresInDays is > 0 ? now.AddDays(body.KeyExpiresInDays.Value) : (DateTime?)null;
        var (outcome, id) = await ExternalTriggerStore.CreateAsync(
            db, definition, key?.Hash, key?.DisplayPrefix, expires, RequestActor.Of(user) ?? RequestActor.Unknown, now, ct).ConfigureAwait(false);
        if (outcome == ExternalTriggerMutation.NameTaken)
        {
            return Problem(StatusCodes.Status409Conflict, "Name taken", $"Another trigger is named '{definition.Name}'.");
        }

        var created = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"External trigger {id} was not found right after it was created.");
        var warnings = await WarningsAsync(db, created, ct).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/external-triggers/{id:D}", new ExternalTriggerWritten(ToDto(created, null), warnings, key?.Secret));
    }

    /// <summary>Changes a trigger. Its kind stays; a key trigger keeps its key (rotate it to change it). 200; 400; 404;
    /// 409 for a configured trigger, a kind change, or a name another trigger has.</summary>
    private static async Task<Results<Ok<ExternalTriggerWritten>, ProblemHttpResult>> UpdateAsync(
        Guid id, ExternalTriggerWriteRequest body, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (definition, problem) = await CheckAsync(db, body, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var outcome = await ExternalTriggerStore.UpdateAsync(db, id, definition!, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (await Refusal(db, id, outcome, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var trigger = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return NotFound(id);
        }

        var latest = await LatestRequestsAsync(db, [id], ct).ConfigureAwait(false);
        return TypedResults.Ok(new ExternalTriggerWritten(ToDto(trigger, latest.GetValueOrDefault(id)), await WarningsAsync(db, trigger, ct).ConfigureAwait(false)));
    }

    private static Task<Results<Ok<ExternalTriggerDto>, ProblemHttpResult>> EnableAsync(Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
        => SetEnabledAsync(id, enabled: true, db, clock, ct);

    private static Task<Results<Ok<ExternalTriggerDto>, ProblemHttpResult>> DisableAsync(Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
        => SetEnabledAsync(id, enabled: false, db, clock, ct);

    /// <summary>Turns a trigger on or off. Off, its next exchange and its next request are refused at once; a request it
    /// already made carries on.</summary>
    private static async Task<Results<Ok<ExternalTriggerDto>, ProblemHttpResult>> SetEnabledAsync(
        Guid id, bool enabled, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await ExternalTriggerStore.SetEnabledAsync(db, id, enabled, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (await Refusal(db, id, outcome, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var trigger = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return NotFound(id);
        }

        var latest = await LatestRequestsAsync(db, [id], ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(trigger, latest.GetValueOrDefault(id)));
    }

    /// <summary>Gives a key trigger a new key, answered once; the old key stops working at once.</summary>
    private static async Task<Results<Ok<ExternalTriggerWritten>, ProblemHttpResult>> RotateKeyAsync(
        Guid id, RotateTriggerKeyRequest? body, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var days = body?.ExpiresInDays;
        if (days is < 0 or > MaxKeyDays)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", $"expiresInDays is 0 (never) to {MaxKeyDays}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var key = AccessTokenGenerator.MintTriggerKey();
        var outcome = await ExternalTriggerStore.RotateKeyAsync(
            db, id, key.Hash, key.DisplayPrefix, days is > 0 ? now.AddDays(days.Value) : null, now, ct).ConfigureAwait(false);
        if (await Refusal(db, id, outcome, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var trigger = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return NotFound(id);
        }

        var latest = await LatestRequestsAsync(db, [id], ct).ConfigureAwait(false);
        return TypedResults.Ok(new ExternalTriggerWritten(ToDto(trigger, latest.GetValueOrDefault(id)), [], key.Secret));
    }

    /// <summary>Removes a trigger. Its requests stay, under the name it had.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var outcome = await ExternalTriggerStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        if (await Refusal(db, id, outcome, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        return TypedResults.NoContent();
    }

    /// <summary>A write body checked by the one rule the configuration is checked by, against a repository that exists.</summary>
    private static async Task<(ExternalTriggerDefinition? Definition, ProblemHttpResult? Problem)> CheckAsync(
        CatalogDbContext db, ExternalTriggerWriteRequest? body, CancellationToken ct)
    {
        if (body is null)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid request", "The request has no body."));
        }

        ExternalTriggerDefinition definition;
        try
        {
            definition = new ExternalTriggerDefinition(
                body.Name ?? string.Empty, body.Kind ?? string.Empty, body.RepoName ?? string.Empty, body.Description,
                body.AllSchedules ?? false, body.Schedules ?? [], body.GitHub?.Repository, body.GitHub?.RepositoryId, body.GitHub?.Ref,
                body.GitHub?.Workflow, body.GitHub?.Environment).Normalized();
        }
        catch (ExternalTriggerDefinitionException ex)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid trigger", ex.Message));
        }

        if (!await db.RepoSources.AsNoTracking().AnyAsync(s => s.Name == definition.RepoName, ct).ConfigureAwait(false))
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid trigger",
                $"No repo source is named '{definition.RepoName}'; a trigger syncs a repository the control plane already syncs."));
        }

        return (definition, null);
    }

    /// <summary>What is worth telling whoever wrote <paramref name="trigger"/>: schedules its repository does not have (yet),
    /// and a GitHub trigger that trusts more than it probably means to.</summary>
    private static async Task<IReadOnlyList<string>> WarningsAsync(CatalogDbContext db, CatalogExternalTrigger trigger, CancellationToken ct)
    {
        var warnings = new List<string>();
        var named = ExternalTriggerStore.ParseSchedules(trigger.Schedules);
        if (named.Count > 0)
        {
            var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName(trigger.RepoName);
            var existing = await db.Schedules.AsNoTracking().Where(s => s.RepoId == repoId).Select(s => s.Name).ToListAsync(ct).ConfigureAwait(false);
            var missing = named.Where(n => !existing.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
            {
                warnings.Add($"'{trigger.RepoName}' has no schedule named {string.Join(", ", missing.Select(m => $"'{m}'"))} yet; a request naming one is told so once its sync has run.");
            }
        }

        if (trigger.Kind == ExternalTriggerKinds.GitHub)
        {
            if (trigger.GitHubWorkflow is null)
            {
                warnings.Add("It trusts every workflow of the repository; name the workflow file so a workflow added later is not trusted with it.");
            }

            if (trigger.GitHubRef == "*")
            {
                warnings.Add("It trusts every ref, pull request merge refs included; name the branch it should run on.");
            }

            if (trigger.GitHubRepositoryId is null)
            {
                warnings.Add("The repository id is not pinned; pin it (the token's repository_id, shown under Last used after the first call) so a repository recreated under the same name is not trusted.");
            }
        }

        return warnings;
    }

    private static async Task<ProblemHttpResult?> Refusal(CatalogDbContext db, Guid id, ExternalTriggerMutation outcome, CancellationToken ct)
    {
        switch (outcome)
        {
            case ExternalTriggerMutation.Applied:
                return null;
            case ExternalTriggerMutation.NotFound:
                return NotFound(id);
            case ExternalTriggerMutation.Configured:
                var setting = await db.ExternalTriggers.AsNoTracking().Where(t => t.Id == id).Select(t => t.ConfiguredBy).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return Problem(StatusCodes.Status409Conflict, "Set by configuration",
                    $"This trigger is set by the control plane's configuration ({setting}); change it there.");
            case ExternalTriggerMutation.KindChanged:
                return Problem(StatusCodes.Status409Conflict, "Kind cannot change",
                    "A trigger keeps the kind it was created with; create a new trigger of the other kind and remove this one.");
            case ExternalTriggerMutation.NameTaken:
                return Problem(StatusCodes.Status409Conflict, "Name taken", "Another trigger has that name.");
            case ExternalTriggerMutation.NotAKeyTrigger:
                return Problem(StatusCodes.Status409Conflict, "Not a key trigger", "Only a key trigger has a key to rotate.");
            default:
                throw new InvalidOperationException($"Unhandled trigger write outcome {outcome}.");
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, ExternalTriggerLastRequestDto>> LatestRequestsAsync(
        CatalogDbContext db, IReadOnlyList<Guid> triggerIds, CancellationToken ct)
    {
        if (triggerIds.Count == 0)
        {
            return new Dictionary<Guid, ExternalTriggerLastRequestDto>();
        }

        var ids = triggerIds.Cast<Guid?>().ToList();
        var latest = await db.TriggerRequests.AsNoTracking()
            .Where(r => ids.Contains(r.TriggerId))
            .GroupBy(r => r.TriggerId)
            .Select(g => g.OrderByDescending(r => r.CreatedUtc).Select(r => new { r.TriggerId, r.Id, r.State, r.CreatedUtc, r.CompletedUtc }).First())
            .ToListAsync(ct).ConfigureAwait(false);
        return latest.ToDictionary(
            r => r.TriggerId!.Value,
            r => new ExternalTriggerLastRequestDto(r.Id, r.State, TriggerRequestEndpoints.Conclusion(r.State), r.CreatedUtc, r.CompletedUtc));
    }

    private static ExternalTriggerDto ToDto(CatalogExternalTrigger t, ExternalTriggerLastRequestDto? latest)
        => new(
            t.Id, t.Name, t.Description, t.Kind, t.RepoName, t.AllSchedules, ExternalTriggerStore.ParseSchedules(t.Schedules),
            t.Kind == ExternalTriggerKinds.GitHub
                ? new ExternalTriggerGitHubDto(t.GitHubRepository, t.GitHubRepositoryId, t.GitHubRef, t.GitHubWorkflow, t.GitHubEnvironment)
                : null,
            t.KeyPrefix, t.KeyExpiresUtc, t.Enabled, t.ConfiguredBy, t.CreatedBy, t.CreatedUtc, t.UpdatedUtc, t.LastUsedUtc,
            t.LastUsedDetail, latest);

    // ---- Exchanges ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Exchanges a GitHub Actions job's OpenID Connect token for a trigger credential: the token is checked against
    /// GitHub's published keys and the configured audience, then matched to the enabled GitHub trigger whose repository,
    /// ref, workflow and environment it states. 200 with the credential; 400 for a body without a token; 401 for a token
    /// that is not a valid GitHub token for this control plane; 403 when no trigger trusts the job (the answer says why,
    /// trigger by trigger); 409 when several do and none was named; 503 while GitHub's keys cannot be read, or while
    /// external triggers are turned off.
    /// </summary>
    private static async Task<IResult> ExchangeGitHubAsync(
        GitHubTriggerExchangeRequest body, HttpContext http, CatalogDbContext db, IGitHubActionsTokenValidator validator,
        TokenIssuer issuer, IOptions<ControlPlaneOptions> options, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken ct)
    {
        AuthEndpoints.NeverCache(http);
        if (!options.Value.ExternalTriggers.Enabled)
        {
            return TriggerRequestEndpoints.TurnedOff();
        }

        if (string.IsNullOrWhiteSpace(body?.Token))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "Give the job's GitHub token (token).");
        }

        var validation = await validator.ValidateAsync(body.Token, ct).ConfigureAwait(false);
        if (validation.Identity is not { } identity)
        {
            return validation.Outage
                ? Problem(StatusCodes.Status503ServiceUnavailable, "GitHub unavailable", validation.Problem!)
                : Problem(StatusCodes.Status401Unauthorized, "Invalid GitHub token", validation.Problem!);
        }

        var candidates = await ExternalTriggerStore.ListEnabledGitHubAsync(db, ct).ConfigureAwait(false);
        var choice = GitHubTriggerMatch.Choose(candidates, identity, body.Trigger);
        if (choice.Trigger is not { } trigger)
        {
            loggers.CreateLogger(typeof(ExternalTriggerEndpoints)).LogWarning("GitHub trigger exchange refused for {Job}: {Reason}", identity.Describe(), choice.Problem);
            return Problem(choice.Status, choice.Status == StatusCodes.Status409Conflict ? "Several triggers" : "Not trusted", choice.Problem!);
        }

        var origin = identity.Describe();
        var originUrl = identity.RunUrl(options.Value.ExternalTriggers.GitHub.ServerUrl);
        return await IssueAsync(db, issuer, options.Value, clock, trigger, origin, originUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges a trigger's key for a trigger credential. 200 with the credential; 400 for a body without a key; 401 for
    /// a key that is not one (unknown, disabled, expired and malformed keys all answer the same); 503 while external
    /// triggers are turned off.
    /// </summary>
    private static async Task<IResult> ExchangeKeyAsync(
        KeyTriggerExchangeRequest body, HttpContext http, CatalogDbContext db, TokenIssuer issuer, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        AuthEndpoints.NeverCache(http);
        if (!options.Value.ExternalTriggers.Enabled)
        {
            return TriggerRequestEndpoints.TurnedOff();
        }

        var key = body?.Key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "Give the trigger's key (key).");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var trigger = AccessTokenGenerator.LooksLikeTriggerKey(key)
            ? await ExternalTriggerStore.FindByKeyHashAsync(db, AccessTokenGenerator.HashSecret(key), now, ct).ConfigureAwait(false)
            : null;
        if (trigger is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "Invalid trigger key", "The key is not one an enabled trigger accepts.");
        }

        return await IssueAsync(db, issuer, options.Value, clock, trigger, origin: null, originUrl: null, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> IssueAsync(
        CatalogDbContext db, TokenIssuer issuer, ControlPlaneOptions options, TimeProvider clock, CatalogExternalTrigger trigger,
        string? origin, string? originUrl, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(options.ExternalTriggers.TokenMinutes);
        var token = issuer.IssueTriggerCredential(RunActors.Trigger(trigger.Name, trigger.Id), trigger.Id, Bound(origin, 1000), Bound(originUrl, 1024), lifetime, now);
        await ExternalTriggerStore.RecordUsedAsync(db, trigger.Id, origin ?? $"key {trigger.KeyPrefix}...", now, ct).ConfigureAwait(false);
        return TypedResults.Ok(new TriggerSessionResponse(
            token.Token, "Bearer", (int)lifetime.TotalSeconds, token.ExpiresUtc,
            new TriggerIdentityDto(trigger.Id, trigger.Name, trigger.RepoName, trigger.AllSchedules, ExternalTriggerStore.ParseSchedules(trigger.Schedules))));
    }

    private static string? Bound(string? value, int max)
        => value is null ? null : value.Length > max ? value[..max] : value;

    private static ProblemHttpResult NotFound(Guid id)
        => TypedResults.Problem(detail: $"No external trigger {id:D}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
