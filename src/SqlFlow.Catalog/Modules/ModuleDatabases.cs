using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog.Modules;

/// <summary>
/// Migrates, reports and verifies module databases, mirroring <see cref="CatalogDatabase"/>: the migrations are the module's
/// EF Core migrations, applied under EF Core's migration lock, each in its own transaction, idempotently; after a migrate the
/// module records its version on the same connection. Every operation first checks, without a connection, that the module's
/// context is on SQL Server and keeps its migrations history and every table in the module's schema.
/// </summary>
public static class ModuleDatabases
{
    /// <summary>
    /// Applies the module's pending migrations, then hands the module the migrated context to record its version. Refused, before
    /// the module database is touched, when the catalog lacks the migration the module needs, or when the database is ahead of
    /// the build (an older build does not migrate a newer schema). With <paramref name="allowCreate"/> false (every automatic
    /// caller) a missing database is refused rather than created, and so is a module schema that holds tables but no migration
    /// history of the module.
    /// </summary>
    /// <param name="database">The module database.</param>
    /// <param name="connectionString">Its resolved connection string.</param>
    /// <param name="allowCreate">True only for an explicit provisioning request: a missing database may be created, a populated schema migrated.</param>
    /// <param name="appliedBy">Who is migrating, handed to the module to record (a host and an actor, for example).</param>
    /// <param name="appliedUtc">When, handed to the module to record.</param>
    /// <param name="catalogAppliedMigrations">The catalog's applied migrations, or null when the host has no catalog to check.</param>
    /// <param name="ct">Cancels the migrate between migrations.</param>
    /// <exception cref="ModuleDatabaseException">A refusal; the message names the module and, where one is involved, the migration.</exception>
    public static async Task<ModuleDatabaseStatus> MigrateAsync(
        ModuleDatabase database,
        string connectionString,
        bool allowCreate,
        string appliedBy,
        DateTime appliedUtc,
        IReadOnlyCollection<string>? catalogAppliedMigrations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedBy);

        await using var context = database.CreateContext(connectionString);
        var historyTable = EnsureShape(database, context);
        if (database.MinimumCatalogMigration is { } minimum
            && catalogAppliedMigrations is not null
            && !catalogAppliedMigrations.Contains(minimum, StringComparer.Ordinal))
        {
            throw new ModuleDatabaseException(database.Module, ModuleDatabaseStatus.MissingCatalogMigration(database.Module, minimum));
        }

        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if (applied.Exists(migration => !known.Contains(migration)))
            {
                var before = await ReadStatusAsync(database, context, databaseExists: true, catalogAppliedMigrations, ct).ConfigureAwait(false);
                before.ThrowIfNotCurrent();
            }

            if (applied.Count == 0 && !allowCreate)
            {
                var tables = await CountSchemaTablesAsync(database, context, historyTable, ct).ConfigureAwait(false);
                if (tables > 0)
                {
                    throw new ModuleDatabaseException(
                        database.Module,
                        $"The schema '{database.Schema}' in {CatalogDatabase.DescribeTarget(connectionString)} holds {tables} table(s) but no migration history of module '{database.Module}'. " +
                        "Refusing to create the module's tables beside objects it did not create. If the schema really belongs to the module, migrate it explicitly with 'sqlflow db migrate --create'.");
                }
            }
        }
        else if (!allowCreate)
        {
            throw new ModuleDatabaseException(
                database.Module,
                $"The database of {database.Describe()} does not exist ({CatalogDatabase.DescribeTarget(connectionString)}). Refusing to create it automatically. " +
                "Provision it explicitly with 'sqlflow db migrate --create', or point the module's connection at an existing database.");
        }

        await context.Database.MigrateAsync(ct).ConfigureAwait(false);

        var migrations = context.Database.GetMigrations().ToList();
        await database.AfterMigrateAsync(
            context,
            new ModuleMigrationApplied(
                database.Module, database.Version, migrations.Count > 0 ? migrations[^1] : null, appliedBy, appliedUtc, database.MinimumCatalogMigration),
            ct).ConfigureAwait(false);
        return await ReadStatusAsync(database, context, databaseExists: true, catalogAppliedMigrations, ct).ConfigureAwait(false);
    }

    /// <summary>The module database's migrations, recorded version and catalog check against the build. Changes nothing.</summary>
    /// <param name="database">The module database.</param>
    /// <param name="connectionString">Its resolved connection string.</param>
    /// <param name="catalogAppliedMigrations">The catalog's applied migrations, or null when the host has no catalog to check.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <exception cref="ModuleDatabaseException">The module's context is not on SQL Server or keeps something outside the module's schema.</exception>
    public static async Task<ModuleDatabaseStatus> StatusAsync(
        ModuleDatabase database, string connectionString, IReadOnlyCollection<string>? catalogAppliedMigrations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var context = database.CreateContext(connectionString);
        EnsureShape(database, context);
        var exists = await context.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct).ConfigureAwait(false);
        return await ReadStatusAsync(database, context, exists, catalogAppliedMigrations, ct).ConfigureAwait(false);
    }

    /// <summary>The status, when the database is current; the refusal a host raises otherwise.</summary>
    /// <exception cref="ModuleDatabaseException">The database is missing, behind, ahead or diverged, or the catalog lacks the module's migration; the message names them.</exception>
    public static async Task<ModuleDatabaseStatus> VerifyAsync(
        ModuleDatabase database, string connectionString, IReadOnlyCollection<string>? catalogAppliedMigrations, CancellationToken ct = default)
    {
        var status = await StatusAsync(database, connectionString, catalogAppliedMigrations, ct).ConfigureAwait(false);
        status.ThrowIfNotCurrent();
        return status;
    }

    /// <summary>
    /// Refuses a module context that is not on SQL Server, keeps its migrations history outside the module's schema, or maps a
    /// table outside it, before any connection is opened. Returns the name of the migrations history table.
    /// </summary>
    private static string EnsureShape(ModuleDatabase database, DbContext context)
    {
        if (!context.Database.IsSqlServer())
        {
            throw new ModuleDatabaseException(
                database.Module, $"The context of module '{database.Module}' is not configured for SQL Server; a module database is a SQL Server database.");
        }

        var relational = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());
        if (!string.Equals(relational.MigrationsHistoryTableSchema, database.Schema, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModuleDatabaseException(
                database.Module,
                $"The context of module '{database.Module}' keeps its migrations history in {(relational.MigrationsHistoryTableSchema is null ? "the connection's default schema" : $"the schema '{relational.MigrationsHistoryTableSchema}'")}, not in the module's schema '{database.Schema}'. " +
                $"Configure it with sql.MigrationsHistoryTable(\"{relational.MigrationsHistoryTableName ?? HistoryRepository.DefaultTableName}\", \"{database.Schema}\").");
        }

        var model = context.GetService<IDesignTimeModel>().Model;
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
            {
                continue;
            }

            var schema = entity.GetSchema();
            if (!string.Equals(schema, database.Schema, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleDatabaseException(
                    database.Module,
                    $"The context of module '{database.Module}' maps the table '{(schema is null ? table : $"{schema}.{table}")}' (entity '{entity.DisplayName()}') outside the module's schema '{database.Schema}'. " +
                    $"A module database keeps every table in its own schema: call modelBuilder.HasDefaultSchema(\"{database.Schema}\") and map no table elsewhere.");
            }
        }

        return relational.MigrationsHistoryTableName ?? HistoryRepository.DefaultTableName;
    }

    private static async Task<ModuleDatabaseStatus> ReadStatusAsync(
        ModuleDatabase database, DbContext context, bool databaseExists, IReadOnlyCollection<string>? catalogAppliedMigrations, CancellationToken ct)
    {
        var known = context.Database.GetMigrations().ToList();
        if (!databaseExists)
        {
            return new ModuleDatabaseStatus(database, databaseExists: false, known, [], recorded: null, catalogAppliedMigrations);
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        // The module's version lives in tables its own migrations create, so it is read only once one of them is applied.
        var recorded = applied.Count > 0
            ? await database.ReadRecordedVersionAsync(context, ct).ConfigureAwait(false)
            : null;
        return new ModuleDatabaseStatus(database, databaseExists: true, known, applied, recorded, catalogAppliedMigrations);
    }

    /// <summary>Tables in the module's schema other than its migrations history.</summary>
    private static async Task<int> CountSchemaTablesAsync(ModuleDatabase database, DbContext context, string historyTable, CancellationToken ct)
    {
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.tables AS t JOIN sys.schemas AS s ON t.schema_id = s.schema_id " +
                "WHERE s.name = @schema AND t.name <> @history";
            AddParameter(command, "@schema", database.Schema);
            AddParameter(command, "@history", historyTable);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is int count ? count : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
