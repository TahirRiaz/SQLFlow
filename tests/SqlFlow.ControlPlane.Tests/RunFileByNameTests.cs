using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Finding the runs that handled a file by its name. The catalog has always answered "what did this run process";
/// this is the other direction, "what has been done with this file", which is what tracing a row back through the
/// flows that landed and loaded it needs. The index makes it a seek rather than a scan of every processed file the
/// estate has ever recorded, so the answer stays in milliseconds however long the estate has been running.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunFileByNameTests
{
    [SkippableFact]
    public async Task Files_are_found_by_name_across_runs_from_an_index()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var repoId = FlowIdentity.FromName("cp_byname_" + suffix);
        var landPipeline = CatalogIdentity.Pipeline(repoId, "cp_land_" + suffix);
        var loadPipeline = CatalogIdentity.Pipeline(repoId, "cp_load_" + suffix);
        var landRun = Guid.NewGuid();
        var loadRun = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        var file = $"{suffix}_readings.csv";

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.AddRange(
                    Pipeline(landPipeline, repoId, "cp_land_" + suffix, "file"),
                    Pipeline(loadPipeline, repoId, "cp_load_" + suffix, "ing"));
                db.Runs.AddRange(
                    Run(landRun, landPipeline, repoId, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
                    Run(loadRun, loadPipeline, repoId, new DateTime(2024, 6, 1, 0, 10, 0, DateTimeKind.Utc), "ing"),
                    Run(otherRun, loadPipeline, repoId, new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc), "ing"));
                db.RunFiles.AddRange(
                    new CatalogRunFile { RunId = landRun, RepoId = repoId, Name = file, Path = "/drop/" + file, Rows = 42, Columns = 7, SizeBytes = 2048 },
                    new CatalogRunFile { RunId = loadRun, RepoId = repoId, Name = file, Path = "/drop/" + file, Rows = 42, Columns = 7, SizeBytes = 2048 },
                    new CatalogRunFile { RunId = otherRun, RepoId = repoId, Name = $"{suffix}_other.csv", Rows = 1, Columns = 1, SizeBytes = 16 });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                // Both runs that handled the file, and only those: the file of another name is a different question.
                var runs = await db.RunFiles.AsNoTracking()
                    .Where(f => f.Name == file)
                    .Join(db.Runs.AsNoTracking(), f => f.RunId, r => r.RunId, (f, r) => new { r.RunId, r.FlowKind, f.Rows })
                    .OrderBy(x => x.FlowKind)
                    .ToListAsync();

                Assert.Equal(2, runs.Count);
                Assert.Equal(["file", "ing"], runs.Select(r => r.FlowKind));
                Assert.All(runs, r => Assert.Equal(42, r.Rows));
                Assert.Contains(runs, r => r.RunId == landRun);
                Assert.Contains(runs, r => r.RunId == loadRun);

                // A prefix of the name works the same way, which is what a search over landed files does.
                Assert.Equal(3, await db.RunFiles.AsNoTracking().CountAsync(f => f.Name.StartsWith(suffix)));
            }

            // The index exists, and leads with the name: a lookup by file name seeks it instead of scanning the table.
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT_BIG(*)
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS c ON c.[object_id] = i.[object_id] AND c.[index_id] = i.[index_id] AND c.[key_ordinal] = 1
                INNER JOIN sys.columns AS col ON col.[object_id] = c.[object_id] AND col.[column_id] = c.[column_id]
                WHERE i.[object_id] = OBJECT_ID(N'[catalog].[RunFile]') AND col.[name] = N'Name';
                """;
            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = name + ".flow.yaml",
        ContentHash = "hash",
        Active = true,
        FirstSeenUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastSeenUtc = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    private static CatalogRun Run(Guid runId, Guid pipelineId, Guid repoId, DateTime at, string kind = "file") => new()
    {
        RunId = runId,
        PipelineId = pipelineId,
        RepoId = repoId,
        FlowName = "flow",
        FlowKind = kind,
        Status = RunStatuses.Succeeded,
        Success = true,
        WrittenUtc = at,
    };
}
