using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A compute operation a host module registers is enqueued through the module's own endpoints, which carry its
/// authorization and validation. SQLFlow's datasource task endpoint keeps accepting only the built-in operations, so a
/// module's operation can never be reached around the module's checks. Needs no database: the operation is refused at the
/// payload validation, before any catalog read.
/// </summary>
public sealed class RegisteredComputeOperationApiTests
{
    [Fact]
    public async Task TheDatasourceTaskEndpoint_RefusesARegisteredModuleOperation()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithServices(services => services.AddSingleton<IComputeOperation, ModuleProbe>());
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/datasources/tasks", UriKind.Relative))
        {
            Content = JsonContent.Create(new ComputeTaskRequest("flow:wells", "moduleProbe")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown compute operation 'moduleProbe'", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private sealed class ModuleProbe : IComputeOperation
    {
        public string Name => "moduleProbe";

        public Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct) => Task.FromResult("{}");
    }
}
