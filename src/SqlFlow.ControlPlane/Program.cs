using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Dispatch;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.ControlPlane.Notifications;
using SqlFlow.ControlPlane.Proposals;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using SqlFlow.SourceControl.Proposals;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (fail fast on a misconfigured deployment) -------------------------------------------------
var options = builder.Configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
options.Validate();
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .Validate(o => { try { o.Validate(); return true; } catch (InvalidOperationException) { return false; } },
        "ControlPlane configuration is invalid; see the eager startup validation message for the specific field.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);

// Settings that no longer exist fail loudly rather than being silently ignored: a deployment still carrying the
// orphan reaper's knobs or the worker's catalog poll interval would otherwise believe it had configured something.
foreach (var (retired, replacement) in new[]
{
    ("ControlPlane:Reaper", "ControlPlane:Dispatch (node leases replaced the orphan reaper)"),
    ("ControlPlane:Worker:PollMilliseconds", "ControlPlane:Dispatch:LongPollSeconds (nodes long-poll the dispatcher; there is no catalog poll)"),
})
{
    if (builder.Configuration.GetSection(retired).Exists())
    {
        throw new InvalidOperationException($"{retired} is no longer a setting; use {replacement}.");
    }
}

static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string scope)
    => user.FindFirst("scope")?.Value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains(scope) == true;

// ---- SqlFlow engine: the one shared composition (source readers, secret chain, connection registry, loaders,
// the flow runner, and the DocumentExecutor). The control plane runs flows through the exact same engine wiring as
// the CLI and worker nodes, so a triggered run behaves identically. This also provides the secret chain
// (IAzureCredentialFactory, the env + Key Vault providers, ISecretResolver) the catalog connection provider needs.
builder.Services.AddSqlFlowEngine();
builder.Services.AddSingleton<CatalogConnectionProvider>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddSingleton<DeviceCodeStore>();

// ---- Identity: regular SQLFlow users (username + PBKDF2 password hash in the catalog) and Azure single sign-on
// (Entra ID token exchange with JIT provisioning). Both paths end in the same SQLFlow-issued token, so the API
// surface authorizes one token type no matter how the user signed in.
builder.Services.AddSingleton<IPasswordHasher<CatalogUser>, PasswordHasher<CatalogUser>>();
builder.Services.AddSingleton<LoginThrottle>();
if (options.AzureAd.IsEnabled)
{
    builder.Services.AddSingleton<IExternalTokenValidator, EntraTokenValidator>();
}

// ---- Bootstrap provisioning: migrations, built-in roles, the initial admin, and the optional demo repo source.
// Retries in the background until the catalog is reachable, so start order (app before database) never matters.
builder.Services.AddHostedService<BootstrapProvisioningService>();

// ---- Dispatch: the run queue lives in this process. The dispatcher holds every queued and executing run and
// compute task in memory, makes each placement decision (pool routing, wave order, group concurrency, one
// execution per pipeline) under one lock, leases work to nodes that pull it, and journals every decision to the
// catalog with plain conditional updates, so runs survive a restart and are visible to the read API from the
// moment they are queued. Exactly one replica dispatches at a time: DispatchService arbitrates through the
// catalog's ownership lease and drives the housekeeping ticks (lease expiry, reconcile, node flush) while active.
// The API enqueues and cancels through IRunDispatcher, which journals first and then tells the dispatcher.
builder.Services.AddSingleton<IDispatchLedger, CatalogDispatchLedger>();
builder.Services.AddSingleton(sp => new Dispatcher(
    sp.GetRequiredService<IDispatchLedger>(),
    sp.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.Dispatch,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<Dispatcher>>()));
builder.Services.AddHostedService<DispatchService>();
builder.Services.AddSingleton<IRunDispatcher, InProcessRunDispatcher>();
// The shared node runtime (also run standalone by `sqlflow worker`), hosted here as a background service that
// polls the dispatcher in-process, so a triggered run starts on this node the instant it is enqueued. API-only
// replicas (Worker:Enabled=false) skip hosting it so the HTTP tier can scale on request load behind an ingress
// while compute scales as separate worker processes speaking the same protocol over HTTP.
builder.Services.AddSingleton<INodeTransport, InProcessNodeTransport>();
builder.Services.AddSingleton<RunWorker>();
if (options.Worker.Enabled)
{
    builder.Services.AddHostedService<RunExecutionWorker>();

    // Give the node its drain window at the host level too. On a stop the worker keeps its in-flight runs going so
    // each records its own outcome, but the generic host stops waiting after HostOptions.ShutdownTimeout and tears
    // the process down regardless - which would sever exactly the runs the drain exists to save. Set only where a
    // worker is actually hosted: an API-only replica has nothing to drain and should still exit promptly.
    builder.Services.Configure<HostOptions>(
        host => host.ShutdownTimeout = RunWorker.DefaultDrainTimeout + TimeSpan.FromSeconds(30));
}

// ---- Scheduler: scans the catalog for due cron/interval schedules and fires them by enqueuing onto the same
// queue (one run path). Schedules come from git (YAML, synced) and the API (ad-hoc / pause-resume); firing
// advances the next-fire atomically so multiple control-plane nodes never double-fire an occurrence.
builder.Services.AddHostedService<SchedulerService>();

// ---- Run-trace retention: the two heaviest per-run tables (RunStatement, the full SQL of every generated
// statement, and RunEvent) grow without bound and are almost never read once a run is old and green. This service
// prunes them on a cadence to each pipeline's latest run + every failed run + anything still recent, keeping the
// run header (its stats and error message) intact. The delete runs on the service's own loop and connection, never
// on the request thread or inside a pipeline run. Hosted on every replica like the orphan reaper (its batched
// delete is idempotent under concurrency); the retention is read fresh each sweep, so a null retention keeps traces
// forever. The Maintenance page's "clean up now" runs the same prune synchronously via the endpoint so it can
// report the outcome. Disabled here turns the automatic sweep off while the manual trigger keeps working.
if (options.RunTrace.Enabled)
{
    builder.Services.AddHostedService<RunTraceReaper>();
}

// ---- Managed sync: keeps the shadow catalog current from git. A background service pulls each registered repo
// source's branch tip on its interval and runs the same catalog sync the CLI's `db sync` runs.
builder.Services.AddHostedService<RepoSyncService>();

// ---- Flow authoring: propose pipelines to a tracked repo source as a pull request. The control plane pushes a
// proposal branch and opens the PR with the source's own stored git credential (github.com or bitbucket.org over
// HTTPS); it never writes the catalog directly, so a human reviews and merges before the managed sync imports the
// flows. A commit-pinned run can test the proposal (via the returned commit SHA) before it merges.
builder.Services.AddHttpClient(GitHubPullRequestPublisher.HttpClientName);
builder.Services.AddSingleton<IGitProposalPublisher>(_ => new GitProposalPublisher());
builder.Services.AddSingleton<IPullRequestPublisher, GitHubPullRequestPublisher>();
builder.Services.AddSingleton<IPullRequestPublisher, BitbucketPullRequestPublisher>();
// The proposal staging area is bound to this process's lifetime: swept clean on start and stop, so a staged git
// clone never outlives the session and a crash leak is reclaimed on the next start.
builder.Services.AddHostedService<ProposalWorkspaceJanitor>();

// ---- Notifications: detects failed runs (and failed assertions on green runs) and sends opted-in users email
// and/or Slack messages, immediately (cooldown-coalesced) or as periodic digests. The pipeline is durable and
// claim-based (events + a delivery outbox in the catalog), so replicas never double-send and a channel outage
// backs up instead of dropping alerts. Which email transport backs IEmailSender is decided here, once, from
// configuration; a channel is registered only when it can actually send, and the endpoints report availability so
// the GUI offers users only what works.
if (options.Notifications.Enabled)
{
    builder.Services.AddHttpClient();
    if (string.Equals(options.Notifications.Email.Provider, EmailNotificationOptions.ProviderSmtp, StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
    }
    else if (string.Equals(options.Notifications.Email.Provider, EmailNotificationOptions.ProviderGraph, StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IEmailSender, GraphEmailSender>();
    }

    if (options.Notifications.EmailConfigured)
    {
        builder.Services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
    }

    if (options.Notifications.SlackConfigured)
    {
        builder.Services.AddSingleton<ISlackApiClient, SlackApiClient>();
        builder.Services.AddSingleton<INotificationChannel, SlackNotificationChannel>();
    }

    builder.Services.AddHostedService<NotificationService>();
}

// ---- GUI chat assistant: the same SqlFlow.Assistant core the Slack bot runs (one code path, two
// surfaces), hosted behind /api/v1/chat with streaming and catalog-persisted conversations. The MCP
// server is shared with the Slack bot unchanged; what differs is authority: every chat run forwards
// the calling user's own bearer, so the assistant's tool access is exactly that user's access. The
// gateways are registered only when the feature is enabled; the chat endpoints stay mapped either
// way and report the switch through /chat/capabilities so the GUI can explain instead of erroring.
if (options.Assistant.Enabled)
{
    builder.Services.AddSingleton(sp =>
    {
        var resolver = sp.GetRequiredService<SqlFlow.Core.Secrets.ISecretResolver>();
        var assistant = sp.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.Assistant;
        var settings = assistant.ToAssistantSettings();
        // The ApiKey fields accept ${env:...}/${keyvault:...} references; resolve them here, once,
        // onto copies so the resolved secrets never flow back into the bound options instances.
        settings.OpenAI = new SqlFlow.Assistant.OpenAIOptions
        {
            ApiKey = resolver.Resolve(assistant.OpenAI.ApiKey),
            Model = assistant.OpenAI.Model,
            BaseUrl = assistant.OpenAI.BaseUrl,
            TranscriptionModel = assistant.OpenAI.TranscriptionModel,
        };
        settings.Anthropic = new SqlFlow.Assistant.AnthropicOptions
        {
            ApiKey = resolver.Resolve(assistant.Anthropic.ApiKey),
            Model = assistant.Anthropic.Model,
            MaxTokens = assistant.Anthropic.MaxTokens,
        };
        return settings;
    });
    if (options.Assistant.Provider == SqlFlow.Assistant.AssistantProvider.Anthropic)
    {
        builder.Services.AddSingleton<SqlFlow.Assistant.IAssistantGateway, SqlFlow.Assistant.AnthropicGateway>();
    }
    else
    {
        builder.Services.AddSingleton<SqlFlow.Assistant.IAssistantGateway, SqlFlow.Assistant.ResponsesApiGateway>();
    }

    builder.Services.AddSingleton<SqlFlow.Assistant.TranscriptionGateway>();
}

// ---- Catalog read model: pooled, read-only, transient-retry --------------------------------------------------
// The provider setup (migrations history table, transient-error resiliency) comes from CatalogDatabase.Configure,
// the single definition shared with the CLI, the worker and bootstrap, so no host runs with weaker resiliency than
// another. Only the pooling and no-tracking read posture is local to this registration.
builder.Services.AddDbContextPool<CatalogDbContext>((sp, db) =>
{
    CatalogDatabase.Configure(db, sp.GetRequiredService<CatalogConnectionProvider>().ConnectionString);
    db.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

// ---- AuthN / AuthZ -------------------------------------------------------------------------------------------
// Two bearer credentials share one Authorization header: a short-lived HS256 session token (the GUI, MCP device
// flow) and a long-lived personal access token (headless clients: the CLI, the VSCode extension, automation). A
// policy scheme reads the presented token and forwards to the right validator by shape, so authorization downstream
// sees one authenticated principal type regardless of which credential arrived.
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SigningKey!));
builder.Services
    .AddAuthentication(PersonalAccessTokenDefaults.PolicyScheme)
    .AddPolicyScheme(PersonalAccessTokenDefaults.PolicyScheme, "Bearer (JWT or PAT)", policy =>
    {
        policy.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer " + AccessTokenGenerator.Prefix, StringComparison.Ordinal)
                ? PersonalAccessTokenDefaults.Scheme
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenHandler>(PersonalAccessTokenDefaults.Scheme, null)
    .AddJwtBearer(jwt =>
    {
        jwt.MapInboundClaims = false; // keep "sub"/"scope" claim types as issued
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            // Pin the algorithm so a token can never be validated under an unintended alg (no 'none', no
            // HS/RS confusion if an asymmetric key is introduced in the Identity phase).
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(authz =>
{
    // The privilege model has exactly two tiers: any authenticated user gets the whole operational product
    // (reading, running and cancelling flows, managing schedules and repo sources, scaling pools, and authoring
    // pull-request proposals), and only user administration (creating accounts and granting roles) is fenced off
    // behind the admin scope. So read, operate, and author all resolve to "signed in", and admin alone checks a
    // scope. This is deliberate: a signed-in user is never stuck unable to use a feature the UI shows them.
    authz.AddPolicy("read", policy => policy.RequireAuthenticatedUser());
    authz.AddPolicy("operate", policy => policy.RequireAuthenticatedUser());
    authz.AddPolicy("author", policy => policy.RequireAuthenticatedUser());
    authz.AddPolicy("admin", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, "admin")));
    // The node protocol is fleet traffic, not a person's session: only a credential minted with the node scope
    // (an admin's personal access token, or a bootstrap token asking for it) may poll for and report work.
    authz.AddPolicy(NodeProtocol.Scope, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, NodeProtocol.Scope)));
});

// ---- Cross-cutting: problem details, OpenAPI, compression, health, rate limiting, CORS -----------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddResponseCompression();
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>("catalog");

builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var subject = context.User.FindFirst("sub")?.Value;
        // Node protocol calls (a long poll every few seconds per node, outcome reports, trace batches) are
        // authenticated fleet traffic: each node token gets its own generous window, so a fleet of hundreds of
        // nodes never trips the per-user limit meant for people and scripts. An unauthenticated hit on the node
        // path stays under the normal per-IP limit, so a token cannot be brute-forced any faster there.
        if (subject is not null
            && context.Request.Path.StartsWithSegments(NodeProtocol.RoutePrefix)
            && HasScope(context.User, NodeProtocol.Scope))
        {
            return RateLimitPartition.GetFixedWindowLimiter("node:" + subject, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RateLimit.NodePermitPerWindow,
                Window = TimeSpan.FromSeconds(options.RateLimit.WindowSeconds),
                QueueLimit = 0,
            });
        }

        var partitionKey = subject
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimit.PermitPerWindow,
            Window = TimeSpan.FromSeconds(options.RateLimit.WindowSeconds),
            QueueLimit = 0,
        });
    });
});

const string corsPolicy = "ConfiguredOrigins";
var hasCors = options.Cors.AllowedOrigins.Length > 0;
if (hasCors)
{
    builder.Services.AddCors(cors => cors.AddPolicy(corsPolicy, policy => policy
        .WithOrigins(options.Cors.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var app = builder.Build();

// ---- Pipeline ------------------------------------------------------------------------------------------------
// Forwarded headers run FIRST: everything downstream (the rate limiter's per-IP partition, the login throttle,
// diagnostics) must see the real client address, not the ingress's. Only the configured proxies are trusted, so
// the header is never honored from an arbitrary client.
if (options.Proxy.Enabled)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = options.Proxy.ForwardLimit,
    };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var cidr in options.Proxy.KnownNetworks)
    {
        var (prefix, length) = ProxyOptions.ParseNetwork(cidr);
        forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
    }

    foreach (var proxy in options.Proxy.KnownProxies)
    {
        forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseResponseCompression();
if (hasCors)
{
    app.UseCors(corsPolicy);
}

app.UseAuthentication();
app.UseRateLimiter(); // after authentication so the partition can key on the subject
app.UseAuthorization();

// Liveness has no dependencies; readiness probes the catalog database. Probes are exempt from the rate limiter
// so a shared egress IP's traffic can never throttle a liveness/readiness check into a false negative.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready").DisableRateLimiting();
app.MapOpenApi();

// The device-authorization approval page (the verificationUri for the RFC 8628 grant). Served at the root so the
// URL advertised to a headless client resolves without a separate front-end deployment.
app.MapDeviceApprovalPage();

var v1 = app.MapGroup("/api/v1");

// The sign-in surface: providers discovery and local login are always available; the Entra token exchange is
// mapped when Azure SSO is enabled; the break-glass bootstrap token endpoint only when a secret is configured.
v1.MapAuthEndpoints(options);

// The authenticated read surface: repos/pipelines, runs, lineage, cross-repo search, and schedules.
v1.MapGroup(string.Empty).RequireAuthorization("read")
    .MapCatalogEndpoints()
    .MapRepoTreeEndpoints()
    .MapGitHistoryEndpoints()
    .MapRunEndpoints()
    .MapActivityEndpoints()
    .MapLineageEndpoints()
    .MapSearchEndpoints()
    .MapScheduleReadEndpoints()
    .MapNodeEndpoints()
    .MapDatasourceReadEndpoints()
    .MapRepoSourceReadEndpoints()
    .MapSummaryEndpoints()
    .MapInsightsEndpoints()
    .MapDataStreamEndpoints()
    .MapIntegrationReadEndpoints()
    // Self-service: any authenticated user manages their own personal access tokens (scopes capped to their own)
    // and their own notification opt-ins.
    .MapMeEndpoints()
    .MapNotificationEndpoints()
    .MapMaintenanceEndpoints()
    // The dispatcher as it sees itself: every queued run with the gate holding it back, every lease, the fleet.
    .MapDispatchEndpoints()
    // The GUI chat assistant: per-user conversations, streamed answers, per-user MCP authority.
    .MapChatEndpoints();

// The operate surface: triggering/cancelling a run and managing schedules are privileged operations, so they live
// under the "operate" scope rather than the read group.
v1.MapGroup(string.Empty).RequireAuthorization("operate")
    .MapCatalogWriteEndpoints()
    .MapRunTriggerEndpoints()
    .MapDatasourceComputeEndpoints()
    .MapQueryEndpoints()
    .MapScheduleWriteEndpoints()
    .MapRepoSourceWriteEndpoints()
    .MapSourceDiscoverEndpoints()
    .MapNodeControlEndpoints()
    .MapIntegrationDebugEndpoints();

// The author surface: proposing pipelines to a source repo as a pull request pushes a branch under the source's own
// credential, so it lives under the "author" scope rather than "operate".
v1.MapGroup(string.Empty).RequireAuthorization("author")
    .MapFlowProposalEndpoints();

// The admin surface: user and role administration requires the "admin" scope (the admin role, or a bootstrap
// token that requested it).
v1.MapGroup(string.Empty).RequireAuthorization("admin")
    .MapUserEndpoints();

// The node protocol: compute nodes poll for work and report outcomes here, under the "node" scope. This is the
// only way work leaves the control plane; a node needs no catalog access to take or finish a run.
v1.MapGroup("/node").RequireAuthorization(NodeProtocol.Scope)
    .MapNodeProtocolEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can host the app through WebApplicationFactory.</summary>
public partial class Program;
