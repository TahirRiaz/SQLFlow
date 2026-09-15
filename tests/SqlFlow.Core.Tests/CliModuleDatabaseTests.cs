using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog.Modules;
using SqlFlow.Cli.Hosting;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A CLI module registers its database through <see cref="CliModuleServices.AddDatabase"/>, and <c>sqlflow db migrate</c> and
/// <c>db status</c> take <c>--module</c> to address one module database. The refusals run before any connection is opened.
/// </summary>
public sealed class CliModuleDatabaseTests
{
    [Fact]
    public async Task DbStatus_RefusesAModuleDatabaseTheHostDoesNotRegister_NamingTheOnesItDoes()
    {
        var exit = await CliHost.RunAsync(["db", "status", "--module", "absent"], new DatabaseModule("probe", "probe"));

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task DbSync_RefusesModule()
    {
        var exit = await CliHost.RunAsync(["db", "sync", "--module", "probe"], new DatabaseModule("probe", "probe"));

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task AModuleRegisteringAnotherModulesDatabase_StopsTheCommand()
    {
        var module = new DatabaseModule("probe", "other");

        var exit = await CliHost.RunAsync(["db", "status", "--module", "other"], module);

        Assert.Equal(1, exit);
        var error = Assert.IsType<CliModuleException>(module.Failure);
        Assert.Contains("registers the database of module 'other'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModuleOption_TakesAValue()
        => Assert.Equal(["db", "status"], new CliArguments(["db", "--module", "probe", "status"]).Positionals);

    private sealed class DatabaseModule(string name, string databaseModule) : ICliModule
    {
        public Exception? Failure { get; private set; }

        public string Name => name;

        public IReadOnlyList<CliVerb> Verbs => [];

        public void ConfigureServices(CliModuleServices services)
        {
            try
            {
                services.AddDatabase(new ModuleDatabase<CliProbeContext>(
                    databaseModule,
                    "cliprobe",
                    "1.0.0",
                    connectionString => new CliProbeContext(new DbContextOptionsBuilder<CliProbeContext>()
                        .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "cliprobe")).Options),
                    connection => new CliProbeContext(new DbContextOptionsBuilder<CliProbeContext>()
                        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "cliprobe")).Options),
                    "${env:SQLFLOW_CLI_PROBE_DB}"));
            }
            catch (CliModuleException ex)
            {
                Failure = ex;
                throw;
            }
        }
    }

    private sealed class CliProbeContext(DbContextOptions<CliProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("cliprobe");
    }
}
