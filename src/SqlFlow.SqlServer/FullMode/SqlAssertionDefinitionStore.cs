using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>
/// The full-mode assertion registry: resolves assertion names to their definitions from flw.Assertion in a
/// single round-trip. Returns a case-insensitive dictionary (matching the control DB's CI collation and the
/// CSV split semantics); names with no active definition are simply omitted, so the log-only assertion runner
/// drops them exactly as the legacy INNER JOIN did.
/// </summary>
public sealed class SqlAssertionDefinitionStore : IAssertionDefinitionStore
{
    private static readonly IReadOnlyDictionary<string, AssertionDefinition> Empty =
        new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase);

    private readonly string _controlConnectionString;

    public SqlAssertionDefinitionStore(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public async Task<IReadOnlyDictionary<string, AssertionDefinition>> ResolveAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var requested = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
        {
            return Empty;
        }

        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(Query, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@names", System.Data.SqlDbType.NVarChar, -1) { Value = string.Join(",", requested) });

        var resolved = new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            resolved[name] = new AssertionDefinition { Name = name, Expression = reader.GetString(1) };
        }

        return resolved;
    }

    private const string Query =
        "SELECT [AssertionName], [AssertionExp] FROM [flw].[Assertion] " +
        "WHERE [IsActive] = 1 AND [AssertionName] IN (SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@names, ','));";
}
