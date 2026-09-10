using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// The WITH-database assertion runner: materializes each named assertion's SQL template by naive REPLACE of the
/// <c>@TableName</c> (the flow's two-part target) and <c>@FilterCriteria</c> (the flow's IncrementalClause)
/// macros, exactly as legacy, then runs each against the loaded TARGET and records the first row's first two
/// columns. Log-only and non-blocking: a per-assertion failure yields Evaluated=false / Result="0" with the
/// error and the remaining assertions still run; an assertion outcome never fails or rolls back the load.
/// </summary>
public sealed class AssertionRunner : IAssertionRunner
{
    private const int AssertionTimeoutSeconds = 3600;

    private readonly IAssertionDefinitionStore _store;

    public AssertionRunner(IAssertionDefinitionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<IReadOnlyList<AssertionResult>> RunAsync(
        IngestionFlow flow, string targetConnectionString, bool includeManual = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.Assertions.Count == 0)
        {
            return [];
        }

        var definitions = await _store.ResolveAsync(flow.Assertions, ct).ConfigureAwait(false);
        var tableName = SchemaQualified(flow.Target.Table);
        var filterCriteria = flow.Source.IncrementalClause ?? string.Empty;

        var results = new List<AssertionResult>();
        foreach (var name in flow.Assertions)
        {
            // Unknown names are dropped (the legacy INNER JOIN), preserving the original list order.
            if (!definitions.TryGetValue(name, out var definition))
            {
                continue;
            }

            // A manual-mode assertion is reserved for the on-demand assertions-only run; an automatic ingestion
            // run (includeManual: false) skips it entirely, recording no result for it.
            if (definition.Mode != ExecutionMode.Auto && !includeManual)
            {
                continue;
            }

            var sql = definition.Expression
                .Replace("@TableName", tableName, StringComparison.Ordinal)
                .Replace("@FilterCriteria", filterCriteria, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(sql))
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var (result, asserted) = await ExecuteScalarsAsync(targetConnectionString, sql, ct).ConfigureAwait(false);
                stopwatch.Stop();
                results.Add(new AssertionResult
                {
                    Name = name,
                    MaterializedSql = sql,
                    Result = result,
                    AssertedValue = asserted,
                    Evaluated = true,
                    Duration = stopwatch.Elapsed,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                results.Add(new AssertionResult
                {
                    Name = name,
                    MaterializedSql = sql,
                    Result = "0",
                    AssertedValue = string.Empty,
                    Evaluated = false,
                    Error = SecretHygiene.RedactedMessage(ex),
                    Duration = stopwatch.Elapsed,
                });
            }
        }

        return results;
    }

    private static async Task<(string Result, string Asserted)> ExecuteScalarsAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = AssertionTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (string.Empty, string.Empty);
        }

        var result = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false)
            ? string.Empty
            : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;

        var asserted = reader.FieldCount >= 2 && !await reader.IsDBNullAsync(1, ct).ConfigureAwait(false)
            ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

        return (result, asserted);
    }

    private static string SchemaQualified(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
