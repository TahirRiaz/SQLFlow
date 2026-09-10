using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The identity surface end to end: providers discovery, local login (success, bad password, throttle lockout),
/// the Entra token exchange with JIT provisioning (validator faked at the DI seam; the real validator is pure
/// framework JWKS validation), the admin-scoped user management API, and bootstrap provisioning of roles + the
/// initial admin. No-database assertions run everywhere; the rest is gated on a reachable catalog database.
/// </summary>
public sealed class IdentityApiTests : IClassFixture<ControlPlaneAppFactory>
{
    private readonly ControlPlaneAppFactory _factory;

    public IdentityApiTests(ControlPlaneAppFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    // ---- No-database surface -----------------------------------------------------------------------------------

    [Fact]
    public async Task Providers_ReportsLocalAndBootstrap_WithEntraDisabledByDefault()
    {
        using var client = _factory.CreateClient();

        var providers = await client.GetFromJsonAsync<AuthProvidersDto>(new Uri("/api/v1/auth/providers", UriKind.Relative));

        Assert.NotNull(providers);
        Assert.True(providers.Local);
        Assert.True(providers.Bootstrap); // the test factory configures a bootstrap secret
        Assert.False(providers.Entra.Enabled);
        Assert.Null(providers.Entra.ClientId);
    }

    [Fact]
    public async Task Login_WithMissingFields_Returns400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest("", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_IsNotMapped_WhenEntraIsDisabled()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/exchange", UriKind.Relative), new ExchangeRequest("some-token"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UserAdminSurface_RequiresTheAdminScope()
    {
        using var client = _factory.CreateClient();

        // No token: 401.
        using var anonymous = await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A read+operate token (no admin scope): 403.
        var token = await BootstrapTokenAsync(client, ["read", "operate"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/users", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var forbidden = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public void LocalPasswords_ProduceAHash_TheFrameworkHasherVerifies()
    {
        // The CLI's `sqlflow user reset-password` hashes through the shared LocalPasswords helper, while the control
        // plane verifies logins with a framework PasswordHasher<CatalogUser>. This locks that compatibility contract
        // so changing the hashing in one place can never silently make CLI-set passwords unloginable. No database
        // needed, so it guards the contract on every CI run, not only where a catalog is reachable.
        var user = new CatalogUser { Username = "hash-contract" };
        var hash = LocalPasswords.Hash(user, "a-long-enough-password");

        var verifier = new PasswordHasher<CatalogUser>();
        Assert.NotEqual(PasswordVerificationResult.Failed, verifier.VerifyHashedPassword(user, hash, "a-long-enough-password"));
        Assert.Equal(PasswordVerificationResult.Failed, verifier.VerifyHashedPassword(user, hash, "the-wrong-password"));
    }

    // ---- Local login against the real catalog ------------------------------------------------------------------

    [SkippableFact]
    public async Task Login_WithSeededUser_IssuesASessionThatWorksOnItsScopeSurface()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var username = $"login-test-{Guid.NewGuid():N}";
        const string password = "a-long-test-password-1234";

        try
        {
            await SeedLocalUserAsync(cs, username, password, RoleNames.Admin);
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, password));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(session);
            Assert.Equal(username, session.Subject);
            Assert.Equal(RoleNames.Admin, session.Role);
            Assert.Contains("admin", session.Scopes);
            Assert.True(session.ExpiresIn > 0);

            // The session token opens the admin surface (which the bootstrap read+operate token cannot).
            using var list = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/users", UriKind.Relative));
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var users = await client.SendAsync(list);
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);

            // A wrong password is a generic 401.
            using var wrong = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, "not-the-password-000"));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Login_AfterRepeatedFailures_LocksOutWith429()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var username = $"lockout-test-{Guid.NewGuid():N}";
        const string password = "a-long-test-password-1234";

        try
        {
            await SeedLocalUserAsync(cs, username, password, RoleNames.Viewer);
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();

            for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            {
                using var attempt = await client.PostAsJsonAsync(
                    new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, "wrong-password-00"));
                Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
            }

            // The pair is now locked out; even the CORRECT password is refused with 429 until the lockout lapses.
            using var locked = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, password));
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task ResetPassword_AppliedThroughTheCliPath_SignsInWithTheNewPassword_AndRejectsTheOld()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var username = $"reset-test-{Guid.NewGuid():N}";
        const string oldPassword = "old-password-1234567";
        const string newPassword = "new-password-7654321";

        try
        {
            await SeedLocalUserAsync(cs, username, oldPassword, RoleNames.Admin);

            // Apply the reset exactly as `sqlflow user reset-password` does: hash with the shared LocalPasswords
            // helper and persist through UserStore.SetPasswordHashAsync (the command's entire database mutation).
            await using (var db = CatalogDatabase.Create(cs))
            {
                var user = await UserStore.FindByUsernameAsync(db, username);
                Assert.NotNull(user);
                var hash = LocalPasswords.Hash(user, newPassword);
                Assert.Equal(UserMutation.Applied, await UserStore.SetPasswordHashAsync(db, user.Id, hash, DateTime.UtcNow));
            }

            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();

            // The new password signs in through the real endpoint: proof the CLI-set hash is login-compatible.
            using var withNew = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, newPassword));
            Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
            var session = await withNew.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(session);
            Assert.Equal(username, session.Subject);
            Assert.Equal(RoleNames.Admin, session.Role);

            // The superseded password no longer works.
            using var withOld = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, oldPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    // ---- Entra token exchange (validator faked at the DI seam) --------------------------------------------------

    [SkippableFact]
    public async Task Exchange_ProvisionsJustInTime_ThenSignsInWithoutDuplicating()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var objectId = Guid.NewGuid().ToString("N");
        var username = $"sso-test-{Guid.NewGuid():N}@example.test";
        var profile = new ExternalUserProfile(objectId, username, "SSO Test User", username);

        try
        {
            using var factory = EntraEnabledFactory(cs, new FakeTokenValidator(token =>
                token == "valid-entra-token" ? profile : null));
            using var client = factory.CreateClient();

            // An invalid external token is a generic 401.
            using var invalid = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/exchange", UriKind.Relative), new ExchangeRequest("garbage"));
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

            // First exchange provisions the user with the default (viewer) role and signs them in.
            using var first = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/exchange", UriKind.Relative), new ExchangeRequest("valid-entra-token"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var session = await first.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(session);
            Assert.Equal(username, session.Subject);
            Assert.Equal(RoleNames.Viewer, session.Role);
            Assert.Equal(["read"], session.Scopes);

            // The session works on the read surface.
            using var read = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/repos", UriKind.Relative));
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var repos = await client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, repos.StatusCode);

            // A second exchange signs in the SAME user (no duplicate row).
            using var second = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/exchange", UriKind.Relative), new ExchangeRequest("valid-entra-token"));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            await using var db = CatalogDatabase.Create(cs);
            var rows = await db.Users.AsNoTracking().Where(u => u.ExternalObjectId == objectId).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(UserProviders.Entra, row.Provider);
            Assert.NotNull(row.LastLoginUtc);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Renew_RollsALiveSessionOntoAFreshToken_ThatOpensTheSameSurface()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var username = $"renew-test-{Guid.NewGuid():N}";
        const string password = "a-long-test-password-1234";

        try
        {
            await SeedLocalUserAsync(cs, username, password, RoleNames.Admin);
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();

            var session = await WaitForLoginAsync(client, username, password);

            using var renewResponse = await SendAsync(client, session.AccessToken, HttpMethod.Post, "/api/v1/auth/renew", null);
            Assert.Equal(HttpStatusCode.OK, renewResponse.StatusCode);
            var renewed = await renewResponse.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(renewed);
            Assert.Equal(username, renewed.Subject);
            Assert.Equal(RoleNames.Admin, renewed.Role);
            Assert.True(renewed.ExpiresIn > 0);
            // A genuinely new token, not the one presented handed back.
            Assert.NotEqual(session.AccessToken, renewed.AccessToken);

            // The rolled token carries the same authority as the one it replaced.
            using var users = await SendAsync(client, renewed.AccessToken, HttpMethod.Get, "/api/v1/users", null);
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Renew_ForAnAccountDeactivatedSinceSignIn_Returns401()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var username = $"renew-off-{Guid.NewGuid():N}";
        const string password = "a-long-test-password-1234";

        try
        {
            // A viewer, not an admin: the catalog refuses to deactivate the last remaining admin, which would leave
            // this test asserting against a mutation that never applied.
            await SeedLocalUserAsync(cs, username, password, RoleNames.Viewer);
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();

            var session = await WaitForLoginAsync(client, username, password);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var user = await UserStore.FindByUsernameAsync(db, username);
                Assert.Equal(UserMutation.Applied, await UserStore.SetActiveAsync(db, user!.Id, active: false, DateTime.UtcNow));
            }

            // The roll is where a deactivation catches up with an already-issued token: without this, a rolling
            // session would outlive the authority behind it.
            using var response = await SendAsync(client, session.AccessToken, HttpMethod.Post, "/api/v1/auth/renew", null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Exchange_ForADeactivatedUser_Returns403()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var objectId = Guid.NewGuid().ToString("N");
        var username = $"sso-off-{Guid.NewGuid():N}@example.test";

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await SeedRolesAsync(db);
                var (_, user) = await UserStore.EnsureExternalAsync(
                    db, new ExternalUserProfile(objectId, username, null, null), RoleNames.Viewer, DateTime.UtcNow);
                Assert.Equal(UserMutation.Applied, await UserStore.SetActiveAsync(db, user!.Id, active: false, DateTime.UtcNow));
            }

            using var factory = EntraEnabledFactory(cs, new FakeTokenValidator(_ =>
                new ExternalUserProfile(objectId, username, null, null)));
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/exchange", UriKind.Relative), new ExchangeRequest("valid"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await CleanupUsersAsync(cs, username);
        }
    }

    // ---- User administration ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task UserAdmin_CreateSetRoleDeactivate_EnforcesTheGuards()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var prefix = $"admin-api-{Guid.NewGuid():N}";

        try
        {
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            await using (var db = CatalogDatabase.Create(cs))
            {
                await SeedRolesAsync(db);
            }

            var token = await BootstrapTokenAsync(client, ["read", "operate", "admin"]);

            // An anchor admin, so the last-active-admin guard never fires on the user this test manipulates
            // (the shared test catalog cannot be assumed to hold any other active admin).
            using var anchor = await SendAsync(client, token, HttpMethod.Post, "/api/v1/users",
                new CreateUserRequest($"{prefix}-anchor", "an-anchor-admin-password", RoleNames.Admin, null, null));
            Assert.Equal(HttpStatusCode.Created, anchor.StatusCode);

            // Create: a too-short password is refused, a valid one lands with the requested role.
            using var shortPw = await SendAsync(client, token, HttpMethod.Post, "/api/v1/users",
                new CreateUserRequest($"{prefix}-a", "short", RoleNames.Operator, null, null));
            Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);

            using var create = await SendAsync(client, token, HttpMethod.Post, "/api/v1/users",
                new CreateUserRequest($"{prefix}-a", "a-perfectly-long-password", RoleNames.Operator, "a@example.test", "User A"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<UserDto>();
            Assert.NotNull(created);
            Assert.Equal(RoleNames.Operator, created.Role);

            using var duplicate = await SendAsync(client, token, HttpMethod.Post, "/api/v1/users",
                new CreateUserRequest($"{prefix}-a", "another-long-password-1", RoleNames.Viewer, null, null));
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            using var badRole = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/users/{created.Id}/role",
                new SetRoleRequest("no-such-role"));
            Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

            using var promote = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/users/{created.Id}/role",
                new SetRoleRequest(RoleNames.Admin));
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

            using var deactivate = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/users/{created.Id}/deactivate", body: null);
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
            var deactivated = await deactivate.Content.ReadFromJsonAsync<UserDto>();
            Assert.False(deactivated!.Active);

            using var reactivate = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/users/{created.Id}/activate", body: null);
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

            // Password reset on an SSO user is a 409 (their credential lives in Entra).
            Guid ssoId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var (_, ssoUser) = await UserStore.EnsureExternalAsync(
                    db, new ExternalUserProfile(Guid.NewGuid().ToString("N"), $"{prefix}-sso@example.test", null, null),
                    RoleNames.Viewer, DateTime.UtcNow);
                ssoId = ssoUser!.Id;
            }

            using var ssoReset = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/users/{ssoId}/password",
                new SetPasswordRequest("a-perfectly-long-password"));
            Assert.Equal(HttpStatusCode.Conflict, ssoReset.StatusCode);

            // The list surface pages and filters.
            using var list = new HttpRequestMessage(HttpMethod.Get,
                new Uri($"/api/v1/users?username={prefix}", UriKind.Relative));
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var listResponse = await client.SendAsync(list);
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var page = await listResponse.Content.ReadFromJsonAsync<PagedResult<UserDto>>();
            Assert.NotNull(page);
            Assert.Equal(3, page.Total); // anchor admin, the managed user, and the SSO user

            // Roles are listable for the GUI's role picker.
            using var roles = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/roles", UriKind.Relative));
            roles.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var rolesResponse = await client.SendAsync(roles);
            Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
            var roleList = await rolesResponse.Content.ReadFromJsonAsync<List<RoleDto>>();
            Assert.NotNull(roleList);
            Assert.Contains(roleList, r => r.Name == RoleNames.Admin && r.Scopes.Contains("admin", StringComparison.Ordinal));
        }
        finally
        {
            await CleanupUsersAsync(cs, prefix);
        }
    }

    // ---- Bootstrap provisioning ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task BootstrapProvisioning_SeedsRolesAndTheInitialAdmin_Idempotently()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var adminUsername = $"bootstrap-admin-{Guid.NewGuid():N}";
        const string adminPassword = "bootstrap-admin-password-1";

        try
        {
            for (var round = 0; round < 2; round++) // second round proves idempotency
            {
                using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
                using var host = factory.WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ControlPlane:Bootstrap:AdminUsername", adminUsername);
                    builder.UseSetting("ControlPlane:Bootstrap:AdminPasswordReference", adminPassword);
                });
                using var client = host.CreateClient();

                // Provisioning runs in the background after host start; poll sign-in until it lands.
                var session = await WaitForLoginAsync(client, adminUsername, adminPassword);
                Assert.Equal(RoleNames.Admin, session.Role);
                Assert.Contains("admin", session.Scopes);
            }

            await using var db = CatalogDatabase.Create(cs);
            Assert.NotNull(await UserStore.FindRoleAsync(db, RoleNames.Admin));
            Assert.NotNull(await UserStore.FindRoleAsync(db, RoleNames.Operator));
            Assert.NotNull(await UserStore.FindRoleAsync(db, RoleNames.Viewer));
            var admins = await db.Users.AsNoTracking().Where(u => u.Username == adminUsername).ToListAsync();
            Assert.Single(admins); // two boots, one admin
        }
        finally
        {
            await CleanupUsersAsync(cs, adminUsername);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static WebApplicationFactoryDerived EntraEnabledFactory(string cs, IExternalTokenValidator validator)
    {
        var inner = new ControlPlaneAppFactory().WithCatalog(cs);
        var host = inner.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ControlPlane:AzureAd:Enabled", "true");
            builder.UseSetting("ControlPlane:AzureAd:AllowedTenantIds:0", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("ControlPlane:AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
            builder.ConfigureTestServices(services =>
                services.AddSingleton(validator));
        });
        return new WebApplicationFactoryDerived(inner, host);
    }

    /// <summary>Owns both the base factory and its derived host so a single using disposes the pair.</summary>
    private sealed class WebApplicationFactoryDerived : IDisposable
    {
        private readonly ControlPlaneAppFactory _inner;
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

        public WebApplicationFactoryDerived(
            ControlPlaneAppFactory inner, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
        {
            _inner = inner;
            _host = host;
        }

        public HttpClient CreateClient() => _host.CreateClient();

        public void Dispose()
        {
            _host.Dispose();
            _inner.Dispose();
        }
    }

    private sealed class FakeTokenValidator : IExternalTokenValidator
    {
        private readonly Func<string, ExternalUserProfile?> _validate;

        public FakeTokenValidator(Func<string, ExternalUserProfile?> validate) => _validate = validate;

        public Task<ExternalUserProfile?> ValidateAsync(string token, CancellationToken ct)
            => Task.FromResult(_validate(token));
    }

    private static async Task<SessionResponse> WaitForLoginAsync(HttpClient client, string username, string password)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, password));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
                Assert.NotNull(session);
                return session;
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"bootstrap provisioning did not produce a signable admin within 60s (last status {(int)response.StatusCode})");
            await Task.Delay(500);
        }
    }

    private static async Task SeedLocalUserAsync(string cs, string username, string password, string role)
    {
        await using var db = CatalogDatabase.Create(cs);
        await SeedRolesAsync(db);
        var hasher = new PasswordHasher<CatalogUser>();
        var hash = hasher.HashPassword(new CatalogUser { Username = username }, password);
        var (status, _) = await UserStore.CreateLocalAsync(db, username, hash, role, null, null, DateTime.UtcNow);
        Assert.Equal(UserCreateStatus.Created, status);
    }

    private static async Task SeedRolesAsync(CatalogDbContext db)
    {
        var now = DateTime.UtcNow;
        await UserStore.EnsureRoleAsync(db, RoleNames.Admin, "read operate admin", "Full control.", now);
        await UserStore.EnsureRoleAsync(db, RoleNames.Operator, "read operate", "Operations.", now);
        await UserStore.EnsureRoleAsync(db, RoleNames.Viewer, "read", "Read-only.", now);
    }

    private static async Task<string> BootstrapTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request);
    }

    private static async Task CleanupUsersAsync(string cs, string prefix)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Users.Where(u => u.Username.StartsWith(prefix)).ExecuteDeleteAsync();
    }
}
