using SqlFlow.Core;
using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// The run values a schedule declares under <c>values:</c>, as YAML writes them: each parameter a scalar, or a sequence of
/// scalars for a parameter every fire gives several values (<c>logSource: [COMPOSITE, WIRELINE]</c>). A sequence becomes
/// the text of a <see cref="RunValueList"/>, so a fire carries exactly what a trigger giving the same values does.
/// </summary>
internal static class YamlRunValues
{
    /// <summary>The declared values as run values, in declaration order; throws <see cref="SqlFlowException"/> naming a
    /// parameter whose value is neither a scalar nor a sequence of scalars.</summary>
    public static Dictionary<string, string> From(Dictionary<string, object?>? declared)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (declared is null)
        {
            return values;
        }

        foreach (var (name, value) in declared)
        {
            values[name] = value switch
            {
                string text => text,
                List<object?> items => RunValueList.Of(items.Select(item => item as string
                    ?? throw new SqlFlowException($"values.{name} lists something other than a value; each of its entries is a string."))),
                null => throw new SqlFlowException($"values.{name} has no value: give it a value, or a list of values."),
                _ => throw new SqlFlowException($"values.{name} must be a value, or a list of values."),
            };
        }

        return values;
    }
}
