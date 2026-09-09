using LibGit2Sharp;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.SourceControl;
using SqlFlow.SourceControl;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The real SMO end to end: against a live database, create a table (with identity and a primary key), a view,
/// and a stored procedure, then run the whole source-control flow into a local git repository and prove the
/// objects were scripted to the legacy folder layout, the chosen table's data was scripted, and a commit was
/// made. Skips when no sink database is configured, so the unit suite still runs everywhere.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceControlIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scm_it_" + Guid.NewGuid().ToString("N"));

    public SourceControlIntegrationTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task ScriptsObjects_AndData_ThenCommits()
    {
        var cs = IntegrationDb.Require();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var table = $"SCM_T_{tag}";
        var view = $"SCM_V_{tag}";
        var proc = $"SCM_P_{tag}";
        var database = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();") ?? "master";

        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [dbo].[{table}] (Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, Name nvarchar(100) NOT NULL);
            INSERT INTO [dbo].[{table}] (Name) VALUES (N'Acme'), (N'Globex');
            """);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE VIEW [dbo].[{view}] AS SELECT Id, Name FROM [dbo].[{table}];");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE PROCEDURE [dbo].[{proc}] AS SELECT COUNT(*) FROM [dbo].[{table}];");

        // Reference the connection through an env var so it is a whole-secret reference (the canonical form);
        // a literal inline connection string with a password would trip the secretless gate.
        var envName = $"SQLFLOW_SCM_IT_{tag}";
        Environment.SetEnvironmentVariable(envName, cs);

        try
        {
            var connection = new DataSource
            {
                Alias = "DW",
                Kind = DataSourceKind.MSSQL,
                ConnectionRef = "${env:" + envName + "}",
                Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
            };

            var flow = new SourceControlFlow
            {
                FlowId = 1,
                SysAlias = "scm-it",
                Server = "DW",
                Repository = new SourceControlRepository { WorkingDirectory = _dir, AuthorName = "IT", AuthorEmail = "it@test.local" },
                Scripting = new SourceControlScripting
                {
                    IncludeTypes = ["Table", "View", "StoredProcedure"],
                    DataTables = [$"dbo.{table}"],
                },
            };

            var service = WithoutDatabaseSourceControl.BuildService([connection]);
            var result = await service.RunAsync(flow);

            Assert.True(result.Success, result.Error);
            Assert.Equal(database, result.DatabaseName);
            Assert.True(result.Committed);
            Assert.NotNull(result.CommitSha);

            var tablePath = Path.Combine(_dir, database, "Table", $"dbo.{table}.sql");
            var viewPath = Path.Combine(_dir, database, "View", $"dbo.{view}.sql");
            var procPath = Path.Combine(_dir, database, "StoredProcedure", $"dbo.{proc}.sql");
            var dataPath = Path.Combine(_dir, database, "Data", $"dbo.{table}.sql");

            Assert.True(File.Exists(tablePath), "table script not written");
            Assert.True(File.Exists(viewPath), "view script not written");
            Assert.True(File.Exists(procPath), "procedure script not written");
            Assert.True(File.Exists(dataPath), "data script not written");

            Assert.Contains("CREATE TABLE", File.ReadAllText(tablePath), StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"PK_{table}", File.ReadAllText(tablePath), StringComparison.Ordinal);

            // The table has an identity column, so its scripted data is wrapped in SET IDENTITY_INSERT.
            var dataSql = File.ReadAllText(dataPath);
            Assert.Contains("SET IDENTITY_INSERT", dataSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("INSERT", dataSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Acme", dataSql, StringComparison.Ordinal);

            // A real commit landed on the default branch with the scripted files.
            using var repo = new Repository(_dir);
            Assert.NotNull(repo.Head.Tip);
            Assert.NotNull(repo.Head.Tip[$"{database}/Table/dbo.{table}.sql"]);

            // Re-running with no schema change commits nothing (deterministic, idempotent snapshot).
            var second = await service.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.False(second.Committed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
            await IntegrationDb.ExecuteAsync(cs, $"DROP PROCEDURE IF EXISTS [dbo].[{proc}];");
            await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS [dbo].[{table}];");
        }
    }

    [SkippableFact]
    public async Task StagingSchema_IsSkipped_UnlessTheFlowAsksForIt()
    {
        var cs = IntegrationDb.Require();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var work = $"SCM_W_{tag}";
        var kept = $"SCM_K_{tag}";
        var staging = StagingConventions.SchemaName;
        var database = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();") ?? "master";

        await IntegrationDb.ExecuteAsync(cs, $"IF SCHEMA_ID(N'{staging}') IS NULL EXEC(N'CREATE SCHEMA [{staging}]');");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [{staging}].[{work}] (Id int NOT NULL, Payload nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{kept}] (Id int NOT NULL);");

        var envName = $"SQLFLOW_SCM_IT_{tag}";
        Environment.SetEnvironmentVariable(envName, cs);

        try
        {
            var connection = new DataSource
            {
                Alias = "DW",
                Kind = DataSourceKind.MSSQL,
                ConnectionRef = "${env:" + envName + "}",
                Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
            };
            var service = WithoutDatabaseSourceControl.BuildService([connection]);

            SourceControlFlow Flow(SourceControlScripting scripting) => new()
            {
                FlowId = 2,
                SysAlias = "scm-it-staging",
                Server = "DW",
                Repository = new SourceControlRepository { WorkingDirectory = _dir, AuthorName = "IT", AuthorEmail = "it@test.local" },
                Scripting = scripting,
            };

            // The default: the engine's staging schema is not part of the database's tracked definition, so its
            // work tables (and the schema itself) never reach the snapshot, while dbo is scripted as usual.
            var defaults = await service.RunAsync(Flow(new SourceControlScripting { IncludeTypes = ["Schema", "Table"] }));
            Assert.True(defaults.Success, defaults.Error);
            Assert.False(File.Exists(Path.Combine(_dir, database, "Table", $"{staging}.{work}.sql")), "a staging work table was scripted");
            Assert.False(File.Exists(Path.Combine(_dir, database, "Schema", $"{staging}.sql")), "the staging schema itself was scripted");
            Assert.True(File.Exists(Path.Combine(_dir, database, "Table", $"dbo.{kept}.sql")), "the ordinary table was not scripted");

            // An explicitly empty exclusion list opts back in, which is the only way to get them.
            var everything = await service.RunAsync(Flow(new SourceControlScripting { IncludeTypes = ["Schema", "Table"], ExcludeSchemas = [] }));
            Assert.True(everything.Success, everything.Error);
            Assert.True(File.Exists(Path.Combine(_dir, database, "Table", $"{staging}.{work}.sql")), "an opted-in staging table was still skipped");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS [{staging}].[{work}];");
            await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS [dbo].[{kept}];");
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }
}
