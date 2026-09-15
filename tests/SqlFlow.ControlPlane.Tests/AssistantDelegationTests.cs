using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The credential an AI assistant run presents, and the fence around it, exercised through the in-memory host with
/// no database: a delegated token is shaped so it can never roll or administer, the control plane refuses it on every
/// endpoint that has not opted in (token management, sign-in approval and renewal, chat, run triggers, writes on the
/// read surface), and admits it on the read surface and the assistant's own read-only tool writes. Also covers the
/// device grant inheriting the approving session's <c>auth_time</c>, which is what lets the MCP server roll a device
/// sign-in under the same absolute cap as the GUI.
/// </summary>
public sealed class AssistantDelegationTests : IClassFixture<ControlPlaneAppFactory>
{
    private const string SomeGuid = "3f1c1b0e-8a53-4c1e-9d5e-6f0b7a2c9e41";

    private readonly ControlPlaneAppFactory _factory;

    public AssistantDelegationTests(ControlPlaneAppFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    [Fact]
    public void DelegatedToken_CarriesTheMarker_NeverRolls_AndDropsAdminAndNode()
    {
        var userId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        var result = Issuer().IssueAssistantDelegation(
            Caller("chat-user", userId, "Admin", "read operate author admin node"), TimeSpan.FromMinutes(4), nowUtc);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(result.Token);
        Assert.Equal(AssistantDelegation.ClaimValue, jwt.GetClaim(AssistantDelegation.ClaimType).Value);
        Assert.Equal("chat-user", jwt.Subject);
        Assert.Equal(userId.ToString("D"), jwt.GetClaim("uid").Value);
        Assert.Equal("Admin", jwt.GetClaim("role").Value);
        Assert.Equal("read operate author", jwt.GetClaim("scope").Value);
        // No auth_time: /auth/renew refuses it, so the run's token cannot outlive the run.
        Assert.False(jwt.TryGetClaim(JwtRegisteredClaimNames.AuthTime, out _));
        Assert.InRange(result.ExpiresUtc, nowUtc.AddMinutes(4).AddSeconds(-1), nowUtc.AddMinutes(4).AddSeconds(1));
    }

    [Theory]
    [InlineData("GET", "/api/v1/me/tokens")]
    [InlineData("POST", "/api/v1/me/tokens")]
    [InlineData("DELETE", "/api/v1/me/tokens/" + SomeGuid)]
    [InlineData("POST", "/api/v1/auth/renew")]
    [InlineData("POST", "/api/v1/auth/device/approve")]
    [InlineData("GET", "/api/v1/chat/conversations")]
    [InlineData("POST", "/api/v1/chat/ask")]
    [InlineData("GET", "/api/v1/me/notifications/subscriptions")]
    [InlineData("POST", "/api/v1/runs")]
    [InlineData("POST", "/api/v1/runs/" + SomeGuid + "/cancel")]
    [InlineData("POST", "/api/v1/schedules/" + SomeGuid + "/run")]
    [InlineData("POST", "/api/v1/repos/sources/" + SomeGuid + "/proposals")]
    [InlineData("PUT", "/api/v1/nodes/pools/scale")]
    [InlineData("POST", "/api/v1/repos/" + SomeGuid + "/sync")]
    public async Task DelegatedToken_IsRefused_EverywhereItHasNotBeenOpenedTo(string method, string path)
    {
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, DelegatedToken(), method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/v1/pipelines")]
    [InlineData("GET", "/api/v1/runs")]
    [InlineData("GET", "/api/v1/dataops/capabilities")]
    [InlineData("POST", "/api/v1/datasources/tasks")]
    [InlineData("POST", "/api/v1/dataops/queries/prepare")]
    [InlineData("POST", "/api/v1/dataops/queries/" + SomeGuid + "/run")]
    public async Task DelegatedToken_PassesTheFence_OnTheSurfaceTheAssistantToolsUse(string method, string path)
    {
        // Past the fence the endpoint does its own work (and against the placeholder catalog may fail, or refuse
        // because data-ops is off), so this asserts only that neither authentication nor the fence stopped it.
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, DelegatedToken(), method, path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegatedToken_CanAskWhoItIs_SoTheMcpServerCanVerifyIt()
    {
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, DelegatedToken(), "GET", "/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = await response.Content.ReadFromJsonAsync<IdentityDto>();
        Assert.NotNull(identity);
        Assert.Equal("chat-user", identity.Subject);
    }

    [Fact]
    public async Task AnInteractiveSession_IsNotFencedAtAll()
    {
        // The same self-service endpoint that refuses a delegated token lets the user's own session through (the
        // placeholder catalog then fails the query, which is past the point this asserts).
        using var client = _factory.CreateClient();
        var nowUtc = DateTime.UtcNow;
        var session = Issuer().Issue("chat-user", ["read", "operate"], nowUtc, "Operator", Guid.NewGuid(), nowUtc).Token;

        using var response = await SendAsync(client, session, "GET", "/api/v1/me/tokens");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceToken_ApprovedFromAnInteractiveSession_InheritsItsSignInTime()
    {
        using var client = _factory.CreateClient();
        var nowUtc = DateTime.UtcNow;
        var signedIn = nowUtc.AddDays(-3);
        var approver = Issuer().Issue("approver", ["read", "operate"], nowUtc, "Operator", Guid.NewGuid(), signedIn).Token;

        var deviceToken = await ApprovedDeviceTokenAsync(client, approver);

        // The device rolls under the approver's cap, measured from their real sign-in, never from the approval.
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(deviceToken);
        Assert.True(jwt.TryGetClaim(JwtRegisteredClaimNames.AuthTime, out var authTime));
        Assert.Equal(
            new DateTimeOffset(signedIn).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            authTime.Value);
    }

    [Fact]
    public async Task DeviceToken_ApprovedByACredentialThatDoesNotRoll_DoesNotRollEither()
    {
        using var client = _factory.CreateClient();
        using var bootstrap = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        bootstrap.EnsureSuccessStatusCode();
        var approver = (await bootstrap.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

        var deviceToken = await ApprovedDeviceTokenAsync(client, approver);

        Assert.False(new JsonWebTokenHandler().ReadJsonWebToken(deviceToken)
            .TryGetClaim(JwtRegisteredClaimNames.AuthTime, out _));
        using var renew = await SendAsync(client, deviceToken, "POST", "/api/v1/auth/renew");
        Assert.Equal(HttpStatusCode.Forbidden, renew.StatusCode);
    }

    private TokenIssuer Issuer() => _factory.Services.GetRequiredService<TokenIssuer>();

    private string DelegatedToken()
        => Issuer().IssueAssistantDelegation(
            Caller("chat-user", Guid.NewGuid(), "Operator", "read operate"), TimeSpan.FromMinutes(4), DateTime.UtcNow)
            .Token;

    private static ClaimsPrincipal Caller(string subject, Guid userId, string role, string scope)
        => new(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim("uid", userId.ToString("D")),
            new Claim("role", role),
            new Claim("scope", scope),
        ], "test"));

    private static async Task<string> ApprovedDeviceTokenAsync(HttpClient client, string approverToken)
    {
        using var start = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/device", UriKind.Relative),
            new DeviceAuthorizationRequest("sqlflow-mcp", "read operate"));
        start.EnsureSuccessStatusCode();
        var authorization = await start.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();
        Assert.NotNull(authorization);

        using var approve = await SendAsync(client, approverToken, "POST", "/api/v1/auth/device/approve",
            new DeviceApprovalRequest(authorization.UserCode));
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        using var poll = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/device/token", UriKind.Relative), new DeviceTokenRequest(authorization.DeviceCode));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var token = await poll.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, string method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null || method is "POST" or "PUT" or "DELETE")
        {
            request.Content = JsonContent.Create(body ?? new { });
        }

        return await client.SendAsync(request);
    }
}
