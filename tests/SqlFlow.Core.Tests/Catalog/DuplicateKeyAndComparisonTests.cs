using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Quality;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The trust boundary for the two data-operations checks: the duplicate-key check and the old-versus-new
/// baseline comparison. Both run against live production estates from an interactive surface, so what they
/// REFUSE matters as much as what they measure, and both must stay read-only.
/// </summary>
public sealed class DuplicateKeyAndComparisonTests
{
    private static DuplicateKeyRequest Duplicates(string schema = "arc", string objectName = "Ferde_Passeringer")
        => new() { Schema = schema, ObjectName = objectName, Database = "dwh" };

    [Fact]
    public void Duplicates_AcceptsAPlainObjectScope()
    {
        var request = Duplicates().Validate(DataSourceKind.AZDB);
        Assert.Equal("arc.Ferde_Passeringer", request.Target);
        Assert.Empty(request.Columns);
    }

    [Fact]
    public void Duplicates_RejectsANonSqlServerSource_BecauseTheCheckIsTSql()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Duplicates().Validate(DataSourceKind.PostgreSQL));
        Assert.Contains("T-SQL", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Every identifier reaching generated SQL passes the same strict guard, so the read-only property rests on
    // one check rather than on every call site quoting correctly.
    [InlineData("arc]; DROP TABLE arc.Sales --", "T")]
    [InlineData("arc", "T]; TRUNCATE TABLE arc.Sales --")]
    [InlineData("arc", "T' OR 1=1 --")]
    public void Duplicates_RefusesAnIdentifierCarryingSqlSyntax(string schema, string objectName)
    {
        Assert.Throws<SqlFlowException>(() => Duplicates(schema, objectName).Validate(DataSourceKind.MSSQL));
    }

    [Fact]
    public void Duplicates_RefusesAColumnCarryingSqlSyntax()
    {
        var request = Duplicates() with { Columns = ["Dato]; DROP TABLE x --"] };
        Assert.Throws<SqlFlowException>(() => request.Validate(DataSourceKind.MSSQL));
    }

    [Fact]
    public void Duplicates_RefusesMoreColumnsThanAKeyCouldPlausiblyHave()
    {
        var request = Duplicates() with
        {
            Columns = Enumerable.Range(0, DuplicateKeyRequest.MaxColumns + 1).Select(i => $"c{i}").ToArray(),
        };

        Assert.Throws<SqlFlowException>(() => request.Validate(DataSourceKind.MSSQL));
    }

    // ----------------------------------------------------------------------------------------------------
    // Baseline comparison.
    // ----------------------------------------------------------------------------------------------------

    private static readonly string[] Allowed = ["old-dwh-prod", "old-pre-prod", "OLDPROD"];

    private static BaselineComparisonRequest Comparison(BaselineCompareMode mode) => new()
    {
        Mode = mode,
        LinkedServer = "old-dwh-prod",
        BaselineDatabase = "dw-dwh-prod",
        Schema = "arc",
        ObjectName = mode == BaselineCompareMode.Inventory ? null : "Ferde_Passeringer",
    };

    [Fact]
    public void Comparison_RefusesALinkedServerThatIsNotAllowlisted()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { LinkedServer = "SOMEWHERE_ELSE" };
        var ex = Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        Assert.Contains("not allowlisted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_RefusesEveryLinkedServerWhenNoneIsConfigured()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Comparison(BaselineCompareMode.Inventory).Validate([]));
        Assert.Contains("LinkedServers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_MatchesTheAllowlistCaseInsensitively()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { LinkedServer = "OLD-DWH-PROD" };
        Assert.Equal("OLD-DWH-PROD", request.Validate(Allowed).LinkedServer);
    }

    [Fact]
    public void Comparison_RequiresAnObjectForSchemaAndDataModes()
    {
        foreach (var mode in new[] { BaselineCompareMode.Schema, BaselineCompareMode.Data })
        {
            var request = Comparison(mode) with { ObjectName = null };
            Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        }
    }

    [Fact]
    public void Comparison_RefusesAnObjectInInventoryMode_RatherThanIgnoringIt()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { ObjectName = "Ferde_Passeringer" };
        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RequiresALogicalKeyInDataMode()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Comparison(BaselineCompareMode.Data).Validate(Allowed));
        Assert.Contains("LOGICAL key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_AcceptsAnExpressionKeyAndDefaultsTheOldSideToTheNewSide()
    {
        var request = (Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato", "Sted", "CAST(Klokkeslett AS time)"],
            Where = "Dato >= '2025-01-01'",
        }).Validate(Allowed);

        Assert.Equal("arc", request.EffectiveBaselineSchema);
        Assert.Equal("Ferde_Passeringer", request.EffectiveBaselineObject);
        Assert.Equal(3, request.KeyExpressions.Count);
    }

    [Fact]
    public void Comparison_RefusesAnInjectedKeyExpression()
    {
        var request = Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato) DROP TABLE arc.Sales --"],
        };

        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RefusesAnInjectedFilter()
    {
        var request = Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato"],
            Where = "1=1; TRUNCATE TABLE arc.Sales",
        };

        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RefusesKeysAndFiltersOutsideDataMode()
    {
        var request = Comparison(BaselineCompareMode.Schema) with { KeyExpressions = ["Dato"] };
        var ex = Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        Assert.Contains("data mode only", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------
    // The compute payload that carries both through the queue.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Payload_RoundTripsADuplicateKeyRequestThroughTheQueue()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.DuplicateKeys,
            SourceRef = "${env:SQLFLOW_DWH}",
            Database = "dwh",
            Schema = "arc",
            ObjectName = "Ferde_Passeringer",
            Columns = ["Dato", "Sted"],
        };

        var request = ComputeTaskPayload.FromJson(payload.ToJson()).ToDuplicateKeyRequest();
        Assert.Equal("arc.Ferde_Passeringer", request.Target);
        Assert.Equal(["Dato", "Sted"], request.Columns);
    }

    [Fact]
    public void Payload_RoundTripsAComparisonRequestThroughTheQueue()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.CompareBaseline,
            SourceRef = "${env:SQLFLOW_DWH}",
            CompareMode = BaselineCompareMode.Data,
            LinkedServer = "old-dwh-prod",
            BaselineDatabase = "dw-dwh-prod",
            Schema = "arc",
            ObjectName = "Ferde_Passeringer",
            BaselineObjectName = "Ferde_Passeringer_Old",
            KeyExpressions = ["Dato", "CAST(Klokkeslett AS time)"],
        };

        var request = ComputeTaskPayload.FromJson(payload.ToJson()).ToComparisonRequest(Allowed);
        Assert.Equal(BaselineCompareMode.Data, request.Mode);
        Assert.Equal("Ferde_Passeringer_Old", request.EffectiveBaselineObject);
    }

    [Fact]
    public void Payload_RequiresBothSchemaAndObjectForTheDuplicateCheck()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.DuplicateKeys,
            SourceRef = "${env:SQLFLOW_DWH}",
            Schema = "arc",
        };

        var ex = Assert.Throws<SqlFlowException>(payload.Validate);
        Assert.Contains("objectName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_RequiresACompareMode()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.CompareBaseline,
            SourceRef = "${env:SQLFLOW_DWH}",
            LinkedServer = "OLDPROD",
            BaselineDatabase = "dw-dwh-prod",
            Schema = "arc",
        };

        var ex = Assert.Throws<SqlFlowException>(payload.Validate);
        Assert.Contains("compareMode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGatedOperation_IsDiscoverable_SoNoCapabilityIsInvisible()
    {
        // The capabilities endpoint derives its operation list from this set rather than keeping a copy. A
        // hand-kept copy goes stale the moment an operation is added, and the failure is silent from the
        // server's side: it answers 200, and a reading client concludes the missing capability does not exist
        // and tells the user the product cannot do it. runQuery shipped, deployed, and stayed unusable for
        // exactly that reason, so the set is pinned here by name.
        var gated = ComputeOperations.All.Where(ComputeOperations.IsDataOps).ToArray();

        Assert.Contains(ComputeOperations.DuplicateKeys, gated);
        Assert.Contains(ComputeOperations.CompareBaseline, gated);
        Assert.Contains(ComputeOperations.RunQuery, gated);
        Assert.Equal(3, gated.Length);
    }

    [Fact]
    public void OnlyTheGatedOperationsAreGated_TheOlderProbesAreUntouched()
    {
        // The four warehouse-health probes predate this surface and back the insights recommendations. They
        // must NOT sit behind the DataOps switch, or turning it off would silently break the dashboard.
        Assert.True(ComputeOperations.IsDataOps(ComputeOperations.DuplicateKeys));
        Assert.True(ComputeOperations.IsDataOps(ComputeOperations.CompareBaseline));

        foreach (var probe in ComputeOperations.All.Where(ComputeOperations.IsWarehouseHealth))
        {
            Assert.False(ComputeOperations.IsDataOps(probe));
        }

        Assert.False(ComputeOperations.IsDataOps(ComputeOperations.ListObjects));
    }

    // ----------------------------------------------------------------------------------------------------
    // The read-only contract, asserted against the shipped SQL rather than left as an intention.
    // ----------------------------------------------------------------------------------------------------

    private static IEnumerable<(string File, string Sql)> RawSqlLiterals(string relativeDirectory)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SqlFlow.SqlServer", relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Expected sources at {Path.GetFullPath(directory)}.");

        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var chunks = File.ReadAllText(file).Split("\"\"\"");
            Assert.True(chunks.Length % 2 == 1, $"{Path.GetFileName(file)} has an unbalanced raw string literal.");
            for (var i = 1; i < chunks.Length; i += 2)
            {
                yield return (Path.GetFileName(file), chunks[i]);
            }
        }
    }

    private static readonly string[] WriteVerbs =
        ["INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "CREATE", "ALTER", "EXEC", "EXECUTE",
         "GRANT", "REVOKE", "DBCC", "BACKUP", "RESTORE"];

    private static IEnumerable<string> WriteStatements(string sql)
        => sql.Split(';', '\n')
            .Select(line => line.Trim())
            .Where(line => WriteVerbs.Any(verb => line.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void DuplicateKeyCheck_ExecutesReadOnlySql()
    {
        var literals = RawSqlLiterals("Quality").ToList();
        Assert.NotEmpty(literals);

        foreach (var (file, sql) in literals)
        {
            foreach (var statement in WriteStatements(sql))
            {
                Assert.Fail($"{file} executes a write statement: {statement}");
            }
        }
    }

    [Fact]
    public void BaselineComparison_WritesOnlyToItsOwnSessionTempTables()
    {
        // The comparison DOES write: it stages both estates into session temp tables so a single pass can
        // compare them. Proving the targets are all #-prefixed constants BEFORE substituting them is what
        // makes "it only writes to tempdb" a fact rather than an assumption.
        var stagingTables = System.Text.RegularExpressions.Regex
            .Matches(
                File.ReadAllText(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "src", "SqlFlow.SqlServer", "Comparison", "SqlServerBaselineComparer.cs")),
                @"private const string (\w+) = ""([^""]+)"";")
            .Select(m => (Name: m.Groups[1].Value, Value: m.Groups[2].Value))
            .Where(c => c.Value.StartsWith('#'))
            .ToList();

        Assert.Equal(4, stagingTables.Count);

        var writes = 0;
        foreach (var (file, rawSql) in RawSqlLiterals("Comparison"))
        {
            var sql = stagingTables.Aggregate(
                rawSql, (current, t) => current.Replace("{" + t.Name + "}", t.Value, StringComparison.Ordinal));

            foreach (var statement in WriteStatements(sql))
            {
                writes++;
                Assert.True(
                    statement.Contains("#sf_cmp_", StringComparison.Ordinal),
                    $"{file} executes a write that does not target a comparison temp table: {statement}");
                Assert.True(
                    statement.StartsWith("DROP TABLE IF EXISTS #sf_cmp_", StringComparison.Ordinal)
                    || statement.StartsWith("CREATE INDEX ", StringComparison.Ordinal),
                    $"{file} executes an unexpected staging statement: {statement}");
            }
        }

        Assert.True(writes > 0, "Expected the comparison to stage into temp tables.");
    }

    [Fact]
    public void TheSqlSuggestedForReview_IsNeverExecuted()
    {
        foreach (var (file, sql) in RawSqlLiterals("Quality").Concat(RawSqlLiterals("Comparison")))
        {
            Assert.False(
                sql.Contains("ListDuplicatesSql", StringComparison.Ordinal),
                $"{file} interpolates a suggestion into executed SQL.");
        }
    }
}
