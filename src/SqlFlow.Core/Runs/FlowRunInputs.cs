using System.Globalization;
using System.Text.Json.Serialization;

namespace SqlFlow.Core.Runs;

/// <summary>
/// What a run of one flow of a registered kind may be given, as the flow's document says: the parameters it declares, each
/// with the value it takes when a run gives none and the values it may take; the settings of the document a run may change
/// for itself, each with the value the document gives it; and the run values the kind reads beside them (the partition a
/// run acts in, say). A client builds its run form from it, a schedule's preview explains its values against it
/// (<see cref="RunValueResolution"/>), and the kind resolves a run's values with the same rules, so what a client shows is
/// what a run takes. Built by <c>IFlowDocumentKind.DescribeRun</c>; a built-in kind describes none.
/// </summary>
public sealed record FlowRunInputs
{
    /// <summary>A flow that declares nothing a run may be given.</summary>
    public static FlowRunInputs None { get; } = new();

    /// <summary>The parameters the document declares, in its order.</summary>
    public IReadOnlyList<DeclaredRunParameter> Parameters { get; init; } = [];

    /// <summary>
    /// The operations whose runs resolve the declared parameters (those that read the rows the parameters scope, say);
    /// empty when every operation does. A run of another operation takes the values it is given as they
    /// are, and requires none.
    /// </summary>
    public IReadOnlyList<string> ParameterOperations { get; init; } = [];

    /// <summary>The settings of the document a run may change for itself, in the order a client offers them.</summary>
    public IReadOnlyList<RunSetting> Settings { get; init; } = [];

    /// <summary>The run values the kind reads beside the declared parameters, which a run or a schedule may give.</summary>
    public IReadOnlyList<KindRunValue> KindValues { get; init; } = [];

    /// <summary>
    /// Which parameters take several values, in the words a refusal names them by ("a parameter source.record.scope
    /// binds"); null when the kind gives no reason beyond the parameters' own <see cref="DeclaredRunParameter.TakesSeveral"/>.
    /// </summary>
    public string? SeveralValuesRule { get; init; }

    /// <summary>The declared parameter named <paramref name="name"/> (compared ordinally), or null.</summary>
    public DeclaredRunParameter? Parameter(string name)
        => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// True when a run of <paramref name="operation"/>, the operation the run performs (its kind's default when it names
    /// none), resolves the declared parameters (<see cref="ParameterOperations"/>).
    /// </summary>
    public bool TakesParameters(string? operation)
        => ParameterOperations.Count == 0 || (operation is not null && ParameterOperations.Contains(operation, StringComparer.Ordinal));
}

/// <summary>
/// One parameter a flow's document declares: whether a run has to give it, the value it takes when none is given, what it
/// is for, and the values it may take, from a fixed list the document writes (<see cref="Choices"/>) or looked up when a
/// client asks (<see cref="Lookup"/>).
/// </summary>
public sealed record DeclaredRunParameter
{
    /// <summary>The parameter's name, as <c>--set name=value</c>, a trigger's values and a schedule's values give it.</summary>
    public required string Name { get; init; }

    /// <summary>True when a run without a value for it is refused: it has no default either.</summary>
    public bool Required { get; init; }

    /// <summary>The value a run that gives none takes, as the document declares it; null when it declares none.</summary>
    [JsonConverter(typeof(RunValueJsonConverter))]
    public string? Default { get; init; }

    /// <summary>What the parameter is for, as the document says; null when it says nothing.</summary>
    public string? Description { get; init; }

    /// <summary>True when a run may give it several values (a <see cref="RunValueList"/>), which the kind applies together.</summary>
    public bool TakesSeveral { get; init; }

    /// <summary>
    /// The values it may take, as the document lists them, in its order: a run giving any other is refused. Empty when the
    /// document lists none.
    /// </summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// Where the kind looks the values it may take up when a client asks, in words an operator reads ("the values of column
    /// region of sales.dbo.Customers"); null when it looks none up. Looked-up values are offered, not enforced: the
    /// source may hold another by the time the run reads it.
    /// </summary>
    public string? Lookup { get; init; }
}

/// <summary>The input a client renders for a <see cref="RunSetting"/>, serialized by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunSettingInput
{
    /// <summary>A whole number between <see cref="RunSetting.Minimum"/> and <see cref="RunSetting.Maximum"/>, sent as a JSON number.</summary>
    WholeNumber,

    /// <summary>On or off, sent as a JSON boolean.</summary>
    Toggle,

    /// <summary>Free text, sent as a JSON string.</summary>
    Text,

    /// <summary>One of <see cref="RunSetting.Choices"/>, sent as a JSON string.</summary>
    Choice,
}

/// <summary>
/// One setting of a flow's document a run may change for itself (the most rows a run reads, say): the payload
/// property a run gives it in, the value the document gives it, and what a value may be. A run that leaves it out runs
/// with the document's value; a run that gives one runs with that, for itself alone.
/// </summary>
public sealed record RunSetting
{
    /// <summary>The run payload property a run gives the setting in.</summary>
    public required string Key { get; init; }

    /// <summary>A short label.</summary>
    public required string Label { get; init; }

    /// <summary>The input a client renders.</summary>
    public required RunSettingInput Input { get; init; }

    /// <summary>One or two sentences saying what a value does.</summary>
    public required string Help { get; init; }

    /// <summary>Where the document writes the setting (<c>source.record.maxRecords</c>), for an operator to find it; null for a setting no key writes.</summary>
    public string? DocumentKey { get; init; }

    /// <summary>The value the document gives the setting, as text; null when the document leaves it out.</summary>
    public string? DocumentValue { get; init; }

    /// <summary>What a run does when neither the document nor the run gives the setting a value, in words.</summary>
    public string? WhenUnset { get; init; }

    /// <summary>The smallest whole number a <see cref="RunSettingInput.WholeNumber"/> takes.</summary>
    public long? Minimum { get; init; }

    /// <summary>The largest whole number a <see cref="RunSettingInput.WholeNumber"/> takes.</summary>
    public long? Maximum { get; init; }

    /// <summary>The values a <see cref="RunSettingInput.Choice"/> takes.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The operations a run may change the setting on; empty for every operation of the kind.</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];
}

/// <summary>A run value a kind reads beside the parameters a document declares (<c>partition</c>, say), and what it does.</summary>
/// <param name="Name">The value's name in a run's values.</param>
/// <param name="Description">What the value does, and what a run without it does.</param>
public sealed record KindRunValue(string Name, string Description);

/// <summary>Where the value a run takes for a parameter comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunValueOrigin
{
    /// <summary>The run, or the schedule that fires it, gives it.</summary>
    Given,

    /// <summary>The document's default, since the run gives none.</summary>
    Default,

    /// <summary>Neither: the parameter is optional and the run takes no value for it.</summary>
    Unset,

    /// <summary>Neither, and the parameter is required: the run is refused.</summary>
    Missing,

    /// <summary>Given for a name the document does not declare: the run is refused.</summary>
    NotDeclared,

    /// <summary>A run value the kind reads beside the declared parameters (<see cref="FlowRunInputs.KindValues"/>).</summary>
    Kind,
}

/// <summary>What a run of a flow takes for one parameter, or one value it was given, and the problem when it is refused.</summary>
/// <param name="Name">The parameter, or the name the value was given under.</param>
/// <param name="Value">The value the run takes (a list as its <see cref="RunValueList"/> text); null when it takes none.</param>
/// <param name="Origin">Where the value comes from.</param>
/// <param name="Problem">Why a run with these values is refused, naming the parameter; null when nothing is wrong.</param>
public sealed record ResolvedRunValue(
    string Name, [property: JsonConverter(typeof(RunValueJsonConverter))] string? Value, RunValueOrigin Origin, string? Problem)
{
    /// <summary>True when a run with this value is refused.</summary>
    [JsonIgnore]
    public bool Refused => Problem is not null;
}

/// <summary>
/// The one rule for the values a run of a flow takes for the parameters its document declares (<see cref="FlowRunInputs"/>):
/// the value the run (or the schedule firing it) gives, else the document's default, else none, which refuses a run when the
/// parameter is required. A name the document does not declare is refused, unless the kind reads it beside them; several
/// values are refused for a parameter that takes one; a value outside a parameter's <see cref="DeclaredRunParameter.Choices"/>
/// is refused. A kind resolves every run's values through it, and a client explains a schedule's or a run form's values with
/// it, so the two never disagree.
/// </summary>
public static class RunValueResolution
{
    /// <summary>
    /// What a run given <paramref name="given"/> takes for every declared parameter, in the document's order, followed by
    /// each given value of a name the document does not declare (the kind's own values first, then the refused ones, in
    /// ordinal order).
    /// </summary>
    public static IReadOnlyList<ResolvedRunValue> Explain(FlowRunInputs inputs, IReadOnlyDictionary<string, string>? given)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var resolved = new List<ResolvedRunValue>(inputs.Parameters.Count);
        foreach (var parameter in inputs.Parameters)
        {
            if (given is not null && given.TryGetValue(parameter.Name, out var value))
            {
                resolved.Add(new ResolvedRunValue(parameter.Name, value, RunValueOrigin.Given, ProblemWith(inputs, parameter, value)));
            }
            else if (parameter.Default is { } fallback)
            {
                resolved.Add(new ResolvedRunValue(parameter.Name, fallback, RunValueOrigin.Default, null));
            }
            else if (parameter.Required)
            {
                resolved.Add(new ResolvedRunValue(
                    parameter.Name, null, RunValueOrigin.Missing,
                    $"parameter '{parameter.Name}' is required. Supply it with --set {parameter.Name}=value or the run's values."));
            }
            else
            {
                resolved.Add(new ResolvedRunValue(parameter.Name, null, RunValueOrigin.Unset, null));
            }
        }

        if (given is not null)
        {
            var undeclared = given.Keys.Where(name => inputs.Parameter(name) is null).ToList();
            foreach (var name in undeclared.Where(IsKindValue).Order(StringComparer.Ordinal))
            {
                resolved.Add(new ResolvedRunValue(name, given[name], RunValueOrigin.Kind, null));
            }

            foreach (var name in undeclared.Where(name => !IsKindValue(name)).Order(StringComparer.Ordinal))
            {
                resolved.Add(new ResolvedRunValue(name, given[name], RunValueOrigin.NotDeclared, $"parameter '{name}' is not declared under parameters."));
            }
        }

        return resolved;

        bool IsKindValue(string name) => inputs.KindValues.Any(k => string.Equals(k.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The values a run given <paramref name="given"/> takes, one per parameter that takes one (the kind's own values left
    /// out); throws <see cref="FlowValidationException"/> with the first problem, prefixed by <paramref name="where"/>, when
    /// the run is refused.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(FlowRunInputs inputs, IReadOnlyDictionary<string, string>? given, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in Explain(inputs, given))
        {
            if (value.Problem is { } problem)
            {
                throw new FlowValidationException($"{where}: {problem}");
            }

            if (value.Value is { } taken && value.Origin is RunValueOrigin.Given or RunValueOrigin.Default)
            {
                values[value.Name] = taken;
            }
        }

        return values;
    }

    /// <summary>What refuses <paramref name="value"/> for <paramref name="parameter"/>: several values it cannot take, or one outside its choices.</summary>
    private static string? ProblemWith(FlowRunInputs inputs, DeclaredRunParameter parameter, string value)
    {
        var items = RunValueList.Items(value);
        if (RunValueList.IsList(value) && !parameter.TakesSeveral)
        {
            var several = inputs.Parameters.Where(p => p.TakesSeveral).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
            var count = items.Count.ToString(CultureInfo.InvariantCulture);
            return several.Count == 0
                ? $"parameter '{parameter.Name}' is given {count} values, and takes one."
                : $"parameter '{parameter.Name}' is given {count} values, and takes one: only {inputs.SeveralValuesRule ?? "a parameter that takes several values"} ({string.Join(", ", several)}) takes several.";
        }

        if (parameter.Choices.Count > 0)
        {
            var outside = items.Where(item => !parameter.Choices.Contains(item, StringComparer.Ordinal)).ToList();
            if (outside.Count > 0)
            {
                return $"parameter '{parameter.Name}' is given '{outside[0]}', which is not one of its choices: {string.Join(", ", parameter.Choices)}.";
            }
        }

        return null;
    }
}
