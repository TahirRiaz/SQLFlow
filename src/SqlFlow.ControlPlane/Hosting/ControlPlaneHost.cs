using SqlFlow.Core;
using SqlFlow.Core.Secrets;
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
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Dispatch;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.ControlPlane.Maintenance;
using SqlFlow.ControlPlane.Notifications;
using SqlFlow.ControlPlane.Proposals;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core.Hosting;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using SqlFlow.SourceControl.Proposals;

namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// The control plane's composition: every service, the middleware pipeline and every endpoint, plus the modules a host
/// passes. SQLFlow's own <see cref="Program"/> runs it with no modules; a product built on SQLFlow has a program of its own
/// that runs it with its modules, and gets exactly the same control plane around them.
/// </summary>
public static class ControlPlaneHost
{
    /// <summary>Builds the control plane with <paramref name="modules"/>, branded as SQLFlow, and runs it until the host shuts down.</summary>
    /// <exception cref="ControlPlaneModuleException">A module could not be registered, configured or mapped.</exception>
    public static Task RunAsync(string[] args, params IControlPlaneModule[] modules)
        => RunAsync(args, ProductBranding.SqlFlow, modules);

    /// <summary>Builds the control plane with <paramref name="modules"/>, branded as <paramref name="branding"/>, and runs it until the host shuts down.</summary>
    /// <exception cref="ControlPlaneModuleException">A module could not be registered, configured or mapped.</exception>
    public static async Task RunAsync(string[] args, ProductBranding branding, params IControlPlaneModule[] modules)
    {
        var app = Build(args, branding, modules);
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the control plane with <paramref name="modules"/>, ready to run: configuration validated, services
    /// registered (SQLFlow's, then each module's in order), the pipeline assembled and every endpoint mapped (SQLFlow's, then
    /// each module's).
    /// </summary>
    /// <exception cref="InvalidOperationException">The control plane's configuration is invalid.</exception>
    /// <exception cref="ControlPlaneModuleException">A module could not be registered, configured or mapped.</exception>
    public static WebApplication Build(string[] args, params IControlPlaneModule[] modules)
        => Build(args, ProductBranding.SqlFlow, modules);

    /// <summary>
    /// Builds the control plane as <see cref="Build(string[], IControlPlaneModule[])"/> does, branded as
    /// <paramref name="branding"/>: the OpenAPI document's title and notification subjects carry the product's name, and the
    /// branding is registered for modules to use.
    /// </summary>
    /// <exception cref="InvalidOperationException">The control plane's configuration is invalid.</exception>
    /// <exception cref="ControlPlaneModuleException">A module could not be registered, configured or mapped.</exception>
    public static WebApplication Build(string[] args, ProductBranding branding, params IControlPlaneModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(modules);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(branding);

        // The local development values the CLI has always read, read here too. A host and the documents it runs live in
        // one checkout, so the nearest .sqlflow/env at the content root or any parent is this deployment's, and the
        // in-process node resolves a flow's ${env:...} references against this very process. The process environment
        // always wins, so a container's or CI's variable is never shadowed, and only the NAMES applied are logged.
        // A host that must not read it (a test estate that would otherwise pick up a developer's real credentials) sets
        // SQLFLOW_LOCAL_ENV_FILE to false.
        ApplyLocalEnvFile(builder.Environment.ContentRootPath);

        // ---- Configuration (fail fast on a misconfigured deployment) -------------------------------------------------
        var options = builder.Configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
        options.Validate();

        // Registered first so it starts before every other hosted service: the search categories modules contribute are
        // checked before the host does any work.
        builder.Services.AddHostedService<SearchContributorStartupCheck>();
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
            ("ControlPlane:PowerAI:QuestionGeneration", "'generateQuestions: true' on each subscriber in its subscriber library (questions are generated per subscriber wherever ControlPlane:Assistant:Anthropic:ApiKey is set)"),
            ("ControlPlane:RunTrace", "ControlPlane:Maintenance (the Maintenance page's automatic cleanup, which covers the run trace and every other storage area)"),
        })
        {
            if (builder.Configuration.GetSection(retired).Exists())
            {
                throw new InvalidOperationException($"{retired} is no longer a setting; use {replacement}.");
            }
        }

        // ---- Limits: the platform's own, raised by modules while they are configured and applied to the server when its
        // options are first read. Without a module they are the server's defaults.
        ControlPlaneLimits.GetOrAdd(builder.Services);

        // ---- SqlFlow engine: the one shared composition (source readers, secret chain, connection registry, loaders,
        // the flow runner, and the DocumentExecutor). The control plane runs flows through the exact same engine wiring as
        // the CLI and worker nodes, so a triggered run behaves identically. This also provides the secret chain
        // (IAzureCredentialFactory, the env + Key Vault providers, ISecretResolver) the catalog connection provider needs.
        builder.Services.AddSqlFlowEngine();
        builder.Services.AddSingleton<CatalogConnectionProvider>();
        // ---- Module databases: a module's own schema, on the catalog connection or a reference of its own, migrated and
        // verified by bootstrap after the catalog; readiness stays red until they are verified.
        builder.Services.AddSingleton<IModuleDatabaseConnections>(sp => new ModuleDatabaseConnections(
            sp.GetRequiredService<SqlFlow.Core.Secrets.ISecretResolver>(),
            () => sp.GetRequiredService<CatalogConnectionProvider>().ConnectionString));
        builder.Services.AddSingleton<ModuleDatabaseVerification>();
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
        // The start-up pass, step by step: what readiness and GET /api/v1/bootstrap report.
        builder.Services.AddSingleton<BootstrapProgress>();
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
        // ---- Managed workers (off by default): the dispatch owner starts and stops this estate's workers through the
        // launcher a module registers, instead of an external autoscaler reading the scale target.
        ManagedWorkersService.Register(builder.Services, options.ManagedWorkers, options.Dispatch);
        // The values every run and node task is queued carrying for its references, from the sources the modules register.
        builder.Services.AddSingleton<RunReferences>();
        builder.Services.AddSingleton<IRunDispatcher, InProcessRunDispatcher>();
        builder.Services.AddSingleton<RepoSyncWaiter>();
        builder.Services.AddSingleton<RepoSyncSignal>();
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

        // ---- Maintenance: what the catalog's database holds, described area by area (SQLFlow's own here, each module's by
        // its own contributor), the cleanups an admin runs from the Maintenance page and the automatic pass that applies the
        // retention an admin set. Both go through the one runner, so a cleanup asked for and one applied automatically run
        // the same code and land in the same log. The pass runs on every replica; its batched deletes are idempotent.
        builder.Services.AddScoped<IStorageContributor, CatalogStorageContributor>();
        builder.Services.AddSingleton<MaintenanceRunner>();
        builder.Services.AddHostedService<StorageAreaStartupCheck>();
        if (options.Maintenance.Enabled)
        {
            builder.Services.AddHostedService<MaintenanceReaper>();
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
        // server is shared with the Slack bot unchanged; what differs is authority: every chat run carries a
        // short-lived token delegated from the calling user (AssistantDelegation), bounded to the read surface
        // the tools need and never renewable, so the model host never holds the user's own session. The
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
            // The gateways read the assistant's rules from the MCP server itself, the one text every client of it follows.
            builder.Services.AddSingleton<SqlFlow.Assistant.McpServerInstructions>();
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

        // ---- AI context: question generation and synonym expansion, each independent of the interactive chat assistant above and
        // of each other (a deployment may run any one without the others). Both reuse the same Anthropic account/model
        // rather than declaring their own, since both draw on the same subscription, so the resolved options are
        // registered once when EITHER needs them. Question generation runs for the subscribers whose library asks for it
        // (generateQuestions: true), so its generator is registered wherever there is a key to generate with. Synonym
        // expansion is registered only when its own switch is on. A deployment without a key never attempts an Anthropic
        // call.
        var generationAvailable = QuestionGenerationAvailability.IsAvailable(options);
        var expandsSynonyms = options.AiContext.ExpandsSynonyms(options.Assistant.Anthropic);
        if (generationAvailable || expandsSynonyms)
        {
            builder.Services.AddSingleton(sp =>
            {
                var resolver = sp.GetRequiredService<SqlFlow.Core.Secrets.ISecretResolver>();
                var anthropic = sp.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.Assistant.Anthropic;
                return new SqlFlow.Assistant.AnthropicOptions
                {
                    ApiKey = resolver.Resolve(anthropic.ApiKey),
                    Model = anthropic.Model,
                    MaxTokens = anthropic.MaxTokens,
                };
            });
        }

        if (generationAvailable)
        {
            builder.Services.AddSingleton<SqlFlow.Assistant.QuestionGenerator>();
        }

        if (expandsSynonyms)
        {
            builder.Services.AddSingleton<SqlFlow.Assistant.QuestionExpander>();
        }

        // PowerAI report uploads: a .pbix is forwarded to the isolated extractor service and never parsed in this process.
        // Registered unconditionally so the report endpoints can explain a disabled switch instead of failing to bind; the
        // client itself refuses to call out while ControlPlane:AiContext:ReportExtraction is off. The client applies its own
        // per-extraction budget, so the transport timeout is left unbounded.
        builder.Services.AddHttpClient(ReportExtractionClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<ReportExtractionClient>();

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
            authz.AddPolicy(ControlPlanePolicies.Read, policy => policy.RequireAuthenticatedUser());
            authz.AddPolicy(ControlPlanePolicies.Operate, policy => policy.RequireAuthenticatedUser());
            authz.AddPolicy(ControlPlanePolicies.Author, policy => policy.RequireAuthenticatedUser());
            authz.AddPolicy(ControlPlanePolicies.Admin, policy => policy
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
        // A branded host's OpenAPI document is titled with its product name and described by its lockup; SQLFlow's own
        // keeps the framework's title.
        builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer((document, context, _) =>
        {
            var product = context.ApplicationServices.GetRequiredService<ProductBranding>();
            if (!product.IsSqlFlow)
            {
                document.Info ??= new Microsoft.OpenApi.Models.OpenApiInfo();
                document.Info.Title = product.ProductName;
                document.Info.Description = product.Lockup;
            }

            return Task.CompletedTask;
        }));
        builder.Services.AddResponseCompression();
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<CatalogDbContext>("catalog")
            .AddCheck<ModuleDatabaseVerification>("module-databases")
            // Reported at /health/startup and not by readiness: a control plane with a failed start-up step still serves
            // the API (GET /api/v1/bootstrap) that says why, rather than being taken out of rotation.
            .AddCheck<BootstrapReadiness>("bootstrap", tags: [BootstrapReadiness.Tag]);

        builder.Services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var subject = context.User.FindFirst("sub")?.Value;
                // Node protocol calls (a long poll every few seconds per node, outcome reports, trace batches several times
                // a second per executing run) are authenticated fleet traffic: each NODE gets its own generous window, keyed
                // by the token's subject plus the node name the transport stamps on every call, so a fleet of hundreds of
                // nodes sharing one node token never trips a limit meant for people and scripts, and one misbehaving node
                // cannot starve its siblings. An unauthenticated hit on the node path stays under the normal per-IP limit,
                // so a token cannot be brute-forced any faster there.
                if (subject is not null
                    && context.Request.Path.StartsWithSegments(NodeProtocol.RoutePrefix)
                    && HasScope(context.User, NodeProtocol.Scope))
                {
                    var nodeName = context.Request.Headers[NodeProtocol.NodeHeader].ToString();
                    return RateLimitPartition.GetFixedWindowLimiter("node:" + subject + "/" + nodeName, _ => new FixedWindowRateLimiterOptions
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

        // ---- Modules: each registers its services, options and hosted services after SQLFlow's own, so it may extend or
        // replace them, and raises the platform limits it needs.
        foreach (var module in modules)
        {
            builder.Services.AddControlPlaneModule(module, builder.Configuration, builder.Environment);
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
        // Inside the exception handler, so whatever a request throws once its client has gone reaches it as a cancellation.
        app.UseMiddleware<ClientAbortMiddleware>();
        app.UseResponseCompression();
        if (hasCors)
        {
            app.UseCors(corsPolicy);
        }

        app.UseAuthentication();
        app.UseRateLimiter(); // after authentication so the partition can key on the subject
        app.UseAuthorization();
        // After authorization, so the principal is resolved: refuses an assistant run's delegated token on every endpoint not
        // explicitly opened to it (default-deny; see AssistantDelegation).
        app.UseMiddleware<AssistantDelegationMiddleware>();
        // Likewise refuses a credential the control plane issued to a worker it started on every route but the node
        // protocol (see NodeCredential).
        app.UseMiddleware<NodeCredentialMiddleware>();

        // Liveness has no dependencies; readiness probes the catalog database. Probes are exempt from the rate limiter
        // so a shared egress IP's traffic can never throttle a liveness/readiness check into a false negative.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => !check.Tags.Contains(BootstrapReadiness.Tag) })
            .DisableRateLimiting();
        app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = check => check.Tags.Contains(BootstrapReadiness.Tag) })
            .DisableRateLimiting();
        app.MapOpenApi();

        // The device-authorization approval page (the verificationUri for the RFC 8628 grant). Served at the root so the
        // URL advertised to a headless client resolves without a separate front-end deployment.
        app.MapDeviceApprovalPage();

        var v1 = app.MapGroup("/api/v1");

        // The sign-in surface: providers discovery and local login are always available; the Entra token exchange is
        // mapped when Azure SSO is enabled; the break-glass bootstrap token endpoint only when a secret is configured.
        v1.MapAuthEndpoints(options);

        // The authenticated read surface: repos/pipelines, runs, lineage, cross-repo search, and schedules. Its safe methods
        // are open to an assistant run's delegated token (the MCP tools read from here); its few writes are not.
        v1.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Read).AllowAssistantRead()
            .MapCatalogEndpoints()
            .MapRepoTreeEndpoints()
            .MapGitHistoryEndpoints()
            .MapRunEndpoints()
            .MapKindEndpoints()
            .MapActivityEndpoints()
            .MapLineageEndpoints()
            .MapSearchEndpoints()
            // The semantic layer: what the approved AI contexts open, with its business context, as an assistant reads it.
            .MapSemanticLayerEndpoints()
            .MapScheduleReadEndpoints()
            .MapNodeEndpoints()
            .MapDatasourceReadEndpoints()
            .MapRepoSourceReadEndpoints()
            .MapSummaryEndpoints()
            .MapInsightsEndpoints()
            .MapDataStreamEndpoints()
            .MapIntegrationReadEndpoints()
            // The dispatcher as it sees itself: every queued run with the gate holding it back, every lease, the fleet.
            .MapDispatchEndpoints();

        // The authenticated self-service surface: the caller's own personal access tokens (scopes capped to their own),
        // notification opt-ins, maintenance, and chat conversations. Closed to an assistant run's delegated token (only the
        // /me whoami opts back in), so a model host holding that token can never mint a credential or read the user's chats.
        v1.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Read)
            .MapMeEndpoints()
            .MapBootstrapEndpoints()
            .MapNotificationEndpoints()
            .MapMaintenanceReadEndpoints()
            // The GUI chat assistant: per-user conversations, streamed answers, each run under its own delegated token.
            .MapChatEndpoints();

        // The operate surface: triggering/cancelling a run and managing schedules are privileged operations, so they live
        // under the "operate" scope rather than the read group.
        v1.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Operate)
            .MapCatalogWriteEndpoints()
            .MapRunTriggerEndpoints()
            // A run form's looked-up parameter values read the flow's own data, as a run does.
            .MapRunInputOperateEndpoints()
            .MapDatasourceComputeEndpoints()
            .MapQueryEndpoints()
            .MapQuestionExampleEndpoints()
            .MapScheduleWriteEndpoints()
            .MapRepoSourceWriteEndpoints()
            .MapSourceDiscoverEndpoints()
            .MapNodeControlEndpoints()
            .MapIntegrationDebugEndpoints()
            .MapMaintenanceOperateEndpoints();

        // The author surface: proposing pipelines to a source repo as a pull request pushes a branch under the source's own
        // credential, so it lives under the "author" scope rather than "operate".
        v1.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Author)
            .MapFlowProposalEndpoints();

        // The admin surface: user and role administration requires the "admin" scope (the admin role, or a bootstrap
        // token that requested it).
        v1.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Admin)
            .MapUserEndpoints()
            // The AI contexts files propose: where each stands, what approving it would change, approve and revoke.
            .MapAiContextEndpoints()
            .MapColumnPolicyEndpoints()
            .MapSemanticLayerAdminEndpoints()
            // The semantic layer's example queries (the saved answers): list, read, and remove one a person or a report gave.
            .MapSemanticExampleAdminEndpoints()
            // The Power BI reports the layer holds: store (extracted in the isolated extractor), list, and remove.
            .MapSemanticReportEndpoints()
            // The Maintenance page's writes: an area's retention, and a cleanup now.
            .MapMaintenanceAdminEndpoints();

        // The node protocol: compute nodes poll for work and report outcomes here, under the "node" scope. This is the
        // only way work leaves the control plane; a node needs no catalog access to take or finish a run.
        v1.MapGroup("/node").RequireAuthorization(NodeProtocol.Scope)
            .MapNodeProtocolEndpoints();

        // The modules' endpoints, on route groups of their own under the same policies.
        ControlPlaneModuleServiceCollectionExtensions.MapModuleEndpoints(app.Services, v1);

        return app;
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string scope)
        => user.FindFirst("scope")?.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(scope) == true;

    /// <summary>The environment variable a host sets to false to keep the local development env file from being read.</summary>
    public const string LocalEnvFileVariable = "SQLFLOW_LOCAL_ENV_FILE";

    /// <summary>
    /// Applies the nearest <c>.sqlflow/env</c> at <paramref name="contentRoot"/> or any parent, unless this host was told
    /// not to. Returns the names applied; none when no file is found or the host opted out. A file that cannot be opened
    /// is reported and skipped, because an unreadable file says nothing about what the values should be.
    /// </summary>
    /// <exception cref="SqlFlowException">
    /// The file holds a malformed line. A half-loaded secrets file is a debugging trap: the host would start with some
    /// values and not others, and a flow would fail later on a reference that looks like it should have resolved. The
    /// message names the file and the line.
    /// </exception>
    internal static IReadOnlyList<string> ApplyLocalEnvFile(string contentRoot)
    {
        if (Environment.GetEnvironmentVariable(LocalEnvFileVariable) is { } opt
            && (opt.Equals("false", StringComparison.OrdinalIgnoreCase) || opt == "0"))
        {
            return [];
        }

        try
        {
            return LocalEnvFile.ApplyNearest(contentRoot).Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The local environment file could not be applied ({ex.Message}); every reference resolves from the process environment alone.");
            return [];
        }
    }
}
