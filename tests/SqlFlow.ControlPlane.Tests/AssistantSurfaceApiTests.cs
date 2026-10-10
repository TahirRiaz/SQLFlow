using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant surface end to end: a request carrying the surface header is shown no text naming a column outside
/// the semantic layer (here a flow's YAML, through the same filter every read endpoint shares) and may not enqueue a
/// data-operations task reading one, while the same requests without the header are served exactly as before.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantSurfaceApiTests
{
    [SkippableFact]
    public async Task TheAssistantSurface_SeesNoTextOrTaskOutsideTheLayer_AndPeopleAreUnaffected()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_ASSIST_" + suffix + "}";
        var tableName = "Employee_" + suffix;
        var objectKey = $"{serverRef}|dw|dbo|{tableName.ToLowerInvariant()}";
        var secret = "Ssn_" + suffix;
        var flowName = "assist_flow_" + suffix;
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = objectKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = tableName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 1, Name = "Name", DataType = "nvarchar(100)", Tier = "Observed",
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 2, Name = secret, DataType = "varchar(11)", Tier = "Observed",
                });
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "assist_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = Guid.NewGuid(), RepoId = repoId, Name = flowName, Kind = "ing",
                    RelativePath = "assist/flow.yaml", Active = true, SourceServer = serverRef, TargetServer = serverRef,
                    DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
                    Yaml = $"source:\n  query: SELECT Name, {secret} FROM dbo.{tableName}\n",
                });
                db.ColumnPolicies.Add(new CatalogColumnPolicy
                {
                    ObjectKey = objectKey, ColumnName = "Name", IsAllowed = true, UpdatedUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
            var scriptUri = $"/api/v1/lineage/script?key={Uri.EscapeDataString(flowName)}";

            // 1. A person reads the flow's YAML whole; the assistant surface gets it withheld, with the flow still named.
            var asPerson = await GetJsonAsync<NodeScriptDto>(client, token, scriptUri, assistant: false);
            Assert.Contains(secret, asPerson.Script, StringComparison.Ordinal);

            var asAssistantJson = await GetStringAsync(client, token, scriptUri, assistant: true);
            Assert.DoesNotContain(secret, asAssistantJson, StringComparison.OrdinalIgnoreCase);
            var asAssistant = await GetJsonAsync<NodeScriptDto>(client, token, scriptUri, assistant: true);
            Assert.Equal(AssistantScope.WithheldText, asAssistant.Script);
            Assert.Equal(flowName, asAssistant.Name);

            // 2. The assistant may not check duplicates over a column outside the layer, nor without naming the key...
            using (var denied = await PostTaskAsync(client, token, assistant: true, objectName: tableName, serverRef, [secret]))
            {
                Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            }

            using (var unnamed = await PostTaskAsync(client, token, assistant: true, objectName: tableName, serverRef, []))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            }

            // ...but may over an allowed one, and a person's own request is not narrowed at all.
            using (var allowed = await PostTaskAsync(client, token, assistant: true, objectName: tableName, serverRef, ["Name"]))
            {
                Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
            }

            using (var person = await PostTaskAsync(client, token, assistant: false, objectName: tableName, serverRef, []))
            {
                Assert.Equal(HttpStatusCode.Accepted, person.StatusCode);
            }

            // 3. Once the column is allowed, the assistant surface reads the YAML whole too.
            using (var allow = await SendAsync(client, token, HttpMethod.Put, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, secret, IsAllowed: true, Reason: null), assistant: false))
            {
                allow.EnsureSuccessStatusCode();
            }

            var afterAllow = await GetJsonAsync<NodeScriptDto>(client, token, scriptUri, assistant: true);
            Assert.Contains(secret, afterAllow.Script, StringComparison.Ordinal);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ComputeTasks.Where(t => t.SourceRef == serverRef).ExecuteDeleteAsync();
            await db.ColumnPolicies.Where(p => p.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static Task<HttpResponseMessage> PostTaskAsync(
        HttpClient client, string token, bool assistant, string objectName, string serverRef, string[] columns)
        => SendAsync(client, token, HttpMethod.Post, "/api/v1/datasources/tasks", new
        {
            operation = "duplicateKeys",
            reference = serverRef,
            schema = "dbo",
            objectName,
            columns,
        }, assistant);

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var issued = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(issued);
        return issued.AccessToken;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string token, string relativeUri, bool assistant)
    {
        using var response = await SendAsync<object?>(client, token, HttpMethod.Get, relativeUri, null, assistant);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri, bool assistant)
    {
        using var response = await SendAsync<object?>(client, token, HttpMethod.Get, relativeUri, null, assistant);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string relativeUri, T body, bool assistant)
    {
        using var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (assistant)
        {
            request.Headers.Add(AssistantScope.HeaderName, AssistantScope.AssistantValue);
        }

        return await client.SendAsync(request);
    }
}
