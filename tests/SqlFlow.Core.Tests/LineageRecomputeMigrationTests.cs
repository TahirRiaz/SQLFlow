using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The lineage scan changed what it records without any flow document changing, so the catalog migration that ships
/// with the change asks every repository source for a full recompute on its next sync; the unchanged-estate shortcut
/// would otherwise keep serving the graph computed before. Checked on the generated script, which needs no database.
/// </summary>
public sealed class LineageRecomputeMigrationTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private const string Migration = "20260916130431_RequestLineageRecompute";

    [Fact]
    public void The_migration_requests_a_lineage_recompute_for_every_repository_source()
    {
        using var context = CatalogDatabase.Create(Placeholder);
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.IndexOf(Migration);
        Assert.True(index > 0, $"{Migration} is not a catalog migration.");

        var script = context.GetService<IMigrator>().GenerateScript(fromMigration: migrations[index - 1], toMigration: Migration);

        Assert.Contains("UPDATE [catalog].[RepoSource] SET [ForceLineageOnNextSync] = 1;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.Ordinal);
    }
}
