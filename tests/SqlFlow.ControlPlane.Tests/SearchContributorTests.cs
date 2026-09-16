using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Search categories a host module contributes (<see cref="ISearchContributor"/>): the registration rules checked at
/// startup, the fan-out the combined search runs (the caller's policy, the parsed query and preview size, a failing
/// contributor isolated to its own category with a redacted error, the result contract), the combined result's shape,
/// and the paged endpoint through the in-memory host. Nothing here needs a database: the contributors are test doubles
/// and the paged endpoint reads no catalog table.
/// </summary>
public sealed class SearchContributorTests
{
    private const string CorrelationId = "corr-search-1";

    // ---- Registration ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Records")]
    [InlineData("record-hits")]
    [InlineData("")]
    [InlineData("a0123456789012345678901234567890123456789")]
    public async Task Validate_RefusesAKeyOutsideThePattern(string key)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(new FakeContributor(key)));
        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lower camel case", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flows")]
    [InlineData("subscribers")]
    [InlineData("statementWindowDays")]
    [InlineData("query")]
    public async Task Validate_RefusesAKeyTheCombinedResultAlreadyUses(string key)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(new FakeContributor(key)));
        Assert.Contains("already uses for a built-in category", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RefusesTwoContributorsSharingAKey_IgnoringCase()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidateAsync(new FakeContributor("recordHits"), new FakeContributor("recordhits")));
        Assert.Contains("is registered by both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RefusesABlankLabel()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(new FakeContributor("records", label: " ")));
        Assert.Contains("non-blank label", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RefusesAPolicyTheHostDoesNotDefine()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidateAsync(new FakeContributor("records", policy: "curator")));
        Assert.Contains("'curator'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_AcceptsDistinctContributors_WithDefinedPolicies()
    {
        await ValidateAsync(new FakeContributor("records"), new FakeContributor("submissions", policy: "admin"));
    }

    // ---- The combined search's fan-out ----------------------------------------------------------------------------

    [Fact]
    public async Task Collect_AFailingContributor_ReportsOnlyItsOwnCategory_WithoutItsMessage()
    {
        var logger = new CapturingLogger();
        var failing = new FakeContributor("broken", "Broken",
            answer: (_, _) => throw new InvalidOperationException("Server=db;Password=hunter2;exploded"));
        var working = new FakeContributor("records", "Records",
            answer: (_, _) => Task.FromResult(new SearchContribution([Hit("r1")], 12, TotalCapped: true)));

        var categories = await CollectAsync([failing, working], logger: logger);

        var broken = Assert.IsType<ContributedSearchCategoryDto>(categories["broken"]);
        Assert.Equal("Broken", broken.Label);
        Assert.Equal(0, broken.Total);
        Assert.Empty(broken.Items);
        Assert.False(broken.TotalCapped);
        Assert.NotNull(broken.Error);
        Assert.Contains(CorrelationId, broken.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", broken.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("exploded", broken.Error, StringComparison.Ordinal);

        var records = Assert.IsType<ContributedSearchCategoryDto>(categories["records"]);
        Assert.Null(records.Error);
        Assert.Equal(12, records.Total);
        Assert.True(records.TotalCapped);
        Assert.Equal("r1", Assert.Single(records.Items).Id);

        // The log names the category and the request, and its message is redacted like the error.
        var line = Assert.Single(logger.Messages);
        Assert.Contains("broken", line, StringComparison.Ordinal);
        Assert.Contains(CorrelationId, line, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_ARefusal_ShowsItsRedactedMessage()
    {
        var refusing = new FakeContributor("records", "Records",
            answer: (_, _) => throw new SqlFlowException("lookup refused for Password=hunter2;"));

        var categories = await CollectAsync([refusing]);

        var error = Assert.IsType<ContributedSearchCategoryDto>(categories["records"]).Error;
        Assert.NotNull(error);
        Assert.Contains("The Records search failed: lookup refused for Password=[redacted]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_AsksForThePreview_WithTheParsedQueryAndTheCaller()
    {
        var contributor = new FakeContributor("records");
        var user = SignedIn();

        await CollectAsync([contributor], user, phrase: "wells north");

        var request = Assert.Single(contributor.Requests);
        Assert.Equal("wells north", request.Phrase);
        Assert.Equal(["wells", "north"], request.Tokens);
        Assert.Equal(1, request.Page);
        Assert.Equal(5, request.PageSize);
        Assert.Same(user, request.User);
    }

    [Fact]
    public async Task Collect_LeavesOutACategoryTheCallerMayNotSee_WithoutAskingIt()
    {
        var restricted = new FakeContributor("audits", policy: "admin");

        var hidden = await CollectAsync([restricted], SignedIn());
        Assert.False(hidden.ContainsKey("audits"));
        Assert.Empty(restricted.Requests);

        var shown = await CollectAsync([restricted], SignedIn(new Claim("scope", "admin")));
        Assert.True(shown.ContainsKey("audits"));
        Assert.Single(restricted.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("negativeTotal")]
    [InlineData("tooManyHits")]
    [InlineData("blankId")]
    [InlineData("blankTitle")]
    [InlineData("protocolRelativeRoute")]
    [InlineData("scriptRoute")]
    [InlineData("ftpRoute")]
    public async Task Collect_AResultOutsideTheContract_IsTheCategorysError(string breach)
    {
        SearchContribution result = breach switch
        {
            "null" => null!,
            "negativeTotal" => new SearchContribution([], -1),
            "tooManyHits" => new SearchContribution(Enumerable.Range(1, 6).Select(i => Hit($"r{i}")).ToList(), 6),
            "blankId" => new SearchContribution([Hit(" ")], 1),
            "blankTitle" => new SearchContribution([Hit("r1") with { Title = "" }], 1),
            "protocolRelativeRoute" => new SearchContribution([Hit("r1") with { Route = "//evil.example/r1" }], 1),
            "scriptRoute" => new SearchContribution([Hit("r1") with { Route = "javascript:alert(1)" }], 1),
            "ftpRoute" => new SearchContribution([Hit("r1") with { Route = "ftp://files.example/r1" }], 1),
            _ => throw new ArgumentOutOfRangeException(nameof(breach), breach, "unknown breach"),
        };
        var contributor = new FakeContributor("records", "Records", answer: (_, _) => Task.FromResult(result));

        var category = Assert.IsType<ContributedSearchCategoryDto>((await CollectAsync([contributor]))["records"]);

        Assert.NotNull(category.Error);
        Assert.StartsWith("The Records search returned an invalid result:", category.Error, StringComparison.Ordinal);
        Assert.Empty(category.Items);
    }

    [Theory]
    [InlineData("/records/r1")]
    [InlineData("https://portal.example/records/r1")]
    [InlineData(null)]
    public async Task Collect_AcceptsAGuiRouteAnHttpLinkOrNoRoute(string? route)
    {
        var contributor = new FakeContributor("records",
            answer: (_, _) => Task.FromResult(new SearchContribution([Hit("r1") with { Route = route }], 1)));

        var category = Assert.IsType<ContributedSearchCategoryDto>((await CollectAsync([contributor]))["records"]);

        Assert.Null(category.Error);
        Assert.Equal(route, Assert.Single(category.Items).Route);
    }

    [Fact]
    public async Task Collect_CancellationOfTheRequest_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var contributor = new FakeContributor("records", answer: (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SearchContribution([], 0));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync([contributor], ct: cancelled.Token));
    }

    [Fact]
    public void CombinedResult_CarriesEachContributedCategoryAsATopLevelMember()
    {
        var dto = new AllSearchDto(
            "wells", ["wells"], 90,
            new SearchCategoryDto<ObjectHitDto>(0, []), new SearchCategoryDto<ColumnHitDto>(0, []),
            new SearchCategoryDto<DefinitionHitDto>(0, []), new SearchCategoryDto<FileHitDto>(0, []),
            new SearchCategoryDto<FlowHitDto>(0, []), new SearchCategoryDto<FlowColumnHitDto>(0, []),
            new SearchCategoryDto<StatementHitDto>(0, []), new SearchCategoryDto<SubscriberHitDto>(0, []))
        {
            Contributed = new Dictionary<string, object>
            {
                ["records"] = new ContributedSearchCategoryDto(
                    "Records", 1000, [Hit("r1") with { Data = new { sourceKey = "W-1" } }], TotalCapped: true, Error: null),
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonSerializerOptions.Web));
        var root = json.RootElement;

        var records = root.GetProperty("records");
        Assert.Equal("Records", records.GetProperty("label").GetString());
        Assert.Equal(1000, records.GetProperty("total").GetInt64());
        Assert.True(records.GetProperty("totalCapped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, records.GetProperty("error").ValueKind);
        var hit = Assert.Single(records.GetProperty("items").EnumerateArray());
        Assert.Equal("r1", hit.GetProperty("id").GetString());
        Assert.Equal("Well r1", hit.GetProperty("title").GetString());
        Assert.Equal("/records/r1", hit.GetProperty("route").GetString());
        Assert.Equal("W-1", hit.GetProperty("data").GetProperty("sourceKey").GetString());

        // The built-in categories keep their shape, and report an exact total.
        Assert.False(root.GetProperty("flows").GetProperty("totalCapped").GetBoolean());
        Assert.False(root.TryGetProperty("contributed", out _));
    }

    // ---- The paged endpoint, through the host ---------------------------------------------------------------------

    [Fact]
    public async Task PagedEndpoint_ServesThePageAskedFor_WithItsCappedTotal()
    {
        var contributor = new FakeContributor("records", "Records",
            answer: (_, _) => Task.FromResult(new SearchContribution([Hit("r4")], 500, TotalCapped: true)));
        using var factory = Host(contributor);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, ["read"]);

        using var response = await GetAsync(client, token, "/api/v1/search/categories/records?q=wells%20north&page=3&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<SearchHitDto>>();
        Assert.NotNull(page);
        Assert.Equal(3, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(500, page.Total);
        Assert.True(page.TotalCapped);
        Assert.Equal("r4", Assert.Single(page.Items).Id);

        var request = Assert.Single(contributor.Requests);
        Assert.Equal("wells north", request.Phrase);
        Assert.Equal(["wells", "north"], request.Tokens);
        Assert.Equal((3, 10), (request.Page, request.PageSize));
        Assert.True(request.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task PagedEndpoint_AnUnknownCategory_Is404()
    {
        using var factory = Host(new FakeContributor("records"));
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, ["read"]);

        using var response = await GetAsync(client, token, "/api/v1/search/categories/nothing?q=wells");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PagedEndpoint_ACategoryOutsideTheCallersPolicy_Is403_WithoutAskingIt()
    {
        var restricted = new FakeContributor("audits", policy: "admin");
        using var factory = Host(restricted);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, ["read"]);

        using var response = await GetAsync(client, token, "/api/v1/search/categories/audits?q=wells");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(restricted.Requests);
    }

    [Fact]
    public async Task PagedEndpoint_AFailingContributor_Is500_WithTheRedactedError()
    {
        var refusing = new FakeContributor("records", "Records",
            answer: (_, _) => throw new SqlFlowException("lookup refused for Password=hunter2;"));
        using var factory = Host(refusing);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, ["read"]);

        using var response = await GetAsync(client, token, "/api/v1/search/categories/records?q=wells");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Password=[redacted]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagedEndpoint_ABlankQuery_Is400()
    {
        var contributor = new FakeContributor("records");
        using var factory = Host(contributor);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client, ["read"]);

        using var response = await GetAsync(client, token, "/api/v1/search/categories/records?q=%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(contributor.Requests);
    }

    [Fact]
    public async Task PagedEndpoint_WithoutAToken_Is401()
    {
        using var factory = Host(new FakeContributor("records"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/search/categories/records?q=wells", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Startup_RefusesAConflictingRegistration_NamingTheKey()
    {
        using var factory = Host(new FakeContributor("flows"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("the key 'flows'", ex.ToString(), StringComparison.Ordinal);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static SearchHitDto Hit(string id) => new(id, $"Well {id}", "wellbore", $"/records/{id}");

    private static ClaimsPrincipal SignedIn(params Claim[] claims)
        => new(new ClaimsIdentity([new Claim("sub", "alice"), .. claims], "test"));

    private static ServiceProvider AuthorizationServices()
        => new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddPolicy("admin", policy => policy.RequireClaim("scope", "admin")))
            .BuildServiceProvider();

    private static async Task ValidateAsync(params ISearchContributor[] contributors)
    {
        using var services = AuthorizationServices();
        await SearchContributors.ValidateAsync(contributors, services.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private static async Task<Dictionary<string, object>> CollectAsync(
        IEnumerable<ISearchContributor> contributors, ClaimsPrincipal? user = null, ILogger? logger = null,
        string phrase = "wells", CancellationToken ct = default)
    {
        using var services = AuthorizationServices();
        return await SearchContributors.CollectAsync(
            contributors, phrase, phrase.Split(' '), 5, user ?? SignedIn(),
            services.GetRequiredService<IAuthorizationService>(), CorrelationId, logger ?? NullLogger.Instance, ct);
    }

    private static ControlPlaneAppFactory Host(params ISearchContributor[] contributors)
        => new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(services =>
            {
                foreach (var contributor in contributors)
                {
                    services.AddSingleton(contributor);
                }
            });

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>A contributor that records what it is asked and answers with <c>answer</c>, or with no hits.</summary>
    // ---- The result contract, as a contributor's own tests may assert it ---------------------------------------------

    [Fact]
    public void ContractViolation_PassesAWellFormedPage()
    {
        var page = new SearchContribution(
            [
                new SearchHitDto("1", "First", "a subtitle", "/records/1"),
                new SearchHitDto("2", "Second", null, "https://example.org/records/2"),
                new SearchHitDto("3", "Third", null, null),
            ],
            Total: 3);

        Assert.Null(SearchContributors.ContractViolation(page, pageSize: 5));
    }

    [Fact]
    public void ContractViolation_NamesWhatIsWrongWithTheAnswerItself()
    {
        Assert.Equal("no result.", SearchContributors.ContractViolation(null, 5));
        Assert.Equal("no hit list.", SearchContributors.ContractViolation(new SearchContribution(null!, 0), 5));
        Assert.Equal("a negative total (-1).", SearchContributors.ContractViolation(new SearchContribution([], -1), 5));

        var overflowing = new SearchContribution([Hit("1"), Hit("2"), Hit("3")], 3);
        Assert.Equal("3 hits for a page of 2.", SearchContributors.ContractViolation(overflowing, pageSize: 2));
    }

    [Theory]
    [InlineData("", "Title")]
    [InlineData("  ", "Title")]
    [InlineData("1", "")]
    [InlineData("1", "   ")]
    public void ContractViolation_RefusesAHitWithoutAnIdOrATitle(string id, string title)
    {
        var page = new SearchContribution([new SearchHitDto(id, title, null, "/records/1")], 1);

        Assert.Equal("hit 1 has no id or no title.", SearchContributors.ContractViolation(page, 5));
    }

    [Theory]
    [InlineData("//evil.example/records/1")]   // protocol relative: leaves the GUI's origin
    [InlineData("/records\\1")]                 // a backslash is not a GUI path separator
    [InlineData("ftp://example.org/records/1")]
    [InlineData("records/1")]                   // relative: the GUI cannot open it
    public void ContractViolation_RefusesARouteTheGuiCannotOpen(string route)
    {
        var page = new SearchContribution([new SearchHitDto("1", "First", null, route)], 1);

        Assert.Equal(
            "hit 1 has a route that is neither an absolute GUI path nor an http(s) link.",
            SearchContributors.ContractViolation(page, 5));
    }

    [Fact]
    public void ContractViolation_ReportsTheFirstOffendingHitByItsPosition()
    {
        var page = new SearchContribution([Hit("1"), new SearchHitDto("2", " ", null, "/records/2"), Hit("3")], 3);

        Assert.Equal("hit 2 has no id or no title.", SearchContributors.ContractViolation(page, 5));
    }

    private sealed class FakeContributor(
        string key,
        string label = "Things",
        string? policy = null,
        Func<SearchContributionRequest, CancellationToken, Task<SearchContribution>>? answer = null) : ISearchContributor
    {
        public List<SearchContributionRequest> Requests { get; } = [];

        public string Key => key;

        public string Label => label;

        public string? RequiredPolicy => policy;

        public Task<SearchContribution> SearchAsync(SearchContributionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return answer is null ? Task.FromResult(new SearchContribution([], 0)) : answer(request, ct);
        }
    }

    /// <summary>Keeps every formatted log message, so a test can read what a failure logged.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
