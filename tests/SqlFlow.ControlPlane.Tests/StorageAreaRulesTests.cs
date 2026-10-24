using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Maintenance;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The rules every storage contributor is held to (<see cref="StorageAreaRules"/>), and SQLFlow's own areas against them:
/// every table of the catalog's model belongs to exactly one area, so the Maintenance page accounts for the whole catalog,
/// and a table added to the model without an area fails here, naming it. No database.
/// </summary>
public sealed class StorageAreaRulesTests
{
    [Fact]
    public void CatalogAreas_FollowTheRules()
    {
        var problems = StorageAreaRules.Check([new CatalogStorageContributor(new ControlPlaneOptions())]);
        Assert.Empty(problems);
    }

    [Fact]
    public void EveryCatalogTable_BelongsToExactlyOneArea_AndEveryAreaTableExists()
    {
        using var db = CatalogDatabase.Create("Server=(local);Database=StorageAreaRulesTests;Integrated Security=true;TrustServerCertificate=true");
        var modelTables = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Select(e => (Schema: e.GetSchema() ?? CatalogDbContext.SchemaName, Name: e.GetTableName()!))
            .Append((Schema: CatalogDbContext.SchemaName, Name: CatalogDatabase.MigrationsHistoryTable))
            .Distinct()
            .ToList();
        var areas = new CatalogStorageContributor(new ControlPlaneOptions()).Areas;

        var unclaimed = modelTables.Where(t => !areas.Any(a => a.Tables.Any(c => c.Matches(t.Schema, t.Name)))).ToList();
        Assert.True(unclaimed.Count == 0, "Tables no storage area holds: " + string.Join(", ", unclaimed.Select(t => $"{t.Schema}.{t.Name}")));

        var phantom = areas.SelectMany(a => a.Tables.Select(c => (Area: a.Id, Claim: c)))
            .Where(x => !modelTables.Any(t => x.Claim.Matches(t.Schema, t.Name)))
            .ToList();
        Assert.True(phantom.Count == 0, "Areas holding tables the model does not have: " + string.Join(", ", phantom.Select(p => $"{p.Area}: {p.Claim}")));
    }

    [Fact]
    public void OnlyTheRunTraceAreas_TakeARetention()
    {
        var cleanable = new CatalogStorageContributor(new ControlPlaneOptions()).Areas.Where(a => a.Retention is not null).Select(a => a.Id).Order().ToList();
        Assert.Equal([CatalogStorageContributor.RunEventsArea, CatalogStorageContributor.RunStatementsArea], cleanable);
    }

    [Fact]
    public void Lifecycles_NameTheSettingsTheyFollow()
    {
        var options = new ControlPlaneOptions();
        options.Notifications.EventRetentionDays = 12;
        options.Dispatch.NodeRetentionHours = 0;
        var areas = new CatalogStorageContributor(options).Areas;

        Assert.Contains("12 days", areas.Single(a => a.Id == "sqlflow.notification-events").Lifecycle, StringComparison.Ordinal);
        Assert.Contains("NodeRetentionHours is 0", areas.Single(a => a.Id == "sqlflow.nodes").Lifecycle, StringComparison.Ordinal);

        options.Notifications.Enabled = false;
        Assert.Contains("notifications are off", new CatalogStorageContributor(options).Areas.Single(a => a.Id == "sqlflow.notification-events").Lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_NamesEveryBrokenRule()
    {
        var problems = StorageAreaRules.Check(
        [
            new Contributor("Mod",
            [
                Area("mod.a", StorageVerdict.Keep, [StorageTable.Named("mod", "A")]) with { Retention = new StorageRetention("all", "rows") },
                Area("mod.b", StorageVerdict.Optional, [StorageTable.Named("mod", "B")]),
                Area("mod.a", StorageVerdict.Keep, [StorageTable.Named("mod", "C")]),
                Area("Bad Id", StorageVerdict.Keep, [StorageTable.Named("mod", "D")]),
                Area("mod.e", StorageVerdict.Keep, [StorageTable.StartingWith("mod", "dim_")]),
                Area("mod.f", StorageVerdict.Keep, [StorageTable.Named("mod", "dim_Well")]),
                Area("mod.g", StorageVerdict.Safe, []) with { Column = new StorageColumn("mod", "Missing", "Log"), Retention = new StorageRetention("each latest", "logs") },
                Area("mod.h", StorageVerdict.Keep, []),
                Area("mod.i", StorageVerdict.Safe, [StorageTable.Named("mod", "I")]) with { Retention = new StorageRetention("x", "", MinDays: 5, MaxDays: 2) },
            ]),
        ]);

        Assert.Contains(problems, p => p.Contains("'mod.a'", StringComparison.Ordinal) && p.Contains("takes no retention", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.b'", StringComparison.Ordinal) && p.Contains("needs a retention", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("already used", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Bad Id'", StringComparison.Ordinal) && p.Contains("lowercase", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.e'", StringComparison.Ordinal) && p.Contains("'mod.f'", StringComparison.Ordinal) && p.Contains("one area", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.g'", StringComparison.Ordinal) && p.Contains("no other area holds mod.Missing", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.h'", StringComparison.Ordinal) && p.Contains("no table and no column", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.i'", StringComparison.Ordinal) && p.Contains("the least first", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'mod.i'", StringComparison.Ordinal) && p.Contains("what its cleanup counts", StringComparison.Ordinal));

        var briefless = StorageAreaRules.Check([new Contributor("Mod", [Area("mod.j", StorageVerdict.Keep, [StorageTable.Named("mod", "J")]) with { Brief = null }])]);
        Assert.Contains(briefless, p => p.Contains("'mod.j'", StringComparison.Ordinal) && p.Contains("needs a brief", StringComparison.Ordinal));
        var wordy = StorageAreaRules.Check([new Contributor("Mod", [Area("mod.k", StorageVerdict.Keep, [StorageTable.Named("mod", "K")]) with { Brief = new string('x', 41) }])]);
        Assert.Contains(wordy, p => p.Contains("'mod.k'", StringComparison.Ordinal) && p.Contains("longer than 40", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_AcceptsAColumnAreaInATableAnotherAreaHolds()
    {
        var problems = StorageAreaRules.Check(
        [
            new Contributor("Mod",
            [
                Area("mod.activities", StorageVerdict.Keep, [StorageTable.Named("mod", "Activity")]),
                Area("mod.activity-logs", StorageVerdict.Optional, []) with
                {
                    Column = new StorageColumn("mod", "Activity", "Log"),
                    Retention = new StorageRetention("Activities still running keep their log.", "logs"),
                },
            ]),
        ]);
        Assert.Empty(problems);
    }

    [Fact]
    public void TableClaims_MatchByNameOrPrefix_IgnoringCase()
    {
        Assert.True(StorageTable.Named("mod", "Record").Matches("MOD", "record"));
        Assert.False(StorageTable.Named("mod", "Record").Matches("mod", "RecordIdentity"));
        Assert.True(StorageTable.StartingWith("mod", "tmp_").Matches("mod", "tmp_Orders"));
        Assert.False(StorageTable.StartingWith("mod", "tmp_").Matches("catalog", "tmp_Orders"));
        Assert.Equal("mod.tmp_*", StorageTable.StartingWith("mod", "tmp_").ToString());
    }

    [Fact]
    public async Task ColumnMeasure_RefusesAnythingButPlainIdentifiers()
    {
        await using var db = CatalogDatabase.Create("Server=(local);Database=StorageAreaRulesTests;Integrated Security=true;TrustServerCertificate=true");
        await Assert.ThrowsAsync<ArgumentException>(() => CatalogStorage.MeasureColumnAsync(db, "catalog", "Run]; DROP TABLE x;--", "Error"));
        await Assert.ThrowsAsync<ArgumentException>(() => CatalogStorage.MeasureColumnAsync(db, "catalog", "Run", "Error Text"));
    }

    private static StorageArea Area(string id, StorageVerdict verdict, IReadOnlyList<StorageTable> tables) => new()
    {
        Id = id,
        Group = "Group",
        Title = id,
        Verdict = verdict,
        Holds = "rows",
        Reason = "why",
        Brief = "Never removed",
        Tables = tables,
    };

    private sealed class Contributor(string owner, IReadOnlyList<StorageArea> areas) : IStorageContributor
    {
        public string Owner => owner;

        public IReadOnlyList<StorageArea> Areas => areas;

        public Task<long> CountAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> CleanAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct) => Task.FromResult(0L);
    }
}
