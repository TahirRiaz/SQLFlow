using SqlFlow.Core;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The one rule for the values a run of a registered kind's flow takes (<see cref="RunValueResolution"/>): the value the run
/// or its schedule gives, else the document's default, else none, which refuses a required parameter; a name the document
/// does not declare refused unless the kind reads it; several values only where the parameter takes several; a value inside
/// a parameter's choices. What a preview explains and what a run resolves are the same answers.
/// </summary>
public sealed class RunValueResolutionTests
{
    private static readonly FlowRunInputs Inputs = new()
    {
        Parameters =
        [
            new DeclaredRunParameter { Name = "logSource", Required = true, TakesSeveral = true, Lookup = "the values of column log_source" },
            new DeclaredRunParameter { Name = "region", Default = "north", Choices = ["north", "south"] },
            new DeclaredRunParameter { Name = "label" },
        ],
        KindValues = [new KindRunValue("partition", "The partition a run acts in.")],
        SeveralValuesRule = "a parameter source.record.scope binds",
    };

    private static Dictionary<string, string> Given(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void Each_declared_parameter_takes_the_given_value_else_its_default_else_none_in_the_documents_order()
    {
        var explained = RunValueResolution.Explain(Inputs, Given(("logSource", "STAT_COMP")));

        Assert.Equal(
            [
                new ResolvedRunValue("logSource", "STAT_COMP", RunValueOrigin.Given, null),
                new ResolvedRunValue("region", "north", RunValueOrigin.Default, null),
                new ResolvedRunValue("label", null, RunValueOrigin.Unset, null),
            ],
            explained);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "STAT_COMP", ["region"] = "north" },
            RunValueResolution.Resolve(Inputs, Given(("logSource", "STAT_COMP")), "flows/logs.yaml"));
    }

    [Fact]
    public void A_required_parameter_without_a_value_or_a_default_refuses_the_run_naming_it()
    {
        var missing = Assert.Single(RunValueResolution.Explain(Inputs, null), v => v.Name == "logSource");

        Assert.Equal(RunValueOrigin.Missing, missing.Origin);
        Assert.True(missing.Refused);
        Assert.Equal("parameter 'logSource' is required. Supply it with --set logSource=value or the run's values.", missing.Problem);
        var refused = Assert.Throws<FlowValidationException>(() => RunValueResolution.Resolve(Inputs, null, "flows/logs.yaml"));
        Assert.Equal("flows/logs.yaml: parameter 'logSource' is required. Supply it with --set logSource=value or the run's values.", refused.Message);
    }

    [Fact]
    public void A_given_value_overrides_the_default_and_an_empty_text_is_a_value()
    {
        var explained = RunValueResolution.Explain(Inputs, Given(("logSource", "A"), ("region", "south"), ("label", string.Empty)));

        Assert.Equal(("south", RunValueOrigin.Given), (explained[1].Value, explained[1].Origin));
        Assert.Equal((string.Empty, RunValueOrigin.Given), (explained[2].Value, explained[2].Origin));
    }

    [Fact]
    public void A_name_the_document_does_not_declare_is_refused_unless_the_kind_reads_it_and_listed_after_the_declared_ones()
    {
        var explained = RunValueResolution.Explain(Inputs, Given(("logSource", "A"), ("zeta", "1"), ("partition", "dev"), ("alpha", "2")));

        Assert.Equal(["logSource", "region", "label", "partition", "alpha", "zeta"], explained.Select(v => v.Name));
        Assert.Equal(new ResolvedRunValue("partition", "dev", RunValueOrigin.Kind, null), explained[3]);
        Assert.Equal(new ResolvedRunValue("alpha", "2", RunValueOrigin.NotDeclared, "parameter 'alpha' is not declared under parameters."), explained[4]);
        Assert.Equal("parameter 'zeta' is not declared under parameters.", explained[5].Problem);

        // The kind's own values never reach the parameters a run resolves.
        Assert.DoesNotContain(
            "partition",
            RunValueResolution.Resolve(Inputs, Given(("logSource", "A"), ("partition", "dev")), "f").Keys);
        Assert.Equal(
            "f: parameter 'alpha' is not declared under parameters.",
            Assert.Throws<FlowValidationException>(() => RunValueResolution.Resolve(Inputs, Given(("logSource", "A"), ("alpha", "2")), "f")).Message);
    }

    [Fact]
    public void Several_values_are_taken_where_the_parameter_takes_several_and_refused_elsewhere_naming_those_that_do()
    {
        var several = RunValueList.Of(["STAT_COMP", "STAT_CPI"]);
        Assert.Null(RunValueResolution.Explain(Inputs, Given(("logSource", several)))[0].Problem);

        var refused = RunValueResolution.Explain(Inputs, Given(("logSource", "A"), ("label", RunValueList.Of(["x", "y"]))))[2];
        Assert.Equal(
            "parameter 'label' is given 2 values, and takes one: only a parameter source.record.scope binds (logSource) takes several.",
            refused.Problem);

        var none = new FlowRunInputs { Parameters = [new DeclaredRunParameter { Name = "label" }] };
        Assert.Equal(
            "parameter 'label' is given 2 values, and takes one.",
            RunValueResolution.Explain(none, Given(("label", RunValueList.Of(["x", "y"]))))[0].Problem);

        var unruled = new FlowRunInputs { Parameters = [new DeclaredRunParameter { Name = "a", TakesSeveral = true }, new DeclaredRunParameter { Name = "b" }] };
        Assert.Equal(
            "parameter 'b' is given 2 values, and takes one: only a parameter that takes several values (a) takes several.",
            RunValueResolution.Explain(unruled, Given(("b", RunValueList.Of(["x", "y"]))))[1].Problem);
    }

    [Fact]
    public void A_value_outside_the_parameters_choices_is_refused_naming_them_and_one_inside_is_taken()
    {
        Assert.Null(RunValueResolution.Explain(Inputs, Given(("logSource", "A"), ("region", "south")))[1].Problem);
        Assert.Equal(
            "parameter 'region' is given 'west', which is not one of its choices: north, south.",
            RunValueResolution.Explain(Inputs, Given(("logSource", "A"), ("region", "west")))[1].Problem);

        // Every value of a list is held to the choices, and the first outside them is named.
        var listed = new FlowRunInputs { Parameters = [new DeclaredRunParameter { Name = "r", TakesSeveral = true, Choices = ["a", "b"] }] };
        Assert.Null(RunValueResolution.Explain(listed, Given(("r", RunValueList.Of(["a", "b"]))))[0].Problem);
        Assert.Equal(
            "parameter 'r' is given 'c', which is not one of its choices: a, b.",
            RunValueResolution.Explain(listed, Given(("r", RunValueList.Of(["a", "c", "d"]))))[0].Problem);
    }

    [Fact]
    public void A_looked_up_parameter_takes_any_value_since_its_source_may_hold_another_by_the_time_a_run_reads_it()
    {
        Assert.Null(RunValueResolution.Explain(Inputs, Given(("logSource", "NOT_LISTED_ANYWHERE")))[0].Problem);
    }

    [Fact]
    public void A_flow_that_declares_nothing_takes_no_values_and_refuses_any_it_is_given_but_the_kinds_own()
    {
        Assert.Empty(RunValueResolution.Explain(FlowRunInputs.None, null));
        Assert.Empty(RunValueResolution.Resolve(FlowRunInputs.None, new Dictionary<string, string>(StringComparer.Ordinal), "f"));
        Assert.Equal(RunValueOrigin.NotDeclared, Assert.Single(RunValueResolution.Explain(FlowRunInputs.None, Given(("x", "1")))).Origin);
    }

    [Fact]
    public void Resolve_raises_the_first_problem_in_the_order_explain_lists_them()
    {
        var refused = Assert.Throws<FlowValidationException>(
            () => RunValueResolution.Resolve(Inputs, Given(("region", "west"), ("zz", "1")), "flows/logs.yaml"));

        Assert.Equal("flows/logs.yaml: parameter 'logSource' is required. Supply it with --set logSource=value or the run's values.", refused.Message);
        Assert.Throws<ArgumentException>(() => RunValueResolution.Resolve(Inputs, null, " "));
    }

    [Fact]
    public void A_list_value_crosses_json_as_an_array_and_reads_back_as_its_text_and_a_single_value_as_a_string()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var list = new ResolvedRunValue("logSource", RunValueList.Of(["A", "B"]), RunValueOrigin.Given, null);
        var json = System.Text.Json.JsonSerializer.Serialize(list, web);

        Assert.Equal("""{"name":"logSource","value":["A","B"],"origin":"Given","problem":null}""", json);
        Assert.Equal(list, System.Text.Json.JsonSerializer.Deserialize<ResolvedRunValue>(json, web));
        Assert.Equal(
            """{"name":"region","value":"north","origin":"Default","problem":null}""",
            System.Text.Json.JsonSerializer.Serialize(new ResolvedRunValue("region", "north", RunValueOrigin.Default, null), web));
        Assert.Equal(
            """{"name":"label","value":null,"origin":"Unset","problem":null}""",
            System.Text.Json.JsonSerializer.Serialize(new ResolvedRunValue("label", null, RunValueOrigin.Unset, null), web));
        Assert.Throws<System.Text.Json.JsonException>(
            () => System.Text.Json.JsonSerializer.Deserialize<ResolvedRunValue>("""{"name":"x","value":1,"origin":"Given"}""", web));
    }

    [Fact]
    public void A_declared_parameter_is_found_by_its_exact_name()
    {
        Assert.Equal("region", Inputs.Parameter("region")!.Name);
        Assert.Null(Inputs.Parameter("Region"));
        Assert.Null(FlowRunInputs.None.Parameter("region"));
    }

    [Fact]
    public void Every_operation_resolves_the_parameters_unless_the_kind_names_those_that_do()
    {
        Assert.True(Inputs.TakesParameters("verify"));
        Assert.True(Inputs.TakesParameters(null));

        var reading = Inputs with { ParameterOperations = ["deliver", "plan"] };
        Assert.True(reading.TakesParameters("deliver"));
        Assert.False(reading.TakesParameters("verify"));
        Assert.False(reading.TakesParameters("Deliver"));
        Assert.False(reading.TakesParameters(null));
    }
}
