using Microsoft.Data.SqlClient;
using SqlFlow.Core.Profiling;
using SqlFlow.SqlServer.Profiling;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The edge-case matrix for the SQL Server probe against a real database, grouped by the surface being stressed:
/// which declared indexes count as keys (filtered, disabled, INCLUDE columns, several at once), identifier handling
/// (bracket-quoted names, a database-qualified object and its catalog prefix), measurement correctness (tuple
/// aliasing under the composite hash, unicode and mixed-type data, batch-boundary stitching on wide tables), and
/// the sampling boundaries (a sample larger than the table, empty tables with and without a declared key, views on
/// a full scan). Skips when no sink database is configured. Each test drops its own objects up front.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueKeyDetectionEdgeCaseIntegrationTests
{
    private const string Schema = IntegrationDb.Schema;

    private static Task<SqlServerUniquenessProbe> ProbeAsync(string cs, string obj, IReadOnlyList<string> columns, int? sample = 0)
        => SqlServerUniquenessProbe.CreateAsync(cs, obj, columns, sample);

    private static Task<UniqueKeyReport> DetectAsync(SqlServerUniquenessProbe probe)
        => UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions());

    // ---- Which declared indexes count --------------------------------------------------------------------------

    [SkippableFact]
    public async Task UniqueNonclusteredIndexOnHeap_IsDeclared()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_HeapUnique";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Code nvarchar(10) NOT NULL, Payload int NULL);
            CREATE UNIQUE NONCLUSTERED INDEX [UX_{table}_Code] ON [{Schema}].[{table}] (Code);
            INSERT INTO [{Schema}].[{table}] (Code, Payload) VALUES (N'a', 1), (N'b', 1);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Code", "Payload"]);
        var declared = Assert.Single(probe.DeclaredUniqueKeys);
        Assert.Equal(new[] { "Code" }, declared);

        var report = await DetectAsync(probe);
        var key = Assert.Single(report.Candidates);
        Assert.True(key.Declared);
    }

    [SkippableFact]
    public async Task FilteredUniqueIndex_ProvesNothing_AndIsIgnored()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Filtered";
        await IntegrationDb.DropTableAsync(cs, table);
        // The filtered index only enforces uniqueness among non-null A values; A has nulls, so it is no table-wide
        // key. The probe must ignore it and profiling must find the real key, B.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (A int NULL, B int NOT NULL);
            CREATE UNIQUE NONCLUSTERED INDEX [UXF_{table}_A] ON [{Schema}].[{table}] (A) WHERE A IS NOT NULL;
            INSERT INTO [{Schema}].[{table}] (A, B) VALUES (1, 10), (2, 20), (NULL, 30), (NULL, 40);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["A", "B"]);
        Assert.Empty(probe.DeclaredUniqueKeys);

        var report = await DetectAsync(probe);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.False(key.Declared);
        Assert.Equal(new[] { "B" }, key.Columns);
    }

    [SkippableFact]
    public async Task DisabledUniqueIndex_IsIgnored_ProfilingStillFindsTheKey()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Disabled";
        await IntegrationDb.DropTableAsync(cs, table);
        // A disabled index is not maintained, so it proves nothing; the data still happens to be unique, and the
        // measured (not declared) candidate must say so.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, Extra int NULL);
            CREATE UNIQUE NONCLUSTERED INDEX [UXD_{table}_Id] ON [{Schema}].[{table}] (Id);
            ALTER INDEX [UXD_{table}_Id] ON [{Schema}].[{table}] DISABLE;
            INSERT INTO [{Schema}].[{table}] (Id, Extra) VALUES (1, NULL), (2, NULL), (3, NULL);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Id", "Extra"]);
        Assert.Empty(probe.DeclaredUniqueKeys);

        var report = await DetectAsync(probe);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.False(key.Declared);
        Assert.Equal(new[] { "Id" }, key.Columns);
    }

    [SkippableFact]
    public async Task UniqueIndexWithIncludeColumns_DeclaresOnlyTheKeyColumns()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Include";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (A int NOT NULL, B int NOT NULL);
            CREATE UNIQUE NONCLUSTERED INDEX [UXI_{table}_A] ON [{Schema}].[{table}] (A) INCLUDE (B);
            INSERT INTO [{Schema}].[{table}] (A, B) VALUES (1, 9), (2, 9), (3, 9);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["A", "B"]);
        // INCLUDE columns ride along in the leaf pages but take no part in uniqueness; declaring (A, B) would
        // overstate the key.
        var declared = Assert.Single(probe.DeclaredUniqueKeys);
        Assert.Equal(new[] { "A" }, declared);
    }

    [SkippableFact]
    public async Task MultipleDeclaredKeys_AreDeduped_AndRankedNarrowestFirst()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_MultiKeys";
        await IntegrationDb.DropTableAsync(cs, table);
        // Three unique structures, two of them over the same column: the report must collapse the duplicate and
        // list the single-column key before the composite.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (
                A int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, B int NOT NULL, C int NOT NULL);
            CREATE UNIQUE NONCLUSTERED INDEX [UXM_{table}_BC] ON [{Schema}].[{table}] (B, C);
            CREATE UNIQUE NONCLUSTERED INDEX [UXM_{table}_A2] ON [{Schema}].[{table}] (A);
            INSERT INTO [{Schema}].[{table}] (A, B, C) VALUES (1, 1, 1), (2, 1, 2), (3, 2, 1);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["A", "B", "C"]);
        var report = await DetectAsync(probe);

        Assert.Equal(2, report.Candidates.Count);
        Assert.Equal(new[] { "A" }, report.Candidates[0].Columns);
        Assert.Equal(new[] { "B", "C" }, report.Candidates[1].Columns);
        Assert.All(report.Candidates, c => Assert.True(c.Declared));
    }

    // ---- Identifier handling -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task DatabaseQualifiedName_ResolvesMetadataThroughTheCatalogPrefix()
    {
        var cs = IntegrationDb.Require();
        var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
        Skip.If(string.IsNullOrWhiteSpace(database), "The sink connection string names no database to qualify with.");

        const string table = "UkEdge_CrossDb";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, V int NULL);
            INSERT INTO [{Schema}].[{table}] (Id, V) VALUES (1, NULL), (2, NULL);
            """);

        // A three-part name routes sys.objects/sys.columns/sys.indexes through the [db].sys.* prefix; the declared
        // key must come back exactly as it does for a two-part name.
        await using var probe = await ProbeAsync(cs, $"[{database}].[{Schema}].[{table}]", ["Id", "V"]);
        var declared = Assert.Single(probe.DeclaredUniqueKeys);
        Assert.Equal(new[] { "Id" }, declared);
    }

    [SkippableFact]
    public async Task BracketWorthyIdentifiers_SpacesAndClosingBrackets_AreQuotedEverywhere()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Weird Name";
        await IntegrationDb.DropTableAsync(cs, table);
        // A space in the table name and a ']' in a column name stress every quoting site: metadata lookup, the
        // measurement aggregates, the composite hash, and verification.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] ([Order No] int NOT NULL, [Bad]]Col] nvarchar(5) NOT NULL);
            INSERT INTO [{Schema}].[{table}] ([Order No], [Bad]]Col]) VALUES
                (1, N'x'), (1, N'y'), (2, N'x'), (2, N'y');
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Order No", "Bad]Col"]);
        var report = await DetectAsync(probe);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "Bad]Col", "Order No" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    // ---- Measurement correctness -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task CompositeHash_DoesNotAliasAdjacentValues()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Alias";
        await IntegrationDb.DropTableAsync(cs, table);
        // Naive concatenation would collapse ('a','bc') and ('ab','c') into one tuple and undercount the composite
        // as non-unique; the separator-delimited hash must keep all four tuples distinct so the key is found.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (X nvarchar(5) NOT NULL, Y nvarchar(5) NOT NULL);
            INSERT INTO [{Schema}].[{table}] (X, Y) VALUES
                (N'a', N'bc'), (N'ab', N'c'), (N'a', N'c'), (N'ab', N'bc');
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["X", "Y"]);
        var report = await DetectAsync(probe);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "X", "Y" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task MixedTypesAndUnicode_CompositeKeyIsDetected()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Mixed";
        await IntegrationDb.DropTableAsync(cs, table);
        // date + unicode nvarchar as the key, with decimal and time along for the ride: everything must survive
        // the CONVERT(nvarchar(max), ...) hashing, including surrogate-pair characters.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Dt date NOT NULL, Name nvarchar(20) NOT NULL, Amt decimal(10,2) NULL, T time NULL);
            INSERT INTO [{Schema}].[{table}] (Dt, Name, Amt, T) VALUES
                ('2026-01-01', N'Blåbær', 1.10, '08:00'), ('2026-01-01', N'😀smile', 1.10, '08:00'),
                ('2026-01-02', N'Blåbær', 1.10, '08:00'), ('2026-01-02', N'😀smile', 1.10, '08:00');
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Dt", "Name", "Amt", "T"]);
        var report = await DetectAsync(probe);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "Dt", "Name" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task NullableColumnInAComposite_DisqualifiesIt_AlternativeCompositeWins()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_NullComposite";
        await IntegrationDb.DropTableAsync(cs, table);
        // (A,B) would be a perfectly selective composite, but B has one null and a null can never take part in a
        // key; (A,C) is the only legal key.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (A int NOT NULL, B int NULL, C int NOT NULL);
            INSERT INTO [{Schema}].[{table}] (A, B, C) VALUES
                (1, 1, 1), (1, 2, 2), (2, 3, 1), (2, NULL, 2);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["A", "B", "C"]);
        var report = await DetectAsync(probe);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "A", "C" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(report.Candidates, c => c.IsUnique && c.Columns.Contains("B"));
    }

    [SkippableFact]
    public async Task KeyColumnBeyondTheFirstMeasurementBatch_IsStitchedToTheRightColumn()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Batches";
        await IntegrationDb.DropTableAsync(cs, table);
        // 19 noise columns ahead of the key column push it into the second measurement batch
        // (MaxSetsPerMeasurement = 16): if batch results were stitched to the wrong columns, the unique column
        // would be misattributed and this either finds a false key or misses the real one.
        var noise = Enumerable.Range(0, 19).Select(j => $"N{j:00}").ToArray();
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (
                {string.Join(", ", noise.Select(c => $"[{c}] int NOT NULL"))}, KeyCol int NOT NULL);
            WITH n AS (SELECT TOP (24) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO [{Schema}].[{table}] ({string.Join(", ", noise.Select(c => $"[{c}]"))}, KeyCol)
            SELECT {string.Join(", ", noise.Select((_, idx) => $"i % {(idx % 3) + 2}"))}, i FROM n;
            """);

        var columns = noise.Append("KeyCol").ToArray();
        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", columns);
        var report = await DetectAsync(probe);

        Assert.Equal(20, report.Columns.Count);
        Assert.Equal(24, report.Columns.Single(s => s.Column == "KeyCol").Distinct);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "KeyCol" }, key.Columns);
    }

    // ---- Sampling and table-size boundaries --------------------------------------------------------------------

    [SkippableFact]
    public async Task SampleLargerThanTheTable_FallsBackToAFullScan()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_TinySample";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL);
            INSERT INTO [{Schema}].[{table}] (Id) VALUES (1), (2), (3);
            """);

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Id"], sample: 1000);
        var report = await DetectAsync(probe);

        Assert.False(report.Sampled);
        Assert.Equal(3, report.TotalRows);
        Assert.Equal(3, report.ScannedRows);
        Assert.True(Assert.Single(report.Candidates).IsUnique);
    }

    [SkippableFact]
    public async Task EmptyTable_WithoutKeys_ReportsTheEmptyNote()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_Empty";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL);");

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Id"], sample: null);
        var report = await DetectAsync(probe);

        Assert.Empty(report.Candidates);
        Assert.Equal(0, report.TotalRows);
        Assert.Contains("empty", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task EmptyTable_WithAPrimaryKey_StillAnswersFromMetadata()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_EmptyPk";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(
            cs, $"CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY);");

        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{table}]", ["Id"]);
        var report = await DetectAsync(probe);

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.IsUnique);
        Assert.True(candidate.Declared);
        Assert.Equal(0, report.TotalRows);
    }

    [SkippableFact]
    public async Task View_OnAFullScan_IsProfiledLikeATable()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkEdge_ViewBase";
        const string view = "UkEdge_View";
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [{Schema}].[{view}];");
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, G int NOT NULL);
            INSERT INTO [{Schema}].[{table}] (Id, G) VALUES (1, 1), (2, 1), (3, 2);
            """);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE VIEW [{Schema}].[{view}] AS SELECT Id, G FROM [{Schema}].[{table}];");

        // The view itself declares nothing (the PK lives on the base table), so this is the pure profiling path
        // over a non-table object.
        await using var probe = await ProbeAsync(cs, $"[{Schema}].[{view}]", ["Id", "G"]);
        Assert.Empty(probe.DeclaredUniqueKeys);

        var report = await DetectAsync(probe);
        Assert.False(report.Sampled);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.False(key.Declared);
        Assert.Equal(new[] { "Id" }, key.Columns);
    }
}
