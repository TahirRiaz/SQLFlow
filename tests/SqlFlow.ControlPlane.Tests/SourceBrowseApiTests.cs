using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The file-source tree API end to end against the real shadow catalog: the test seeds file objects across an
/// Azure storage account, an SFTP server, and a local path, then asserts <c>/lineage/file-tree</c> decomposes
/// each into its canonical parent (origin / container / folder / leaf) so the GUI can fold them into a source
/// tree the twin of the database tree. Non-file objects are excluded. Every seeded row is removed in a finally
/// so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceBrowseApiTests
{
    [SkippableFact]
    public async Task FileTree_DecomposesEveryFileEndpoint_ToItsCanonicalParent()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var account = "acct" + suffix;
        var host = "ftp" + suffix + ".acme.com:22";
        var now = DateTime.UtcNow;

        // One file per origin kind, plus a non-file object that must NOT appear in the file tree. The file
        // NAME is the clean canonical identity; the KEY is the node identity 'file|||<name>' the catalog
        // actually stores (server-reference-prefixed), so the test exercises parsing the Name, not the Key.
        var azureName = $"az://{account}/raw/boatbooking/history/detail";
        var azureRootName = $"az://{account}/landing/orders.csv";
        var sftpName = $"sftp://{host}/exports/daily/orders.csv";
        var localName = $"data_{suffix}/incoming/orders.csv";
        var azureKey = $"file|||{azureName}";
        var azureRootKey = $"file|||{azureRootName}";
        var sftpKey = $"file|||{sftpName}";
        var localKey = $"file|||{localName}";
        var tableKey = $"${{env:SRV_{suffix}}}|dw|dbo|orders_{suffix}";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(SeedFile(azureKey, azureName, now));
                db.Objects.Add(SeedFile(azureRootKey, azureRootName, now));
                db.Objects.Add(SeedFile(sftpKey, sftpName, now));
                db.Objects.Add(SeedFile(localKey, localName, now));
                db.Objects.Add(new CatalogObject
                {
                    Key = tableKey,
                    ServerRef = $"${{env:SRV_{suffix}}}",
                    Database = "dw",
                    Schema = "dbo",
                    Name = "orders_" + suffix,
                    Kind = "Table",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var tree = await GetJsonAsync<IReadOnlyList<FileNodeDto>>(client, token, "/api/v1/lineage/file-tree");

            // The table is not a file endpoint: the file tree never lists it.
            Assert.DoesNotContain(tree, n => n.Key == tableKey);

            var azure = tree.Single(n => n.Key == azureKey);
            Assert.Equal("AzureStorage", azure.OriginKind);
            Assert.Equal(account, azure.Origin);
            Assert.Equal("raw", azure.Container);
            Assert.Equal("boatbooking/history", azure.Path);
            Assert.Equal("detail", azure.Name);

            var azureRoot = tree.Single(n => n.Key == azureRootKey);
            Assert.Equal("landing", azureRoot.Container);
            Assert.Null(azureRoot.Path);
            Assert.Equal("orders.csv", azureRoot.Name);

            var sftp = tree.Single(n => n.Key == sftpKey);
            Assert.Equal("Sftp", sftp.OriginKind);
            Assert.Equal(host, sftp.Origin);
            Assert.Null(sftp.Container);
            Assert.Equal("exports/daily", sftp.Path);
            Assert.Equal("orders.csv", sftp.Name);

            var local = tree.Single(n => n.Key == localKey);
            Assert.Equal("Local", local.OriginKind);
            Assert.Equal(SqlFlow.Core.FileOrigin.LocalOrigin, local.Origin);
            Assert.Equal($"data_{suffix}/incoming", local.Path);
            Assert.Equal("orders.csv", local.Name);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Objects.Where(o => o.Key == azureKey || o.Key == azureRootKey || o.Key == sftpKey
                || o.Key == localKey || o.Key == tableKey).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task FileFlows_ReportsProducersAndConsumers_WithLandingTargets()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "fflows_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var producer = "acquire_" + suffix;
        var consumer = "ingest_" + suffix;
        var producerId = CatalogIdentity.Pipeline(repoId, producer);
        var consumerId = CatalogIdentity.Pipeline(repoId, consumer);
        var fileName = $"az://acct{suffix}/raw/orders";
        var fileKey = $"file|||{fileName}";
        var tableKey = $"${{env:SINK_{suffix}}}|dw|pre|orders_{suffix}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(SeedPipeline(producerId, repoId, producer, "api", now));
                db.Pipelines.Add(SeedPipeline(consumerId, repoId, consumer, "ing", now));
                db.Objects.Add(SeedFile(fileKey, fileName, now));
                db.Objects.Add(new CatalogObject
                {
                    Key = tableKey, ServerRef = $"${{env:SINK_{suffix}}}", Database = "dw", Schema = "pre",
                    Name = "orders_" + suffix, Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });

                // The acquire produces (writes) the file; the ingest reads the file and writes the table.
                db.LineageEdges.AddRange(
                    Edge(repoId, producer, producerId, "Writes", fileKey, "orders"),
                    Edge(repoId, consumer, consumerId, "Reads", fileKey, "orders"),
                    Edge(repoId, consumer, consumerId, "Writes", tableKey, "orders_" + suffix));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var flows = await GetJsonAsync<FileFlowsDto>(
                client, token, $"/api/v1/lineage/file-flows?key={Uri.EscapeDataString(fileKey)}");

            var produced = Assert.Single(flows.Producers);
            Assert.Equal(producer, produced.Flow);

            var consumed = Assert.Single(flows.Consumers);
            Assert.Equal(consumer, consumed.Flow);
            var landing = Assert.Single(consumed.Lands);
            Assert.Equal(tableKey, landing.Key);
            Assert.Equal("orders_" + suffix, landing.Name);
            Assert.Equal("pre", landing.Schema);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == fileKey || o.Key == tableKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline SeedPipeline(Guid id, Guid repoId, string name, string kind, DateTime now)
        => new()
        {
            Id = id, RepoId = repoId, Name = name, Kind = kind,
            RelativePath = name + ".flow.yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\n", DefinitionJson = "{}", Active = true, Wave = 0,
            FirstSeenUtc = now, LastSeenUtc = now,
        };

    private static CatalogLineageEdge Edge(Guid repoId, string flow, Guid pipelineId, string relation, string objectKey, string objectName)
        => new()
        {
            RepoId = repoId, Flow = flow, PipelineId = pipelineId, Relation = relation,
            ObjectKey = objectKey, ObjectName = objectName, Tier = "Declared",
        };

    private static CatalogObject SeedFile(string key, string name, DateTime now)
        => new()
        {
            Key = key,
            ServerRef = "file",
            Name = name,
            Kind = "File",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

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
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
