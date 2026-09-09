using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.Execution;

/// <summary>The canonical JSON serialization settings for run artifacts (run.json, batch.json, healthcheck.json,
/// scm.json) and the CLI's <c>--json</c> output: indented, camelCase, enums as names. One shared instance keeps
/// every artifact written by the run path and every JSON the CLI prints byte-for-byte consistent.</summary>
public static class ExecutionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
