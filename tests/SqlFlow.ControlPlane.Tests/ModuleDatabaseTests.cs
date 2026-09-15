using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A module declares its own database: an EF Core context it builds itself, in a schema of its own that holds its tables and
/// migrations history, on the catalog connection or a secret reference of its own, with a version it records after each
/// migrate and a catalog migration it needs. Declaration, registration, connection resolution, the shape checks, the status
/// rules and readiness run everywhere; migrating, reporting and verifying against SQL Server run against the DB-test database
/// (each test in a schema of its own it drops) and skip without one.
/// </summary>
public sealed class ModuleDatabaseTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    [Fact]
    public void AModuleDatabase_BuildsTheModulesContext_WithItsHistoryInTheModuleSchema_AndFindsItsMigrations()
    {
        ProbeSchema.Current = "sfprobe_history";
        var database = Probe<ProbeContextV2>("sfprobe_history");

        using var context = database.CreateContext(Placeholder);

        Assert.IsType<ProbeContextV2>(context);
        Assert.Contains("[sfprobe_history].[__EFMigrationsHistory]", context.GetService<IHistoryRepository>().GetCreateScript(), StringComparison.Ordinal);
        Assert.Equal([ProbeMigrationIds.First, ProbeMigrationIds.Second], context.Database.GetMigrations());
        Assert.Equal("module 'probe' (schema 'sfprobe_history', catalog connection)", database.Describe());
    }

    [Fact]
    public void AModuleDatabase_BuildsTheModulesContextOnAnOpenConnection()
    {
        ProbeSchema.Current = "sfprobe_connection";
        var database = Probe<ProbeContextV1>("sfprobe_connection");
        using var connection = new SqlConnection(Placeholder);

        using var context = database.CreateContext(connection);

        Assert.Same(connection, context.Database.GetDbConnection());
    }

    public static TheoryData<string, string, string, string?, string?, string> InvalidDeclarations => new()
    {
        { "Probe", "sfprobe", "1.0.0", null, null, "not a valid module name" },
        { "probe", "1schema", "1.0.0", null, null, "declares the schema '1schema'" },
        { "probe", "sf-probe", "1.0.0", null, null, "declares the schema 'sf-probe'" },
        { "probe", "catalog", "1.0.0", null, null, "SQLFlow's catalog schema or a SQL Server schema" },
        { "probe", "DBO", "1.0.0", null, null, "SQLFlow's catalog schema or a SQL Server schema" },
        { "probe", "sfprobe", "", null, null, "declares the version ''" },
        { "probe", "sfprobe", "1.0 beta", null, null, "declares the version '1.0 beta'" },
        { "probe", "sfprobe", "1.0.0", "", null, "must be a secret reference" },
        { "probe", "sfprobe", "1.0.0", "${env:A} ${env:B}", null, "must be a secret reference" },
        { "probe", "sfprobe", "1.0.0", "${vault:secret}", null, "must be a secret reference" },
        { "probe", "sfprobe", "1.0.0", null, "20990101000000_NotInThisBuild", "needs the catalog migration '20990101000000_NotInThisBuild', which this SQLFlow build does not include" },
    };

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void AnInvalidDeclaration_IsRefused_NamingTheRule(string module, string schema, string version, string? reference, string? minimumCatalogMigration, string message)
    {
        var error = Assert.Throws<ModuleDatabaseException>(() => new ModuleDatabase<ProbeContextV1>(
            module, schema, version, _ => throw new InvalidOperationException("not built"), _ => throw new InvalidOperationException("not built"),
            reference, minimumCatalogMigration));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiteralConnectionString_IsRefused_WithoutEchoingIt()
    {
        var error = Assert.Throws<ModuleDatabaseException>(() => Probe<ProbeContextV1>("sfprobe", reference: "Server=db;User ID=sa;Password=hunter2"));

        Assert.Equal("probe", error.Module);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=db", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${env:SQLFLOW_PROBE_DB}")]
    [InlineData("${keyvault:probe-vault/probe-db}")]
    public void ASecretReferenceAndACatalogMigrationOfThisBuild_AreAccepted(string reference)
    {
        var minimum = CatalogDatabase.KnownMigrations[^1];

        var database = Probe<ProbeContextV1>("sfprobe", reference: reference, minimumCatalogMigration: minimum);

        Assert.Equal(reference, database.ConnectionReference);
        Assert.Equal(minimum, database.MinimumCatalogMigration);
        Assert.Equal("module 'probe' (schema 'sfprobe', own connection)", database.Describe());
    }

    [Fact]
    public void Registration_ProvidesTheDatabaseAndAContextFactoryOnTheCatalogConnection()
    {
        ProbeSchema.Current = "sfprobe_factory";
        var services = new ServiceCollection();
        services.AddSingleton<IModuleDatabaseConnections>(new ModuleDatabaseConnections(new MapResolver([]), () => Placeholder));
        var database = Probe<ProbeContextV1>("sfprobe_factory");

        services.AddModuleDatabase(database);
        using var provider = services.BuildServiceProvider();
        using var context = provider.GetRequiredService<IDbContextFactory<ProbeContextV1>>().CreateDbContext();

        Assert.Same(database, Assert.Single(provider.GetServices<ModuleDatabase>()));
        Assert.Same(database, provider.GetRequiredService<ModuleDatabase<ProbeContextV1>>());
        Assert.Equal(Placeholder, context.Database.GetConnectionString());
    }

    [Fact]
    public void Registration_RefusesASecondDatabaseWithTheModuleNameOrTheSchema()
    {
        var services = new ServiceCollection();
        services.AddModuleDatabase(Probe<ProbeContextV1>("sfprobe"));

        var sameName = Assert.Throws<ModuleDatabaseException>(() => services.AddModuleDatabase(Probe<ProbeContextV2>("sfprobe_other")));
        var sameSchema = Assert.Throws<ModuleDatabaseException>(() => services.AddModuleDatabase(Probe<ProbeContextV2>("SFPROBE", module: "other")));

        Assert.Contains("registered twice", sameName.Message, StringComparison.Ordinal);
        Assert.Contains("which module 'probe' already uses", sameSchema.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connections_ResolveAModuleReference_AndRefuseTheCatalogWhereTheHostHasNone()
    {
        var resolver = new MapResolver(new() { ["${env:SQLFLOW_PROBE_DB}"] = "Server=probe;Database=own", ["${env:SQLFLOW_EMPTY}"] = " " });
        var own = Probe<ProbeContextV1>("sfprobe", reference: "${env:SQLFLOW_PROBE_DB}");
        var empty = Probe<ProbeContextV1>("sfempty", module: "empty", reference: "${env:SQLFLOW_EMPTY}");
        var onCatalog = Probe<ProbeContextV1>("sfcatalogprobe");

        var withCatalog = new ModuleDatabaseConnections(resolver, () => "Server=catalog;Database=catalog");
        var node = ModuleDatabaseConnections.WithoutCatalog(resolver, "a worker node has no catalog connection");

        Assert.Equal("Server=probe;Database=own", withCatalog.ConnectionString(own));
        Assert.Equal("Server=catalog;Database=catalog", withCatalog.ConnectionString(onCatalog));
        Assert.Equal("Server=probe;Database=own", node.ConnectionString(own));
        var noCatalog = Assert.Throws<ModuleDatabaseException>(() => node.ConnectionString(onCatalog));
        Assert.Contains("module 'probe' uses the catalog connection, but a worker node has no catalog connection", noCatalog.Message, StringComparison.Ordinal);
        Assert.Contains("resolved to an empty value", Assert.Throws<ModuleDatabaseException>(() => withCatalog.ConnectionString(empty)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ClassifiesTheDatabaseAgainstTheBuild_AndNamesTheMigrationsItRefuses()
    {
        var database = Probe<ProbeContextV1>("sfprobe");
        string[] build = ["20260101000000_A", "20260102000000_B"];
        var recorded = new ModuleRecordedVersion("1.0.0", "20260102000000_B");

        var current = new ModuleDatabaseStatus(database, true, build, build, recorded, null);
        var missing = new ModuleDatabaseStatus(database, false, build, [], null, null);
        var behind = new ModuleDatabaseStatus(database, true, build, ["20260101000000_A"], new ModuleRecordedVersion("0.9.0", "20260101000000_A"), null);
        var ahead = new ModuleDatabaseStatus(database, true, build, [.. build, "20260103000000_C"], new ModuleRecordedVersion("1.1.0", "20260103000000_C"), null);
        var diverged = new ModuleDatabaseStatus(database, true, build, ["20260101000000_A", "20260103000000_C"], null, null);

        Assert.Equal(ModuleDatabaseState.Current, current.State);
        Assert.Null(current.Problem());
        current.ThrowIfNotCurrent();
        Assert.Contains("current at '20260102000000_B'", current.Summary(), StringComparison.Ordinal);
        Assert.Contains("recorded version 1.0.0", current.Summary(), StringComparison.Ordinal);

        Assert.Equal(ModuleDatabaseState.Missing, missing.State);
        Assert.Equal(ModuleDatabaseState.Behind, behind.State);
        Assert.Equal(["20260102000000_B"], behind.Pending);
        Assert.Equal(ModuleDatabaseState.Ahead, ahead.State);
        Assert.Equal(["20260103000000_C"], ahead.Unknown);
        Assert.Equal(ModuleDatabaseState.Diverged, diverged.State);

        var behindError = Assert.Throws<ModuleDatabaseException>(behind.ThrowIfNotCurrent);
        Assert.Equal("probe", behindError.Module);
        Assert.Same(behind, behindError.Status);
        Assert.Contains("module 'probe'", behindError.Message, StringComparison.Ordinal);
        Assert.Contains("'20260102000000_B'", behindError.Message, StringComparison.Ordinal);

        var aheadError = Assert.Throws<ModuleDatabaseException>(ahead.ThrowIfNotCurrent);
        Assert.Contains("ahead of this build", aheadError.Message, StringComparison.Ordinal);
        Assert.Contains("'20260103000000_C'", aheadError.Message, StringComparison.Ordinal);
        Assert.Contains("recorded version 1.1.0", aheadError.Message, StringComparison.Ordinal);

        Assert.Contains("does not exist", Assert.Throws<ModuleDatabaseException>(missing.ThrowIfNotCurrent).Message, StringComparison.Ordinal);
        Assert.Contains("diverged", Assert.Throws<ModuleDatabaseException>(diverged.ThrowIfNotCurrent).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_RefusesACatalogWithoutTheMigrationTheModuleNeeds_AndNotesWhenNoCatalogWasChecked()
    {
        var needed = CatalogDatabase.KnownMigrations[^1];
        var database = Probe<ProbeContextV1>("sfprobe", minimumCatalogMigration: needed);
        string[] build = ["20260101000000_A"];
        var catalogWithout = CatalogDatabase.KnownMigrations.Take(CatalogDatabase.KnownMigrations.Count - 1).ToList();

        var lacking = new ModuleDatabaseStatus(database, true, build, build, null, catalogWithout);
        var having = new ModuleDatabaseStatus(database, true, build, build, null, CatalogDatabase.KnownMigrations.ToList());
        var unchecked_ = new ModuleDatabaseStatus(database, true, build, build, null, null);

        Assert.Equal(ModuleDatabaseState.CatalogBehind, lacking.State);
        var error = Assert.Throws<ModuleDatabaseException>(lacking.ThrowIfNotCurrent);
        Assert.Equal($"Module 'probe' needs the catalog migration '{needed}', which the catalog has not applied. Migrate the catalog first ('sqlflow db migrate').", error.Message);
        Assert.Equal(ModuleDatabaseState.Current, having.State);
        Assert.Contains($"catalog has '{needed}'", having.Summary(), StringComparison.Ordinal);
        Assert.Equal(ModuleDatabaseState.Current, unchecked_.State);
        Assert.Contains($"catalog migration '{needed}' not checked", unchecked_.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_RefusesACatalogWithoutTheModulesMigration_BeforeTouchingTheModuleDatabase()
    {
        ProbeSchema.Current = "sfprobe_catalog";
        var needed = CatalogDatabase.KnownMigrations[^1];
        var database = Probe<ProbeContextV1>("sfprobe_catalog", minimumCatalogMigration: needed);

        var error = await Assert.ThrowsAsync<ModuleDatabaseException>(() => ModuleDatabases.MigrateAsync(
            database, Placeholder, allowCreate: false, "tests", DateTime.UtcNow, catalogAppliedMigrations: []));

        Assert.Contains($"needs the catalog migration '{needed}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextMappingATableOutsideTheModuleSchema_IsRefusedBeforeAnyConnection()
    {
        ProbeSchema.Current = "sfprobe_outside";
        var database = Probe<OutsideSchemaContext>("sfprobe_outside");

        var status = await Assert.ThrowsAsync<ModuleDatabaseException>(() => ModuleDatabases.StatusAsync(database, Placeholder, null));
        var migrate = await Assert.ThrowsAsync<ModuleDatabaseException>(
            () => ModuleDatabases.MigrateAsync(database, Placeholder, allowCreate: false, "tests", DateTime.UtcNow, null));

        Assert.Contains("maps the table 'dbo.ProbeRow'", status.Message, StringComparison.Ordinal);
        Assert.Contains("outside the module's schema 'sfprobe_outside'", migrate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextKeepingItsMigrationsHistoryOutsideTheModuleSchema_IsRefusedBeforeAnyConnection()
    {
        ProbeSchema.Current = "sfprobe_elsewhere";
        var database = Probe<ProbeContextV1>("sfprobe_elsewhere", historySchema: "dbo");

        var error = await Assert.ThrowsAsync<ModuleDatabaseException>(() => ModuleDatabases.VerifyAsync(database, Placeholder, null));

        Assert.Contains("keeps its migrations history in the schema 'dbo', not in the module's schema 'sfprobe_elsewhere'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_IsHealthyWithoutModuleDatabases_AndWaitsForVerificationWithThem()
    {
        var none = new ModuleDatabaseVerification([]);
        var one = new ModuleDatabaseVerification([Probe<ProbeContextV1>("sfprobe")]);
        var refused = new ModuleDatabaseVerification([Probe<ProbeContextV1>("sfprobe")]);

        Assert.Equal(HealthStatus.Healthy, (await none.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await one.CheckHealthAsync(new HealthCheckContext())).Status);
        one.MarkVerified();
        Assert.Equal(HealthStatus.Healthy, (await one.CheckHealthAsync(new HealthCheckContext())).Status);
        refused.MarkRefused("the database of module 'probe' is behind this build");
        var result = await refused.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("the database of module 'probe' is behind this build", result.Description);
    }

    [Fact]
    public async Task AControlPlaneModule_RegistersItsDatabase_AndReadinessWaitsForIt()
    {
        ProbeSchema.Current = "sfprobe_host";
        await using var factory = new ControlPlaneAppFactory().WithModules(new DatabaseModule("probe", Probe<ProbeContextV1>("sfprobe_host")));
        using var client = factory.CreateClient();

        var database = Assert.Single(factory.Services.GetServices<ModuleDatabase>());
        Assert.Equal("sfprobe_host", database.Schema);
        var readiness = await factory.Services.GetRequiredService<ModuleDatabaseVerification>().CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, readiness.Status);
    }

    [Fact]
    public async Task AControlPlaneModule_CannotRegisterAnotherModulesDatabase()
    {
        await using var factory = new ControlPlaneAppFactory().WithModules(new DatabaseModule("probe", Probe<ProbeContextV1>("sfprobe", module: "other")));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var module = FindInnermost<ControlPlaneModuleException>(error);
        Assert.Equal("probe", module.ModuleName);
        Assert.Contains("registers the database of module 'other'", module.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Migrate_CreatesTheModuleTablesAndHistoryInItsSchema_RecordsTheVersionOnTheSameConnection_AndVerifies()
    {
        var connection = CatalogTestDb.Require();
        var schema = NewSchema();
        try
        {
            var catalogApplied = CatalogDatabase.KnownMigrations.ToList();
            var database = Probe<ProbeContextV2>(schema, version: "2.0.0", minimumCatalogMigration: catalogApplied[^1]);

            var migrated = await ModuleDatabases.MigrateAsync(database, connection, allowCreate: false, "module database tests", DateTime.UtcNow, catalogApplied);
            var again = await ModuleDatabases.MigrateAsync(database, connection, allowCreate: false, "module database tests again", DateTime.UtcNow, catalogApplied);
            var verified = await ModuleDatabases.VerifyAsync(database, connection, catalogApplied);

            Assert.True(migrated.IsCurrent, migrated.Summary());
            Assert.True(again.IsCurrent, again.Summary());
            Assert.Equal([ProbeMigrationIds.First, ProbeMigrationIds.Second], verified.Applied);
            Assert.Equal(new ModuleRecordedVersion("2.0.0", ProbeMigrationIds.Second), verified.Recorded);
            Assert.True(verified.CatalogHasMinimumMigration);
            Assert.Equal(3, await CountTablesAsync(connection, schema));
            Assert.Equal("module database tests again", await ScalarAsync(connection, $"SELECT [AppliedBy] FROM [{schema}].[ProbeVersion] WHERE [Id] = 1"));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    [SkippableFact]
    public async Task Verify_RefusesADatabaseBehindTheBuild_AndOneAheadOfIt()
    {
        var connection = CatalogTestDb.Require();
        var schema = NewSchema();
        try
        {
            var older = Probe<ProbeContextV1>(schema);
            var newer = Probe<ProbeContextV2>(schema, version: "2.0.0");

            await ModuleDatabases.MigrateAsync(older, connection, allowCreate: false, "module database tests", DateTime.UtcNow, null);
            var behind = await Assert.ThrowsAsync<ModuleDatabaseException>(() => ModuleDatabases.VerifyAsync(newer, connection, null));
            Assert.Equal(ModuleDatabaseState.Behind, behind.Status?.State);
            Assert.Contains($"'{ProbeMigrationIds.Second}'", behind.Message, StringComparison.Ordinal);

            await ModuleDatabases.MigrateAsync(newer, connection, allowCreate: false, "module database tests", DateTime.UtcNow, null);
            var ahead = await Assert.ThrowsAsync<ModuleDatabaseException>(() => ModuleDatabases.VerifyAsync(older, connection, null));
            Assert.Equal(ModuleDatabaseState.Ahead, ahead.Status?.State);
            Assert.Contains($"'{ProbeMigrationIds.Second}'", ahead.Message, StringComparison.Ordinal);

            var olderMigrate = await Assert.ThrowsAsync<ModuleDatabaseException>(
                () => ModuleDatabases.MigrateAsync(older, connection, allowCreate: false, "module database tests", DateTime.UtcNow, null));
            Assert.Contains("ahead of this build", olderMigrate.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    [SkippableFact]
    public async Task Migrate_RefusesASchemaHoldingTablesTheModuleDidNotCreate()
    {
        var connection = CatalogTestDb.Require();
        var schema = NewSchema();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA [{schema}];");
            await ExecuteAsync(connection, $"CREATE TABLE [{schema}].[Stranger] ([Id] int NOT NULL);");
            var database = Probe<ProbeContextV1>(schema);

            var refused = await Assert.ThrowsAsync<ModuleDatabaseException>(
                () => ModuleDatabases.MigrateAsync(database, connection, allowCreate: false, "module database tests", DateTime.UtcNow, null));

            Assert.Contains("holds 1 table(s) but no migration history of module 'probe'", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, await CountTablesAsync(connection, schema));
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
        }
    }

    [SkippableFact]
    public async Task MigrateAndStatus_ReportAMissingDatabase_WithoutCreatingIt()
    {
        var reachable = CatalogTestDb.Require();
        var missingName = $"SqlFlowModuleMissing_{Guid.NewGuid():N}";
        var missing = new SqlConnectionStringBuilder(reachable) { InitialCatalog = missingName }.ConnectionString;
        ProbeSchema.Current = "sfprobe_missing";
        var database = Probe<ProbeContextV1>("sfprobe_missing");

        var status = await ModuleDatabases.StatusAsync(database, missing, null);
        var refused = await Assert.ThrowsAsync<ModuleDatabaseException>(
            () => ModuleDatabases.MigrateAsync(database, missing, allowCreate: false, "module database tests", DateTime.UtcNow, null));

        Assert.Equal(ModuleDatabaseState.Missing, status.State);
        Assert.Contains("does not exist", refused.Message, StringComparison.Ordinal);
        Assert.Contains(missingName, refused.Message, StringComparison.Ordinal);
        var master = new SqlConnectionStringBuilder(reachable) { InitialCatalog = "master" }.ConnectionString;
        Assert.Equal(DBNull.Value, await ScalarAsync(master, "SELECT DB_ID(@name)", ("@name", missingName)));
    }

    private static ModuleDatabase<TContext> Probe<TContext>(
        string schema, string version = "1.0.0", string module = "probe", string? reference = null, string? minimumCatalogMigration = null, string? historySchema = null)
        where TContext : ProbeContextBase
        => new(
            module,
            schema,
            version,
            connectionString => ProbeContextBase.Create<TContext>(builder => builder.UseSqlServer(
                connectionString, sql => sql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, historySchema ?? schema))),
            connection => ProbeContextBase.Create<TContext>(builder => builder.UseSqlServer(
                connection, sql => sql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, historySchema ?? schema))),
            reference,
            minimumCatalogMigration)
        {
            AfterMigrate = ProbeContextBase.RecordVersionAsync,
            ReadRecordedVersion = ProbeContextBase.ReadVersionAsync,
        };

    private static string NewSchema()
    {
        var schema = $"sfmod_{Guid.NewGuid():N}";
        ProbeSchema.Current = schema;
        return schema;
    }

    private static T FindInnermost<T>(Exception error)
        where T : Exception
    {
        T? found = null;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            found = current as T ?? found;
            if (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                return FindInnermost<T>(aggregate.InnerExceptions[0]);
            }
        }

        return found ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} in: {error}");
    }

    private static async Task<int> CountTablesAsync(string connectionString, string schema)
        => (int)(await ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = @schema",
            ("@schema", schema)))!;

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'; '
            FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = @schema;
            IF SCHEMA_ID(@schema) IS NOT NULL SET @sql = @sql + N'DROP SCHEMA ' + QUOTENAME(@schema) + N';';
            EXEC sys.sp_executesql @sql;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MapResolver(Dictionary<string, string> values) : ISecretResolver
    {
        public string Resolve(string value) => values.TryGetValue(value, out var resolved) ? resolved : value;

        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(Resolve(value));
    }

    private sealed class DatabaseModule(string name, ModuleDatabase database) : IControlPlaneModule
    {
        public string Name => name;

        public void ConfigureServices(ControlPlaneModuleServices services) => services.AddDatabase(database);

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
        {
        }
    }
}

/// <summary>The schema the probe contexts and migrations use. The control plane suite runs its tests one at a time, so each
/// test sets it before it builds a context; the model cache is keyed by it, so a context never reuses another schema's model.</summary>
public static class ProbeSchema
{
    public static string Current { get; set; } = "sfprobe";
}

public static class ProbeMigrationIds
{
    public const string First = "20260101000000_ProbeFirst";
    public const string Second = "20260102000000_ProbeSecond";
}

public sealed class ProbeRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>The probe module's single-row version table, written by its after-migrate hook.</summary>
public sealed class ProbeVersion
{
    public int Id { get; set; }

    public string ModuleVersion { get; set; } = string.Empty;

    public string? LastMigration { get; set; }

    public string AppliedBy { get; set; } = string.Empty;
}

/// <summary>A tiny module context: one table, one version table, in <see cref="ProbeSchema.Current"/>.</summary>
public abstract class ProbeContextBase : DbContext
{
    protected ProbeContextBase(DbContextOptions options)
        : base(options)
    {
        Schema = ProbeSchema.Current;
    }

    public string Schema { get; }

    public DbSet<ProbeRow> Rows => Set<ProbeRow>();

    public DbSet<ProbeVersion> Versions => Set<ProbeVersion>();

    /// <summary>The module's context over options it configures itself, as a real module's factory does.</summary>
    public static TContext Create<TContext>(Action<DbContextOptionsBuilder<TContext>> configure)
        where TContext : ProbeContextBase
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        configure(builder);
        return (TContext)Activator.CreateInstance(typeof(TContext), builder.Options)!;
    }

    public static async Task RecordVersionAsync(ProbeContextBase context, ModuleMigrationApplied applied, CancellationToken ct)
    {
        var row = await context.Versions.SingleOrDefaultAsync(v => v.Id == 1, ct);
        if (row is null)
        {
            row = new ProbeVersion { Id = 1 };
            context.Versions.Add(row);
        }

        row.ModuleVersion = applied.Version;
        row.LastMigration = applied.LastMigration;
        row.AppliedBy = applied.AppliedBy;
        await context.SaveChangesAsync(ct);
    }

    public static async Task<ModuleRecordedVersion?> ReadVersionAsync(ProbeContextBase context, CancellationToken ct)
        => await context.Versions.AsNoTracking()
            .Where(v => v.Id == 1)
            .Select(v => new ModuleRecordedVersion(v.ModuleVersion, v.LastMigration))
            .SingleOrDefaultAsync(ct);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // The probe migrations are hand-written without a model snapshot.
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ProbeRow>().ToTable("ProbeRow");
        modelBuilder.Entity<ProbeVersion>().ToTable("ProbeVersion").Property(v => v.Id).ValueGeneratedNever();
    }
}

public sealed class ProbeContextV1(DbContextOptions<ProbeContextV1> options) : ProbeContextBase(options);

public sealed class ProbeContextV2(DbContextOptions<ProbeContextV2> options) : ProbeContextBase(options);

/// <summary>A module context that maps its table into dbo: refused by every module database operation.</summary>
public sealed class OutsideSchemaContext(DbContextOptions<OutsideSchemaContext> options) : ProbeContextBase(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ProbeRow>().ToTable("ProbeRow", "dbo");
        modelBuilder.Entity<ProbeVersion>().ToTable("ProbeVersion").Property(v => v.Id).ValueGeneratedNever();
    }
}

public sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), (context as ProbeContextBase)?.Schema, designTime);
}

internal static class ProbeMigrationSteps
{
    public static void First(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(ProbeSchema.Current);
        migrationBuilder.CreateTable(
            name: "ProbeRow",
            schema: ProbeSchema.Current,
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(200)", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProbeRow", row => row.Id));
        migrationBuilder.CreateTable(
            name: "ProbeVersion",
            schema: ProbeSchema.Current,
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ModuleVersion = table.Column<string>(type: "nvarchar(32)", nullable: false),
                LastMigration = table.Column<string>(type: "nvarchar(150)", nullable: true),
                AppliedBy = table.Column<string>(type: "nvarchar(256)", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProbeVersion", row => row.Id));
    }

    public static void Second(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(name: "Label", schema: ProbeSchema.Current, table: "ProbeRow", type: "nvarchar(50)", nullable: true);
}

[DbContext(typeof(ProbeContextV1))]
[Migration(ProbeMigrationIds.First)]
public sealed class ProbeFirstV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ProbeMigrationSteps.First(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ProbeVersion", ProbeSchema.Current);
        migrationBuilder.DropTable("ProbeRow", ProbeSchema.Current);
    }
}

[DbContext(typeof(ProbeContextV2))]
[Migration(ProbeMigrationIds.First)]
public sealed class ProbeFirstV2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ProbeMigrationSteps.First(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ProbeVersion", ProbeSchema.Current);
        migrationBuilder.DropTable("ProbeRow", ProbeSchema.Current);
    }
}

[DbContext(typeof(ProbeContextV2))]
[Migration(ProbeMigrationIds.Second)]
public sealed class ProbeSecondV2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ProbeMigrationSteps.Second(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Label", "ProbeRow", ProbeSchema.Current);
}
