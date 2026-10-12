using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// <see cref="DatasourceInference"/> against a real catalog: which declared datasource a query belongs to, from a
/// registration, from the object keys a caller holds, or from the tables its SQL reads, and when it declines to say.
/// Every case seeds its own datasources, pipelines, objects and edges under references of its own and removes them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatasourceInferenceTests
{
    [SkippableFact]
    public async Task ATableOnOneDeclaredDatasource_IsThatDatasource()
    {
        await WithEstateAsync(async (db, e) =>
        {
            var result = await DatasourceInference.InferAsync(db, $"SELECT COUNT(*) FROM edw.{e.OnlyA}", null, default);
            Assert.Equal(e.RefA, result.Reference);
            Assert.Equal([e.RefA], result.Candidates);
        });
    }

    [SkippableFact]
    public async Task ATableOnTwoDatasources_NamesBoth_SortedAndDecidesNeither()
    {
        await WithEstateAsync(async (db, e) =>
        {
            var result = await DatasourceInference.InferAsync(db, $"SELECT COUNT(*) FROM edw.{e.Shared}", null, default);
            Assert.Null(result.Reference);
            Assert.Equal(new[] { e.RefA, e.RefB }.Order(StringComparer.OrdinalIgnoreCase), result.Candidates);
        });
    }

    [SkippableFact]
    public async Task TheKeysACallerHolds_DecideBeforeTheSql()
    {
        await WithEstateAsync(async (db, e) =>
        {
            // The SQL alone could not tell; a key names the datasource, whatever the case of its server segment.
            var result = await DatasourceInference.InferAsync(
                db, $"SELECT COUNT(*) FROM edw.{e.Shared}", [KeyOf(e.RefB, e.Shared).ToUpperInvariant()], default);
            Assert.Equal(e.RefB, result.Reference);
        });
    }

    [SkippableTheory]
    [InlineData("no-bar-at-all")]
    [InlineData("|dw|edw|t")]
    [InlineData("   ")]
    [InlineData("${env:NOT_DECLARED}|dw|edw|t")]
    public async Task AKeyThatNamesNoDeclaredDatasource_FallsBackToTheSql(string key)
    {
        await WithEstateAsync(async (db, e) =>
        {
            var result = await DatasourceInference.InferAsync(db, $"SELECT COUNT(*) FROM edw.{e.OnlyA}", [key], default);
            Assert.Equal(e.RefA, result.Reference);
        });
    }

    [SkippableFact]
    public async Task ACteNamedLikeARealTable_DoesNotHideTheRealOne()
    {
        await WithEstateAsync(async (db, e) =>
        {
            var sql = $"WITH {e.OnlyA} AS (SELECT 1 AS x) SELECT COUNT(*) FROM edw.{e.OnlyA}";
            Assert.Equal(e.RefA, (await DatasourceInference.InferAsync(db, sql, null, default)).Reference);

            // Read unqualified, the name is the CTE's, which lives nowhere.
            var cteOnly = $"WITH {e.OnlyA} AS (SELECT 1 AS x) SELECT x FROM {e.OnlyA}";
            Assert.Empty((await DatasourceInference.InferAsync(db, cteOnly, null, default)).Candidates);
        });
    }

    [SkippableFact]
    public async Task ARegistrationOnAnUndeclaredServer_IsNoEvidence_SoTheSqlIsAsked()
    {
        await WithEstateAsync(async (db, e) =>
        {
            // The registering pipeline's source server is not a whole reference, so it names nowhere a query may run.
            var result = await DatasourceInference.InferAsync(
                db, $"SELECT COUNT(*) FROM edw.{e.OnlyA}", [KeyOf(e.RefA, e.OnlyA)], default);
            Assert.Equal(e.RefA, result.Reference);
        }, registerOnlyAOn: "Server=inline;Database=dw");
    }

    [SkippableFact]
    public async Task ARegistrationOnADeclaredServer_DecidesAndNamesTheDatabase()
    {
        await WithEstateAsync(async (db, e) =>
        {
            var result = await DatasourceInference.InferAsync(db, $"SELECT COUNT(*) FROM edw.{e.OnlyA}", null, default);
            Assert.Equal(e.RefB, result.Reference);
            Assert.Equal("dw", result.Database);
        }, registerOnlyAOn: "B");
    }

    [SkippableFact]
    public async Task AnInactivePipelinesReference_IsNotDeclared()
    {
        await WithEstateAsync(async (db, e) =>
        {
            Assert.False(await DatasourceInference.IsDeclaredAsync(db, e.RefRetired, default));
            Assert.True(await DatasourceInference.IsDeclaredAsync(db, " " + e.RefA + " ", default));

            var result = await DatasourceInference.InferAsync(db, $"SELECT COUNT(*) FROM edw.{e.OnRetired}", null, default);
            Assert.Null(result.Reference);
            Assert.Empty(result.Candidates);
        });
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("DELETE FROM edw.x")]
    [InlineData("SELECT FROM WHERE")]
    [InlineData("SELECT 1")]
    public async Task NothingToGoOn_IsNoAnswer(string sql)
    {
        await WithEstateAsync(async (db, _) =>
        {
            var result = await DatasourceInference.InferAsync(db, sql, null, default);
            Assert.Null(result.Reference);
            Assert.Empty(result.Candidates);
        });
    }

    [SkippableFact]
    public async Task ObjectKeysFromSql_AreTheCataloguedObjectsTheStatementReads()
    {
        await WithEstateAsync(async (db, e) =>
        {
            var keys = await DatasourceInference.ObjectKeysFromSqlAsync(
                db, $"SELECT a.Id FROM edw.{e.OnlyA} a JOIN edw.{e.Shared} s ON s.Id = a.Id", default);
            Assert.Equal(
                new[] { KeyOf(e.RefA, e.OnlyA), KeyOf(e.RefA, e.Shared), KeyOf(e.RefB, e.Shared) }.Order(StringComparer.Ordinal),
                keys.Order(StringComparer.Ordinal));
            Assert.Empty(await DatasourceInference.ObjectKeysFromSqlAsync(db, "  ", default));
        });
    }

    /// <summary>The estate one case runs against: three declared datasources and their tables.</summary>
    private sealed record Estate(string RefA, string RefB, string RefRetired, string OnlyA, string Shared, string OnRetired);

    /// <summary>The node key of <paramref name="table"/> on <paramref name="reference"/>.</summary>
    private static string KeyOf(string reference, string table) => $"{reference}|dw|edw|{table}".ToLowerInvariant();

    /// <summary>
    /// Seeds two active datasources (A and B) and a retired one, a table only A holds, a table both hold, and a table only
    /// the retired one holds. <paramref name="registerOnlyAOn"/>, when given, adds a schema registration of the table
    /// only A holds, by a pipeline whose source server is "B" (datasource B) or the literal given.
    /// </summary>
    private static async Task WithEstateAsync(Func<CatalogDbContext, Estate, Task> check, string? registerOnlyAOn = null)
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var estate = new Estate(
            "${env:SQLFLOW_INFER_A_" + suffix + "}",
            "${env:SQLFLOW_INFER_B_" + suffix + "}",
            "${env:SQLFLOW_INFER_RETIRED_" + suffix + "}",
            "OnlyA_" + suffix,
            "Shared_" + suffix,
            "Retired_" + suffix);
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var keys = new List<string>();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Repos.Add(new CatalogRepo { Id = repoId, Name = "infer_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                foreach (var (reference, active) in new[] { (estate.RefA, true), (estate.RefB, true), (estate.RefRetired, false) })
                {
                    seed.Pipelines.Add(Pipeline(repoId, reference, reference, active, "ing", now));
                }

                foreach (var (reference, table) in new[]
                {
                    (estate.RefA, estate.OnlyA), (estate.RefA, estate.Shared), (estate.RefB, estate.Shared), (estate.RefRetired, estate.OnRetired),
                })
                {
                    var key = KeyOf(reference, table);
                    keys.Add(key);
                    seed.Objects.Add(new CatalogObject
                    {
                        Key = key, ServerRef = reference, Database = "dw", Schema = "edw", Name = table, Kind = "Table",
                        FirstSeenUtc = now, LastSeenUtc = now,
                    });
                }

                if (registerOnlyAOn is not null)
                {
                    var source = registerOnlyAOn == "B" ? estate.RefB : registerOnlyAOn;
                    var registrar = Pipeline(repoId, source, null, true, "sch", now);
                    seed.Pipelines.Add(registrar);
                    seed.LineageEdges.Add(new CatalogLineageEdge
                    {
                        RepoId = repoId, Flow = registrar.Name, PipelineId = registrar.Id, Relation = "Registers",
                        ObjectKey = KeyOf(estate.RefA, estate.OnlyA), ObjectName = estate.OnlyA, Tier = "Declared",
                    });
                }

                await seed.SaveChangesAsync();
            }

            await using var db = CatalogDatabase.Create(cs);
            await check(db, estate);
        }
        finally
        {
            await using var cleanup = CatalogDatabase.Create(cs);
            await cleanup.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await cleanup.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
            await cleanup.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await cleanup.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline Pipeline(Guid repoId, string? source, string? target, bool active, string kind, DateTime now)
        => new()
        {
            Id = Guid.NewGuid(), RepoId = repoId, Name = "infer_flow_" + Guid.NewGuid().ToString("N")[..8], Kind = kind,
            RelativePath = "infer/flow.yaml", Active = active, SourceServer = source, TargetServer = target,
            DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
        };
}
