using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// The start-up pass: brings a fresh (or upgraded) deployment to a usable state from its configuration alone, in one ordered
/// pass of named steps, so readiness and <c>GET /api/v1/bootstrap</c> say which step the control plane is on and why one
/// failed. In order: applies pending catalog migrations (so a pointed-at empty database self-provisions), migrates and
/// verifies every module database (refusing to run, by stopping the host, when one is missing, behind or ahead of the
/// build), seeds the built-in roles, creates the configured initial admin when absent, writes the repo sources the
/// configuration names, makes the node credential it names the one active node token, seeds the default worker pool's
/// floor, and then applies the steps the host's modules add (<see cref="IBootstrapStep"/>), in the order they registered.
/// </summary>
/// <remarks>
/// Every step is idempotent and never overwrites operator state: an existing admin keeps their password, an edited role
/// keeps its scopes, a pool floor someone set stays. A step that fails is retried with backoff, from that step, so a
/// control plane that starts before its database (compose ordering, cold restores) or its vault converges instead of
/// crashing; the steps before it are not applied again in the same pass. A mistake retrying cannot fix (a database that
/// is missing or not a catalog, a module database that is not current) stops the pass where it is.
/// </remarks>
public sealed class BootstrapProvisioningService : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)];

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>The name the configured node token is listed under by its owner.</summary>
    public const string NodeTokenName = "Node credential set by the control plane's configuration";

    private readonly CatalogConnectionProvider _connection;
    private readonly ISecretResolver _secrets;
    private readonly IPasswordHasher<CatalogUser> _hasher;
    private readonly ControlPlaneOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BootstrapProvisioningService> _logger;
    private readonly IReadOnlyList<ModuleDatabase> _moduleDatabases;
    private readonly IModuleDatabaseConnections _moduleConnections;
    private readonly ModuleDatabaseVerification _moduleVerification;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IBootstrapStep> _moduleSteps;
    private readonly BootstrapProgress _progress;
    private readonly RepoSyncSignal _syncSignal;

    public BootstrapProvisioningService(
        CatalogConnectionProvider connection, ISecretResolver secrets, IPasswordHasher<CatalogUser> hasher,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, ILogger<BootstrapProvisioningService> logger,
        IEnumerable<ModuleDatabase> moduleDatabases, IModuleDatabaseConnections moduleConnections,
        ModuleDatabaseVerification moduleVerification, IHostApplicationLifetime lifetime, IServiceProvider services,
        IEnumerable<IBootstrapStep> moduleSteps, BootstrapProgress progress, RepoSyncSignal syncSignal)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(moduleDatabases);
        ArgumentNullException.ThrowIfNull(moduleConnections);
        ArgumentNullException.ThrowIfNull(moduleVerification);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleSteps);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(syncSignal);
        _connection = connection;
        _secrets = secrets;
        _hasher = hasher;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _moduleDatabases = [.. moduleDatabases];
        _moduleConnections = moduleConnections;
        _moduleVerification = moduleVerification;
        _lifetime = lifetime;
        _services = services;
        _moduleSteps = [.. moduleSteps];
        _progress = progress;
        _syncSignal = syncSignal;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Steps())
        {
            if (string.IsNullOrWhiteSpace(step.Name) || !names.Add(step.Name))
            {
                throw new InvalidOperationException($"The start-up step '{step.Name}' is blank or registered more than once; every step needs a name of its own.");
            }
        }

        _progress.Plan(names);
    }

    /// <summary>A step of the pass: its name, and what applying it does.</summary>
    private sealed record Step(string Name, Func<IServiceProvider, CancellationToken, Task<string>> ApplyAsync);

    /// <summary>The pass's steps in order: the built-in ones, then the modules'.</summary>
    private IReadOnlyList<Step> Steps()
        =>
        [
            new("catalog", (_, ct) => MigrateCatalogAsync(ct)),
            new("module-databases", (_, ct) => ProvisionModuleDatabasesAsync(ct)),
            new("roles", (_, ct) => SeedRolesAsync(ct)),
            new("admin", (_, ct) => ProvisionAdminAsync(ct)),
            new("repositories", (_, ct) => ProvisionRepositoriesAsync(ct)),
            new("external-triggers", (_, ct) => ProvisionExternalTriggersAsync(ct)),
            new("node-credential", (_, ct) => ProvisionNodeCredentialAsync(ct)),
            new("worker-pool", (_, ct) => EnsureDefaultPoolFloorAsync(ct)),
            .. _moduleSteps.Select(step => new Step(step.Name, step.ApplyAsync)),
        ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var steps = Steps();
        var next = 0;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var step = steps[next];
            try
            {
                _progress.Started(step.Name, Now());
                string summary;
                await using (var scope = _services.CreateAsyncScope())
                {
                    summary = await step.ApplyAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                }

                _progress.Succeeded(step.Name, summary, Now());
                _logger.LogInformation("Start-up step '{Step}' done: {Summary}.", step.Name, summary);
                failures = 0;
                if (++next == steps.Count)
                {
                    // The exact line SQLFlow's deployment scripts wait for before they read anything later in the log.
                    _logger.LogInformation("Bootstrap provisioning completed.");
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (CatalogProvisioningException ex)
            {
                // A deterministic configuration mistake (the target database is missing, or is not a catalog). Retrying
                // cannot fix it and creating the database is exactly what we refuse to do, so stop and surface it.
                // Readiness stays red, signalling the misconfiguration without touching the database.
                _progress.Refused(step.Name, ex.Message, Now());
                _logger.LogCritical(
                    "Start-up step '{Step}' refused: {Error} Set ControlPlane:Bootstrap:AllowCreate=true only if you intend to provision this exact database.",
                    step.Name, ex.Message);
                return;
            }
            catch (ModuleDatabaseException ex)
            {
                // A module database is missing, behind or ahead of this build, or refused provisioning. Serving the API
                // over it would fail the module's requests one by one, so the control plane refuses to run: readiness
                // carries the refusal and the host stops.
                _progress.Refused(step.Name, ex.Message, Now());
                _moduleVerification.MarkRefused(ex.Message);
                _logger.LogCritical("The control plane refuses to run: {Error}", ex.Message);
                _lifetime.StopApplication();
                return;
            }
            catch (Exception ex)
            {
                var error = SecretHygiene.RedactedMessage(ex);
                _progress.Failed(step.Name, error, Now());
                var delay = failures < RetryDelays.Length ? RetryDelays[failures] : MaxRetryDelay;
                failures++;
                _logger.LogError(
                    "Start-up step '{Step}' failed on attempt {Attempt} ({Error}); retrying in {Delay}s.",
                    step.Name, failures, error, (int)delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private async Task<string> MigrateCatalogAsync(CancellationToken ct)
    {
        var connectionString = _connection.ConnectionString;

        // Always announce the resolved target up front (secret-free), so an operator can see at a glance which database
        // this control plane is about to bootstrap, before any schema or seed work happens.
        _logger.LogInformation(
            "Catalog target: {Target} (Bootstrap:AllowCreate={AllowCreate}).",
            CatalogDatabase.DescribeTarget(connectionString), _options.Bootstrap.AllowCreate);

        if (!_options.Bootstrap.ApplyMigrations)
        {
            var (_, pending) = await CatalogDatabase.StatusAsync(connectionString, ct).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                return "migrations are applied out of band; none is pending";
            }

            _logger.LogWarning(
                "Catalog migrations are disabled (ControlPlane:Bootstrap:ApplyMigrations=false) but {Count} migration(s) are pending: {Migrations}. Apply them out of band; parts of the API may fail until then.",
                pending.Count, string.Join(", ", pending));
            return $"migrations are applied out of band; {pending.Count} pending";
        }

        if (_options.Bootstrap.AllowCreate)
        {
            // Explicit opt-in: create the database and initialise the catalog when absent. Reserved for first-time
            // provisioning and ephemeral/test catalogs.
            _logger.LogWarning(
                "Bootstrap:AllowCreate is enabled: the catalog database will be created if it does not exist ({Target}).",
                CatalogDatabase.DescribeTarget(connectionString));
            await CatalogDatabase.MigrateAsync(connectionString, ct).ConfigureAwait(false);
        }
        else
        {
            // Guarded default: migrate an existing catalog only. A missing database or a populated non-catalog database
            // raises CatalogProvisioningException instead of being provisioned.
            await CatalogDatabase.MigrateExistingAsync(connectionString, ct).ConfigureAwait(false);
        }

        return "current";
    }

    /// <summary>
    /// Every module database after the catalog: migrated under the same switches as the catalog (ApplyMigrations, and
    /// AllowCreate for a missing database or a module schema holding tables the module did not create), then verified
    /// against the build and against the catalog migration the module needs, whether or not migrations were applied. A
    /// database that is not current raises <see cref="ModuleDatabaseException"/>, which stops the host.
    /// </summary>
    private async Task<string> ProvisionModuleDatabasesAsync(CancellationToken ct)
    {
        if (_moduleDatabases.Count > 0)
        {
            var catalogConnectionString = _connection.ConnectionString;
            var (catalogApplied, _) = await CatalogDatabase.StatusAsync(catalogConnectionString, ct).ConfigureAwait(false);
            foreach (var database in _moduleDatabases)
            {
                var connectionString = _moduleConnections.ConnectionString(database);
                _logger.LogInformation(
                    "Module database target: {Database} on {Target}.", database.Describe(), CatalogDatabase.DescribeTarget(connectionString));

                if (_options.Bootstrap.ApplyMigrations)
                {
                    await ModuleDatabases.MigrateAsync(
                        database, connectionString, _options.Bootstrap.AllowCreate, $"control plane on {Environment.MachineName}",
                        Now(), catalogApplied, ct).ConfigureAwait(false);
                }

                var status = await ModuleDatabases.VerifyAsync(database, connectionString, catalogApplied, ct).ConfigureAwait(false);
                _logger.LogInformation("{Status}", status.Summary());
            }
        }

        _moduleVerification.MarkVerified();
        return _moduleDatabases.Count == 0 ? "none registered" : $"{_moduleDatabases.Count} current";
    }

    private async Task<string> SeedRolesAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var nowUtc = Now();
        await UserStore.EnsureRoleAsync(catalog, RoleNames.Admin, "read operate admin author node",
            "Full control: read, operate, author pull requests, user administration, and minting node credentials.", nowUtc, ct).ConfigureAwait(false);
        await UserStore.EnsureRoleAsync(catalog, RoleNames.Operator, "read operate author",
            "Day-to-day operations: read everything, trigger and cancel runs, manage schedules and repo sources, and propose pipelines as pull requests.",
            nowUtc, ct).ConfigureAwait(false);
        await UserStore.EnsureRoleAsync(catalog, RoleNames.Viewer, "read",
            "Read-only access to the whole API surface.", nowUtc, ct).ConfigureAwait(false);
        return "admin, operator, viewer";
    }

    /// <summary>Seeds the default pool with an always-on floor of one worker, so a fresh SQLFlow always has a node
    /// running rather than an empty, scale-to-zero fleet an operator has to notice and turn on. Applied only when the
    /// default pool has no desired state yet, so it never overrides an operator who later set the floor (including to
    /// zero for pure scale-to-zero); it is the bootstrap default, not an enforced minimum.</summary>
    private async Task<string> EnsureDefaultPoolFloorAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var existing = await WorkerPoolStore.GetDesiredAsync(catalog, string.Empty, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return "kept as set";
        }

        await WorkerPoolStore.SaveScaleAsync(
            catalog, string.Empty, minReplicas: 1, manualReplicas: 0, manualUntilUtc: null,
            updatedBy: "bootstrap", nowUtc: Now(), ct).ConfigureAwait(false);
        _logger.LogInformation("Seeded the default worker pool with an always-on floor of 1 (bootstrap default).");
        return "seeded with a floor of 1";
    }

    /// <summary>
    /// Creates the configured initial admin when absent. A password reference that does not resolve, or resolves to a
    /// password too short to accept, fails the step (and so readiness) rather than leaving the deployment without its
    /// admin: the next attempt picks up a secret put right in the vault.
    /// </summary>
    private async Task<string> ProvisionAdminAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var bootstrap = _options.Bootstrap;
        if (string.IsNullOrWhiteSpace(bootstrap.AdminUsername))
        {
            if (!await UserStore.AnyUsersAsync(catalog, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "No users exist and no initial admin is configured (ControlPlane:Bootstrap:AdminUsername / AdminPasswordReference). Only the bootstrap secret can access the API until a user is provisioned.");
            }

            return "none configured";
        }

        var username = bootstrap.AdminUsername.Trim();
        if (await UserStore.FindByUsernameAsync(catalog, username, ct).ConfigureAwait(false) is not null)
        {
            // Converged already. The password is deliberately NOT reset: the configured reference seeds the first
            // credential, it does not manage the credential's lifecycle.
            return $"'{username}' exists";
        }

        var password = await _secrets.ResolveAsync(bootstrap.AdminPasswordReference!, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"The initial admin password reference resolved to an empty value, so the admin user '{username}' was not created. Put the password in the secret ControlPlane:Bootstrap:AdminPasswordReference names.");
        }

        if (password.Length < UserEndpoints.MinPasswordLength)
        {
            throw new InvalidOperationException(
                $"The initial admin password is shorter than {UserEndpoints.MinPasswordLength} characters, so the admin user '{username}' was not created. Set a stronger secret.");
        }

        var hash = _hasher.HashPassword(new CatalogUser { Username = username }, password);
        var (status, id) = await UserStore.CreateLocalAsync(
            catalog, username, hash, RoleNames.Admin, email: null, displayName: null, Now(), ct).ConfigureAwait(false);
        switch (status)
        {
            case UserCreateStatus.Created:
                _logger.LogInformation("Initial admin user '{Username}' provisioned ({UserId}).", username, id);
                return $"'{username}' created";
            case UserCreateStatus.UsernameTaken:
                // A concurrent control-plane node won the race; converged either way.
                return $"'{username}' exists";
            default:
                throw new InvalidOperationException("The admin role vanished between seeding and admin creation; retrying bootstrap.");
        }
    }

    /// <summary>
    /// Writes the repo sources the configuration names (<see cref="BootstrapOptions.Repositories"/> and
    /// <see cref="BootstrapOptions.DemoRepo"/>), releasing the ones it no longer names, and wakes the managed sync when it
    /// registered or changed one, so a fresh deployment's estate syncs at once.
    /// </summary>
    private async Task<string> ProvisionRepositoriesAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var applied = await RepoSourceStore.ApplyConfiguredAsync(catalog, _options.Bootstrap.ConfiguredRepositories(), Now(), ct).ConfigureAwait(false);
        if (applied.Registered.Count > 0 || applied.Updated.Count > 0)
        {
            _syncSignal.Wake();
        }

        return applied.Describe();
    }

    /// <summary>
    /// Writes the external triggers the configuration names (<see cref="BootstrapOptions.ExternalTriggers"/>), disabling and
    /// releasing the ones it no longer names. A key trigger's key is read from its secret at every start, so rotating the
    /// secret and restarting rotates the key; a secret that does not hold a key of SQLFlow's form fails the step (and
    /// readiness) rather than leaving the trigger accepting a weak key. A trigger whose repo source the configuration does
    /// not name and the catalog does not hold is written, and logged, since its repository may be registered later.
    /// </summary>
    private async Task<string> ProvisionExternalTriggersAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var entries = _options.Bootstrap.ConfiguredExternalTriggers();
        var configured = new List<ConfiguredExternalTrigger>(entries.Count);
        foreach (var entry in entries)
        {
            string? hash = null;
            string? prefix = null;
            if (entry.KeyReference is { } reference)
            {
                var key = (await _secrets.ResolveAsync(reference, ct).ConfigureAwait(false)).Trim();
                if (!AccessTokenGenerator.LooksLikeTriggerKey(key))
                {
                    throw new InvalidOperationException(
                        $"The secret {entry.Setting}:KeyReference names is not a trigger key of SQLFlow's form: '{AccessTokenGenerator.TriggerKeyPrefix}' followed by at least {AccessTokenGenerator.MinTriggerKeyBody} random characters. Generate one (for example '{AccessTokenGenerator.TriggerKeyPrefix}' and 43 random letters and digits) and keep it in the vault.");
                }

                hash = AccessTokenGenerator.HashSecret(key);
                prefix = AccessTokenGenerator.DisplayPrefixOf(key);
            }

            if (!await catalog.RepoSources.AnyAsync(s => s.Name == entry.Definition.RepoName, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "{Setting} names repo source '{Repo}', which the catalog does not hold; the trigger is written, and its requests are refused until the repository is registered.",
                    entry.Setting, entry.Definition.RepoName);
            }

            configured.Add(new ConfiguredExternalTrigger(entry.Definition, hash, prefix, entry.Setting));
        }

        var applied = await ExternalTriggerStore.ApplyConfiguredAsync(catalog, configured, Now(), ct).ConfigureAwait(false);
        return applied.Describe();
    }

    /// <summary>
    /// Makes the token <see cref="BootstrapOptions.NodeTokenReference"/> names the one active node credential the
    /// configuration sets, owned by the initial admin, revoking the one it replaced; with none named, revokes the one an
    /// earlier configuration set. The token is read from its secret at every start, so rotating the secret and restarting
    /// rotates the credential.
    /// </summary>
    private async Task<string> ProvisionNodeCredentialAsync(CancellationToken ct)
    {
        await using var catalog = CatalogDatabase.Create(_connection.ConnectionString);
        var bootstrap = _options.Bootstrap;
        if (string.IsNullOrWhiteSpace(bootstrap.NodeTokenReference))
        {
            var revoked = await AccessTokenStore.RevokeConfiguredAsync(catalog, BootstrapOptions.NodeTokenKey, Now(), ct).ConfigureAwait(false);
            return revoked == 0 ? "none configured" : $"none configured; {revoked} revoked";
        }

        var token = (await _secrets.ResolveAsync(bootstrap.NodeTokenReference, ct).ConfigureAwait(false)).Trim();
        if (!AccessTokenGenerator.LooksLikeToken(token) || token.Length < AccessTokenGenerator.Prefix.Length + MinNodeTokenBody)
        {
            throw new InvalidOperationException(
                $"The secret {BootstrapOptions.NodeTokenKey} names is not a node token of SQLFlow's form: '{AccessTokenGenerator.Prefix}' followed by at least {MinNodeTokenBody} random characters. Generate one (for example '{AccessTokenGenerator.Prefix}' and 43 random letters and digits) and keep it in the vault.");
        }

        var owner = await UserStore.FindByUsernameAsync(catalog, bootstrap.AdminUsername!.Trim(), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The node credential {BootstrapOptions.NodeTokenKey} names is owned by the initial admin '{bootstrap.AdminUsername}', who does not exist yet.");
        return await AccessTokenStore.EnsureConfiguredAsync(
            catalog, BootstrapOptions.NodeTokenKey, owner.Id, NodeTokenName, AccessTokenGenerator.HashSecret(token),
            token[..Math.Min(12, token.Length)], NodeScope, Now(), ct).ConfigureAwait(false);
    }

    /// <summary>The fewest random characters a configured node token carries after its prefix: 32, about 190 bits.</summary>
    public const int MinNodeTokenBody = 32;

    /// <summary>The one scope a configured node token is granted: the node protocol's, which the admin role carries.</summary>
    private const string NodeScope = "node";
}
