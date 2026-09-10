using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog;

/// <summary>
/// The bootstrap and upgrade surface for the shadow catalog database. There are two entry points with different
/// safety postures:
/// <list type="bullet">
/// <item><see cref="MigrateAsync"/> is the EXPLICIT provisioning path: on an empty server it creates the database
/// and the <c>catalog</c> schema, and on an existing database it applies the pending migrations. Use it only when
/// the caller has knowingly asked to provision (the <c>sqlflow db migrate --create</c> verb, tests, ephemeral
/// databases).</item>
/// <item><see cref="MigrateExistingAsync"/> is the GUARDED path used by every AUTOMATIC caller (control-plane
/// startup, the per-run catalog write-back, <c>db sync</c>): it refuses to create a missing database or to inject
/// catalog tables into a populated non-catalog database, so a wrong or mistyped connection fails loudly instead of
/// provisioning against the wrong (possibly production) server.</item>
/// </list>
/// Both apply exactly the pending migrations tracked in <c>catalog.__CatalogMigrationsHistory</c>, each in its own
/// transaction (no migration is ever half-applied), and both are idempotent (a no-op when already current).
/// </summary>
public static class CatalogDatabase
{
    /// <summary>
    /// THE single definition of how a catalog <see cref="DbContext"/> talks to SQL Server: the migrations history
    /// table pinned into the catalog schema, and transient-error resiliency. Every catalog context in the product is
    /// configured through here (this class's <see cref="BuildOptions"/> for the CLI, the worker and bootstrap; the
    /// control plane's pooled registration for the API), so no host can end up with weaker resiliency than another.
    ///
    /// The resiliency is not optional polish. The catalog is a genuinely concurrent OLTP workload: one schedule fire
    /// enqueues every member flow at once and each run writes its own claim, status, event and statement rows, and
    /// <see cref="CatalogTransaction"/> deliberately runs its units at SERIALIZABLE. SQL Server resolves the
    /// resulting lock cycles by picking a deadlock victim (error 1205), which is a retryable outcome, not a fault.
    /// Without a retrying execution strategy EF Core surfaces it as "An exception has been raised that is likely due
    /// to a transient failure...", which fails the RUN over a catalog bookkeeping collision that had nothing to do
    /// with the data. That is exactly what happened to a 23-flow schedule fire (batch Trapeze) where the worker,
    /// which builds its context here, had no retry while the control plane's pooled context did.
    ///
    /// EF Core forbids a user-initiated transaction under a retrying strategy, because a retry has to replay the
    /// whole transaction rather than half of it; <see cref="CatalogTransaction"/> already wraps its serializable
    /// unit in <c>CreateExecutionStrategy().ExecuteAsync</c> and clears the change tracker per attempt, so every
    /// transactional catalog path is replay-safe.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsHistoryTable("__CatalogMigrationsHistory", CatalogDbContext.SchemaName);
            sql.EnableRetryOnFailure();
        });
    }

    /// <summary>The EF options for the catalog, configured by <see cref="Configure"/>.</summary>
    public static DbContextOptions<CatalogDbContext> BuildOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new DbContextOptionsBuilder<CatalogDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    public static CatalogDbContext Create(string connectionString) => new(BuildOptions(connectionString));

    /// <summary>EXPLICIT provisioning: creates the database if missing and applies all pending migrations. This is
    /// the only path that may bring a database into existence, so reserve it for callers that have deliberately
    /// asked to provision (the <c>db migrate --create</c> verb, tests). Automatic callers use
    /// <see cref="MigrateExistingAsync"/> instead.</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        await context.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>GUARDED migration for automatic callers: applies pending migrations to an EXISTING catalog, but
    /// never creates a missing database and never initialises catalog tables into a database that already holds
    /// unrelated objects. Throws <see cref="CatalogProvisioningException"/> in those cases (a configuration
    /// mistake to surface, not retry). A server that is unreachable throws the underlying transient error, which a
    /// retrying caller can distinguish from the deterministic <see cref="CatalogProvisioningException"/>.</summary>
    public static async Task MigrateExistingAsync(string connectionString, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var context = Create(connectionString);

        // ExistsAsync connects to the server: an unreachable server throws (a transient the caller may retry),
        // while a reachable server with no such database returns false (a configuration problem we refuse to
        // paper over by silently creating it).
        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            throw new CatalogProvisioningException(
                $"The catalog database does not exist ({DescribeTarget(connectionString)}). Refusing to create it " +
                "automatically. Provision it explicitly with 'sqlflow db migrate --create --db <ref>', or point the " +
                "connection at your existing catalog.");
        }

        // The database exists. With no catalog migration history it is either an empty database made for the
        // catalog (safe to initialise) or, dangerously, an unrelated populated database (e.g. a data warehouse)
        // this connection reached by mistake. Never inject catalog tables into the latter.
        var applied = await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
        if (!applied.Any())
        {
            var existingTables = await CountUserTablesAsync(context, ct).ConfigureAwait(false);
            if (existingTables > 0)
            {
                throw new CatalogProvisioningException(
                    $"The database ({DescribeTarget(connectionString)}) exists and contains {existingTables} table(s) " +
                    "but has no SqlFlow catalog schema. Refusing to initialise catalog tables into a populated database " +
                    "that may not be a catalog. If this really is a new, dedicated catalog database, provision it with " +
                    "'sqlflow db migrate --create'.");
            }
        }

        await context.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A human-readable, secret-free description of a connection's target (server and database), for logs
    /// and for the messages the provisioning guards raise.</summary>
    public static string DescribeTarget(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrWhiteSpace(builder.DataSource) ? "(unknown server)" : builder.DataSource;
            var database = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "(unknown database)" : builder.InitialCatalog;
            return $"server '{server}', database '{database}'";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or KeyNotFoundException)
        {
            return "(unparseable connection string)";
        }
    }

    /// <summary>Counts real user tables in the connected database, ignoring the EF migrations-history table an
    /// interrupted first run may have left behind. Used to tell an empty database (safe to initialise) from a
    /// populated one (refuse).</summary>
    private static async Task<int> CountUserTablesAsync(CatalogDbContext context, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "WHERE NOT (s.name = N'catalog' AND t.name = N'__CatalogMigrationsHistory')";
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is int n ? n : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The migration names this build knows (applied) vs. those already in the database (so a caller can
    /// report drift). Empty pending means the database is current.</summary>
    public static async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending)> StatusAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        var pending = (await context.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        return (applied, pending);
    }
}

/// <summary>
/// Raised by <see cref="CatalogDatabase.MigrateExistingAsync"/> when it refuses to provision: the target database
/// does not exist, or exists but is not a SqlFlow catalog. It marks a deterministic configuration mistake (a wrong
/// or mistyped connection), so callers surface it to the operator rather than retrying.
/// </summary>
public sealed class CatalogProvisioningException : Exception
{
    public CatalogProvisioningException(string message)
        : base(message)
    {
    }

    public CatalogProvisioningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Design-time factory so the EF tools (<c>dotnet ef migrations add ...</c>) can construct the context without the
/// app's DI. The connection string is irrelevant for generating migrations; a real value is only needed when the
/// tool talks to a database, supplied via <c>SQLFLOW_CATALOG_DB</c>.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SQLFLOW_CATALOG_DB")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=SqlFlowCatalog;Trusted_Connection=True;TrustServerCertificate=True";
        return new CatalogDbContext(CatalogDatabase.BuildOptions(connectionString));
    }
}
