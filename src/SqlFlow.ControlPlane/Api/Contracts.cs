using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One page of results plus the totals a client needs to paginate. Offset paging (page/pageSize) is
/// sufficient for the catalog's cardinality; the contract can move to a cursor without breaking callers.</summary>
/// <typeparam name="T">The item DTO.</typeparam>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>Page request normalization: clamps to safe bounds so a client cannot request an unbounded page.</summary>
public static class PageRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
        => (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

/// <summary>One commit in a synced repository's history: who changed what, when, and which paths it touched.</summary>
public sealed record GitCommitDto(
    string Sha,
    string ShortSha,
    string AuthorName,
    string AuthorEmail,
    DateTime CommittedUtc,
    string Message,
    IReadOnlyList<string> ChangedPaths);

/// <summary>The patch one commit applied, optionally scoped to a single file. <c>Truncated</c> says the text was
/// cut at the inline limit, so a reader never mistakes a clipped patch for a complete one.</summary>
public sealed record GitDiffDto(
    string Sha,
    string AuthorName,
    DateTime CommittedUtc,
    string Message,
    string? Path,
    int LinesAdded,
    int LinesDeleted,
    string Patch,
    bool Truncated);

/// <summary>One commit in a snapshot repository, named as an endpoint of a comparison: enough to say which
/// snapshot a side of the diff came from, without the changed-path list a log entry carries.</summary>
public sealed record GitRevisionDto(
    string Sha,
    string ShortSha,
    string AuthorName,
    DateTime CommittedUtc,
    string Message);

/// <summary>
/// One database object's DDL at the two ends of a window: what the snapshot repository held before the window
/// opened (<c>Before</c>) and what it holds now (<c>After</c>), so a reader sees the net change rather than
/// replaying one commit at a time.
///
/// A null <c>Before</c> revision means the repository has no commit at or before the window start (the history
/// begins inside the window), and a null text on either side means the object's file was absent at that
/// revision: no <c>BeforeText</c> is an object added during the window, no <c>AfterText</c> one dropped in it.
/// <c>Truncated</c> says at least one side was cut at the inline limit, so a clipped script is never mistaken
/// for the whole of it.
/// </summary>
public sealed record SchemaObjectCompareDto(
    string Path,
    GitRevisionDto? Before,
    GitRevisionDto After,
    string? BeforeText,
    string? AfterText,
    int LinesAdded,
    int LinesDeleted,
    bool Truncated);

/// <summary>One object a source-control snapshot found added, changed, or dropped, as the schema-history feed
/// returns it. <c>CommitSha</c> links the row to the commit that carries the diff.</summary>
public sealed record SchemaChangeDto(
    long Id,
    Guid RepoId,
    Guid RunId,
    Guid? PipelineId,
    string Database,
    string Category,
    string? Schema,
    string Name,
    string ChangeType,
    string? CommitSha,
    DateTime OccurredUtc);

/// <summary>One tracked database's change tally over the requested window, and when it last changed.</summary>
public sealed record SchemaChangeDatabaseDto(
    string Database,
    int Total,
    int Added,
    int Changed,
    int Deleted,
    DateTime LastChangeUtc);

/// <summary>A synced source repository.</summary>
public sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

/// <summary>The outcome of a manual local-path repo sync: the pipeline reconciliation counts and the lineage tallies
/// (objects, columns, edges, waves, dependencies), whether the derived tier connected to the live database, and any
/// warnings the pass surfaced (bounded). This is the compact summary the GUI shows after a "Sync now".</summary>
public sealed record RepoSyncResultDto(
    int PipelinesAdded, int PipelinesUpdated, int PipelinesUnchanged, int PipelinesDeactivated, int PipelinesDeleted,
    int Objects, int Columns, int Edges, int Waves, int Dependencies,
    bool Connected, IReadOnlyList<string> Warnings);

/// <summary>One entry in a repository's content listing: a repo-relative, forward-slashed path, whether it is a
/// folder, and the file's size in bytes (0 for a folder, and for a file the host could not stat).</summary>
public sealed record RepoTreeEntryDto(string Path, bool IsFolder, long SizeBytes);

/// <summary>Everything one repository holds, path-ordered: the folders and files as they stand on the synced branch
/// (or on disk for a local-path repo), not only what the catalog imported. <c>ReadFrom</c> is <c>git</c> or
/// <c>disk</c>; <c>Truncated</c> says the listing hit its cap and is partial.</summary>
public sealed record RepoTreeDto(string ReadFrom, IReadOnlyList<RepoTreeEntryDto> Entries, bool Truncated);

/// <summary>A pipeline (flow) as it appears in lists: the hot dimensions, without the heavy YAML/definition body.
/// <c>ExecutionMode</c> is <c>auto</c> or <c>manual</c> (the flow's YAML <c>mode:</c>); manual flows are excluded
/// from schedules and group runs and execute only when triggered directly. <c>Lifecycle</c> is <c>production</c>
/// or <c>development</c> (the YAML <c>lifecycle:</c>); development flows run normally but never generate
/// notification events.</summary>
public sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string Lifecycle, string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>One batch (source-system grouping) of a repo's flows with how many flows it holds: the grouping level
/// between a repo and its flows in batch-grouped browsing surfaces. A flow that declares no batch reports under
/// <see cref="SqlFlow.Catalog.CatalogPipeline.DefaultBatch"/>.</summary>
public sealed record PipelineBatchDto(Guid RepoId, string Batch, int FlowCount, int ActiveCount);

/// <summary>A single pipeline with its full (secret-redacted) definition for the detail view.</summary>
public sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string Lifecycle, string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>The run parameters that apply to a pipeline, driven by its flow kind and definition, so the GUI renders
/// a trigger form of exactly the controls the engine will honor. <see cref="FlowKind"/> is the flow's kind (<c>cpy</c>,
/// <c>file</c>, <c>ing</c>, ...); <see cref="Parameters"/> is empty for kinds with no selection surface.</summary>
public sealed record FlowParametersDto(string FlowKind, IReadOnlyList<RunParameterDescriptor> Parameters);

/// <summary>One resolved column of a pipeline's pre-ingestion transformation view: <c>declared</c> rows come
/// from the flow YAML (the source of truth), <c>detected</c> rows from the latest run's generated view.</summary>
public sealed record PipelineColumnDto(
    string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? Expression, string? DataType,
    int? SortOrder, bool IsVirtual, bool ExcludeFromView, bool Converted);

/// <summary>One distinct file a pipeline has processed across its whole run history (deduplicated by name+path,
/// carrying the newest processing's metadata). <see cref="LastRun"/> flags the files processed by the pipeline's
/// most recent file-bearing run, so the view can separate "what the last run found" from "everything ever seen".</summary>
public sealed record PipelineFileDto(
    string Name, string? Path, DateTimeOffset? Modified, long Rows, long SizeBytes, bool LastRun, DateTime? LastProcessedUtc);

/// <summary>
/// The size profile of a pipeline's file deliveries, computed from every distinct file across its whole run
/// history (deduplicated by name + path, so a re-pulled file counts once): what a normal delivery from this flow
/// looks like. <see cref="AvgBytes"/> is the headline; <see cref="MedianBytes"/> is the more honest "typical file"
/// when a few outsized deliveries drag the mean, and <see cref="StdDevBytes"/> (population) is the spread that
/// tells a caller how much variation is normal before a file counts as anomalous. A pipeline that has processed no
/// files reports zeros with no timestamps and no <see cref="Recent"/> window.
/// </summary>
/// <param name="FileCount">How many distinct files the pipeline has processed.</param>
/// <param name="TotalBytes">The summed size of those files.</param>
/// <param name="AvgBytes">The mean file size, rounded to whole bytes.</param>
/// <param name="MedianBytes">The median file size, rounded to whole bytes.</param>
/// <param name="MinBytes">The smallest file.</param>
/// <param name="MaxBytes">The largest file.</param>
/// <param name="StdDevBytes">The population standard deviation of the file sizes, rounded to whole bytes.</param>
/// <param name="TotalRows">The summed row count across those files.</param>
/// <param name="AvgRows">The mean row count per file, rounded.</param>
/// <param name="OldestModified">The oldest last-modified timestamp seen, null when no file carries one.</param>
/// <param name="NewestModified">The newest last-modified timestamp seen, null when no file carries one.</param>
/// <param name="Recent">The newest files' window, for comparing the current regime against the all-time profile.</param>
public sealed record PipelineFileStatsDto(
    long FileCount, long TotalBytes, long AvgBytes, long MedianBytes, long MinBytes, long MaxBytes,
    long StdDevBytes, long TotalRows, long AvgRows,
    DateTimeOffset? OldestModified, DateTimeOffset? NewestModified, PipelineRecentFilesDto? Recent);

/// <summary>The newest files a pipeline has processed (by last-modified), profiled on their own so a caller can
/// tell the current delivery shape from the all-time one: a source whose files grew tenfold this month reads as
/// normal against its lifetime average and abnormal against this window.</summary>
/// <param name="FileCount">How many files the window covers (at most the window size).</param>
/// <param name="AvgBytes">The mean size within the window, rounded to whole bytes.</param>
/// <param name="MinBytes">The smallest file in the window.</param>
/// <param name="MaxBytes">The largest file in the window.</param>
/// <param name="OldestModified">The oldest last-modified timestamp in the window, null when none carries one.</param>
/// <param name="NewestModified">The newest last-modified timestamp in the window, null when none carries one.</param>
public sealed record PipelineRecentFilesDto(
    int FileCount, long AvgBytes, long MinBytes, long MaxBytes,
    DateTimeOffset? OldestModified, DateTimeOffset? NewestModified);

/// <summary>The bootstrap token request body (only honored when a bootstrap secret is configured).</summary>
public sealed record TokenRequest(string Secret, string? Subject, IReadOnlyList<string>? Scopes);

/// <summary>An issued bearer token.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

/// <summary>A local (regular SQLFlow user) sign-in.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>An external-token sign-in: the SPA posts the Entra ID token it obtained via MSAL and receives a
/// SQLFlow token in exchange.</summary>
public sealed record ExchangeRequest(string Token);

/// <summary>A signed-in session: the SQLFlow bearer token plus the identity facts the GUI shapes itself with
/// (which nav to show, which actions to enable) without decoding the token client-side.</summary>
public sealed record SessionResponse(
    string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, IReadOnlyList<string> Scopes);

/// <summary>The Entra ID sign-in settings a browser client needs to run the MSAL flow. All values are public by
/// design (a SPA's client id and tenant id ship in its bundle); secrets never appear here.</summary>
public sealed record EntraProviderDto(bool Enabled, string? ClientId, string? Authority);

/// <summary>Which sign-in methods this control plane offers, so the login page renders only what works.</summary>
public sealed record AuthProvidersDto(bool Local, bool Bootstrap, EntraProviderDto Entra);

/// <summary>A control-plane user as the admin surface lists it. Never carries the password hash.</summary>
public sealed record UserDto(
    Guid Id, string Username, string? Email, string? DisplayName, string Role, string Provider, bool Active,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? LastLoginUtc);

/// <summary>A role and the scopes it grants.</summary>
public sealed record RoleDto(string Name, string Scopes, string Description);

/// <summary>Creates a local user (SSO users are provisioned by their first sign-in, never through this).</summary>
public sealed record CreateUserRequest(string Username, string Password, string Role, string? Email, string? DisplayName);

/// <summary>Renames a user and rewrites their display name and email (a null or blank optional field clears it).
/// The username only changes for a local user; an SSO account's is owned by Entra.</summary>
public sealed record UpdateUserProfileRequest(string Username, string? Email, string? DisplayName);

/// <summary>Changes a user's role.</summary>
public sealed record SetRoleRequest(string Role);

/// <summary>Resets a local user's password.</summary>
public sealed record SetPasswordRequest(string Password);

/// <summary>A personal access token as its owner lists it. Never carries the secret (unrecoverable after creation)
/// nor its hash; <see cref="Prefix"/> is the recognizable, non-secret lead. <see cref="RevokedUtc"/> non-null means
/// the token is revoked; <see cref="ExpiresUtc"/> in the past means expired.</summary>
public sealed record AccessTokenDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedUtc,
    DateTime? ExpiresUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);

/// <summary>Creates a personal access token. <see cref="Scopes"/> is capped server-side to the caller's own scopes
/// (an empty/omitted list defaults to all of them); <see cref="ExpiresInDays"/> null means the token never
/// expires.</summary>
public sealed record CreateAccessTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>The response to creating a token: the listing view plus the one-time <see cref="Secret"/>. The secret is
/// shown exactly once here and is never retrievable again.</summary>
public sealed record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);

/// <summary>Who a bearer credential authenticates as: the resolved subject, role, and effective scopes, plus
/// the backing catalog user id (null for a bootstrap token, which has no account). A headless client's
/// "whoami": one call that answers "am I signed in, as whom, and what am I allowed to do".</summary>
public sealed record IdentityDto(string Subject, string? Role, IReadOnlyList<string> Scopes, Guid? UserId);

/// <summary>One notification channel's availability on this deployment: whether it can send at all, and (for
/// email) which transport backs it, so the settings page can say "email via smtp" instead of a bare toggle.</summary>
public sealed record NotificationChannelAvailabilityDto(bool Available, string? Provider);

/// <summary>What the notification settings page needs to render itself: whether the pipeline runs at all, which
/// channels this deployment can send on, the valid kind / mode vocabularies with their defaults, and the caller's
/// account email (the default destination when a subscription sets no override).</summary>
public sealed record NotificationOptionsDto(
    bool Enabled,
    NotificationChannelAvailabilityDto Email,
    NotificationChannelAvailabilityDto Slack,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> DefaultKinds,
    IReadOnlyList<string> Modes,
    string? UserEmail,
    int DefaultDigestIntervalMinutes,
    int DefaultCooldownMinutes,
    EstateDigestOptionsDto EstateDigest);

/// <summary>How this deployment produces estate digests: whether the periodic generator runs and on what period,
/// plus the bounds the on-demand window must fall inside. Lets the settings page state the cadence in words
/// instead of leaving the reader to guess when the next one lands.</summary>
public sealed record EstateDigestOptionsDto(
    bool Enabled, int IntervalMinutes, int MinWindowMinutes, int MaxWindowMinutes);

/// <summary>One estate digest as a list shows it: its window, headline and per-kind counts, without the rendered
/// bodies. <see cref="GeneratedBy"/> names the person who asked for a manual digest; null for a scheduled one.</summary>
public sealed record NotificationDigestSummaryDto(
    Guid Id, string Origin, DateTime PeriodStartUtc, DateTime PeriodEndUtc, DateTime GeneratedUtc,
    string? GeneratedBy, string Subject, int EventCount, int FlowCount, int FailedCount, int CancelledCount,
    int SkippedCount, int AssertionFailedCount, bool Truncated);

/// <summary>One flow's slice of a digest, as the GUI's digest table renders it: what happened, how many times,
/// when it last happened, the error worth reading, and the run to open. <see cref="Kind"/> is the notification
/// event kind (<c>run_failed</c>, <c>assertion_failed</c>, ...), not a run status.</summary>
public sealed record NotificationDigestFlowDto(
    string FlowName, string FlowKind, string Kind, int Count, DateTime LastOccurredUtc, Guid LastRunId,
    Guid PipelineId, string? LastError);

/// <summary>One estate digest opened for reading: the summary, the per-flow rows behind it, and the composed
/// bodies. <see cref="Flows"/> is what the GUI tabulates; <see cref="TextBody"/> is the message as it would be
/// delivered, which is what a reader copies out or checks before sending.</summary>
public sealed record NotificationDigestDto(
    NotificationDigestSummaryDto Summary, IReadOnlyList<NotificationDigestFlowDto> Flows, string TextBody,
    string HtmlBody);

/// <summary>
/// Generates a digest on demand over one period, most often a single day. Both instants are UTC, and both are
/// required: a digest answers "what happened between these two moments", which a lookback from now cannot express
/// once the question is about a particular day. An end in the future is clamped to now, so asking for today
/// yields today so far rather than a period the estate has not lived through yet.
/// </summary>
public sealed record GenerateNotificationDigestRequest(DateTime FromUtc, DateTime ToUtc);

/// <summary>Sends an already-generated digest through one of the caller's own subscriptions, which is what
/// supplies the channel and destination.</summary>
public sealed record SendNotificationDigestRequest(Guid SubscriptionId);

/// <summary>One notification opt-in as its owner lists it.</summary>
public sealed record NotificationSubscriptionDto(
    Guid Id, string Channel, string Mode, IReadOnlyList<string> Kinds, string? FlowPattern, string? EmailAddress,
    string? SlackTarget, int DigestIntervalMinutes, int CooldownMinutes, bool Enabled, DateTime? LastSentUtc,
    DateTime? NextDueUtc, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>Creates a notification opt-in. <see cref="Channel"/> is fixed for the subscription's lifetime (make a
/// new one to switch); everything else has a served default: <see cref="Mode"/> immediate, <see cref="Kinds"/> the
/// server defaults, a null <see cref="EmailAddress"/> / <see cref="SlackTarget"/> meaning "my account email" /
/// "direct-message me", and the interval / cooldown defaults from the options endpoint.</summary>
public sealed record CreateNotificationSubscriptionRequest(
    string Channel, string? Mode, IReadOnlyList<string>? Kinds, string? FlowPattern, string? EmailAddress,
    string? SlackTarget, int? DigestIntervalMinutes, int? CooldownMinutes);

/// <summary>Edits a notification opt-in. Null means "keep the current value"; for the clearable strings
/// (<see cref="FlowPattern"/>, <see cref="EmailAddress"/>, <see cref="SlackTarget"/>) an empty string clears the
/// field back to its default behavior. The channel is not editable.</summary>
public sealed record UpdateNotificationSubscriptionRequest(
    string? Mode, IReadOnlyList<string>? Kinds, string? FlowPattern, string? EmailAddress, string? SlackTarget,
    int? DigestIntervalMinutes, int? CooldownMinutes, bool? Enabled);

/// <summary>One outbound notification as its owner sees it in the history: where it went, what it covered, and
/// whether (and why not) it arrived. The composed bodies are omitted; <see cref="Subject"/> identifies the message.</summary>
public sealed record NotificationDeliveryDto(
    Guid Id, Guid SubscriptionId, string Channel, string Target, string Subject, string Status, int Attempts,
    int EventCount, string? LastError, DateTime CreatedUtc, DateTime? SentUtc);

/// <summary>The response to anything that puts a message on the outbox (a test send, a digest send): the
/// delivery row to watch in the deliveries history.</summary>
public sealed record NotificationQueuedDeliveryDto(Guid DeliveryId);
