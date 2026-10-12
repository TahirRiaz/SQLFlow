using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant surface end to end: schema is not sensitive, so a request carrying the surface header is shown the
/// same text as anyone (here a flow's YAML naming a column no AI context opens), while a data-operations task it
/// enqueues reads live rows and may not read such a column. The same requests without the header are not narrowed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantSurfaceApiTests
{
    [SkippableFact]
    public async Task TheAssistantSurface_SeesTheSchemaWhole_ButEnqueuesNoTaskReadingAColumnOutsideTheLayer()
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
        await using var contexts = new AiContextTestKit(cs, repoId);
        var context = "people-" + suffix;
        string ContextFile(params string[] denied)
            => $"aiContext: {context}\ntables:\n  - table: dw.dbo.{tableName}\n"
                + (denied.Length == 0 ? string.Empty : $"    deny: [{string.Join(", ", denied)}]\n");
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
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            // The table is in an approved AI context that denies the secret column.
            contexts.Write("people.ai-context.yaml", ContextFile(secret));
            await contexts.SyncAsync();
            await AiContextTestKit.ApproveAsync(client, token, context);
            var scriptUri = $"/api/v1/lineage/script?key={Uri.EscapeDataString(flowName)}";

            // 1. A person and the assistant surface alike read the flow's YAML whole, the denied column named in it.
            var asPerson = await GetJsonAsync<NodeScriptDto>(client, token, scriptUri, assistant: false);
            Assert.Contains(secret, asPerson.Script, StringComparison.Ordinal);
            var asAssistant = await GetJsonAsync<NodeScriptDto>(client, token, scriptUri, assistant: true);
            Assert.Equal(asPerson.Script, asAssistant.Script);
            Assert.Equal(flowName, asAssistant.Name);

            // So does the object's column list: every column is listed, the denied one too.
            var columns = await GetStringAsync(
                client, token, $"/api/v1/lineage/objects/columns?key={Uri.EscapeDataString(objectKey)}", assistant: true);
            Assert.Contains(secret, columns, StringComparison.Ordinal);

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

            // 3. Once the file stops denying the column and an admin approves that, the assistant may check it too.
            contexts.Write("people.ai-context.yaml", ContextFile());
            await contexts.SyncAsync();
            await AiContextTestKit.ApproveAsync(client, token, context);

            using (var nowOpen = await PostTaskAsync(client, token, assistant: true, objectName: tableName, serverRef, [secret]))
            {
                Assert.Equal(HttpStatusCode.Accepted, nowOpen.StatusCode);
            }
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
