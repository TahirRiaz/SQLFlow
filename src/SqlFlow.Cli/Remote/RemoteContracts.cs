using System.Text.Json.Serialization;

namespace SqlFlow.Cli.Remote;

// The CLI's client-side mirror of the control plane's wire contracts. The CLI deliberately does not reference
// the SqlFlow.ControlPlane project (an ASP.NET Core host); these records ARE the protocol boundary, kept
// field-for-field with the server records they mirror (see src/SqlFlow.ControlPlane/Api). Everything is
// camelCase JSON on the wire (ASP.NET's web defaults), deserialized case-insensitively here; the device token
// response alone follows the OAuth snake_case convention and pins its names explicitly.

/// <summary>The server's page envelope for every list endpoint.</summary>
internal sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>A synced source repository, as <c>GET /api/v1/repos</c> lists them.</summary>
internal sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

/// <summary>A local sign-in request (<c>POST /api/v1/auth/login</c>).</summary>
internal sealed record LoginRequest(string Username, string Password);

/// <summary>A signed-in session: the bearer token plus the identity facts.</summary>
internal sealed record SessionResponse(
    string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, IReadOnlyList<string> Scopes);

/// <summary>Starts the RFC 8628 device grant (<c>POST /api/v1/auth/device</c>).</summary>
internal sealed record DeviceAuthorizationRequest(string? ClientId, string? Scope);

/// <summary>The device-authorization response: the codes, the approval URL, and polling hints.</summary>
internal sealed record DeviceAuthorizationResponse(
    string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);

/// <summary>Request body for <c>POST /api/v1/auth/device/token</c>.</summary>
internal sealed record DeviceTokenRequest(string DeviceCode);

/// <summary>The minted device token; field names follow the OAuth token-response convention (snake_case).</summary>
internal sealed record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>The RFC 8628 polling error envelope: <c>authorization_pending</c>, <c>slow_down</c>,
/// <c>access_denied</c>, <c>expired_token</c>, or <c>invalid_request</c>.</summary>
internal sealed record DeviceErrorResponse([property: JsonPropertyName("error")] string Error);

/// <summary>A personal access token as the listing shows it: never the secret, never the hash.</summary>
internal sealed record AccessTokenDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedUtc,
    DateTime? ExpiresUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);

/// <summary>Creates a personal access token (<c>POST /api/v1/me/tokens</c>); scopes are capped server-side to
/// the caller's own, and a null expiry means the token never expires.</summary>
internal sealed record CreateAccessTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>The one-time response to creating a token: the listing view plus the never-again-retrievable
/// secret.</summary>
internal sealed record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);

/// <summary>The body that triggers a run (<c>POST /api/v1/runs</c>): references only, never a secret.</summary>
internal sealed record RunTriggerRequest(
    Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null,
    bool FullLoad = false, DateTime? BackfillFrom = null, DateTime? BackfillTo = null, string? FilePattern = null,
    string? Scope = null, string? Batch = null, bool AssertionsOnly = false, string? SourceFilter = null,
    bool IncludeAll = false);

/// <summary>The accepted-run acknowledgement for a single-flow trigger.</summary>
internal sealed record RunTriggerAccepted(Guid RunId, string Status);

/// <summary>The accepted-group acknowledgement for a node/batch trigger.</summary>
internal sealed record RunGroupAccepted(Guid GroupId, int MemberCount, string Status);

/// <summary>One flow a scope expansion would run, with the wave that orders it.</summary>
internal sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>What a node or batch trigger would enqueue, without enqueuing anything.</summary>
internal sealed record RunScopePreviewDto(
    string Scope, string Anchor, int MemberCount, int WaveCount, IReadOnlyList<RunScopePreviewMemberDto> Members);

/// <summary>A run as it appears in lists.</summary>
internal sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, Guid? GroupId,
    string? LastAction, DateTime? LastActionUtc);

/// <summary>One run with its full header for the detail view.</summary>
internal sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, string? Error, string? Host,
    bool FullLoad, DateTime? BackfillFrom, DateTime? BackfillTo, string? FilePattern, bool AssertionsOnly,
    bool ReprocessFromSourceMin,
    string? IncrementalMode, string? IncrementalFilter, string? IncrementalWatermark, string? IncrementalWatermarkSource,
    string? DataSetConvention,
    int? FailedStatementOrdinal, string? FailedStatementStep, string? FailedStatementSql, Guid? GroupId);

/// <summary>One file a run processed (file flows).</summary>
internal sealed record RunFileDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string? Path, long Rows, int Columns, long SizeBytes);

/// <summary>One data-quality assertion a run evaluated.</summary>
internal sealed record RunAssertionDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string Result, string AssertedValue, bool Evaluated, string? Error);

/// <summary>One generated SQL statement a run executed, in execution order.</summary>
internal sealed record RunStatementDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, DateTime? TimestampUtc, string Step, string Sql, string? Error);

/// <summary>One entry of a run's consolidated trace: an event or a generated statement, interleaved by time.</summary>
internal sealed record RunTraceEntryDto(
    long Id, Guid RunId, Guid? RepoId, string Kind, int Ordinal, DateTime? TimestampUtc, string Level,
    string? Step, string? Message, string? Sql, string? Error, long? Rows, double? ElapsedMs);

/// <summary>The payload of the live trace stream's final <c>end</c> event: the run's terminal status.</summary>
internal sealed record RunTraceStreamEndDto(string Status);

/// <summary>One surrogate-key generation outcome of a run (ingestion flows).</summary>
internal sealed record RunSurrogateKeyDto(
    long Id, Guid RunId, Guid? RepoId, int SurrogateKeyId, string SurrogateTable, string SurrogateColumn,
    bool IsRemote, long KeysGenerated, long RowsStamped, bool Executed, string? Error);

/// <summary>One per-metric health-check summary of a run (hc flows).</summary>
internal sealed record RunHealthCheckMetricDto(
    long Id, Guid RunId, Guid? RepoId, string Name, int SeriesPoints, int ImputedPoints, int ImmaturePoints,
    int Anomalies, int LevelShifts, bool ModelTrained, string? ModelTrainer, string? Error);

/// <summary>A run group's member counts by lifecycle state.</summary>
internal sealed record RunGroupCountsDto(
    int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

/// <summary>One run group's header plus the live rollup of its members' states.</summary>
internal sealed record RunGroupDto(
    Guid GroupId, Guid RepoId, string Mode, string Anchor, int MemberCount, string? CommitSha, DateTime EnqueuedUtc,
    RunGroupCountsDto Counts);

/// <summary>Who the presented credential authenticates as (<c>GET /api/v1/me</c>).</summary>
internal sealed record IdentityDto(string Subject, string? Role, IReadOnlyList<string> Scopes, Guid? UserId);

/// <summary>The dashboard rollup (<c>GET /api/v1/summary</c>).</summary>
internal sealed record RunCountsDto(long Queued, long Running, long Succeeded, long Failed, long Cancelled, long Last24h);

/// <summary>The control-plane landing rollup: estate size, run queue, fleet, scheduling, managed sync.</summary>
internal sealed record DashboardDto(
    long Repos, long Pipelines, long ActivePipelines, RunCountsDto Runs,
    long NodesOnline, long NodesTotal, long SchedulesEnabled, long SchedulesPaused,
    long RepoSources, long RepoSourcesWithErrors, DateTime AsOfUtc);

/// <summary>A worker node as the fleet view lists it.</summary>
internal sealed record NodeDto(string Name, DateTime FirstSeenUtc, DateTime LastSeenUtc, string? Version, bool Online);

/// <summary>A schedule as the catalog holds it: a named member SET (what a fire runs), not a single flow.</summary>
internal sealed record ScheduleDto(
    Guid Id, Guid RepoId, string Name, IReadOnlyList<Guid> MemberPipelineIds, string? Cron, int? IntervalSeconds,
    string Timezone, bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc,
    DateTime? LastFireUtc, Guid? LastRunId, Guid? LastGroupId, bool LastGroupActive, DateTime CreatedUtc,
    DateTime UpdatedUtc, int? MaxConcurrency, RunGroupCountsDto? LastCounts);

/// <summary>Creates a schedule: at least one member flow, exactly one of cron / interval. The name defaults to the
/// first member's flow name on the server.</summary>
internal sealed record CreateScheduleRequest(
    Guid RepoId, IReadOnlyList<string> Members, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled,
    bool? Catchup = null, string? Name = null, int? MaxConcurrency = null);

/// <summary>The created-schedule acknowledgement.</summary>
internal sealed record ScheduleCreated(Guid Id, DateTime? NextFireUtc);

/// <summary>The manual run-now acknowledgement: the enqueued run (the group's first member for a multi-member
/// schedule), plus the run group and member count when the fire expanded to a wave-ordered set.</summary>
internal sealed record ScheduleRunAccepted(Guid RunId, Guid? GroupId = null, int MemberCount = 1);

/// <summary>A managed git source the control plane keeps the catalog synced from.</summary>
internal sealed record RepoSourceDto(
    Guid Id, string Name, string RemoteUrl, string Branch, bool Enabled, int SyncIntervalSeconds,
    DateTime? NextSyncUtc, DateTime? LastSyncUtc, string? LastSyncedSha, string? LastError,
    string? CredentialReference, string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths,
    DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>Registers (upserts by name) a managed git source; the credential is a ${...} reference, never a raw token.</summary>
internal sealed record RegisterRepoSourceRequest(
    string Name, string RemoteUrl, string? Branch, int? SyncIntervalSeconds, bool? Enabled,
    string? CredentialReference, string? CredentialUsername, string[]? ExcludedFlowPaths);

/// <summary>The registered-source acknowledgement.</summary>
internal sealed record RepoSourceRegistered(Guid Id);

/// <summary>Previews a remote's flows without importing anything.</summary>
internal sealed record DiscoverRepoRequest(string RemoteUrl, string? Branch, string? CredentialReference, string? CredentialUsername);

/// <summary>One flow a discover found: parsed identity or the parse error, for the selection step.</summary>
internal sealed record DiscoveredFlowDto(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

/// <summary>The outcome of a manual local-path repo sync.</summary>
internal sealed record RepoSyncResultDto(
    int PipelinesAdded, int PipelinesUpdated, int PipelinesUnchanged, int PipelinesDeactivated, int PipelinesDeleted,
    int Objects, int Columns, int Edges, int Waves, int Dependencies,
    bool Connected, IReadOnlyList<string> Warnings);

/// <summary>A pipeline as the registry lists it. <c>Lifecycle</c> defaults to production when the server
/// predates the field, so the CLI renders sensibly against an older control plane.</summary>
internal sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    string Lifecycle = "production");

/// <summary>A single pipeline with its full (secret-redacted) definition.</summary>
internal sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    string Lifecycle = "production");

/// <summary>One declared or detected column of a pipeline.</summary>
internal sealed record PipelineColumnDto(
    string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? Expression, string? DataType,
    int? SortOrder, bool IsVirtual, bool ExcludeFromView, bool Converted);

/// <summary>One distinct file a pipeline has processed across its run history.</summary>
internal sealed record PipelineFileDto(
    string Name, string? Path, DateTimeOffset? Modified, long Rows, long SizeBytes, bool LastRun, DateTime? LastProcessedUtc);

/// <summary>A datasource reference the estate declares (<c>GET /api/v1/datasources</c>).</summary>
internal sealed record DatasourceDto(
    string Reference, string? Kind, bool Resolvable, int SourcePipelines, int TargetPipelines);

/// <summary>Queues one ad-hoc compute task on a worker node (<c>POST /api/v1/datasources/tasks</c>).</summary>
internal sealed record ComputeTaskRequest(
    string? Reference, string? Operation, string? Kind, string? Pool,
    string? Database, string? Schema, string? ObjectName, string? NameLike, string? SearchTerm,
    bool IncludeTables = true, bool IncludeViews = true, bool IncludeSystem = false,
    int Offset = 0, int Limit = 200, long? SampleSize = null, int MaxKeyColumns = 4, int MaxCandidates = 5,
    bool VerifyCandidates = true, bool TrustDeclaredKeys = true);

/// <summary>The accepted-task acknowledgement.</summary>
internal sealed record ComputeTaskAccepted(Guid TaskId, string Status);

/// <summary>A compute task as the task list shows it (no result body).</summary>
internal sealed record ComputeTaskSummaryDto(
    Guid TaskId, string Operation, string SourceRef, string? ProviderKind, string? Pool, string Status,
    string? RequestedBy, DateTime EnqueuedUtc, DateTime? StartUtc, DateTime? EndUtc, string? ClaimedByNode,
    DateTime? CancelRequestedUtc, string? Error, bool HasResult, string? Target);

/// <summary>A single compute task with its operation-shaped JSON result once succeeded.</summary>
internal sealed record ComputeTaskDto(
    Guid TaskId, string Operation, string SourceRef, string? ProviderKind, string? Pool, string Status,
    string? RequestedBy, DateTime EnqueuedUtc, DateTime? StartUtc, DateTime? EndUtc, string? ClaimedByNode,
    DateTime? CancelRequestedUtc, string? Error, System.Text.Json.JsonElement? Result, string? Target);

/// <summary>A lineage object as it appears in lists.</summary>
internal sealed record ObjectDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind, int? Level,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>One attributed lineage edge: a flow (or module) relating to an object.</summary>
internal sealed record EdgeDto(
    long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule,
    string Relation, string ObjectKey, string ObjectName, string? ObjectDatabase, string? ObjectSchema, string Tier);

/// <summary>One pipeline within an execution wave.</summary>
internal sealed record WavePipelineDto(Guid Id, string Name, string Kind);

/// <summary>One execution wave of a repo's plan.</summary>
internal sealed record WaveDto(int Wave, IReadOnlyList<WavePipelineDto> Pipelines);

/// <summary>The code behind any lineage node (a pipeline's YAML or an object's SQL), one consistent shape.</summary>
internal sealed record NodeScriptDto(string Key, string Kind, string Language, string? Script, string? Source, string? Name);

/// <summary>An object that matched a global search.</summary>
internal sealed record ObjectHitDto(string Key, string Name, string Kind, string ServerRef, string? Database, string? Schema);

/// <summary>A column that matched a name search, with its owning object.</summary>
internal sealed record ColumnHitDto(string ObjectKey, string ObjectName, string ColumnName, string? DataType, bool Nullable);

/// <summary>An object whose code matched a definition search, with an excerpt.</summary>
internal sealed record DefinitionHitDto(string Key, string Name, string Kind, string Snippet, string Source);

/// <summary>A processed file that matched, deep-linkable to its run and pipeline.</summary>
internal sealed record FileHitDto(
    string Name, string? Path, string RunId, string FlowName, string FlowKind,
    long Rows, int Columns, long SizeBytes, DateTime? RunUtc, string PipelineId, string? RepoId, string? RepoName);

/// <summary>A flow (YAML) that matched by name, path, or body.</summary>
internal sealed record FlowHitDto(
    string Id, string Name, string Kind, string? Batch, string RelativePath, string RepoId, string RepoName,
    string MatchedIn, string Snippet);

/// <summary>A column a flow produces, matched by output name, source column, or the expression computing it.</summary>
internal sealed record FlowColumnHitDto(
    string PipelineId, string FlowName, string FlowKind, string? Batch, string RepoId, string RepoName,
    string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? DataType, string? Expression,
    string MatchedIn);

/// <summary>SQL a flow executed, collapsed to one row per (flow, step) with an occurrence count.</summary>
internal sealed record StatementHitDto(
    string PipelineId, string FlowName, string FlowKind, string Step,
    long Occurrences, string RunId, DateTime? LastSeenUtc, string Snippet);

/// <summary>One category of a combined search: the full count plus a preview of top hits.</summary>
internal sealed record SearchCategoryDto<T>(long Total, IReadOnlyList<T> Items);

/// <summary>The combined result of one global search across every catalog surface. <c>Tokens</c> is how the raw
/// term was parsed (a multi-word query matches word by word), and <c>StatementWindowDays</c> how far back the
/// executed-SQL surface reached.</summary>
internal sealed record AllSearchDto(
    string Query,
    IReadOnlyList<string> Tokens,
    int StatementWindowDays,
    SearchCategoryDto<ObjectHitDto> Objects,
    SearchCategoryDto<ColumnHitDto> Columns,
    SearchCategoryDto<DefinitionHitDto> Definitions,
    SearchCategoryDto<FileHitDto> Files,
    SearchCategoryDto<FlowHitDto> Flows,
    SearchCategoryDto<FlowColumnHitDto> FlowColumns,
    SearchCategoryDto<StatementHitDto> Statements);
