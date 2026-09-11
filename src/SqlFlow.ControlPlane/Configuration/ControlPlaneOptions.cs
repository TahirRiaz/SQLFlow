using System.Net;
using System.Text;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// The bound configuration for the control plane (section <c>ControlPlane</c>). Every value comes from
/// configuration (env vars / appsettings); nothing security-relevant has an insecure default, and the required
/// fields are validated at startup (fail fast) by <see cref="Validate"/>.
/// </summary>
public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    public CatalogOptions Catalog { get; set; } = new();

    public JwtOptions Jwt { get; set; } = new();

    public CorsOptions Cors { get; set; } = new();

    public AzureAdOptions AzureAd { get; set; } = new();

    public BootstrapOptions Bootstrap { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public ProxyOptions Proxy { get; set; } = new();

    public SchedulerOptions Scheduler { get; set; } = new();

    public WorkerOptions Worker { get; set; } = new();

    /// <summary>The in-memory dispatcher: leases, long polls, reconcile cadence, ownership. Bound from
    /// <c>ControlPlane:Dispatch</c>.</summary>
    public SqlFlow.Dispatch.DispatchOptions Dispatch { get; set; } = new();

    public ManagedSyncOptions ManagedSync { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public RunTraceRetentionOptions RunTrace { get; set; } = new();

    public DataStreamOptions DataStreams { get; set; } = new();

    public AssistantChatOptions Assistant { get; set; } = new();

    public DataOpsOptions DataOps { get; set; } = new();

    /// <summary>Validates the options, throwing a clear startup error for any missing or unsafe required value.
    /// Called during host build so a misconfigured deployment never starts serving.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Catalog.ConnectionReference))
        {
            throw new InvalidOperationException(
                "ControlPlane:Catalog:ConnectionReference is required (a connection string or a ${env:...}/${keyvault:...} reference; defaults to ${env:SQLFLOW_CATALOG_DB}).");
        }

        if (string.IsNullOrWhiteSpace(Jwt.Issuer) || string.IsNullOrWhiteSpace(Jwt.Audience))
        {
            throw new InvalidOperationException("ControlPlane:Jwt:Issuer and ControlPlane:Jwt:Audience are required.");
        }

        if (string.IsNullOrWhiteSpace(Jwt.SigningKey) || Encoding.UTF8.GetByteCount(Jwt.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "ControlPlane:Jwt:SigningKey is required and must be at least 32 bytes (256-bit) for HS256. Set it from a secret (env/Key Vault), never a literal in source.");
        }

        // The bootstrap secret is optional (its endpoint is only mapped when set), but when present it is a
        // credential a caller presents verbatim, so a guessable one is as dangerous as a weak signing key.
        if (!string.IsNullOrEmpty(Jwt.BootstrapSecret) && Encoding.UTF8.GetByteCount(Jwt.BootstrapSecret) < 32)
        {
            throw new InvalidOperationException(
                "ControlPlane:Jwt:BootstrapSecret, when set, must be at least 32 bytes (UTF-8). Set it from a secret (env/Key Vault), never a literal in source.");
        }

        if (Jwt.AccessTokenMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("ControlPlane:Jwt:AccessTokenMinutes must be between 1 and 1440.");
        }

        if (Jwt.SessionMaxDays is < 1 or > 365)
        {
            throw new InvalidOperationException("ControlPlane:Jwt:SessionMaxDays must be between 1 and 365.");
        }

        if (RateLimit.PermitPerWindow < 1 || RateLimit.WindowSeconds < 1 || RateLimit.NodePermitPerWindow < 1)
        {
            throw new InvalidOperationException("ControlPlane:RateLimit:PermitPerWindow, NodePermitPerWindow and WindowSeconds must be positive.");
        }

        if (Scheduler.PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Scheduler:PollSeconds must be positive.");
        }

        if (Worker.MaxConcurrentRuns < 1)
        {
            throw new InvalidOperationException("ControlPlane:Worker:MaxConcurrentRuns must be at least 1.");
        }

        if (Worker.MaxConcurrentComputeTasks < 1)
        {
            throw new InvalidOperationException("ControlPlane:Worker:MaxConcurrentComputeTasks must be at least 1.");
        }

        try
        {
            Dispatch.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("ControlPlane:" + ex.Message, ex);
        }

        AzureAd.Validate();
        Bootstrap.Validate();
        Proxy.Validate();
        Notifications.Validate();
        RunTrace.Validate();
        Assistant.Validate();
        DataOps.Validate();
    }
}

/// <summary>How the control plane reaches the shadow catalog database.</summary>
/// <summary>
/// How the data-stream board tells a VENDOR DELIVERY apart from OUR PROCESSING, which is the single most
/// useful distinction it can draw and the one that decides who a finding belongs to.
///
/// <para>
/// A table that stopped receiving data because the vendor sent nothing is a completely different incident
/// from one that stopped because a transformation of ours broke, and mixing them on one board means every
/// reader has to re-derive which is which on every row. Worse, one upstream that goes quiet lights up its
/// whole downstream chain, so a single vendor outage can fill the board with a dozen findings that are all
/// the same finding. Splitting them means the source board answers "has the vendor delivered" and the
/// internal board answers "have we processed it", and neither is noise to the other.
/// </para>
///
/// <para>
/// The classification is deliberately configuration and not cleverness, because where an estate draws that
/// line is an estate's own convention. It is decided in order: a target schema known to be downstream wins,
/// then a target schema known to be a landing area, then the flow kind. The defaults suit the common
/// warehouse shape (raw/staging/archive schemas fed by acquisition and ingestion flows, curated schemas
/// built by stored procedures), and an estate that names things differently sets its own.
/// </para>
/// </summary>
public sealed class DataStreamOptions
{
    /// <summary>Flow kinds that bring data INTO the estate from outside it: an API, an SFTP server, an object
    /// store, landed files, or a source database. A stream of one of these kinds is a vendor delivery unless
    /// its target schema says otherwise.</summary>
    public List<string> SourceFlowKinds { get; set; } = ["api", "sftp", "cpy", "file", "ing"];

    /// <summary>Schemas that hold data as the vendor sent it: the landing, staging, and archive layers. A
    /// stream writing here is a vendor delivery whatever kind of flow loads it.</summary>
    public List<string> LandingSchemas { get; set; } = ["raw", "arc", "stg", "staging", "landing", "src", "ext", "pre"];

    /// <summary>Schemas that hold what we DERIVED: the curated warehouse. A stream writing here is our own
    /// processing even when an ingestion-shaped flow builds it, which is why this is checked first.</summary>
    public List<string> DownstreamSchemas { get; set; } = ["edw", "dwh", "dw", "mart", "rpt", "skey"];
}

public sealed class CatalogOptions
{
    /// <summary>A connection string or a secret reference (<c>${env:NAME}</c> / <c>${keyvault:vault/secret}</c>),
    /// resolved through the SqlFlow secret resolver. Defaults to <c>${env:SQLFLOW_CATALOG_DB}</c>.</summary>
    public string ConnectionReference { get; set; } = "${env:SQLFLOW_CATALOG_DB}";
}

/// <summary>JWT bearer settings. The signing key is symmetric (HS256) for the foundation; the Identity phase
/// replaces issuance with an asymmetric key / external provider without changing the validation contract.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "sqlflow-control-plane";

    public string Audience { get; set; } = "sqlflow";

    /// <summary>The HS256 signing key (>= 32 bytes). Required; sourced from a secret, never a literal.</summary>
    public string? SigningKey { get; set; }

    /// <summary>How long one issued session token is valid. A signed-in GUI rolls its token well before this lapses
    /// (<c>POST /auth/renew</c>), so this is not how long a user stays signed in: it is how long a leaked token keeps
    /// working, and how long a working day may pause before the session lapses on its own.</summary>
    public int AccessTokenMinutes { get; set; } = 720;

    /// <summary>The absolute ceiling on a rolled session: renewal is refused once this long has passed since the
    /// user actually authenticated, no matter how continuously they have used SQLFlow. Bounds "keep me signed in on
    /// this device" so it is a long convenience, not a permanent one.</summary>
    public int SessionMaxDays { get; set; } = 30;

    /// <summary>When set, the bootstrap token endpoint is enabled and issues a token for callers that present
    /// this exact secret. Left null in production once a real identity provider is in place (Identity phase);
    /// with it null the endpoint is not mapped at all.</summary>
    public string? BootstrapSecret { get; set; }
}

/// <summary>CORS allowlist. Empty means same-origin only (no cross-origin access granted).</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Microsoft Entra ID single sign-on. When enabled, the SPA signs the user in against Entra (MSAL, auth code +
/// PKCE) and posts the resulting ID token to <c>POST /api/v1/auth/exchange</c>; the control plane validates it
/// against the tenant's published keys and issues its own SQLFlow token, so every API call downstream uses one
/// token type regardless of how the user signed in.
/// </summary>
public sealed class AzureAdOptions
{
    /// <summary>Explicit on/off override for Entra single sign-on. Left unset (the default), SSO is offered
    /// automatically whenever a tenant id and client id are configured, so a deployment turns it on simply by
    /// supplying those credentials, with no separate flag to remember. Set it to <c>true</c> to require SSO (a
    /// missing credential is then a startup error), or to <c>false</c> to force it off even when credentials are
    /// present (a kill switch). The effective state is <see cref="IsEnabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>The directory (tenant) ids allowed to sign in. A token asserting any other tenant is rejected by
    /// <see cref="Security.EntraTokenValidator"/> regardless of how it was signed, so this list is the actual
    /// trust boundary: adding a tenant here is what grants its users access, not the app registration's own
    /// single-tenant/multi-tenant setting.</summary>
    public IReadOnlyList<string> AllowedTenantIds { get; set; } = [];

    /// <summary>The app registration (client) id the SPA signs in with; the audience the ID token must carry.</summary>
    public string? ClientId { get; set; }

    /// <summary>The OIDC authority host. Defaults to the public-cloud host (<c>https://login.microsoftonline.com</c>);
    /// override only for sovereign clouds (e.g. <c>https://login.microsoftonline.us</c>).</summary>
    public string? Authority { get; set; }

    /// <summary>The role a first-time Entra user is provisioned with (least privilege by default; an admin raises
    /// it afterwards).</summary>
    public string DefaultRole { get; set; } = "viewer";

    /// <summary>Whether both credentials Entra needs (at least one allowed tenant id and a client id) are
    /// configured, i.e. SSO CAN be offered. The auto-enable default keys on this.</summary>
    public bool HasCredentials
        => AllowedTenantIds.Any(id => !string.IsNullOrWhiteSpace(id)) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Whether Entra single sign-on is actually offered: the explicit <see cref="Enabled"/> flag when set,
    /// otherwise on automatically when <see cref="HasCredentials"/> is true. Local username/password sign-in is
    /// always available regardless; this only gates the additive Microsoft option.</summary>
    public bool IsEnabled => Enabled ?? HasCredentials;

    private string AuthorityHost
        => string.IsNullOrWhiteSpace(Authority) ? "https://login.microsoftonline.com" : Authority.TrimEnd('/');

    /// <summary>The authority the SPA authenticates against.
    /// <para>With exactly one allowed tenant this is that tenant's own authority, which matters for more than
    /// tidiness: a tenant-pinned authority resolves an external address (a consultant's <c>@partner.com</c>) as a
    /// B2B GUEST of that tenant, delegating the password check to their home tenant but issuing the token from
    /// this one. Sending the same person to the shared "organizations" endpoint instead resolves them to their own
    /// home tenant, where this app is unknown and consent has never been granted, so guest sign-in breaks.</para>
    /// <para>With more than one allowed tenant there is no single tenant to pin, so the shared endpoint is the only
    /// option and each additional tenant must consent to the app itself. Either way the actual tenant restriction is
    /// enforced afterward, token by token, in <see cref="Security.EntraTokenValidator"/> against
    /// <see cref="AllowedTenantIds"/>.</para></summary>
    public string ResolveAuthority()
    {
        var tenants = AllowedTenantIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        return tenants.Count == 1 ? TenantAuthority(tenants[0].Trim()) : $"{AuthorityHost}/organizations/v2.0";
    }

    /// <summary>The concrete, tenant-specific authority used to fetch one allowed tenant's own OIDC metadata
    /// (signing keys, issuer) so a token claiming to be from it can actually be verified against that tenant.</summary>
    public string TenantAuthority(string tenantId) => $"{AuthorityHost}/{tenantId}/v2.0";

    public void Validate()
    {
        // An explicit request for SSO with nothing to sign in against is a misconfiguration, not a silent no-op:
        // fail fast and name the missing credentials. (Leaving Enabled unset instead auto-enables only when both
        // are present, so this never fires for the credential-driven default.)
        if (Enabled == true && !HasCredentials)
        {
            throw new InvalidOperationException(
                "ControlPlane:AzureAd:Enabled is true but AllowedTenantIds and/or ClientId are missing. Provide at least one tenant id and a client id, or leave Enabled unset to enable SSO automatically only when the credentials are present.");
        }

        if (!IsEnabled)
        {
            return;
        }

        if (AllowedTenantIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("ControlPlane:AzureAd:AllowedTenantIds must not contain a blank entry.");
        }

        if (string.IsNullOrWhiteSpace(DefaultRole))
        {
            throw new InvalidOperationException("ControlPlane:AzureAd:DefaultRole must not be blank.");
        }
    }
}

/// <summary>
/// First-run provisioning. On startup the control plane (retrying until the catalog database is reachable)
/// applies pending catalog migrations, seeds the built-in roles, creates the initial admin user when configured
/// and absent, and registers the optional demo repo source. Everything is idempotent: a restart converges to the
/// same state and never overwrites operator changes (an existing admin's password is not reset).
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>Whether startup applies pending EF catalog migrations. Disable only when a DBA applies migration
    /// scripts out of band; pending migrations are then logged as a warning.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Whether startup may CREATE the catalog database when it does not exist (and initialise the catalog
    /// schema into an empty database). Default <c>false</c>: startup only migrates an EXISTING catalog and refuses
    /// to conjure a database or inject catalog tables into a populated non-catalog database, so a wrong or mistyped
    /// connection fails loudly instead of provisioning against the wrong (possibly production) server. Set
    /// <c>true</c> only for first-time provisioning or ephemeral/test databases.</summary>
    public bool AllowCreate { get; set; }

    /// <summary>The initial admin's sign-in name. Set together with <see cref="AdminPasswordReference"/>.</summary>
    public string? AdminUsername { get; set; }

    /// <summary>The initial admin's password as a secret reference (<c>${env:...}</c> / <c>${keyvault:...}</c>),
    /// resolved through the SqlFlow secret resolver. Never a literal in source control.</summary>
    public string? AdminPasswordReference { get; set; }

    /// <summary>An optional repo source registered at bootstrap, so a fresh install has a synced estate to look at.</summary>
    public DemoRepoOptions? DemoRepo { get; set; }

    public void Validate()
    {
        var hasUsername = !string.IsNullOrWhiteSpace(AdminUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(AdminPasswordReference);
        if (hasUsername != hasPassword)
        {
            throw new InvalidOperationException(
                "ControlPlane:Bootstrap:AdminUsername and ControlPlane:Bootstrap:AdminPasswordReference must be set together.");
        }

        if (DemoRepo is not null)
        {
            if (string.IsNullOrWhiteSpace(DemoRepo.Name) || string.IsNullOrWhiteSpace(DemoRepo.RemoteUrl))
            {
                throw new InvalidOperationException(
                    "ControlPlane:Bootstrap:DemoRepo requires both Name and RemoteUrl when configured.");
            }

            if (DemoRepo.SyncIntervalSeconds < 1)
            {
                throw new InvalidOperationException("ControlPlane:Bootstrap:DemoRepo:SyncIntervalSeconds must be positive.");
            }
        }
    }
}

/// <summary>The demo repo source bootstrap registers (same shape as an API registration).</summary>
public sealed class DemoRepoOptions
{
    public string? Name { get; set; }

    public string? RemoteUrl { get; set; }

    public string Branch { get; set; } = "main";

    public int SyncIntervalSeconds { get; set; } = 300;
}

/// <summary>The global fixed-window rate limit, partitioned per authenticated subject (or client IP when
/// anonymous), so one caller cannot exhaust the host.</summary>
public sealed class RateLimitOptions
{
    public int PermitPerWindow { get; set; } = 120;

    /// <summary>The per-node window for the node protocol, keyed by the node token's subject plus the node name
    /// each call carries (so a fleet sharing one token still gets a window per node): a node long-polls every few
    /// seconds, reports outcomes, and streams trace batches several times a second per executing run, so its
    /// ceiling is far above a person's. Still bounded, so a misbehaving node cannot flood.</summary>
    public int NodePermitPerWindow { get; set; } = 6000;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>The schedule scanner cadence: how often the control plane checks the catalog for due schedules and
/// enqueues their runs. Cron schedules have minute granularity, so the default catches a due schedule within a few
/// seconds of its minute; lower it only for tests that need a fast tick.</summary>
public sealed class SchedulerOptions
{
    public int PollSeconds { get; set; } = 15;
}

/// <summary>The control plane's in-process worker. <see cref="Pools"/> are the run pools it serves: empty (the
/// default) means it claims only untargeted runs, which is the single-node default; set it to also drain runs
/// routed to those pools. <see cref="Enabled"/> turns the in-process worker off entirely for API-only replicas:
/// in a scaled deployment the HTTP tier scales on request load behind the ingress while compute scales on queue
/// depth as separate worker processes, and this switch is what keeps the two independent.</summary>
public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;

    public string[] Pools { get; set; } = [];

    /// <summary>How many claimed runs this node executes at once. The queue's atomic claim already supports
    /// concurrent claimants, so this only sizes the node's own in-flight work; a saturated node stops claiming
    /// and leaves queued runs for other nodes. Minimum 1 (a strictly serial node).</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>How many compute tasks (interactive datasource inspections) this node executes at once, on a gate
    /// separate from the run gate so a node saturated with long runs still answers an operator promptly.</summary>
    public int MaxConcurrentComputeTasks { get; set; } = 2;
}

/// <summary>
/// Housekeeping for the per-run SQL trace. Every run persists its generated statements (the RunStatement table, a
/// full SQL blob per statement) and its events (RunEvent); across a busy estate these two tables grow without
/// bound and are almost never read once a run is old and succeeded. This prunes that detail on a cadence, keeping
/// only what stays useful: each pipeline's most recent run, every failed run (so the offending SQL is always there
/// to debug), any run still in flight, and anything still inside the grace window. The run header row itself (its
/// stats and its error message) is never touched, so the history and every failure reason stay complete and only
/// the heavy, unread detail is reclaimed. The manual GUI trigger runs the exact same prune on demand.
/// </summary>
public sealed class RunTraceRetentionOptions
{
    /// <summary>Turns the automatic pruning sweep off. Off means the two trace tables grow unbounded until someone
    /// triggers the manual cleanup, which still works. On by default. Note the sweep also does nothing while no
    /// retention period is set (that setting is operator-tunable from the GUI and defaults to "keep forever").</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the automatic prune sweep runs (when enabled and a retention period is set).</summary>
    public int PollSeconds { get; set; } = 3600;

    public void Validate()
    {
        if (PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:RunTrace:PollSeconds must be positive.");
        }
    }
}

/// <summary>
/// Reverse-proxy awareness. Behind an ingress or load balancer the raw connection address is the proxy's, which
/// would collapse every caller into one rate-limit partition and one login-throttle key. When enabled, the
/// standard <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> headers are honored, but ONLY from the proxies listed
/// here (a client on the open internet can never spoof its address by sending the header itself). List the
/// ingress/LB addresses as CIDRs in <see cref="KnownNetworks"/> (for example <c>10.0.0.0/8</c> inside a cluster)
/// or as single IPs in <see cref="KnownProxies"/>.
/// </summary>
public sealed class ProxyOptions
{
    public bool Enabled { get; set; }

    /// <summary>Trusted proxy networks in CIDR form (for example <c>10.244.0.0/16</c>).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Trusted individual proxy addresses (IPv4 or IPv6).</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>How many proxy hops to unwind from <c>X-Forwarded-For</c>; 1 for a single ingress (the default),
    /// 2 when a CDN sits in front of the ingress, and so on.</summary>
    public int ForwardLimit { get; set; } = 1;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (KnownNetworks.Length == 0 && KnownProxies.Length == 0)
        {
            throw new InvalidOperationException(
                "ControlPlane:Proxy is enabled but trusts no proxies; set KnownNetworks (CIDRs) and/or KnownProxies (IPs) to the ingress addresses. Trusting nothing would silently ignore the forwarded headers.");
        }

        if (ForwardLimit < 1)
        {
            throw new InvalidOperationException("ControlPlane:Proxy:ForwardLimit must be at least 1.");
        }

        foreach (var network in KnownNetworks)
        {
            ParseNetwork(network);
        }

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                throw new InvalidOperationException($"ControlPlane:Proxy:KnownProxies entry '{proxy}' is not a valid IP address.");
            }
        }
    }

    /// <summary>Parses a CIDR into its prefix and length, throwing a configuration error naming the bad entry.</summary>
    public static (IPAddress Prefix, int PrefixLength) ParseNetwork(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var length)
            && length >= 0
            && length <= (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
        {
            return (prefix, length);
        }

        throw new InvalidOperationException(
            $"ControlPlane:Proxy:KnownNetworks entry '{cidr}' is not a valid CIDR (expected e.g. 10.0.0.0/8 or fd00::/8).");
    }
}

/// <summary>The managed git-to-catalog sync. <see cref="PollSeconds"/> is how often the control plane scans for
/// repo sources due to sync (each source has its own interval); lower it only for tests that need a fast tick.</summary>
public sealed class ManagedSyncOptions
{
    /// <summary>Whether this control-plane instance runs the managed sync loop. On by default: the control
    /// plane owns the git-to-catalog sync (the estate's compute workers run the CLI drain loop and have no
    /// sync service), so its environment must also carry the data-plane connection references the lineage
    /// step resolves. Turn this off for a local dev instance sharing the production catalog: the claim is
    /// queue-based, so a laptop that participates steals due syncs from the deployed estate and runs them
    /// with its own filesystem, credentials, and code version.</summary>
    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 30;

    /// <summary>Whether the managed sync runs the connected (derived) lineage tier: it opens each referenced SQL
    /// Server and expands module bodies (procedures, views) through the T-SQL parser, so a stored-procedure flow
    /// gains the reads/writes of the procedure it executes instead of appearing as an edgeless root. On by
    /// default so the estate's lineage is complete without an operator running <c>db sync --connect</c> by hand;
    /// the connection uses the source's own resolved secrets, and a connect failure is non-fatal (the offline
    /// tiers still land and the failure is recorded as a warning). Turn it off for a deployment whose control
    /// plane cannot reach the data-plane SQL Servers, so those syncs stay purely offline.</summary>
    public bool ConnectLineage { get; set; } = true;
}

/// <summary>
/// The notification pipeline: detects failed runs (and failed assertions on green runs) in the catalog and sends
/// opted-in users email and/or Slack messages, immediately (cooldown-coalesced) or as periodic digests. The
/// service itself always runs (it keeps the event stream current); a channel is offered to users only when its
/// section here is configured. Secrets (SMTP password, Graph client secret, Slack bot token) are references
/// (<c>${env:...}</c> / <c>${keyvault:...}</c>) resolved through the SqlFlow secret resolver, never literals.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>The shortest estate digest period, matching the floor a subscription digest accepts.</summary>
    public const int MinDigestIntervalMinutes = 5;

    /// <summary>The longest estate digest period: one week.</summary>
    public const int MaxDigestIntervalMinutes = 10080;

    /// <summary>Turns the notification background service off entirely (nothing is detected or sent, and the
    /// subscription API reports every channel unavailable). On by default: with no channel configured the service
    /// idles harmlessly, so the default costs nothing until someone configures a channel and opts in.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The pipeline tick: how often detection, dispatch, and sending run. This bounds how quickly an
    /// "immediate" subscriber hears about a failure.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>How far behind the watermark each detection scan re-reads (absorbing writer clock skew and detail
    /// rows landing moments after their run row). Re-scanning is deduplicated, so a generous overlap is free.</summary>
    public int DetectionOverlapMinutes { get; set; } = 30;

    /// <summary>How many send attempts a message gets before it is recorded as permanently failed. Retries back
    /// off (1, 5, 15, then 60 minutes), so transient SMTP / Graph / Slack outages self-heal without spam.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>How long detected events are kept for audit ("what would have been notified") before pruning.</summary>
    public int EventRetentionDays { get; set; } = 30;

    /// <summary>
    /// Whether the control plane generates estate digests on its own. On by default and deliberately independent
    /// of whether anybody subscribes or any channel is configured: the digest is the standing record of what went
    /// wrong in each window, readable in the GUI, and a subscriber is one way to have it pushed, not the reason it
    /// exists. Turning this off leaves on-demand generation from the GUI working.
    /// </summary>
    public bool DigestEnabled { get; set; } = true;

    /// <summary>The estate digest period in minutes (1440 = one digest a day). Windows chain without gaps, so
    /// changing this re-paces the next digest rather than losing or repeating anything.</summary>
    public int DigestIntervalMinutes { get; set; } = 1440;

    /// <summary>Where the digest boundary sits inside the period, in minutes past the aligned UTC boundary: with
    /// the daily default, 0 generates at 00:00 UTC and 300 at 05:00 UTC. Boundaries are computed from the Unix
    /// epoch rather than from process start, so a restart never shifts the rhythm.</summary>
    public int DigestOffsetMinutes { get; set; }

    /// <summary>How long generated digests are kept before pruning.</summary>
    public int DigestRetentionDays { get; set; } = 365;

    /// <summary>How long sent / failed deliveries are kept as per-user history before pruning.</summary>
    public int DeliveryRetentionDays { get; set; } = 90;

    /// <summary>The GUI's public base URL (for example <c>https://sqlflow.example.com</c>), used to render "open
    /// this run" links in messages. When unset, messages carry no links.</summary>
    public string? GuiBaseUrl { get; set; }

    public EmailNotificationOptions Email { get; set; } = new();

    public SlackNotificationOptions Slack { get; set; } = new();

    /// <summary>Whether the email channel can actually send (a provider is configured).</summary>
    public bool EmailConfigured => !string.Equals(Email.Provider, EmailNotificationOptions.ProviderNone, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the Slack channel can actually send (a bot token reference is configured).</summary>
    public bool SlackConfigured => !string.IsNullOrWhiteSpace(Slack.BotTokenReference);

    public void Validate()
    {
        if (PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:PollSeconds must be positive.");
        }

        if (DetectionOverlapMinutes < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:DetectionOverlapMinutes must be positive.");
        }

        if (MaxDeliveryAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:MaxDeliveryAttempts must be between 1 and 10.");
        }

        if (EventRetentionDays < 1 || DeliveryRetentionDays < 1 || DigestRetentionDays < 1)
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:EventRetentionDays, DeliveryRetentionDays and DigestRetentionDays must be positive.");
        }

        if (DigestIntervalMinutes is < MinDigestIntervalMinutes or > MaxDigestIntervalMinutes)
        {
            throw new InvalidOperationException(
                $"ControlPlane:Notifications:DigestIntervalMinutes must be between {MinDigestIntervalMinutes} and {MaxDigestIntervalMinutes} (one week).");
        }

        if (DigestOffsetMinutes < 0 || DigestOffsetMinutes >= DigestIntervalMinutes)
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:DigestOffsetMinutes must be at least 0 and less than DigestIntervalMinutes.");
        }

        if (!string.IsNullOrWhiteSpace(GuiBaseUrl)
            && (!Uri.TryCreate(GuiBaseUrl, UriKind.Absolute, out var gui)
                || (gui.Scheme != Uri.UriSchemeHttp && gui.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:GuiBaseUrl must be an absolute http(s) URL when set.");
        }

        Email.Validate();
        Slack.Validate();
    }
}

/// <summary>The email channel. <see cref="Provider"/> picks the transport: <c>none</c> (email off, the default),
/// <c>smtp</c> (any SMTP relay, authenticated or not), or <c>graph</c> (Microsoft Graph <c>sendMail</c> as a
/// configured mailbox, the Microsoft 365 path that needs no SMTP relay at all).</summary>
public sealed class EmailNotificationOptions
{
    public const string ProviderNone = "none";
    public const string ProviderSmtp = "smtp";
    public const string ProviderGraph = "graph";

    public string Provider { get; set; } = ProviderNone;

    /// <summary>The From address for SMTP mail (Graph sends as its <see cref="GraphEmailOptions.SenderId"/>
    /// mailbox, but this is still used for display defaults). Required whenever a provider is configured.</summary>
    public string? FromAddress { get; set; }

    /// <summary>The display name shown next to the From address.</summary>
    public string FromDisplayName { get; set; } = "SQLFlow";

    public SmtpEmailOptions Smtp { get; set; } = new();

    public GraphEmailOptions Graph { get; set; } = new();

    public void Validate()
    {
        var provider = Provider?.Trim().ToLowerInvariant();
        if (provider is not (ProviderNone or ProviderSmtp or ProviderGraph))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Provider must be one of: none, smtp, graph.");
        }

        if (provider == ProviderNone)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FromAddress) || !System.Net.Mail.MailAddress.TryCreate(FromAddress, out _))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:FromAddress must be a valid email address when a provider is configured.");
        }

        if (provider == ProviderSmtp)
        {
            Smtp.Validate();
        }
        else
        {
            Graph.Validate();
        }
    }
}

/// <summary>An SMTP relay. Credentials are optional (an internal relay often authenticates by network) but must
/// be given as a pair; both are secret references resolved at send time, so rotation needs no restart.</summary>
public sealed class SmtpEmailOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>The transport security: <c>starttls</c> (default, port 587), <c>ssl</c> (implicit TLS, port 465),
    /// <c>auto</c> (opportunistic STARTTLS), or <c>none</c> (plain; only for an isolated internal relay).</summary>
    public string SslMode { get; set; } = "starttls";

    /// <summary>The SMTP username, or a secret reference to it; null for an unauthenticated relay.</summary>
    public string? UsernameReference { get; set; }

    /// <summary>The SMTP password as a secret reference; required exactly when a username is set.</summary>
    public string? PasswordReference { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:Host is required when the email provider is smtp.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Smtp:Port must be between 1 and 65535.");
        }

        var mode = SslMode?.Trim().ToLowerInvariant();
        if (mode is not ("none" or "starttls" or "ssl" or "auto"))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:SslMode must be one of: none, starttls, ssl, auto.");
        }

        if (string.IsNullOrWhiteSpace(UsernameReference) != string.IsNullOrWhiteSpace(PasswordReference))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:UsernameReference and PasswordReference must be set together.");
        }

        if (TimeoutSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Smtp:TimeoutSeconds must be positive.");
        }
    }
}

/// <summary>
/// Microsoft Graph <c>sendMail</c>. The message is sent as the <see cref="SenderId"/> mailbox (the app needs the
/// application permission <c>Mail.Send</c>, ideally scoped to that mailbox with an application access policy).
/// Authentication uses the explicit app registration below when all three values are set; otherwise the ambient
/// SqlFlow Azure credential (managed identity in Azure, the shared <c>SQLFLOW_AZURE_AUTH</c> intent elsewhere).
/// </summary>
public sealed class GraphEmailOptions
{
    /// <summary>The sending mailbox: a user principal name (<c>alerts@contoso.com</c>) or object id.</summary>
    public string? SenderId { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The app's client secret as a secret reference; never a literal.</summary>
    public string? ClientSecretReference { get; set; }

    /// <summary>The Graph endpoint; override only for sovereign clouds.</summary>
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    /// <summary>The token scope requested for Graph; must match <see cref="BaseUrl"/>'s cloud.</summary>
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SenderId))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Graph:SenderId (the sending mailbox) is required when the email provider is graph.");
        }

        var explicitApp = new[] { TenantId, ClientId, ClientSecretReference }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (explicitApp is not (0 or 3))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Graph:TenantId, ClientId, and ClientSecretReference must be set together (or all omitted to use the ambient Azure credential).");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Graph:BaseUrl must be an absolute https URL.");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Graph:Scope must not be blank.");
        }
    }
}

/// <summary>The Slack channel: proactive messages posted with a bot token (<c>chat:write</c>; direct messages by
/// email additionally need <c>users:read.email</c> and <c>im:write</c>). Configured when
/// <see cref="BotTokenReference"/> is set; the same Slack app the SqlFlow Slack assistant uses works here.</summary>
public sealed class SlackNotificationOptions
{
    /// <summary>The bot token (<c>xoxb-...</c>) as a secret reference; never a literal.</summary>
    public string? BotTokenReference { get; set; }

    /// <summary>The Slack Web API base; override only for testing.</summary>
    public string BaseUrl { get; set; } = "https://slack.com/api/";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotTokenReference))
        {
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Slack:BaseUrl must be an absolute http(s) URL.");
        }
    }
}

/// <summary>
/// The GUI chat assistant (section <c>ControlPlane:Assistant</c>): the same SqlFlow.Assistant core
/// the Slack bot runs, hosted behind the control plane's <c>/api/v1/chat</c> surface with GUI
/// (Markdown) formatting and streaming. Disabled by default: the chat endpoints then report the
/// feature as unavailable instead of failing startup, so an estate without an AI deployment runs
/// unchanged. Unlike the Slack bot, no assistant access token is configured here: every agent run
/// forwards the calling user's own bearer to the MCP server, so tool access is exactly that user's
/// access. The <c>ApiKey</c> fields accept <c>${env:...}</c>/<c>${keyvault:...}</c> references,
/// resolved through the engine's secret chain at first use.
/// </summary>
public sealed class AssistantChatOptions
{
    /// <summary>Turns the chat assistant on. Off, the chat endpoints answer with a clear
    /// "not configured" problem and the capabilities endpoint reports it, so the GUI can hide chat.</summary>
    public bool Enabled { get; set; }

    /// <summary>The model provider answering questions (AzureFoundry, OpenAI, Anthropic).</summary>
    public SqlFlow.Assistant.AssistantProvider Provider { get; set; } = SqlFlow.Assistant.AssistantProvider.AzureFoundry;

    public SqlFlow.Assistant.McpOptions Mcp { get; set; } = new();

    public SqlFlow.Assistant.FoundryOptions Foundry { get; set; } = new();

    public SqlFlow.Assistant.OpenAIOptions OpenAI { get; set; } = new();

    public SqlFlow.Assistant.AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>Ceiling for one agent run before it is cancelled and reported as timed out.</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    /// <summary>How many prior conversation messages are replayed when the provider-side
    /// conversation must be rebuilt (the persisted transcript is the durable record).</summary>
    public int MaxReplayMessages { get; set; } = 20;

    /// <summary>How many image attachments one question may carry. 0 disables image input.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest accepted image (bytes); a larger attachment is rejected with a clear error.</summary>
    public long MaxImageBytes { get; set; } = 8_000_000;

    /// <summary>Optional GUI base URL for absolute entity links in answers; empty links relative,
    /// which is correct when the chat renders inside the GUI itself.</summary>
    public string GuiBaseUrl { get; set; } = "";

    /// <summary>Maps the bound configuration onto the shared assistant settings the gateways
    /// consume (GUI surface). The nested option instances are shared, not copied.</summary>
    public SqlFlow.Assistant.AssistantSettings ToAssistantSettings() => new()
    {
        Provider = Provider,
        Surface = SqlFlow.Assistant.AssistantSurface.Gui,
        Mcp = Mcp,
        Foundry = Foundry,
        OpenAI = OpenAI,
        Anthropic = Anthropic,
        RunTimeoutSeconds = RunTimeoutSeconds,
        MaxReplayMessages = MaxReplayMessages,
        MaxImages = MaxImages,
        MaxImageBytes = MaxImageBytes,
        GuiBaseUrl = GuiBaseUrl,
    };

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        var missing = new List<string>();
        ToAssistantSettings().CollectMissing("ControlPlane:Assistant", missing);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "ControlPlane:Assistant is enabled but incomplete:\n  - " + string.Join("\n  - ", missing));
        }
    }
}

/// <summary>
/// The data-operations surface: the standard warehouse maintenance actions and the old-versus-new baseline
/// comparison. Both are READ-ONLY against the warehouse (they measure and emit review-ready SQL; nothing here
/// executes a mutating statement), and both are OFF by default. A deployment that has finished its migration,
/// or one that simply does not want an interactive surface reaching its warehouse and its old estate, leaves
/// the switch alone and the operations are refused at the trust boundary with a clear "not enabled" problem
/// rather than being queued.
///
/// Environment: <c>ControlPlane__DataOps__Enabled=true</c>, and
/// <c>ControlPlane__DataOps__Comparison__LinkedServers__0=OLDPROD</c> for each linked server that may be
/// compared against.
/// </summary>
public sealed class DataOpsOptions
{
    /// <summary>Turns the maintenance and comparison operations on. Off, <c>POST /datasources/tasks</c> refuses
    /// them and the capabilities endpoint reports the switch, so a GUI or an assistant can explain rather than
    /// fail.</summary>
    public bool Enabled { get; set; }

    public BaselineComparisonOptions Comparison { get; set; } = new();

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        Comparison.Validate();
    }
}

/// <summary>
/// The baseline comparison's configuration. A comparison reaches the OLD estate through a linked server, whose
/// name becomes an identifier in generated SQL and a route to another database, so the permitted names are
/// configuration rather than something a request may choose. With none configured the comparison operations
/// are refused with a message naming this setting; the maintenance actions are unaffected.
/// </summary>
public sealed class BaselineComparisonOptions
{
    /// <summary>The linked servers a comparison may name (for example OLDPROD). Matched case-insensitively.</summary>
    public IList<string> LinkedServers { get; set; } = [];

    /// <summary>The linked server used when a request does not name one. Must be in
    /// <see cref="LinkedServers"/>; blank means a request must always name one explicitly.</summary>
    public string DefaultLinkedServer { get; set; } = "";

    /// <summary>The databases on those linked servers a comparison may name. Empty means any database the
    /// linked server's login can reach, which is the usual case: the linked server itself is the boundary.</summary>
    public IList<string> Databases { get; set; } = [];

    /// <summary>The configured names, trimmed and deduplicated, as the validators consume them.</summary>
    public IReadOnlyCollection<string> AllowedLinkedServers()
        => LinkedServers
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Validate()
    {
        var allowed = AllowedLinkedServers();
        if (!string.IsNullOrWhiteSpace(DefaultLinkedServer)
            && !allowed.Contains(DefaultLinkedServer.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ControlPlane:DataOps:Comparison:DefaultLinkedServer is '{DefaultLinkedServer}', which is not " +
                "in ControlPlane:DataOps:Comparison:LinkedServers. Add it there, or clear the default.");
        }
    }
}
