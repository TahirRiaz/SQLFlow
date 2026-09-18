using System.Collections.Frozen;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Hosting;

namespace SqlFlow.Catalog.Modules;

/// <summary>
/// A host module's own database: an EF Core context on SQL Server whose every table, and its migrations history table, lives
/// in one schema of its own. It sits in the catalog's database (the default) or in a database named by a secret reference of
/// its own. SQLFlow never reads a module's tables: it migrates them (then hands the module the connection to record its
/// version), reports them, and verifies them against the build and against the catalog migration the module needs.
/// <c>sqlflow db migrate</c> and <c>db status</c> cover every registered module database, a worker node verifies them before
/// taking work, and the control plane refuses to run over one that is missing, behind or ahead.
/// </summary>
/// <remarks>Construct a <see cref="ModuleDatabase{TContext}"/> and register it through the host module contract.</remarks>
public abstract partial class ModuleDatabase
{
    /// <summary>The longest schema name SQL Server accepts.</summary>
    public const int MaxSchemaLength = 128;

    /// <summary>The longest module version.</summary>
    public const int MaxVersionLength = 32;

    /// <summary>SQLFlow's catalog schema and SQL Server's own schemas: never a module's.</summary>
    private static readonly FrozenSet<string> ReservedSchemas = new[]
    {
        CatalogDbContext.SchemaName, "dbo", "sys", "guest", "INFORMATION_SCHEMA",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private protected ModuleDatabase(string module, string schema, string version, string? connectionReference, string? minimumCatalogMigration)
    {
        if (!HostModuleNames.IsValid(module))
        {
            throw new ModuleDatabaseException(module ?? string.Empty, $"'{module}' is not a valid module name for a module database: use {HostModuleNames.Rule}.");
        }

        if (!IsValidSchema(schema))
        {
            throw new ModuleDatabaseException(
                module,
                $"The database of module '{module}' declares the schema '{schema}': a module schema is ASCII letters, digits and underscores, starting with a letter or an underscore, at most {MaxSchemaLength} characters.");
        }

        if (ReservedSchemas.Contains(schema))
        {
            throw new ModuleDatabaseException(
                module, $"The database of module '{module}' declares the schema '{schema}', which is SQLFlow's catalog schema or a SQL Server schema; give the module a schema of its own.");
        }

        if (!IsValidVersion(version))
        {
            throw new ModuleDatabaseException(
                module,
                $"The database of module '{module}' declares the version '{version}': a version is ASCII letters, digits, '.', '+' and '-', starting with a letter or digit, at most {MaxVersionLength} characters.");
        }

        if (connectionReference is not null && !SecretReference().IsMatch(connectionReference))
        {
            // The value is never echoed: a connection string given where a reference belongs usually carries a password.
            throw new ModuleDatabaseException(
                module,
                $"The connection of module '{module}' must be a secret reference, ${{env:NAME}} or ${{keyvault:NAME}}, or absent to use the catalog connection; the value given is not one (it is not shown).");
        }

        if (minimumCatalogMigration is not null && !CatalogDatabase.KnownMigrations.Contains(minimumCatalogMigration, StringComparer.Ordinal))
        {
            throw new ModuleDatabaseException(
                module,
                $"The database of module '{module}' needs the catalog migration '{minimumCatalogMigration}', which this SQLFlow build does not include; build the module against a SQLFlow that has it.");
        }

        Module = module;
        Schema = schema;
        Version = version;
        ConnectionReference = connectionReference;
        MinimumCatalogMigration = minimumCatalogMigration;
    }

    /// <summary>The name of the module the database belongs to.</summary>
    public string Module { get; }

    /// <summary>The schema holding every table of the module and its migrations history.</summary>
    /// <summary>
    /// Whether rows on <paramref name="connectionString"/> are reachable on <paramref name="connection"/>: the same
    /// server and the same database, so one statement and one transaction can touch both.
    /// <para>
    /// This is what a module asks before it reuses a host's connection instead of opening its own. A module database
    /// may be a database of its own, on its own server, and on Azure SQL there is no cross-database query at all: a
    /// module that assumed otherwise would write its rows into the host's database, or fail. The comparison is
    /// conservative, because the answers are not symmetrical in cost: what it cannot prove identical it calls
    /// different, and the module opens its own connection, which is always correct and sometimes merely slower. Two
    /// spellings of one server (<c>.</c>, <c>(local)</c>, a listener alias) therefore read as different.
    /// </para>
    /// </summary>
    /// <param name="connection">The host's connection, open or not.</param>
    /// <param name="connectionString">The module's own connection string, or null when it has none of its own.</param>
    public static bool IsReachableOn(DbConnection connection, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No connection of its own: the module's rows are wherever the host's connection points, by definition.
            return true;
        }

        SqlConnectionStringBuilder module;
        try
        {
            module = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // A connection string this cannot read is one it cannot vouch for.
            return false;
        }

        var database = module.InitialCatalog;
        var server = module.DataSource;
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        return string.Equals(database, connection.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Host(server), Host(connection.DataSource), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A server as it is compared: trimmed, without the <c>tcp:</c> prefix and without a port.</summary>
    private static string Host(string? server)
    {
        var value = (server ?? string.Empty).Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        return comma < 0 ? value : value[..comma];
    }

    public string Schema { get; }

    /// <summary>The module's schema version in this build, handed to the module when it records a migrate.</summary>
    public string Version { get; }

    /// <summary>The secret reference of the module's own connection, or null when the module uses the catalog connection.</summary>
    public string? ConnectionReference { get; }

    /// <summary>The oldest SQLFlow catalog migration the module needs the catalog to have applied, or null for none.</summary>
    public string? MinimumCatalogMigration { get; }

    /// <summary>The module's EF Core context type.</summary>
    public abstract Type ContextType { get; }

    /// <summary>A secret-free description for messages: the module, its schema and which connection it uses.</summary>
    public string Describe()
        => $"module '{Module}' (schema '{Schema}', {(ConnectionReference is null ? "catalog connection" : "own connection")})";

    /// <summary>The module's context over <paramref name="connectionString"/>, built by the module.</summary>
    public abstract DbContext CreateContext(string connectionString);

    /// <summary>The module's context on an open connection the caller owns (so it can join the caller's transaction), built by the module.</summary>
    public abstract DbContext CreateContext(DbConnection connection);

    /// <summary>Hands the module the migrated context to record its version.</summary>
    internal abstract Task AfterMigrateAsync(DbContext context, ModuleMigrationApplied applied, CancellationToken ct);

    /// <summary>The version the module recorded, or null when it records none.</summary>
    internal abstract Task<ModuleRecordedVersion?> ReadRecordedVersionAsync(DbContext context, CancellationToken ct);

    /// <summary>Registers the database and what it provides into a host's services.</summary>
    internal abstract void AddServices(IServiceCollection services);

    private static bool IsValidSchema(string? schema)
        => schema is { Length: > 0 and <= MaxSchemaLength }
           && (char.IsAsciiLetter(schema[0]) || schema[0] == '_')
           && schema.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsValidVersion(string? version)
        => version is { Length: > 0 and <= MaxVersionLength }
           && char.IsAsciiLetterOrDigit(version[0])
           && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');

    [GeneratedRegex(@"^\$\{(?:env|keyvault):[^{}\s]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();
}

/// <summary>A module database over the module's context type <typeparamref name="TContext"/>.</summary>
public sealed class ModuleDatabase<TContext> : ModuleDatabase
    where TContext : DbContext
{
    private readonly Func<string, TContext> _createForConnectionString;
    private readonly Func<DbConnection, TContext> _createForConnection;

    /// <param name="module">The name of the module registering the database.</param>
    /// <param name="schema">
    /// The module's schema. The context must keep its migrations history table there
    /// (<c>sql.MigrationsHistoryTable(name, schema)</c>) and map every table into it (<c>HasDefaultSchema</c>); every
    /// operation checks both before it opens a connection.
    /// </param>
    /// <param name="version">The module's schema version in this build, such as <c>1.0.0</c>.</param>
    /// <param name="createForConnectionString">Builds the module's context over a connection string.</param>
    /// <param name="createForConnection">Builds the module's context on an open connection the caller owns.</param>
    /// <param name="connectionReference">
    /// <c>${env:NAME}</c> or <c>${keyvault:NAME}</c> naming the module's own connection, or null to use the catalog connection.
    /// </param>
    /// <param name="minimumCatalogMigration">The oldest catalog migration the module needs the catalog to have applied, or null.</param>
    /// <exception cref="ModuleDatabaseException">A value breaks its rule; the message names the module and never echoes a connection.</exception>
    public ModuleDatabase(
        string module,
        string schema,
        string version,
        Func<string, TContext> createForConnectionString,
        Func<DbConnection, TContext> createForConnection,
        string? connectionReference = null,
        string? minimumCatalogMigration = null)
        : base(module, schema, version, connectionReference, minimumCatalogMigration)
    {
        ArgumentNullException.ThrowIfNull(createForConnectionString);
        ArgumentNullException.ThrowIfNull(createForConnection);
        _createForConnectionString = createForConnectionString;
        _createForConnection = createForConnection;
    }

    /// <summary>
    /// Records the module's version after a migrate, on the migrated context's own connection: typically an upsert of the
    /// module's single-row version table. Null when the module records no version.
    /// </summary>
    public Func<TContext, ModuleMigrationApplied, CancellationToken, Task>? AfterMigrate { get; init; }

    /// <summary>
    /// Reads the version the module recorded, for status and verification. Called only once at least one of the module's
    /// migrations is applied. Null when the module records no version.
    /// </summary>
    public Func<TContext, CancellationToken, Task<ModuleRecordedVersion?>>? ReadRecordedVersion { get; init; }

    public override Type ContextType => typeof(TContext);

    public override TContext CreateContext(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return _createForConnectionString(connectionString)
               ?? throw new ModuleDatabaseException(Module, $"The context factory of module '{Module}' returned no context for a connection string.");
    }

    public override TContext CreateContext(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _createForConnection(connection)
               ?? throw new ModuleDatabaseException(Module, $"The context factory of module '{Module}' returned no context for an open connection.");
    }

    internal override Task AfterMigrateAsync(DbContext context, ModuleMigrationApplied applied, CancellationToken ct)
        => AfterMigrate is null ? Task.CompletedTask : AfterMigrate((TContext)context, applied, ct);

    internal override Task<ModuleRecordedVersion?> ReadRecordedVersionAsync(DbContext context, CancellationToken ct)
        => ReadRecordedVersion is null ? Task.FromResult<ModuleRecordedVersion?>(null) : ReadRecordedVersion((TContext)context, ct);

    internal override void AddServices(IServiceCollection services)
    {
        services.AddSingleton<ModuleDatabase>(this);
        services.AddSingleton(this);
        services.AddSingleton<IDbContextFactory<TContext>>(
            provider => new ContextFactory(this, provider.GetRequiredService<IModuleDatabaseConnections>()));
    }

    /// <summary>Creates the module's contexts on the connection the host resolves for it.</summary>
    private sealed class ContextFactory(ModuleDatabase<TContext> database, IModuleDatabaseConnections connections) : IDbContextFactory<TContext>
    {
        public TContext CreateDbContext() => database.CreateContext(connections.ConnectionString(database));
    }
}

/// <summary>What a migrate applied, handed to <see cref="ModuleDatabase{TContext}.AfterMigrate"/> to record.</summary>
/// <param name="Module">The module.</param>
/// <param name="Version">The module's schema version in the build that migrated.</param>
/// <param name="LastMigration">The newest migration the build knows, now applied; null when the module has no migrations.</param>
/// <param name="AppliedBy">Who migrated: the host and the actor.</param>
/// <param name="AppliedUtc">When.</param>
/// <param name="MinimumCatalogMigration">The catalog migration the module declares it needs, or null.</param>
public sealed record ModuleMigrationApplied(
    string Module, string Version, string? LastMigration, string AppliedBy, DateTime AppliedUtc, string? MinimumCatalogMigration);

/// <summary>The version a module recorded in its database, read by <see cref="ModuleDatabase{TContext}.ReadRecordedVersion"/>.</summary>
/// <param name="Version">The module's schema version the last migrate recorded.</param>
/// <param name="LastMigration">The newest migration the last migrate recorded, or null.</param>
public sealed record ModuleRecordedVersion(string Version, string? LastMigration);
