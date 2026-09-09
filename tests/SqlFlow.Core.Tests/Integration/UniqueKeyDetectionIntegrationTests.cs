using SqlFlow.Core.Profiling;
using SqlFlow.SqlServer.Profiling;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the SQL Server uniqueness probe and the detector end to end against a real table with a planted
/// composite key, proving the T-SQL measurements (distinct-tuple counts, null handling, the sample temp table) and
/// the algorithm agree on real data. Skips when no sink database is configured. Each test drops its table up front.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueKeyDetectionIntegrationTests
{
    private const string Schema = IntegrationDb.Schema;

    [SkippableFact]
    public async Task PlantedCompositeKey_IsDetectedAndMinimal()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Composite";
        await IntegrationDb.DropTableAsync(cs, table);
        // (Region, OrderNo) is the only minimal key: Amount and Note are constants, so they cannot substitute for
        // either key column, and neither Region nor OrderNo is unique on its own.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Region nvarchar(10) NOT NULL, OrderNo int NOT NULL, Amount decimal(10,2) NULL, Note nvarchar(50) NULL);
            INSERT INTO [{Schema}].[{table}] (Region, OrderNo, Amount, Note) VALUES
                (N'east', 1, 10.00, N'x'), (N'east', 2, 10.00, N'x'), (N'west', 1, 10.00, N'x'),
                (N'west', 2, 10.00, N'x'), (N'north', 1, 10.00, N'x'), (N'north', 2, 10.00, N'x');
            """);

        var columns = new[] { "Region", "OrderNo", "Amount", "Note" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "OrderNo", "Region" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.Equal(6, report.TotalRows);
    }

    [SkippableFact]
    public async Task SingleColumnKey_IsDetected_AndNullableColumnIsRejected()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Single";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, Alt int NULL, Grp nvarchar(5) NULL);
            INSERT INTO [{Schema}].[{table}] (Id, Alt, Grp) VALUES
                (1, 100, N'x'), (2, 200, N'x'), (3, NULL, N'y'), (4, 400, N'y');
            """);

        var columns = new[] { "Id", "Alt", "Grp" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        Assert.Equal(new[] { "Id" }, report.Candidates.First(c => c.IsUnique).Columns);
        // Alt has all-distinct non-null values but a NULL row, so it can never be a key.
        Assert.DoesNotContain(report.Candidates, c => c.IsUnique && c.Columns.Contains("Alt"));
    }

    [SkippableFact]
    public async Task SampledRun_VerifiesCandidatesAgainstTheWholeTable()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Sampled";
        await IntegrationDb.DropTableAsync(cs, table);
        // 200 rows where Id is a true key; a sample of 50 still resolves it and the report marks it verified.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, Bucket int NOT NULL);
            INSERT INTO [{Schema}].[{table}] (Id, Bucket)
            SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4
            FROM sys.all_objects;
            """);

        var columns = new[] { "Id", "Bucket" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 50);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        Assert.True(report.Sampled);
        Assert.Equal(200, report.TotalRows);
        // The sample is a random draw (page sampling with a row-filter fallback), so its size is approximate: what
        // matters is that a subset was searched and the finding was still proven against the whole table.
        Assert.InRange(report.ScannedRows, 1, 200);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "Id" }, key.Columns);
        Assert.True(key.Verified);
    }

    [SkippableFact]
    public async Task DeclaredPrimaryKey_IsAnsweredFromMetadata_WithoutProfiling()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Declared";
        await IntegrationDb.DropTableAsync(cs, table);
        // The composite PK is the declared answer; the data would also support it, but the fast path must come from
        // the catalog (Declared = true, no scanned rows), not from measurement.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (
                RegionId int NOT NULL, CustomerNo int NOT NULL, Name nvarchar(50) NULL,
                CONSTRAINT [PK_{table}] PRIMARY KEY (RegionId, CustomerNo));
            INSERT INTO [{Schema}].[{table}] (RegionId, CustomerNo, Name) VALUES
                (1, 1, N'a'), (1, 2, N'a'), (2, 1, N'a'), (2, 2, N'a');
            """);

        var columns = new[] { "RegionId", "CustomerNo", "Name" };
        await using (var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0))
        {
            var declared = Assert.Single(probe.DeclaredUniqueKeys);
            Assert.Equal(new[] { "RegionId", "CustomerNo" }, declared);

            var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());
            Assert.Equal(0, report.ScannedRows);
            Assert.Equal(4, report.TotalRows);
            var key = Assert.Single(report.Candidates);
            Assert.True(key.IsUnique);
            Assert.True(key.Verified);
            Assert.True(key.Declared);
            Assert.Equal(new[] { "RegionId", "CustomerNo" }, key.Columns);
            Assert.Contains("metadata", report.Note!, StringComparison.OrdinalIgnoreCase);
        }

        // Opting out of metadata (--no-metadata) profiles the rows and reaches the same key the hard way.
        await using (var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0, trustDeclaredKeys: false))
        {
            Assert.Empty(probe.DeclaredUniqueKeys);
            var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());
            var key = Assert.Single(report.Candidates, c => c.IsUnique);
            Assert.False(key.Declared);
            Assert.Equal(new[] { "CustomerNo", "RegionId" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        }
    }

    [SkippableFact]
    public async Task UnkeyableColumnTypes_AreExcludedBeforeAnyRowIsRead()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Excluded";
        await IntegrationDb.DropTableAsync(cs, table);
        // Every column except Id has a type that can never form a practical key; several of them (xml, nvarchar(max))
        // would previously have crashed or bloated the measurement query.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (
                Id int NOT NULL, Payload nvarchar(max) NULL, Ratio float NULL, Doc xml NULL,
                RowVer rowversion NOT NULL, Doubled AS (Id * 2));
            INSERT INTO [{Schema}].[{table}] (Id, Payload, Ratio, Doc) VALUES
                (1, N'p', 0.5, NULL), (2, N'q', 1.5, NULL), (3, N'r', 2.5, NULL);
            """);

        var columns = new[] { "Id", "Payload", "Ratio", "Doc", "RowVer", "Doubled" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);

        Assert.Equal(new[] { "Id" }, probe.EligibleColumns);
        Assert.Equal(new[] { "Doc", "Doubled", "Payload", "Ratio", "RowVer" },
            probe.ExcludedColumns.Select(e => e.Column).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.All(probe.ExcludedColumns, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));

        var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "Id" }, key.Columns);
    }

    [SkippableFact]
    public async Task RandomSample_FindsACompositeKeyThatAPhysicalPrefixWouldHide()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Prefix";
        const string view = "UkDetect_PrefixView";
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [{Schema}].[{view}];");
        await IntegrationDb.DropTableAsync(cs, table);
        // 400 rows clustered by (Day, Seq): the physical prefix is all Day 0, where Day is constant (so a prefix
        // sample ejects it from the pool) and Seq alone looks unique (so a prefix sample chases a false key). Only a
        // random sample sees several days and finds the real key (Day, Seq). The view forces the row-filter sampling
        // path, which is also what a prefix-biased TOP used to take.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Day int NOT NULL, Seq int NOT NULL, INDEX [CIX_{table}] CLUSTERED (Day, Seq));
            WITH n AS (SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_objects)
            INSERT INTO [{Schema}].[{table}] (Day, Seq) SELECT i / 100, i % 100 FROM n;
            """);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE VIEW [{Schema}].[{view}] AS SELECT Day, Seq FROM [{Schema}].[{table}];");

        var columns = new[] { "Day", "Seq" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{view}]", columns, sampleSize: 80);
        var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());

        Assert.True(report.Sampled);
        Assert.Equal(400, report.TotalRows);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "Day", "Seq" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task WideTable_IsMeasuredInBatches_AndStillResolvesTheKey()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Wide";
        await IntegrationDb.DropTableAsync(cs, table);
        // 41 columns force the per-column cardinality pass across several measurement batches
        // (MaxSetsPerMeasurement = 16); the single-query form used to emit every distinct aggregate at once.
        var noise = Enumerable.Range(0, 40).Select(i => $"C{i:00}").ToArray();
        var ddl = $"CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, {string.Join(", ", noise.Select(c => $"[{c}] int NULL"))});";
        var insert = $"""
            WITH n AS (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO [{Schema}].[{table}] (Id, {string.Join(", ", noise.Select(c => $"[{c}]"))})
            SELECT i, {string.Join(", ", noise.Select((c, idx) => $"i % {(idx % 3) + 2}"))} FROM n;
            """;
        await IntegrationDb.ExecuteAsync(cs, ddl + "\n" + insert);

        var columns = new[] { "Id" }.Concat(noise).ToArray();
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);
        var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());

        Assert.Equal(41, report.Columns.Count);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "Id" }, key.Columns);
    }
}
