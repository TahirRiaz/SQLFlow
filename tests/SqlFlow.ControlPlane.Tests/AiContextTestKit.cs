using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// AI contexts the way they reach the catalog in production: files in a repository folder, read by the step every sync
/// ends with (<see cref="AiContextSync"/>), and approved by an admin over the API. Disposing it removes the folder, the
/// contexts its repository defined and everything they served, so a test leaves the shared database as it found it.
/// </summary>
internal sealed class AiContextTestKit : IAsyncDisposable
{
    private readonly string _cs;

    public AiContextTestKit(string cs, Guid? repoId = null)
    {
        _cs = cs;
        RepoId = repoId ?? Guid.NewGuid();
        Folder = Path.Combine(Path.GetTempPath(), "sqlflow-aictx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Folder);
    }

    /// <summary>The repository the files belong to.</summary>
    public Guid RepoId { get; }

    /// <summary>The repository folder the files are written to.</summary>
    public string Folder { get; }

    /// <summary>Writes (or replaces) the file at <paramref name="relativePath"/>.</summary>
    public void Write(string relativePath, string yaml)
    {
        var path = Path.Combine(Folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
    }

    /// <summary>Removes the file at <paramref name="relativePath"/>.</summary>
    public void Delete(string relativePath) => File.Delete(Path.Combine(Folder, relativePath));

    /// <summary>Runs the AI context step of a sync over the folder, as the repository <see cref="RepoId"/>.</summary>
    public async Task<IReadOnlyList<string>> SyncAsync()
    {
        await using var db = CatalogDatabase.Create(_cs);
        return await AiContextSync.ApplyAsync(db, RepoId, Folder, DateTime.UtcNow, CancellationToken.None);
    }

    /// <summary>The context's row as the catalog holds it.</summary>
    public async Task<CatalogAiContext?> RowAsync(string name)
    {
        await using var db = CatalogDatabase.Create(_cs);
        return await db.AiContexts.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name);
    }

    /// <summary>The columns of <paramref name="objectKey"/> open to an assistant, by name.</summary>
    public async Task<List<string>> AllowedColumnsAsync(string objectKey)
    {
        await using var db = CatalogDatabase.Create(_cs);
        return await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.ObjectKey == objectKey && p.IsAllowed)
            .OrderBy(p => p.ColumnName)
            .Select(p => p.ColumnName)
            .ToListAsync();
    }

    /// <summary>One context in full, through the API.</summary>
    public static async Task<AiContextDetailDto> GetAsync(HttpClient client, string token, string name)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/powerai/ai-contexts/{Uri.EscapeDataString(name)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AiContextDetailDto>())!;
    }

    /// <summary>Approves the version the context currently proposes, as an admin who just reviewed it would.</summary>
    public static async Task<AiContextDetailDto> ApproveAsync(HttpClient client, string token, string name)
    {
        var detail = await GetAsync(client, token, name);
        Assert.NotNull(detail.Summary.CurrentVersion);
        using var response = await TryApproveAsync(client, token, name, detail.Summary.CurrentVersion);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"approving '{name}' answered {(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<AiContextDetailDto>(body, Json)!;
    }

    /// <summary>Asks to approve <paramref name="version"/> of the context and returns the answer as it came.</summary>
    public static Task<HttpResponseMessage> TryApproveAsync(HttpClient client, string token, string name, string? version)
        => SendAsync(client, token, HttpMethod.Post, $"/api/v1/powerai/ai-contexts/{Uri.EscapeDataString(name)}/approve",
            new ApproveAiContextRequest(version));

    /// <summary>Revokes the context's approval.</summary>
    public static async Task<AiContextDetailDto> RevokeAsync(HttpClient client, string token, string name)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/powerai/ai-contexts/{Uri.EscapeDataString(name)}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AiContextDetailDto>())!;
    }

    /// <summary>A bearer token with <paramref name="scopes"/>, from the bootstrap secret.</summary>
    public static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    /// <summary>Sends <paramref name="body"/> (when given) with the bearer token.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string relativeUri, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        await using (var db = CatalogDatabase.Create(_cs))
        {
            var names = await db.AiContexts.Where(c => c.RepoId == RepoId).Select(c => c.Name).ToListAsync();
            foreach (var name in names)
            {
                await AiContextMaterializer.RemoveAsync(db, name, CancellationToken.None);
            }

            await db.AiContexts.Where(c => c.RepoId == RepoId).ExecuteDeleteAsync();
        }

        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
