using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The values the control plane supplies with a run for its <c>${env:NAME}</c> references: inside a scope they answer
/// before the process's own providers, a supplied reference is expanded by the providers alone (no chaining), a value that
/// is its own reference hands the name back, scopes nest and close, concurrent runs keep their own values, a scope says
/// once per name where each value came from, and the rules a run's values are held to refuse what no run may carry. The
/// providers here are fakes that echo the locator, so no test reads or writes the process environment.
/// </summary>
public sealed class SuppliedReferencesTests
{
    private static SecretResolver Resolver() => new([new EchoProvider("env"), new EchoProvider("vault")]);

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    // ---- Resolving inside a scope ---------------------------------------------------------------------------------

    [Fact]
    public void OutsideAScope_TheProvidersAnswer()
    {
        Assert.Empty(SuppliedReferences.Current);
        Assert.Equal("Server=env:WAREHOUSE", Resolver().Resolve("Server=${env:WAREHOUSE}"));
    }

    [Fact]
    public void InsideAScope_ASuppliedName_IsAnsweredBeforeTheEnvironment()
    {
        using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", "Server=sql;Database=dw")));

        Assert.Equal("Server=sql;Database=dw", Resolver().Resolve("${env:WAREHOUSE}"));
        Assert.Equal("env:OTHER", Resolver().Resolve("${env:OTHER}"));
    }

    [Fact]
    public async Task InsideAScope_ResolveAsync_AppliesTheSameRules()
    {
        using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", "${vault:kv/dw}"), ("URL", "${env:URL}")));

        Assert.Equal("a vault:kv/dw b env:URL c env:OTHER", await Resolver().ResolveAsync("a ${env:WAREHOUSE} b ${env:URL} c ${env:OTHER}"));
    }

    [Fact]
    public void ASuppliedReference_IsExpandedByTheProvidersAlone()
    {
        using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", "${vault:kv/dw-conn}")));

        Assert.Equal("vault:kv/dw-conn", Resolver().Resolve("${env:WAREHOUSE}"));
    }

    [Fact]
    public void SuppliedValues_DoNotChain()
    {
        using var scope = SuppliedReferences.Supply(Values(("A", "${env:B}"), ("B", "supplied-b")));

        // A supplied ${env:B} is read from the process environment, even though B is supplied too: no two values can loop.
        Assert.Equal("env:B", Resolver().Resolve("${env:A}"));
        Assert.Equal("supplied-b", Resolver().Resolve("${env:B}"));
    }

    [Fact]
    public void AValueThatIsItsOwnReference_HandsTheNameToTheEnvironment()
    {
        using var scope = SuppliedReferences.Supply(Values(("URL", "${env:URL}")));

        Assert.Equal("env:URL", Resolver().Resolve("${env:URL}"));
    }

    [Fact]
    public void OnlyEnvReferences_AreLookedUpInTheSuppliedValues()
    {
        using var scope = SuppliedReferences.Supply(Values(("dw", "never")));

        Assert.Equal("vault:dw", Resolver().Resolve("${vault:dw}"));
    }

    [Fact]
    public void ScopesNest_TheInnerOverTheOuter_AndClosingRestoresTheOuter()
    {
        using (SuppliedReferences.Supply(Values(("URL", "outer-url"), ("TAG", "outer-tag"))))
        {
            using (SuppliedReferences.Supply(Values(("URL", "inner-url"))))
            {
                Assert.Equal("inner-url outer-tag", Resolver().Resolve("${env:URL} ${env:TAG}"));
            }

            Assert.Equal("outer-url outer-tag", Resolver().Resolve("${env:URL} ${env:TAG}"));
        }

        Assert.Equal("env:URL", Resolver().Resolve("${env:URL}"));
        Assert.Empty(SuppliedReferences.Current);
    }

    [Fact]
    public async Task AScopeOfNoValues_AnswersNothing_ButStillSaysWhereEachValueCameFrom()
    {
        var reports = new List<SuppliedReferenceUse>();

        await Task.Run(() =>
        {
            SuppliedReferences.ReportTo(reports.Add);
            using (SuppliedReferences.Supply(Values(("URL", "outer-url"))))
            {
                using var empty = SuppliedReferences.Supply(new Dictionary<string, string>());
                Assert.Equal("outer-url env:TAG", Resolver().Resolve("${env:URL} ${env:TAG}"));
            }

            using (SuppliedReferences.Supply(new Dictionary<string, string>()))
            {
                Assert.Empty(SuppliedReferences.Current);
                Assert.Equal("env:PORT", Resolver().Resolve("${env:PORT}"));
            }
        });

        Assert.Equal(
            [new SuppliedReferenceUse("URL", true), new SuppliedReferenceUse("TAG", false), new SuppliedReferenceUse("PORT", false)],
            reports);
    }

    [Fact]
    public async Task ConcurrentRuns_EachResolveWithTheirOwnValues()
    {
        var resolver = Resolver();
        async Task<string> RunAsync(string value)
        {
            using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", value)));
            await Task.Delay(20);
            await Task.Yield();
            return await resolver.ResolveAsync("${env:WAREHOUSE}");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => RunAsync($"run-{i}"))));

        Assert.Equal(Enumerable.Range(0, 16).Select(i => $"run-{i}"), results);
        Assert.Empty(SuppliedReferences.Current);
    }

    // ---- Saying where each value came from ------------------------------------------------------------------------

    [Fact]
    public void AScope_ReportsEachNameOnce_WithWhereItsValueCameFrom()
    {
        var reports = new List<SuppliedReferenceUse>();
        using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", "dw"), ("URL", "${env:URL}")));
        SuppliedReferences.ReportTo(reports.Add);

        Resolver().Resolve("${env:WAREHOUSE} ${env:URL} ${env:OTHER}");
        Resolver().Resolve("${env:WAREHOUSE} ${env:OTHER}");

        Assert.Equal(
            [new SuppliedReferenceUse("WAREHOUSE", true), new SuppliedReferenceUse("URL", false, Deferred: true), new SuppliedReferenceUse("OTHER", false)],
            reports);
        Assert.Equal(
            [
                "Reference WAREHOUSE takes its value from the configuration the control plane supplied with this run.",
                "Reference URL takes its value from this node's environment: the supplied configuration hands it there.",
                "Reference OTHER takes its value from this node's environment.",
            ],
            reports.Select(r => r.Describe()));
    }

    [Fact]
    public void AScope_NamesAtMostTheBound_ThenSaysSoOnce()
    {
        var reports = new List<SuppliedReferenceUse>();
        using var scope = SuppliedReferences.Supply(Values(("WAREHOUSE", "dw")));
        SuppliedReferences.ReportTo(reports.Add);

        foreach (var i in Enumerable.Range(0, SuppliedReferences.MaxReported + 5))
        {
            Resolver().Resolve($"${{env:NAME_{i}}}");
        }

        Assert.Equal(SuppliedReferences.MaxReported + 1, reports.Count);
        Assert.True(reports[^1].IsOverflow);
        Assert.Equal($"More than {SuppliedReferences.MaxReported} references were resolved; the rest are not named.", reports[^1].Describe());
    }

    [Fact]
    public async Task TheReport_BelongsToTheRun_SoAScopeOpenedLaterReportsThroughIt_EachNameOnce()
    {
        var reports = new List<SuppliedReferenceUse>();

        // The run says where its values come from before it knows which scopes it opens: an outer one, or none at all and
        // then a narrower one.
        await Task.Run(() =>
        {
            SuppliedReferences.ReportTo(reports.Add);
            Resolver().Resolve("${env:URL}");
            using (SuppliedReferences.Supply(Values(("URL", "partition-url"))))
            {
                Resolver().Resolve("${env:URL} ${env:TAG}");
                using (SuppliedReferences.Supply(Values(("TAG", "narrow-tag"))))
                {
                    Resolver().Resolve("${env:URL} ${env:TAG}");
                }
            }
        });

        Assert.Equal([new SuppliedReferenceUse("URL", true), new SuppliedReferenceUse("TAG", false)], reports);
    }

    [Fact]
    public async Task TheReport_EndsWithTheCallThatSetIt()
    {
        var reports = new List<SuppliedReferenceUse>();

        await Task.Run(() => SuppliedReferences.ReportTo(reports.Add));
        using (SuppliedReferences.Supply(Values(("URL", "u"))))
        {
            Resolver().Resolve("${env:URL}");
        }

        Assert.Empty(reports);
    }

    [Fact]
    public void OutsideAScope_NothingIsReported()
    {
        var reports = new List<SuppliedReferenceUse>();
        SuppliedReferences.ReportTo(reports.Add);

        Resolver().Resolve("${env:WAREHOUSE}");

        Assert.Empty(reports);
    }

    // ---- What no run may carry ------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> Refused => new()
    {
        { "1BAD", "x", "which is not an environment variable name" },
        { "HAS-DASH", "x", "which is not an environment variable name" },
        { new string('N', SuppliedReferences.MaxNameLength + 1), "x", "which is not an environment variable name" },
        { "EMPTY", string.Empty, "a value that is empty" },
        { "LONG", new string('v', SuppliedReferences.MaxValueLength + 1), "longer than 1000 characters" },
        { "CONTROL", "line\nbreak", "holds a control character" },
        { "PLATFORM_CLIENT_SECRET", "s3cr3t", "PLATFORM_CLIENT_SECRET names a credential" },
        { "GIT_TOKEN", "Bearer abc", "GIT_TOKEN names a credential" },
        { "STORAGE_API_KEY", "abc", "STORAGE_API_KEY names a credential" },
        { "WAREHOUSE", "Server=sql;User ID=etl;Password=hunter2", "a connection string with a password, secret or key written out" },
        { "WAREHOUSE", "Server=sql;Pwd='hunter2'", "a connection string with a password, secret or key written out" },
        { "STORAGE", "DefaultEndpointsProtocol=https;AccountName=a;AccountKey=abc==", "a connection string with a password, secret or key written out" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Validate_RefusesWhatNoRunMayCarry(string name, string value, string because)
    {
        var ex = Assert.Throws<SqlFlowException>(() => SuppliedReferences.Validate(Values((name, value)), "the run's references"));

        Assert.StartsWith("The run's references", ex.Message, StringComparison.Ordinal);
        Assert.Contains(because, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RefusesMoreValuesThanARunCarries()
    {
        var many = Enumerable.Range(0, SuppliedReferences.MaxCount + 1).ToDictionary(i => $"NAME_{i}", i => "v", StringComparer.Ordinal);

        var ex = Assert.Throws<SqlFlowException>(() => SuppliedReferences.Validate(many, "the run's references"));

        Assert.Contains($"more than the {SuppliedReferences.MaxCount} a run carries", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Accepted => new()
    {
        { "WAREHOUSE", "Server=tcp:sql.example.net;Database=dw;Authentication=Active Directory Default" },
        { "WAREHOUSE", "Server=sql;User ID=etl;Password=${keyvault:kv/etl-password}" },
        { "PLATFORM_CLIENT_SECRET", "${keyvault:kv/platform-client-secret}" },
        { "GIT_TOKEN", " ${env:GIT_TOKEN} " },
        { "SOURCE_URL", "abfss://landing@account.dfs.core.windows.net/drops/" },
        { "PLATFORM_URL", "${env:PLATFORM_URL}" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Validate_AcceptsReferencesAndCredentialFreeValues(string name, string value)
        => SuppliedReferences.Validate(Values((name, value)), "the run's references");

    [Fact]
    public void Supply_ValidatesWhatItIsGiven()
    {
        var ex = Assert.Throws<SqlFlowException>(() => SuppliedReferences.Supply(Values(("TOKEN", "abc"))));

        Assert.Contains("TOKEN names a credential", ex.Message, StringComparison.Ordinal);
        Assert.Empty(SuppliedReferences.Current);
    }

    // ---- The stored form ------------------------------------------------------------------------------------------

    [Fact]
    public void TheStoredForm_IsOrderedByName_AndReadsBackEqual()
    {
        var values = Values(("URL", "https://x"), ("A_DB", "${vault:kv/a}"));

        var json = SuppliedReferences.ToJson(values);

        Assert.Equal("""{"A_DB":"${vault:kv/a}","URL":"https://x"}""", json);
        Assert.Equal(values, SuppliedReferences.FromJson(json));
    }

    [Fact]
    public void TheStoredForm_OfNothing_IsNull_AndNullReadsAsNothing()
    {
        Assert.Null(SuppliedReferences.ToJson(new Dictionary<string, string>()));
        Assert.Empty(SuppliedReferences.FromJson(null));
        Assert.Empty(SuppliedReferences.FromJson("  "));
    }

    [Fact]
    public void ACorruptStoredForm_IsReported()
    {
        var ex = Assert.Throws<SqlFlowException>(() => SuppliedReferences.FromJson("[1,2]"));

        Assert.StartsWith("The stored supplied references are not a JSON object of strings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesIn_ReadsTheEnvReferencesOfAValue_InOrder()
    {
        Assert.Equal(["A", "B_2"], SuppliedReferences.NamesIn("x ${env:A} ${vault:kv/s} ${env:B_2}"));
        Assert.Empty(SuppliedReferences.NamesIn("no references"));
        Assert.Empty(SuppliedReferences.NamesIn(null));
    }

    /// <summary>A provider that answers any locator with its scheme and the locator, reading nothing.</summary>
    private sealed class EchoProvider(string scheme) : ISecretProvider
    {
        public string Scheme => scheme;

        public string Resolve(string locator) => $"{scheme}:{locator}";
    }
}
