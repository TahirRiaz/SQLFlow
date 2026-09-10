using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// First-run provisioning: brings a fresh (or upgraded) deployment to a usable state without manual SQL. In order:
/// applies pending catalog migrations (so a pointed-at empty database self-provisions), seeds the built-in roles,
/// creates the configured initial admin when absent, and registers the configured demo repo source. Every step is
/// idempotent and never overwrites operator state: an existing admin keeps their password, an edited role keeps
/// its scopes. Runs as a background service that retries with backoff until the catalog is reachable, so a control
/// plane that starts before its database (compose ordering, cold restores) converges instead of crashing; the
/// readiness probe reflects the catalog being unavailable in the meantime.
/// </summary>
public sealed class BootstrapProvisioningService : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)];

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly CatalogConnectionProvider _connection;
    private readonly ISecretResolver _secrets;
    private readonly IPasswordHasher<CatalogUser> _hasher;
    private readonly ControlPlaneOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BootstrapProvisioningService> _logger;

    public BootstrapProvisioningService(
        CatalogConnectionProvider connection, ISecretResolver secrets, IPasswordHasher<CatalogUser> hasher,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, ILogger<BootstrapProvisioningService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _secrets = secrets;
        _hasher = hasher;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await ProvisionAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Bootstrap provisioning completed.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (CatalogProvisioningException ex)
            {
                // A deterministic configuration mistake (the target database is missing, or is not a catalog).
                // Retrying cannot fix it and creating the database is exactly what we refuse to do, so stop and
                // surface it. Readiness stays red, signalling the misconfiguration without touching the database.
                _logger.LogCritical(
                    "Bootstrap provisioning refused: {Error} Set ControlPlane:Bootstrap:AllowCreate=true only if you intend to provision this exact database.",
                    ex.Message);
                return;
            }
            catch (Exception ex)
            {
                var delay = attempt < RetryDelays.Length ? RetryDelays[attempt] : MaxRetryDelay;
                _logger.LogError(
                    "Bootstrap provisioning attempt {Attempt} failed ({Error}); retrying in {Delay}s.",
                    attempt + 1, SecretHygiene.RedactedMessage(ex), (int)delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ProvisionAsync(CancellationToken ct)
    {
        var connectionString = _connection.ConnectionString;

        // Always announce the resolved target up front (secret-free), so an operator can see at a glance which
        // database this control plane is about to bootstrap, before any schema or seed work happens.
        _logger.LogInformation(
            "Catalog target: {Target} (Bootstrap:AllowCreate={AllowCreate}).",
            CatalogDatabase.DescribeTarget(connectionString), _options.Bootstrap.AllowCreate);

        if (_options.Bootstrap.ApplyMigrations)
        {
            if (_options.Bootstrap.AllowCreate)
            {
                // Explicit opt-in: create the database and initialise the catalog when absent. Reserved for
                // first-time provisioning and ephemeral/test catalogs.
                _logger.LogWarning(
                    "Bootstrap:AllowCreate is enabled: the catalog database will be created if it does not exist ({Target}).",
                    CatalogDatabase.DescribeTarget(connectionString));
                await CatalogDatabase.MigrateAsync(connectionString, ct).ConfigureAwait(false);
            }
            else
            {
                // Guarded default: migrate an existing catalog only. A missing database or a populated
                // non-catalog database raises CatalogProvisioningException instead of being provisioned.
                await CatalogDatabase.MigrateExistingAsync(connectionString, ct).ConfigureAwait(false);
            }
        }
        else
        {
            var (_, pending) = await CatalogDatabase.StatusAsync(connectionString, ct).ConfigureAwait(false);
            if (pending.Count > 0)
            {
                _logger.LogWarning(
                    "Catalog migrations are disabled (ControlPlane:Bootstrap:ApplyMigrations=false) but {Count} migration(s) are pending: {Migrations}. Apply them out of band; parts of the API may fail until then.",
                    pending.Count, string.Join(", ", pending));
            }
        }

        await using var catalog = CatalogDatabase.Create(connectionString);
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        await UserStore.EnsureRoleAsync(catalog, RoleNames.Admin, "read operate admin author",
            "Full control: read, operate, author pull requests, and user administration.", nowUtc, ct).ConfigureAwait(false);
        await UserStore.EnsureRoleAsync(catalog, RoleNames.Operator, "read operate author",
            "Day-to-day operations: read everything, trigger and cancel runs, manage schedules and repo sources, and propose pipelines as pull requests.",
            nowUtc, ct).ConfigureAwait(false);
        await UserStore.EnsureRoleAsync(catalog, RoleNames.Viewer, "read",
            "Read-only access to the whole API surface.", nowUtc, ct).ConfigureAwait(false);

        await ProvisionAdminAsync(catalog, nowUtc, ct).ConfigureAwait(false);
        await ProvisionDemoRepoAsync(catalog, nowUtc, ct).ConfigureAwait(false);
        await EnsureDefaultPoolFloorAsync(catalog, nowUtc, ct).ConfigureAwait(false);
    }

    /// <summary>Seeds the default pool with an always-on floor of one worker, so a fresh SQLFlow always has a node
    /// running rather than an empty, scale-to-zero fleet an operator has to notice and turn on. Applied only when the
    /// default pool has no desired state yet, so it never overrides an operator who later set the floor (including to
    /// zero for pure scale-to-zero); it is the bootstrap default, not an enforced minimum.</summary>
    private async Task EnsureDefaultPoolFloorAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct)
    {
        var existing = await WorkerPoolStore.GetDesiredAsync(catalog, string.Empty, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await WorkerPoolStore.SaveScaleAsync(
            catalog, string.Empty, minReplicas: 1, manualReplicas: 0, manualUntilUtc: null,
            updatedBy: "bootstrap", nowUtc: nowUtc, ct).ConfigureAwait(false);
        _logger.LogInformation("Seeded the default worker pool with an always-on floor of 1 (bootstrap default).");
    }

    private async Task ProvisionAdminAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct)
    {
        var bootstrap = _options.Bootstrap;
        if (string.IsNullOrWhiteSpace(bootstrap.AdminUsername))
        {
            if (!await UserStore.AnyUsersAsync(catalog, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "No users exist and no initial admin is configured (ControlPlane:Bootstrap:AdminUsername / AdminPasswordReference). Only the bootstrap secret can access the API until a user is provisioned.");
            }

            return;
        }

        var existing = await UserStore.FindByUsernameAsync(catalog, bootstrap.AdminUsername, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Converged already. The password is deliberately NOT reset: the configured reference seeds the first
            // credential, it does not manage the credential's lifecycle.
            return;
        }

        var password = _secrets.Resolve(bootstrap.AdminPasswordReference!);
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogError(
                "The initial admin password reference resolved to an empty value; the admin user '{Username}' was NOT created. Fix ControlPlane:Bootstrap:AdminPasswordReference (or the secret it points at) and restart.",
                bootstrap.AdminUsername);
            return;
        }

        if (password.Length < UserEndpoints.MinPasswordLength)
        {
            _logger.LogError(
                "The initial admin password is shorter than {Min} characters; the admin user '{Username}' was NOT created. Set a stronger secret and restart.",
                UserEndpoints.MinPasswordLength, bootstrap.AdminUsername);
            return;
        }

        var username = bootstrap.AdminUsername.Trim();
        var hash = _hasher.HashPassword(new CatalogUser { Username = username }, password);
        var (status, id) = await UserStore.CreateLocalAsync(
            catalog, username, hash, RoleNames.Admin, email: null, displayName: null, nowUtc, ct).ConfigureAwait(false);
        switch (status)
        {
            case UserCreateStatus.Created:
                _logger.LogInformation("Initial admin user '{Username}' provisioned ({UserId}).", username, id);
                break;
            case UserCreateStatus.UsernameTaken:
                // A concurrent control-plane node won the race; converged either way.
                break;
            case UserCreateStatus.UnknownRole:
                throw new InvalidOperationException(
                    "The admin role vanished between seeding and admin creation; retrying bootstrap.");
        }
    }

    private async Task ProvisionDemoRepoAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct)
    {
        var demo = _options.Bootstrap.DemoRepo;
        if (demo is null)
        {
            return;
        }

        // Config is desired state: re-registering on every start keeps the source aligned with configuration
        // (UpsertAsync updates in place and preserves the sync schedule of an existing source).
        var id = await RepoSourceStore.UpsertAsync(
            catalog, demo.Name!, demo.RemoteUrl!, demo.Branch, enabled: true, demo.SyncIntervalSeconds, nowUtc, ct: ct)
            .ConfigureAwait(false);
        _logger.LogInformation("Demo repo source '{Name}' registered ({SourceId}).", demo.Name, id);
    }
}
