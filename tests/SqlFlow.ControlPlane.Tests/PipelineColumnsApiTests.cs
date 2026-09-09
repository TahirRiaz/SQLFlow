using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The pipeline transform-columns endpoint over a seeded catalog: declared (YAML-projected) and detected
/// (run-projected) rows are returned declared-first in view order, the kind filter narrows to one provenance,
/// and a pipeline with no transform metadata yields an empty list (not a 404: the pipeline may simply declare
/// nothing).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelineColumnsApiTests
{
    [SkippableFact]
    public async Task Columns_OverSeededCatalog_ReturnsOrderedRows_AndFiltersByKind()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_cols_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_moveabout_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.PipelineColumns.Add(new CatalogPipelineColumn
                {
                    RepoId = repoId,
                    PipelineId = pipelineId,
                    Kind = PipelineColumnKinds.Declared,
                    Ordinal = 1,
                    ColumnName = "vehicle_type_clean",
                    SourceColumn = "vehicle_type",
                    Expression = "CAST([vehicle_type] AS varchar(50))",
                    DataType = "varchar(50)",
                    SortOrder = 10,
                    Converted = true,
                });
                db.PipelineColumns.Add(new CatalogPipelineColumn
                {
                    RepoId = repoId,
                    PipelineId = pipelineId,
                    Kind = PipelineColumnKinds.Declared,
                    Ordinal = 2,
                    ColumnName = "loaded_at",
                    Expression = "SYSUTCDATETIME()",
                    IsVirtual = true,
                    Converted = true,
                });
                db.PipelineColumns.Add(new CatalogPipelineColumn
                {
                    RepoId = repoId,
                    PipelineId = pipelineId,
                    Kind = PipelineColumnKinds.Detected,
                    Ordinal = 1,
                    ColumnName = "amount",
                    Expression = "TRY_CONVERT(decimal(18,2), [amount])",
                    DataType = "decimal(18,2)",
                    Converted = true,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // All rows: declared first (view order), then detected.
            var all = await GetJsonAsync<List<PipelineColumnDto>>(client, token, $"/api/v1/pipelines/{pipelineId}/columns");
            Assert.Equal(3, all.Count);
            Assert.Equal(["declared", "declared", "detected"], all.Select(c => c.Kind));
            Assert.Equal("vehicle_type_clean", all[0].ColumnName);
            Assert.Equal("vehicle_type", all[0].SourceColumn);
            Assert.Equal("CAST([vehicle_type] AS varchar(50))", all[0].Expression);
            Assert.Equal(10, all[0].SortOrder);
            Assert.True(all[1].IsVirtual);
            Assert.Null(all[1].SourceColumn);

            // The kind filter narrows to one provenance.
            var detected = await GetJsonAsync<List<PipelineColumnDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/columns?kind=detected");
            var detectedRow = Assert.Single(detected);
            Assert.Equal("amount", detectedRow.ColumnName);
            Assert.Equal("decimal(18,2)", detectedRow.DataType);

            // A pipeline with no transform metadata is an empty list, not an error.
            var none = await GetJsonAsync<List<PipelineColumnDto>>(client, token, $"/api/v1/pipelines/{Guid.NewGuid()}/columns");
            Assert.Empty(none);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.PipelineColumns.Where(c => c.PipelineId == pipelineId).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }
}
