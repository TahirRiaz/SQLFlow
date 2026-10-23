using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A run value holding several values (<see cref="RunValueList"/>): its text form, the shape rules a list is held to, how
/// the CLI's repeated <c>--set</c> makes one, how every JSON boundary carries it as an array (<see cref="RunValuesJsonConverter"/>),
/// how a schedule declares one as a YAML sequence, and how it crosses the node protocol. The kind is the test-only probe kind.
/// </summary>
public sealed class RunValueListTests
{
    private static YamlDocumentLoader Loader() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind()]);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Body([property: System.Text.Json.Serialization.JsonConverter(typeof(RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values);

    // ---- The text form ---------------------------------------------------------------------------------------------

    [Fact]
    public void OneValue_IsItsOwnText_AndSeveralAreTheirJsonArray()
    {
        Assert.Equal("north", RunValueList.Of(["north"]));
        Assert.Equal("""["north","south"]""", RunValueList.Of(["north", "south"]));
        Assert.Equal("""["Ærø","a \"b\""]""", RunValueList.Of(["Ærø", "a \"b\""]));
    }

    [Fact]
    public void Items_ReadsAListBack_AndAPlainValueAsItself()
    {
        Assert.Equal(["north", "south"], RunValueList.Items("""["north","south"]"""));
        Assert.Equal(["north"], RunValueList.Items("north"));
        Assert.Equal(["a,b"], RunValueList.Items("a,b"));
        Assert.Equal(["[draft]"], RunValueList.Items("[draft]"));
        Assert.Equal([string.Empty], RunValueList.Items(string.Empty));
    }

    [Theory]
    [InlineData("north", false)]
    [InlineData("[draft]", false)]
    [InlineData("[1,2]", false)]
    [InlineData("""["a",1]""", false)]
    [InlineData("""{"a":"b"}""", false)]
    [InlineData("""["a"]""", true)]
    [InlineData("""["a","b"]""", true)]
    [InlineData("[]", true)]
    public void IsList_IsTrueOnlyForAJsonArrayOfStrings(string value, bool expected)
        => Assert.Equal(expected, RunValueList.IsList(value));

    [Fact]
    public void OneValueThatReadsAsAList_StaysOneValue()
    {
        var text = RunValueList.Of(["""["a","b"]"""]);
        Assert.Equal(["""["a","b"]"""], RunValueList.Items(text));
    }

    // ---- Shape rules -----------------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedList_Validates()
        => new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(["north", "south", string.Empty]) } }.Validate();

    [Fact]
    public void AnEmptyList_IsRefused()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["region"] = "[]" } }.Validate());
        Assert.Contains("Parameter 'region' is given 0 values; a list holds 1 to 100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListOfMoreThanTheMostValues_IsRefused()
    {
        var items = Enumerable.Range(0, RunValueList.MaxItems + 1).Select(i => $"v{i}");
        var ex = Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(items) } }.Validate());
        Assert.Contains($"is given {RunValueList.MaxItems + 1} values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListLongerThanItsBound_IsRefused()
    {
        var items = Enumerable.Range(0, 20).Select(i => new string('x', RunParameters.MaxValueLength - 2) + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        var ex = Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(items) } }.Validate());
        Assert.Contains($"a list holds at most {RunValueList.MaxListLength}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListHoldingAValueWithAControlCharacter_OrTooLong_IsRefused()
    {
        var broken = Assert.Throws<SqlFlowException>(
            () => new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(["north", "line\nbreak"]) } }.Validate());
        Assert.Contains("Each value of parameter 'region'", broken.Message, StringComparison.Ordinal);
        Assert.Throws<SqlFlowException>(() => new RunParameters
        {
            Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(["north", new string('x', RunParameters.MaxValueLength + 1)]) },
        }.Validate());
    }

    [Fact]
    public void Describe_ShowsAListAsItsText()
    {
        var text = new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(["north", "south"]) } }.Describe();
        Assert.Equal("""region=["north","south"]""", text);
    }

    // ---- The CLI ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RepeatingSet_GivesTheNameAListInTheOrderWritten()
    {
        var values = RunParameters.ParseValues(["logSource=COMPOSITE", "region=north", "logSource=WIRELINE", "logSource=a,b"]);
        Assert.Equal(["COMPOSITE", "WIRELINE", "a,b"], RunValueList.Items(values["logSource"]));
        Assert.Equal("north", values["region"]);
    }

    // ---- JSON ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnArrayInTheJson_IsReadAsAList_AndWrittenBackAsAnArray()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"values":{"logSource":["COMPOSITE","WIRELINE"],"region":"north"}}""", Web)!;
        Assert.Equal(["COMPOSITE", "WIRELINE"], RunValueList.Items(body.Values!["logSource"]));
        Assert.Equal("north", body.Values["region"]);
        Assert.Equal("""{"values":{"logSource":["COMPOSITE","WIRELINE"],"region":"north"}}""", JsonSerializer.Serialize(body, Web));
    }

    [Fact]
    public void AnArrayOfOneValue_IsThatValue()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"values":{"logSource":["COMPOSITE"]}}""", Web)!;
        Assert.Equal("COMPOSITE", body.Values!["logSource"]);
        Assert.Equal("""{"values":{"logSource":"COMPOSITE"}}""", JsonSerializer.Serialize(body, Web));
    }

    [Theory]
    [InlineData("""{"values":{"logSource":5}}""", "must be a string or an array of strings")]
    [InlineData("""{"values":{"logSource":["a",5]}}""", "its array holds a Number")]
    [InlineData("""{"values":["a"]}""", "must be a JSON object")]
    public void AValueThatIsNeitherAStringNorAnArrayOfStrings_IsRefused(string json, string expected)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>(json, Web));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentValues_StayAbsent()
    {
        Assert.Null(JsonSerializer.Deserialize<Body>("""{"values":null}""", Web)!.Values);
        Assert.Equal("""{"values":null}""", JsonSerializer.Serialize(new Body(null), Web));
    }

    [Fact]
    public void AList_RoundTripsThroughTheStoredForm_AsAnArray()
    {
        var json = RunParameters.ValuesToJson(new Dictionary<string, string> { ["zeta"] = "z", ["alpha"] = RunValueList.Of(["a", "b"]) });
        Assert.Equal("""{"alpha":["a","b"],"zeta":"z"}""", json);
        var back = RunParameters.ValuesFromJson(json);
        Assert.Equal(["a", "b"], RunValueList.Items(back["alpha"]));
        Assert.Equal("z", back["zeta"]);
    }

    [Fact]
    public void AStoredFormFromBeforeLists_ReadsAsItAlwaysDid()
        => Assert.Equal(new Dictionary<string, string> { ["region"] = "north" }, RunParameters.ValuesFromJson("""{"region":"north"}"""));

    // ---- Schedules -------------------------------------------------------------------------------------------------

    private const string ProbeFlow = """
        flowType: probe
        name: wells
        source: s3://drops/wells/
        """;

    [Fact]
    public void AnInlineSchedule_GivesAParameterSeveralValues_AsASequence()
    {
        var document = Loader().Parse(ProbeFlow + """

            schedule:
              cron: "0 6 * * *"
              values:
                region: [north, south]
                tier: gold
            """, "flows/wells.yaml");

        Assert.Equal(["north", "south"], RunValueList.Items(document.Schedule!.Values["region"]));
        Assert.Equal("gold", document.Schedule.Values["tier"]);
    }

    [Fact]
    public void AnInlineSchedule_WithAMappingForAValue_IsRefused()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(ProbeFlow + """

            schedule:
              cron: "0 6 * * *"
              values:
                region:
                  name: north
            """, "flows/wells.yaml"));
        Assert.Contains("flows/wells.yaml: schedule: values.region must be a value, or a list of values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScheduleLibraryEntry_GivesAParameterSeveralValues_AndAnEmptyListIsIgnoredWithAWarning()
    {
        var library = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              nightly:
                cron: "0 2 * * *"
                values:
                  region:
                    - north
                    - south
              empty:
                cron: "0 3 * * *"
                values:
                  region: []
            """, "schedules.yaml");

        var nightly = Assert.Single(library.Schedules);
        Assert.Equal(["north", "south"], RunValueList.Items(nightly.Spec.Values["region"]));
        Assert.Contains(library.Warnings, w => w.Contains("schedule 'empty'", StringComparison.Ordinal)
            && w.Contains("is given 0 values", StringComparison.Ordinal));
    }

    // ---- The node protocol -----------------------------------------------------------------------------------------

    [Fact]
    public void ARunSpec_CarriesAListAcrossTheWire_AsAnArray()
    {
        var spec = new RunSpec(
            Guid.NewGuid(), Guid.NewGuid(), "wells", "repo", null, null, "flows/wells.yaml", null, null,
            new RunParameters { Values = new Dictionary<string, string> { ["region"] = RunValueList.Of(["north", "south"]) } },
            null, null, "alice");

        var wire = JsonSerializer.Serialize(spec, Web);
        Assert.Contains("""
            "values":{"region":["north","south"]}
            """.Trim(), wire, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<RunSpec>(wire, Web)!;
        Assert.Equal(["north", "south"], RunValueList.Items(back.Parameters.Values["region"]));
    }
}
