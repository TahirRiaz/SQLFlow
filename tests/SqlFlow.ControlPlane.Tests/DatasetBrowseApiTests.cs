using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using NodeKey = SqlFlow.Lineage.Collection.NodeKey;
using ServerIdentity = SqlFlow.Lineage.Collection.ServerIdentity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The dataset branch of the catalog API end to end against the real shadow catalog: the test seeds datasets of two
/// external systems with the flows that write and read them, then asserts <c>/lineage/datasets</c> lists each with its
/// system, namespace, group and reader and writer counts (filtered by system and by search, never listing a table),
/// that the schema hierarchy never shows a dataset as a database, that a dataset's provenance lands its consumer's data
/// in a file as well as in a table, and that the project graph captions a dataset node by its system. Every seeded row
/// is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatasetBrowseApiTests
{
    [SkippableFact]
    public async Task Datasets_AreListedByTheirSystem_WithTheirReadersAndWriters_AndTheirProvenanceLandsOnFiles()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "datasets_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var writer = "writer_" + suffix;
        var reader = "reader_" + suffix;
        var writerId = CatalogIdentity.Pipeline(repoId, writer);
        var readerId = CatalogIdentity.Pipeline(repoId, reader);

        var store = ServerIdentity.Dataset("probe-store", $"${{env:STORE_{suffix}}}");
        var queue = ServerIdentity.Dataset("queue-hub", null);
        var tenant = "tenant_" + suffix;
        var wellName = "wks:well" + suffix + ":1.0.0";
        var wellKey = NodeKey.For(store, tenant, "master", wellName);
        var queueName = "orders" + suffix;
        var queueKey = NodeKey.For(queue, tenant, "inbound", queueName);
        var fileName = $"exports_{suffix}/wells";
        var fileKey = $"file|||{fileName}";
        var tableKey = $"${{env:SRV_{suffix}}}|dw|dbo|{tenant}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(SeedPipeline(writerId, repoId, writer, now));
                db.Pipelines.Add(SeedPipeline(readerId, repoId, reader, now));
                db.Objects.Add(SeedObject(wellKey, store, tenant, "master", wellName, "Dataset", now));
                db.Objects.Add(SeedObject(queueKey, queue, tenant, "inbound", queueName, "Dataset", now));
                db.Objects.Add(SeedObject(fileKey, "file", null, null, fileName, "File", now));
                db.Objects.Add(SeedObject(tableKey, $"${{env:SRV_{suffix}}}", "dw", "dbo", tenant, "Table", now));

                // The writer writes the record type; the reader reads it, drops what it read as files, and records the
                // drop in a table; the reader also reads the queue, which nothing writes.
                db.LineageEdges.AddRange(
                    Edge(repoId, writer, writerId, "Writes", wellKey, wellName),
                    Edge(repoId, reader, readerId, "Reads", wellKey, wellName),
                    Edge(repoId, reader, readerId, "Reads", queueKey, queueName),
                    Edge(repoId, reader, readerId, "Writes", fileKey, fileName),
                    Edge(repoId, reader, readerId, "Writes", tableKey, tenant));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var all = (await GetJsonAsync<IReadOnlyList<DatasetNodeDto>>(client, token, "/api/v1/lineage/datasets"))
                .Where(d => d.Namespace == tenant)
                .ToList();
            Assert.Equal(2, all.Count);
            var well = Assert.Single(all, d => d.Key == wellKey);
            Assert.Equal(("probe-store", "master", wellName, 1, 1), (well.System, well.Group, well.Name, well.Readers, well.Writers));
            var inbound = Assert.Single(all, d => d.Key == queueKey);
            Assert.Equal(("queue-hub", "inbound", 1, 0), (inbound.System, inbound.Group, inbound.Readers, inbound.Writers));

            var bySystem = await GetJsonAsync<IReadOnlyList<DatasetNodeDto>>(client, token, "/api/v1/lineage/datasets?system=queue-hub");
            Assert.Contains(bySystem, d => d.Key == queueKey);
            Assert.DoesNotContain(bySystem, d => d.Key == wellKey);

            var byPrefix = await GetJsonAsync<IReadOnlyList<DatasetNodeDto>>(client, token, "/api/v1/lineage/datasets?system=probe");
            Assert.DoesNotContain(byPrefix, d => d.Namespace == tenant);

            var bySearch = await GetJsonAsync<IReadOnlyList<DatasetNodeDto>>(
                client, token, $"/api/v1/lineage/datasets?search={Uri.EscapeDataString("well" + suffix)}");
            Assert.Equal([wellKey], bySearch.Select(d => d.Key).ToList());

            // A dataset's namespace is not a database: the schema hierarchy never lists one.
            var kinds = await GetJsonAsync<IReadOnlyList<SchemaKindCountDto>>(client, token, "/api/v1/lineage/schemas/kinds");
            Assert.DoesNotContain(kinds, k => k.Database == tenant && k.Kind == "Dataset");

            // The reader lands what it read in a file and a table; the writer is where the dataset comes from.
            var flows = await GetJsonAsync<FileFlowsDto>(
                client, token, $"/api/v1/lineage/file-flows?key={Uri.EscapeDataString(wellKey)}");
            Assert.Equal(writer, Assert.Single(flows.Producers).Flow);
            var consumer = Assert.Single(flows.Consumers);
            Assert.Equal(reader, consumer.Flow);
            Assert.Equal(
                new[] { fileKey, tableKey }.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                consumer.Lands.Select(l => l.Key).OrderBy(k => k, StringComparer.Ordinal).ToList());

            // A file's provenance keeps files out of the landing: the reader wrote this file, it did not read it.
            var fileFlows = await GetJsonAsync<FileFlowsDto>(
                client, token, $"/api/v1/lineage/file-flows?key={Uri.EscapeDataString(fileKey)}");
            Assert.Equal(reader, Assert.Single(fileFlows.Producers).Flow);
            Assert.Empty(fileFlows.Consumers);

            // The project graph captions a dataset by its system and places it in its namespace and group.
            var graph = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?expand={Uri.EscapeDataString(wellKey)}");
            var node = Assert.Single(graph.Objects, o => o.Key == wellKey);
            Assert.Equal("probe store", node.Kind);
            Assert.Equal(wellName, node.Name);
            Assert.Equal($"{tenant}.master", node.Location);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == wellKey || o.Key == queueKey || o.Key == fileKey || o.Key == tableKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline SeedPipeline(Guid id, Guid repoId, string name, DateTime now)
        => new()
        {
            Id = id, RepoId = repoId, Name = name, Kind = "probe",
            RelativePath = name + ".yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\n", DefinitionJson = "{}", Active = true, Wave = 0,
            FirstSeenUtc = now, LastSeenUtc = now,
        };

    private static CatalogObject SeedObject(
        string key, string serverRef, string? database, string? schema, string name, string kind, DateTime now)
        => new()
        {
            Key = key, ServerRef = serverRef, Database = database, Schema = schema, Name = name, Kind = kind,
            FirstSeenUtc = now, LastSeenUtc = now,
        };

    private static CatalogLineageEdge Edge(Guid repoId, string flow, Guid pipelineId, string relation, string objectKey, string objectName)
        => new()
        {
            RepoId = repoId, Flow = flow, PipelineId = pipelineId, Relation = relation,
            ObjectKey = objectKey, ObjectName = objectName, Tier = "Declared",
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
