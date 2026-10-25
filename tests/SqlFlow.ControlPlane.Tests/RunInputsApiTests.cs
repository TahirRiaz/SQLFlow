using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a registered kind's flow declares a run may be given, through the control plane: the parameters, with the values its
/// document gives them, the choices it lists and the lookups its kind serves, and the run settings, on the pipeline's
/// parameters; a parameter's values for a run form, from its choices or the kind's lookup; and a schedule's preview of what
/// each member is given when it fires, read as a fire applies it and checked against what a fire then enqueues. The kind is
/// the test-only <c>params</c> kind, whose documents declare their parameters as a module's kinds do.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunInputsApiTests
{
    private const string Document = """
        flowType: params
        parameters:
          region: { required: true, choices: [north, south], description: The region the flow reads. }
          limit: { default: "10" }
          site: { lookup: the sites table }
          sources: { several: true }
        settings:
          maxRows: 50
        """;

    private static ControlPlaneAppFactory Host(string catalog, SitesLookup? lookup = null)
        => new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithCatalog(catalog)
            .WithServices(services =>
            {
                services.AddSingleton<IFlowDocumentKind, ParamsFlowKind>();
                services.AddSingleton<IRunParameterLookup>(lookup ?? new SitesLookup());
            });

    [SkippableFact]
    public async Task A_registered_kinds_flow_lists_what_its_document_declares_and_a_built_in_kinds_flow_none()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string flow = "params_" + suffix, broken = "broken_" + suffix, ing = "ing_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document), (broken, "params", "flowType: params\nbroken: true\n"), (ing, "ing", "flowType: ing\n"));
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            using var described = await GetJsonAsync(client, token, $"/api/v1/pipelines/{CatalogIdentity.Pipeline(repoId, flow)}/parameters");
            var inputs = described.RootElement.GetProperty("inputs");
            var parameters = inputs.GetProperty("parameters").EnumerateArray().ToList();
            Assert.Equal(["region", "limit", "site", "sources"], parameters.Select(p => p.GetProperty("name").GetString()));
            Assert.True(parameters[0].GetProperty("required").GetBoolean());
            Assert.Equal(["north", "south"], parameters[0].GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("The region the flow reads.", parameters[0].GetProperty("description").GetString());
            Assert.Equal("10", parameters[1].GetProperty("default").GetString());
            Assert.Equal("the sites table", parameters[2].GetProperty("lookup").GetString());
            Assert.True(parameters[3].GetProperty("takesSeveral").GetBoolean());
            var setting = Assert.Single(inputs.GetProperty("settings").EnumerateArray());
            Assert.Equal(("maxRows", "WholeNumber", "50"), (setting.GetProperty("key").GetString(), setting.GetProperty("input").GetString(), setting.GetProperty("documentValue").GetString()));
            Assert.Equal("partition", Assert.Single(inputs.GetProperty("kindValues").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, described.RootElement.GetProperty("inputsProblem").ValueKind);

            using var unread = await GetJsonAsync(client, token, $"/api/v1/pipelines/{CatalogIdentity.Pipeline(repoId, broken)}/parameters");
            Assert.Equal($"flows/{broken}.yaml: the document is broken.", unread.RootElement.GetProperty("inputsProblem").GetString());
            Assert.Empty(unread.RootElement.GetProperty("inputs").GetProperty("parameters").EnumerateArray());

            using var builtIn = await GetJsonAsync(client, token, $"/api/v1/pipelines/{CatalogIdentity.Pipeline(repoId, ing)}/parameters");
            Assert.Empty(builtIn.RootElement.GetProperty("inputs").GetProperty("parameters").EnumerateArray());
            Assert.NotEmpty(builtIn.RootElement.GetProperty("parameters").EnumerateArray());
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task A_parameters_values_are_its_choices_or_what_its_kind_looks_up_with_the_forms_values_and_nothing_else()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flow = "params_" + suffix;
        var lookup = new SitesLookup();

        await using var factory = Host(cs, lookup);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document));
            var id = CatalogIdentity.Pipeline(repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using (var choices = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/region/values", new { search = "NOR" }))
            {
                Assert.Equal(HttpStatusCode.OK, choices.Status);
                Assert.Equal(["north"], Values(choices.Body!));
                Assert.Equal("the choices the flow's document lists", choices.Body!.RootElement.GetProperty("source").GetString());
            }

            using (var sites = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/site/values", new { search = "an", values = new { partition = "dev" } }))
            {
                Assert.Equal(HttpStatusCode.OK, sites.Status);
                Assert.Equal(["stavanger"], Values(sites.Body!));
                Assert.Equal(42, sites.Body!.RootElement.GetProperty("values")[0].GetProperty("count").GetInt64());
                var asked = Assert.Single(lookup.Requests);
                Assert.Equal(("site", "an", "dev", flow), (asked.Parameter, asked.Search, asked.Values["partition"], asked.FlowName));
                Assert.Equal(Document, asked.Yaml);
                Assert.StartsWith("user:", asked.Actor, StringComparison.Ordinal);
            }

            using (var failed = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/site/values", new { search = "fail" }))
            {
                Assert.Equal(HttpStatusCode.BadGateway, failed.Status);
                Assert.Contains("The values of parameter 'site' could not be read: the sites table is not reachable with Server=sites;Password=[redacted];", failed.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("hunter2", failed.Text, StringComparison.Ordinal);
            }

            using (var free = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/limit/values", new { }))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, free.Status);
                Assert.Contains("lists no choices and its values are not looked up", free.Text, StringComparison.Ordinal);
            }

            using (var undeclared = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/nope/values", new { }))
            {
                Assert.Equal(HttpStatusCode.NotFound, undeclared.Status);
                Assert.Contains($"Flow '{flow}' declares no parameter 'nope'.", undeclared.Text, StringComparison.Ordinal);
            }

            using (var unknown = await PostJsonAsync(client, token, $"/api/v1/pipelines/{Guid.NewGuid()}/parameters/site/values", new { }))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
            }

            // A lookup reads the flow's own data, as a run does: it is the operate surface's, which a request without a
            // signed-in user never reaches.
            using var anonymous = await client.PostAsJsonAsync(new Uri($"/api/v1/pipelines/{id}/parameters/site/values", UriKind.Relative), new { });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task A_run_forms_inputs_are_explained_as_the_trigger_checks_them_and_nothing_is_queued()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flow = "params_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document));
            var id = CatalogIdentity.Pipeline(repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using (var fine = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new
                   {
                       operation = "send", values = new Dictionary<string, object> { ["region"] = "north", ["sources"] = new[] { "a", "b" } },
                       payload = new { maxRows = 5 },
                   }))
            {
                Assert.Equal(HttpStatusCode.OK, fine.Status);
                Assert.Empty(fine.Body!.RootElement.GetProperty("problems").EnumerateArray());
                var values = fine.Body.RootElement.GetProperty("values").EnumerateArray().ToList();
                Assert.Equal(["region", "limit", "site", "sources"], values.Select(v => v.GetProperty("name").GetString()));
                Assert.Equal(["Given", "Default", "Unset", "Given"], values.Select(v => v.GetProperty("origin").GetString()));
                Assert.Equal(["a", "b"], values[3].GetProperty("value").EnumerateArray().Select(v => v.GetString()));
            }

            using (var refused = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new
                   {
                       values = new Dictionary<string, object> { ["region"] = "west" }, payload = new { bad = true },
                   }))
            {
                Assert.Equal(
                    ["parameter 'region' is given 'west', which is not one of its choices: north, south.", "region 'west' is closed."],
                    refused.Body!.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
            }

            using (var missing = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new { payload = new { bad = true } }))
            {
                Assert.Equal(
                    ["parameter 'region' is required. Supply it with --set region=value or the run's values.", "payload bad is refused."],
                    missing.Body!.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
            }

            await using var db = CatalogDatabase.Create(cs);
            Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task An_operation_that_resolves_no_parameters_takes_its_values_as_given_and_requires_none()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flow = "params_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document + "\nparameterOperations: [send]\n"));
            var id = CatalogIdentity.Pipeline(repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // verify reads no rows the parameters scope: region, required for send, is asked of it neither when left out nor
            // when given a value send would refuse.
            using (var verify = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new { operation = "verify" }))
            {
                Assert.Empty(verify.Body!.RootElement.GetProperty("problems").EnumerateArray());
                Assert.Empty(verify.Body.RootElement.GetProperty("values").EnumerateArray());
            }

            using (var given = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new
                   {
                       operation = "verify", values = new Dictionary<string, object> { ["region"] = "east", ["partition"] = "dev" },
                   }))
            {
                Assert.Empty(given.Body!.RootElement.GetProperty("problems").EnumerateArray());
                var values = given.Body.RootElement.GetProperty("values").EnumerateArray().ToList();
                Assert.Equal(["partition", "region"], values.Select(v => v.GetProperty("name").GetString()));
                Assert.Equal(["Kind", "Given"], values.Select(v => v.GetProperty("origin").GetString()));
            }

            // Naming no operation runs the kind's default, send, which resolves them.
            using (var send = await PostJsonAsync(client, token, $"/api/v1/pipelines/{id}/parameters/explain", new { }))
            {
                Assert.Equal(
                    ["parameter 'region' is required. Supply it with --set region=value or the run's values."],
                    send.Body!.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task A_schedules_preview_shows_what_each_member_is_given_and_is_what_its_fire_then_enqueues()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string flow = "params_" + suffix, ing = "ing_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document), (ing, "ing", "flowType: ing\n"));
            var now = DateTime.UtcNow;
            Guid scheduleId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.StageYamlUpsertAsync(
                    db, repoId, "nightly_" + suffix, [flow, ing], "0 4 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now, operation: "verify",
                    values: new Dictionary<string, string> { ["region"] = "south", ["sources"] = RunValueList.Of(["a", "b"]), ["partition"] = "dev" });
                await db.SaveChangesAsync();
                scheduleId = (await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId)).Id;
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            using var preview = await GetJsonAsync(client, token, $"/api/v1/schedules/{scheduleId}/parameters");
            var root = preview.RootElement;
            Assert.Equal(("verify", "south"), (root.GetProperty("operation").GetString(), root.GetProperty("values").GetProperty("region").GetString()));
            Assert.Equal(["a", "b"], root.GetProperty("values").GetProperty("sources").EnumerateArray().Select(v => v.GetString()));
            var members = root.GetProperty("members").EnumerateArray().ToList();
            Assert.Equal([flow, ing], members.Select(m => m.GetProperty("flowName").GetString()));

            var member = members[0];
            Assert.True(member.GetProperty("takesScheduleArguments").GetBoolean());
            Assert.Equal("verify", member.GetProperty("operation").GetString());
            var values = member.GetProperty("values").EnumerateArray()
                .ToDictionary(v => v.GetProperty("name").GetString()!, v => (Value: v.GetProperty("value"), Origin: v.GetProperty("origin").GetString()));
            Assert.Equal(("south", "Given"), (values["region"].Value.GetString(), values["region"].Origin));
            Assert.Equal(("10", "Default"), (values["limit"].Value.GetString(), values["limit"].Origin));
            Assert.Equal("Unset", values["site"].Origin);
            Assert.Equal(["a", "b"], values["sources"].Value.EnumerateArray().Select(v => v.GetString()));
            Assert.Equal(("dev", "Kind"), (values["partition"].Value.GetString(), values["partition"].Origin));
            Assert.Empty(member.GetProperty("problems").EnumerateArray());

            // maxRows is a setting of the send operation, and this schedule runs verify: the member runs without it.
            Assert.Empty(member.GetProperty("settings").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, member.GetProperty("note").ValueKind);

            var builtIn = members[1];
            Assert.False(builtIn.GetProperty("takesScheduleArguments").GetBoolean());
            Assert.Equal("A built-in kind takes no operation or values: this flow runs as its document defines.", builtIn.GetProperty("note").GetString());
            Assert.Empty(builtIn.GetProperty("values").EnumerateArray());

            // The fire gives the registered kind's member the operation and values the preview showed, and the built-in one none.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId);
                await ScheduleFire.EnqueueAsync(db, new StoreDispatcher(), YamlDocumentLoader.CreateDefault([new ParamsFlowKind()]), schedule, now, default);
                var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
                var run = Assert.Single(runs, r => r.FlowName == flow);
                Assert.Equal("verify", run.Operation);
                Assert.Equal("south", RunParameters.ValuesFromJson(run.ValuesJson)["region"]);
                Assert.Null(Assert.Single(runs, r => r.FlowName == ing).ValuesJson);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task A_schedules_preview_names_what_would_refuse_each_member_and_why_one_runs_as_routed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flow = "params_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flow, "params", Document));
            var now = DateTime.UtcNow;
            Guid refusing, routed;
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.StageYamlUpsertAsync(
                    db, repoId, "refusing_" + suffix, [flow], "0 4 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now, operation: null,
                    values: new Dictionary<string, string> { ["region"] = "west", ["limit"] = RunValueList.Of(["1", "2"]), ["nope"] = "x" });
                await ScheduleStore.StageYamlUpsertAsync(
                    db, repoId, "routed_" + suffix, [flow], "0 5 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now, operation: "archive",
                    values: new Dictionary<string, string> { ["region"] = "north" });
                await db.SaveChangesAsync();
                refusing = (await db.Schedules.AsNoTracking().SingleAsync(s => s.Name == "refusing_" + suffix)).Id;
                routed = (await db.Schedules.AsNoTracking().SingleAsync(s => s.Name == "routed_" + suffix)).Id;
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            using (var preview = await GetJsonAsync(client, token, $"/api/v1/schedules/{refusing}/parameters"))
            {
                var member = Assert.Single(preview.RootElement.GetProperty("members").EnumerateArray());
                Assert.Equal("send", member.GetProperty("operation").GetString());
                Assert.Equal(
                    [
                        "parameter 'region' is given 'west', which is not one of its choices: north, south.",
                        "parameter 'limit' is given 2 values, and takes one: only a parameter that takes several values (sources) takes several.",
                        "parameter 'nope' is not declared under parameters.",
                        "region 'west' is closed.",
                    ],
                    member.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
            }

            // A kind without the schedule's operation runs its default, with its document's own values: a required one is missing.
            using (var preview = await GetJsonAsync(client, token, $"/api/v1/schedules/{routed}/parameters"))
            {
                var member = Assert.Single(preview.RootElement.GetProperty("members").EnumerateArray());
                Assert.False(member.GetProperty("takesScheduleArguments").GetBoolean());
                Assert.Equal("send", member.GetProperty("operation").GetString());
                Assert.Equal(
                    "Its kind has no operation 'archive': this flow runs its default operation (send) with its document's own values.",
                    member.GetProperty("note").GetString());
                Assert.Equal(
                    ["parameter 'region' is required. Supply it with --set region=value or the run's values."],
                    member.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));

                // It runs send, whose setting the document gives a value: the run takes it.
                var setting = Assert.Single(member.GetProperty("settings").EnumerateArray());
                Assert.Equal(("maxRows", "50"), (setting.GetProperty("key").GetString(), setting.GetProperty("documentValue").GetString()));
            }

            using var unknown = await GetAsync(client, token, $"/api/v1/schedules/{Guid.NewGuid()}/parameters");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private static IReadOnlyList<string?> Values(JsonDocument body)
        => body.RootElement.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()).ToList();

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("runinputs_" + suffix), suffix);
    }

    private static async Task SeedAsync(string cs, Guid repoId, params (string Name, string Kind, string Yaml)[] pipelines)
    {
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "runinputs_" + repoId.ToString("N")[..8],
            RemoteUrl = "https://git.invalid/runinputs.git",
            RootPath = Path.Combine(Path.GetTempPath(), "runinputs_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        var wave = 0;
        foreach (var (name, kind, yaml) in pipelines)
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, name),
                RepoId = repoId,
                Name = name,
                Kind = kind,
                RelativePath = $"flows/{name}.yaml",
                Yaml = yaml,
                Active = true,
                Wave = wave++,
                ExecutionMode = PipelineExecutionModes.Auto,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string token, string url)
    {
        using var response = await GetAsync(client, token, url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static async Task<Answer> PostJsonAsync<T>(HttpClient client, string token, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Answer(response.StatusCode, text, response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(text) : null);
    }

    private sealed record Answer(HttpStatusCode Status, string Text, JsonDocument? Body) : IDisposable
    {
        public void Dispose() => Body?.Dispose();
    }

    /// <summary>The <c>params</c> kind's lookup: the sites table, which a search for "fail" cannot reach (its error quotes a secret).</summary>
    private sealed class SitesLookup : IRunParameterLookup
    {
        private static readonly (string Site, long Count)[] Sites = [("oslo", 7), ("bergen", 12), ("stavanger", 42)];

        public ConcurrentQueue<RunParameterLookupRequest> Requests { get; } = new();

        public bool Serves(string flowKind) => flowKind == "params";

        public Task<RunParameterValues> LookUpAsync(RunParameterLookupRequest request, CancellationToken ct)
        {
            if (request.Search == "fail")
            {
                throw new SqlFlowException("the sites table is not reachable with Server=sites;Password=hunter2;Encrypt=True.");
            }

            Requests.Enqueue(request);
            var values = Sites
                .Where(s => request.Search is null || s.Site.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
                .Select(s => new RunParameterValue(s.Site, s.Count))
                .ToList();
            return Task.FromResult(new RunParameterValues(request.Parameter, "the sites table", values, More: false));
        }
    }

    /// <summary>
    /// A test-only kind whose documents declare parameters (<c>required</c>, <c>default</c>, <c>choices</c>, <c>lookup</c>,
    /// <c>several</c>) and one run setting, <c>maxRows</c>; a document with <c>broken: true</c> does not read, and a run
    /// giving region <c>west</c> is refused by the kind itself.
    /// </summary>
    private sealed class ParamsFlowKind : IFlowDocumentKind
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public string FlowType => "params";

        public string Description => "a test-only flow that declares parameters";

        public IReadOnlyList<FlowKindOperation> Operations { get; } =
        [
            new("send", "Send", "Sends the flow's records.", WritesTarget: true),
            new("verify", "Verify", "Reads the records back.", WritesTarget: false),
        ];

        public RegisteredFlowDocument Parse(string yaml, string source)
            => throw new FlowValidationException($"{source}: the params kind parses no documents for a run in these tests.");

        public void ValidateParameters(RunParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (parameters.Values.TryGetValue("region", out var region) && region == "west")
            {
                throw new SqlFlowException("region 'west' is closed.");
            }

            if (parameters.Payload is { } payload)
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("bad", out _))
                {
                    throw new SqlFlowException("payload bad is refused.");
                }
            }
        }

        public FlowRunInputs DescribeRun(string yaml, string source)
        {
            var body = Deserializer.Deserialize<Body>(yaml) ?? new Body();
            if (body.Broken)
            {
                throw new FlowValidationException($"{source}: the document is broken.");
            }

            return new FlowRunInputs
            {
                Parameters = (body.Parameters ?? []).Select(p => new DeclaredRunParameter
                {
                    Name = p.Key,
                    Required = p.Value.Required,
                    Default = p.Value.Default,
                    Description = p.Value.Description,
                    Choices = p.Value.Choices ?? [],
                    Lookup = p.Value.Lookup,
                    TakesSeveral = p.Value.Several,
                }).ToList(),
                Settings = body.Settings?.MaxRows is { } maxRows
                    ?
                    [
                        new RunSetting
                        {
                            Key = "maxRows", Label = "Rows at most", Input = RunSettingInput.WholeNumber, Help = "The most rows a run reads.",
                            DocumentKey = "settings.maxRows", DocumentValue = maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Minimum = 0, Maximum = 1000, Operations = ["send"],
                        },
                    ]
                    : [],
                KindValues = [new KindRunValue("partition", "The partition a run acts in.")],
                ParameterOperations = body.ParameterOperations ?? [],
            };
        }

        private sealed class Body
        {
            public Dictionary<string, ParameterBody>? Parameters { get; set; }

            public List<string>? ParameterOperations { get; set; }

            public SettingsBody? Settings { get; set; }

            public bool Broken { get; set; }
        }

        private sealed class ParameterBody
        {
            public bool Required { get; set; }

            public string? Default { get; set; }

            public string? Description { get; set; }

            public List<string>? Choices { get; set; }

            public string? Lookup { get; set; }

            public bool Several { get; set; }
        }

        private sealed class SettingsBody
        {
            public int? MaxRows { get; set; }
        }
    }

    /// <summary>A dispatcher that enqueues through the real store, without the hosted worker's signalling.</summary>
    private sealed class StoreDispatcher : IRunDispatcher
    {
        public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
            => (await RunQueueStore.EnqueueAsync(catalog, request, DateTime.UtcNow, ct)).RunId;

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueGroupAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
