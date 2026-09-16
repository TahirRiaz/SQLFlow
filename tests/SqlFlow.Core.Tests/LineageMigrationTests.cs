using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The lineage scan changed what it records without any flow document changing, so the catalog migration that ships
/// with the change asks every repository source for a full recompute on its next sync; the unchanged-estate shortcut
/// would otherwise keep serving the graph computed before. A second migration removes the file nodes earlier syncs left
/// behind with no edge. Checked on the generated scripts, which need no database.
/// </summary>
public sealed class LineageMigrationTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    [Fact]
    public void The_migration_requests_a_lineage_recompute_for_every_repository_source()
    {
        var script = ScriptOf("20260916130431_RequestLineageRecompute");

        Assert.Contains("UPDATE [catalog].[RepoSource] SET [ForceLineageOnNextSync] = 1;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sweep_migration_removes_only_file_nodes_no_edge_references_with_their_columns()
    {
        var script = ScriptOf("20260916140501_SweepOrphanFileNodes");

        Assert.Contains("DELETE c FROM [catalog].[ObjectColumn] AS c", script, StringComparison.Ordinal);
        Assert.Contains("DELETE o FROM [catalog].[Object] AS o", script, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(script, "o.[Kind] = N'File'"));
        Assert.Equal(2, CountOf(script, "NOT EXISTS (SELECT 1 FROM [catalog].[LineageEdge] AS e WHERE e.[ObjectKey] = o.[Key])"));
        Assert.True(
            script.IndexOf("DELETE c FROM", StringComparison.Ordinal) < script.IndexOf("DELETE o FROM", StringComparison.Ordinal),
            "the columns go before the objects they are found through.");
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.Ordinal);
    }

    /// <summary>The SQL script of one catalog migration, from the migration before it.</summary>
    private static string ScriptOf(string migration)
    {
        using var context = CatalogDatabase.Create(Placeholder);
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.IndexOf(migration);
        Assert.True(index > 0, $"{migration} is not a catalog migration.");
        return context.GetService<IMigrator>().GenerateScript(fromMigration: migrations[index - 1], toMigration: migration);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
