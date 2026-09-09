using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// Object enumeration, pagination, filtering, and search for the PostgreSQL catalog reader. The reader hides the
/// PostgreSQL-maintained schemas (<c>pg_catalog</c> and every <c>pg_*</c> schema, plus <c>information_schema</c>),
/// so these run against a dedicated <c>sf_itest</c> schema provisioned once by <see cref="PostgresListingFixture"/>
/// (which needs the CREATE privilege; if the login lacks it, or PostgreSQL is unreachable, every test here skips
/// rather than fails). Gated on <c>SQLFLOW_TEST_PG</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresListingTests : IClassFixture<PostgresListingFixture>
{
    private const DataSourceKind Kind = DataSourceKind.PostgreSQL;
    private readonly PostgresListingFixture _fixture;

    public PostgresListingTests(PostgresListingFixture fixture) => _fixture = fixture;

    private void RequireFixture() => Skip.IfNot(_fixture.Available, _fixture.SkipReason);

    [SkippableFact]
    public async Task ListSchemas_IncludesTheTestSchema_ExcludesSystemByDefault()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var schemas = await ForeignDb.Reader(Kind).ListSchemasAsync(connection, null, CatalogQuery.Default);
        Assert.Contains(schemas, s => string.Equals(s.Name, PostgresListingFixture.Schema, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(schemas, s => string.Equals(s.Name, "pg_catalog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(schemas, s => string.Equals(s.Name, "information_schema", StringComparison.OrdinalIgnoreCase));

        var withSystem = await ForeignDb.Reader(Kind).ListSchemasAsync(connection, null, new CatalogQuery { IncludeSystem = true });
        Assert.Contains(withSystem, s => string.Equals(s.Name, "pg_catalog", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task ListObjects_ReturnsTablesAndView_WithTotal()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var page = await ForeignDb.Reader(Kind).ListObjectsAsync(connection, Scope(), CatalogQuery.Default);

        Assert.Equal(4, page.Total);
        var names = page.Items.Select(i => i.Name).ToList();
        Assert.Contains("sf_l_alpha", names);
        Assert.Contains("sf_l_beta", names);
        Assert.Contains("sf_l_gamma", names);
        Assert.Contains("sf_l_view", names);
    }

    [SkippableFact]
    public async Task ListObjects_Paginates()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var reader = ForeignDb.Reader(Kind);

        var firstPage = await reader.ListObjectsAsync(connection, Scope(), new CatalogQuery { Limit = 2, Offset = 0 });
        var secondPage = await reader.ListObjectsAsync(connection, Scope(), new CatalogQuery { Limit = 2, Offset = 2 });

        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(4, firstPage.Total);
        Assert.True(firstPage.HasMore);
        Assert.False(secondPage.HasMore);
        // Ordered and non-overlapping across pages.
        var overlap = firstPage.Items.Select(i => i.Name).Intersect(secondPage.Items.Select(i => i.Name));
        Assert.Empty(overlap);
    }

    [SkippableFact]
    public async Task ListObjects_FiltersByNameLike()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var page = await ForeignDb.Reader(Kind).ListObjectsAsync(connection, Scope(), new CatalogQuery { NameLike = "alpha" });
        Assert.Equal(1, page.Total);
        Assert.Equal("sf_l_alpha", Assert.Single(page.Items).Name);
    }

    [SkippableFact]
    public async Task ListObjects_TogglesTablesAndViews()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var reader = ForeignDb.Reader(Kind);

        var tablesOnly = await reader.ListObjectsAsync(connection, Scope(), new CatalogQuery { IncludeViews = false });
        Assert.Equal(3, tablesOnly.Total);
        Assert.All(tablesOnly.Items, i => Assert.Equal(ObjectType.Table, i.Type));

        var viewsOnly = await reader.ListObjectsAsync(connection, Scope(), new CatalogQuery { IncludeTables = false });
        Assert.Equal(1, viewsOnly.Total);
        Assert.Equal(ObjectType.View, Assert.Single(viewsOnly.Items).Type);
    }

    [SkippableFact]
    public async Task SearchObjects_FindsByTerm()
    {
        RequireFixture();
        await using var connection = await ForeignDb.OpenAsync(Kind, _fixture.ConnectionString);
        var matches = await ForeignDb.Reader(Kind).SearchObjectsAsync(connection, null, "sf_l_");
        Assert.True(matches.Count >= 4, $"expected at least the four seeded objects, saw {matches.Count}");
        Assert.Contains(matches, m => string.Equals(m.Name, "sf_l_alpha", StringComparison.OrdinalIgnoreCase));
    }

    private static ObjectScope Scope() => new() { Schema = PostgresListingFixture.Schema };
}
