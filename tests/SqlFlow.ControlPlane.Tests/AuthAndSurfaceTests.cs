using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The no-database surface of the control plane, exercised through the in-memory host. These run everywhere (no
/// SQL Server needed): the catalog context binds lazily, so auth, liveness, OpenAPI, and the unauthenticated 401
/// boundary all resolve against the placeholder connection without ever opening it.
/// </summary>
public sealed class AuthAndSurfaceTests : IClassFixture<ControlPlaneAppFactory>
{
    private readonly ControlPlaneAppFactory _factory;

    public AuthAndSurfaceTests(ControlPlaneAppFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    [Fact]
    public async Task ReadSurface_WithoutToken_Returns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/pipelines", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthLive_Returns200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_Returns200_AndDescribesThePipelinesPath()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(document));
        Assert.Contains("/api/v1/pipelines", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueToken_WithCorrectSecret_ReturnsBearerTokenWithPositiveLifetime()
    {
        using var client = _factory.CreateClient();

        var token = await IssueTokenAsync(client, ControlPlaneAppFactory.BootstrapSecret, ["read"]);

        Assert.Equal("Bearer", token.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        Assert.Equal(2, token.AccessToken.Count(c => c == '.')); // a compact JWS has exactly two dots
        Assert.True(token.ExpiresIn > 0, $"expected a positive lifetime, got {token.ExpiresIn}");
    }

    [Fact]
    public async Task Renew_WithABootstrapToken_Returns403_BecauseBreakGlassNeverRolls()
    {
        using var client = _factory.CreateClient();
        var token = await IssueTokenAsync(client, ControlPlaneAppFactory.BootstrapSecret, ["read"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/auth/renew", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await client.SendAsync(request);

        // Authenticated, and still refused: the bootstrap token carries no auth_time, so there is no interactive
        // session behind it to roll. It is meant to lapse and be presented again deliberately.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Renew_WithoutAToken_Returns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/api/v1/auth/renew", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IssueToken_WithWrongSecret_Returns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest("definitely-not-the-secret", null, ["read"]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IssueToken_WithUnknownScopeOnly_Returns400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["nonsense-scope"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IssuedToken_PassesAuthentication_OnTheReadSurface()
    {
        // The same /pipelines call is 401 without a token; with a valid token authentication passes, so it is NOT
        // 401. Against the placeholder catalog the query itself fails, so we assert the auth transition (not 401),
        // not a 200: that isolates "the bootstrap token is accepted" from "the database answered".
        using var client = _factory.CreateClient();

        using var anonymous = await client.GetAsync(new Uri("/api/v1/pipelines", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await IssueTokenAsync(client, ControlPlaneAppFactory.BootstrapSecret, ["read"]);
        using var authenticated = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/pipelines", UriKind.Relative));
        authenticated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await client.SendAsync(authenticated);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<TokenResponse> IssueTokenAsync(HttpClient client, string secret, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(secret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token;
    }
}
