using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The kind arguments a run of a registered flow kind carries (an operation, parameter values, a kind-owned JSON
/// payload): their shape rules, their stored form, the loader's one validation rule every trust boundary applies, how a
/// schedule declares them, and how they cross the node protocol. The kind is the test-only probe kind, which declares
/// the operations <c>load</c> and <c>check</c>.
/// </summary>
public sealed class RunKindArgumentsTests
{
    private static YamlDocumentLoader Loader() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind()]);

    private const string ProbeFlow = """
        flowType: probe
        name: wells
        source: s3://drops/wells/
        """;

    // ---- Shape -----------------------------------------------------------------------------------------------------

    [Fact]
    public void WellFormedKindArguments_Validate_AndAreNotDefault()
    {
        var parameters = new RunParameters
        {
            Operation = "check",
            Values = new Dictionary<string, string> { ["region"] = "north", ["empty"] = string.Empty },
            Payload = """{"scope":"records","keys":[1,2]}""",
        };

        parameters.Validate();
        Assert.True(parameters.HasKindArguments);
        Assert.False(parameters.IsDefault);
        Assert.False(RunParameters.None.HasKindArguments);
    }

    [Theory]
    [InlineData("Check")]
    [InlineData("1load")]
    [InlineData("load_all")]
    [InlineData("")]
    [InlineData("an-operation-name-well-over-thirty-two")]
    public void AnOperationThatIsNotAShortLowercaseName_IsRefused(string operation)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new RunParameters { Operation = operation }.Validate());
        Assert.Contains("operation must be", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("9starts")]
    [InlineData("dash-name")]
    public void AValueNameThatIsNotAnIdentifier_IsRefused(string name)
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { [name] = "x" } }.Validate());
        Assert.Contains($"Parameter name '{name}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueWithAControlCharacter_OrTooLong_IsRefused()
    {
        Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["a"] = "line\nbreak" } }.Validate());
        Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["a"] = new string('x', RunParameters.MaxValueLength + 1) } }.Validate());
    }

    [Fact]
    public void TooManyValues_AreRefused()
    {
        var values = Enumerable.Range(0, RunParameters.MaxValues + 1).ToDictionary(i => $"v{i}", _ => "x");
        var ex = Assert.Throws<SqlFlowException>(() => new RunParameters { Values = values }.Validate());
        Assert.Contains($"At most {RunParameters.MaxValues}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1,2]", "must be a JSON object")]
    [InlineData("\"text\"", "must be a JSON object")]
    [InlineData("{not json", "not valid JSON")]
    public void APayloadThatIsNotOneJsonObject_IsRefused(string payload, string expected)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new RunParameters { Payload = payload }.Validate());
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedPayload_IsRefused()
    {
        var payload = "{\"a\":\"" + new string('x', RunParameters.MaxPayloadLength) + "\"}";
        var ex = Assert.Throws<SqlFlowException>(() => new RunParameters { Payload = payload }.Validate());
        Assert.Contains($"at most {RunParameters.MaxPayloadLength}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NamesTheKindArguments()
    {
        var text = new RunParameters
        {
            Operation = "check",
            Values = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
            Payload = "{\"scope\":\"x\"}",
        }.Describe();

        Assert.Equal("operation check, a=1, b=2, payload (13 chars)", text);
    }

    // ---- Stored form -----------------------------------------------------------------------------------------------

    [Fact]
    public void Values_RoundTripThroughTheirStoredForm_OrderedByName()
    {
        var json = RunParameters.ValuesToJson(new Dictionary<string, string> { ["zeta"] = "z", ["alpha"] = "a=b" });
        Assert.Equal("""{"alpha":"a=b","zeta":"z"}""", json);
        Assert.Equal(new Dictionary<string, string> { ["alpha"] = "a=b", ["zeta"] = "z" }, RunParameters.ValuesFromJson(json));
    }

    [Fact]
    public void NoValues_AreStoredAsNull_AndBlankReadsAsNone()
    {
        Assert.Null(RunParameters.ValuesToJson(new Dictionary<string, string>()));
        Assert.Empty(RunParameters.ValuesFromJson(null));
        Assert.Empty(RunParameters.ValuesFromJson("  "));
    }

    [Fact]
    public void ACorruptStoredValueSet_IsReportedNotSwallowed()
    {
        var ex = Assert.Throws<SqlFlowException>(() => RunParameters.ValuesFromJson("[1]"));
        Assert.Contains("stored parameter values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseValues_ReadsNameValueAssignments_LastWins()
    {
        var values = RunParameters.ParseValues(["region=north", "filter=a=b", "region=south", "empty="]);
        Assert.Equal("south", values["region"]);
        Assert.Equal("a=b", values["filter"]);
        Assert.Equal(string.Empty, values["empty"]);
        Assert.Throws<SqlFlowException>(() => RunParameters.ParseValues(["novalue"]));
        Assert.Throws<SqlFlowException>(() => RunParameters.ParseValues(["=x"]));
    }

    // ---- The loader's one rule -------------------------------------------------------------------------------------

    [Fact]
    public void ValidateRunParameters_AcceptsDefaultsForAnyKind()
    {
        Loader().ValidateRunParameters("ing", RunParameters.None);
        Loader().ValidateRunParameters("probe", RunParameters.None);
    }

    [Fact]
    public void ValidateRunParameters_RefusesKindArgumentsForABuiltInKind()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => Loader().ValidateRunParameters("ing", new RunParameters { Operation = "load" }));
        Assert.Contains("apply to flows of a registered kind; 'ing' flows take none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRunParameters_RefusesAnOperationTheKindDoesNotDeclare_NamingTheDeclaredOnes()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => Loader().ValidateRunParameters("probe", new RunParameters { Operation = "purge" }));
        Assert.Contains("operation must be one of load, check for 'probe' flows; 'purge' is not", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRunParameters_SurfacesTheKindsOwnRefusal()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Loader().ValidateRunParameters(
            "probe", new RunParameters { Operation = "check", Payload = "{\"scope\":\"x\"}" }));
        Assert.Contains("a check takes no payload", ex.Message, StringComparison.Ordinal);

        Loader().ValidateRunParameters("PROBE", new RunParameters { Operation = "load", Payload = "{\"scope\":\"x\"}" });
    }

    [Fact]
    public void ValidateRunParameters_AppliesTheShapeRulesFirst()
    {
        Assert.Throws<SqlFlowException>(() => Loader().ValidateRunParameters("probe", new RunParameters { Payload = "[]" }));
    }

    // ---- Schedules -------------------------------------------------------------------------------------------------

    [Fact]
    public void AnInlineSchedule_CarriesItsOperationAndValues()
    {
        var document = Loader().Parse(ProbeFlow + """

            schedule:
              cron: "0 6 * * *"
              operation: check
              values:
                region: north
            """, "flows/wells.yaml");

        Assert.Equal("check", document.Schedule!.Operation);
        Assert.Equal("north", document.Schedule.Values["region"]);
    }

    [Fact]
    public void AnInlineSchedule_WithAnOperationTheKindDoesNotDeclare_IsRefused()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(ProbeFlow + """

            schedule:
              cron: "0 6 * * *"
              operation: purge
            """, "flows/wells.yaml"));
        Assert.Contains("flows/wells.yaml: schedule: operation must be one of load, check", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInFlowsSchedule_WithAnOperation_IsRefused()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            name: orders
            schedule:
              cron: "0 6 * * *"
              operation: load
            source:
              type: csv
              location: ./orders.csv
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: dbo
              table: Orders
            """, "flows/orders.yaml"));
        Assert.Contains("'file' flows take none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScheduleLibraryEntry_CarriesItsOperationAndValues_AndAnUnusableOneIsIgnoredWithAWarning()
    {
        var library = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              nightly:
                cron: "0 2 * * *"
                operation: load
                values:
                  region: north
              broken:
                cron: "0 3 * * *"
                operation: Not-An-Operation
            """, "schedules.yaml");

        var nightly = Assert.Single(library.Schedules);
        Assert.Equal("nightly", nightly.Name);
        Assert.Equal("load", nightly.Spec.Operation);
        Assert.Equal("north", nightly.Spec.Values["region"]);
        Assert.Contains(library.Warnings, w => w.Contains("schedule 'broken'", StringComparison.Ordinal)
            && w.Contains("The schedule is ignored", StringComparison.Ordinal));
    }

    // ---- The executor ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheExecutor_RefusesKindArgumentsForABuiltInDocument_BeforeRunningAnything()
    {
        using var provider = new ServiceCollection().AddSingleton(Loader()).BuildServiceProvider();
        var document = Loader().Parse("""
            name: orders
            source:
              type: csv
              location: ./orders.csv
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: dbo
              table: Orders
            """);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => new DocumentExecutor(provider).ExecuteAsync(
            document, "flows/orders.yaml",
            new DocumentExecutionOptions { Parameters = new RunParameters { Operation = "load" } }));
        Assert.Contains("'built-in' flows take none", ex.Message, StringComparison.Ordinal);
    }

    // ---- The node protocol -----------------------------------------------------------------------------------------

    [Fact]
    public void ARunSpec_CarriesTheKindArgumentsAndTheRequester_AcrossTheWire()
    {
        var spec = new RunSpec(
            Guid.NewGuid(), Guid.NewGuid(), "wells", "repo", null, null, "flows/wells.yaml", null, null,
            new RunParameters
            {
                Operation = "check",
                Values = new Dictionary<string, string> { ["region"] = "north" },
                Payload = "{\"scope\":\"x\"}",
            },
            null, null, "alice");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<RunSpec>(JsonSerializer.Serialize(spec, options), options)!;

        Assert.Equal("alice", back.RequestedBy);
        Assert.Equal("check", back.Parameters.Operation);
        Assert.Equal("north", back.Parameters.Values["region"]);
        Assert.Equal("{\"scope\":\"x\"}", back.Parameters.Payload);
    }
}
