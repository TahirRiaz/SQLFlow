using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Sanity-checks inferred conversions in a single set-based scan: for every converted column it counts
/// the non-null values and how many <c>TRY_CONVERT</c> would null out (silent data loss). Always uses
/// <c>TRY_CONVERT</c> so the check itself never errors, even when the chosen mode would have been the
/// failing <c>CONVERT</c>. No SMO; one connection, one query.
/// </summary>
public sealed class SqlServerInferenceValidator : IInferenceValidator
{
    public async Task<IReadOnlyList<ConversionCheckResult>> CheckAsync(
        string connectionString,
        string schema,
        string table,
        IReadOnlyList<ConversionCheck> checks,
        ServerLocale locale,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(locale);
        if (checks.Count == 0)
        {
            return [];
        }

        var qualified = $"[{Escape(schema)}].[{Escape(table)}]";
        var select = new StringBuilder("SELECT\n");

        for (var i = 0; i < checks.Count; i++)
        {
            var value = Normalized(checks[i].ColumnName);
            var tryConvert = TryConvert(checks[i], value, locale);

            // Two aggregates per column, addressed by ordinal aliases so odd column names are irrelevant.
            select.Append("    COUNT_BIG(").Append(value).Append(") AS c").Append(i).Append("_nn,\n");
            select.Append("    COUNT_BIG(CASE WHEN ").Append(value).Append(" IS NOT NULL AND ")
                  .Append(tryConvert).Append(" IS NULL THEN 1 END) AS c").Append(i).Append("_sn");
            select.Append(i < checks.Count - 1 ? ",\n" : "\n");
        }

        select.Append("FROM ").Append(qualified).Append(';');

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // Two aggregates per checked column over the full table: a scan whose duration scales with the target.
        await using var command = new SqlCommand(select.ToString(), connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var results = new List<ConversionCheckResult>(checks.Count);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < checks.Count; i++)
            {
                results.Add(new ConversionCheckResult
                {
                    ColumnName = checks[i].ColumnName,
                    NonNull = AsLong(reader[$"c{i}_nn"]),
                    SilentNulls = AsLong(reader[$"c{i}_sn"]),
                });
            }
        }

        return results;
    }

    // Mirror the profiler's value normalization so counts line up: trim, treat empty as null.
    private static string Normalized(string column)
        => $"NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), [{Escape(column)}]))), '')";

    private static string TryConvert(ConversionCheck check, string value, ServerLocale locale)
    {
        // Numeric columns are normalized the same way the transform will, so the validation counts the
        // same conversion the load performs (no false silent-null on locale-formatted numbers).
        var input = check.NumericFormat is { } format ? LocaleConversion.NumericInput(value, format, locale) : value;
        var style = check.Style is { } s ? $", {s.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        return $"TRY_CONVERT({check.DataType}, {input}{style})";
    }

    private static long AsLong(object value) => value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
