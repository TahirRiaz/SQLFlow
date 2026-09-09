using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Profiles raw columns server-side with one set-based pass over a shared sample, using SQL Server's own
/// <c>TRY_CONVERT</c> as the oracle. Counts how many non-null values convert to each candidate type,
/// so the inferencer can only ever pick a type SQL Server can actually produce. Date and numeric
/// candidates are probed in the resolved locale's preferred order (and numbers are normalized through
/// <see cref="LocaleConversion"/>), so the counts the inferencer chooses from are locale-consistent.
/// Every column's aggregates run against the same <c>TOP(sample)</c> scan instead of one scan per column;
/// a table too wide for a single SELECT list is profiled in batches over one materialized sample, so all
/// batches see identical rows.
/// </summary>
public sealed class SqlServerColumnProfiler : IColumnProfiler
{
    // SQL Server caps a SELECT list at 4096 expressions; this keeps each aggregate batch comfortably
    // under that limit while still packing many columns into a single scan.
    private const int MaxAggregatesPerQuery = 512;

    // Materializing the shared sample stores one nvarchar(4000) column per profiled column, and a
    // temp table allows at most 1024 non-sparse columns.
    private const int MaxSampleTableColumns = 1024;

    private const string SampleTableName = "#SqlFlowColumnProfileSample";

    public async Task<IReadOnlyList<ColumnProfile>> ProfileAsync(
        string connectionString,
        string schema,
        string table,
        IReadOnlyList<string> columns,
        TypeInferencePolicy policy,
        ServerLocale locale,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(locale);
        if (columns.Count == 0)
        {
            return [];
        }

        // The locale-preferred order the inferencer will scan; identical for every column in the run.
        var dateTimeStyles = LocaleConversion.DateTimeStyleCandidates(locale.DateOrder);
        var dateStyles = LocaleConversion.DateStyleCandidates(locale.DateOrder);
        // Probe the invariant convention too only when it differs from the locale one (i.e. ','-decimal).
        var numericFormats = locale.DecimalSeparator == ','
            ? new[] { NumericFormat.Locale, NumericFormat.Invariant }
            : [NumericFormat.Locale];

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var qualified = $"[{Escape(schema)}].[{Escape(table)}]";
        var top = policy.SampleSize > 0 ? $"TOP ({policy.SampleSize.ToString(CultureInfo.InvariantCulture)}) " : string.Empty;

        // Per-column aggregate count (the shared COUNT_BIG(*) Total is emitted once per query, not per column).
        var perColumn = 8 + dateTimeStyles.Count + dateStyles.Count + 4 * numericFormats.Length;
        var columnsPerBatch = Math.Max(1, (MaxAggregatesPerQuery - 1) / perColumn);
        var batches = new List<List<(string Name, string Key)>>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (i % columnsPerBatch == 0)
            {
                batches.Add([]);
            }

            batches[^1].Add((columns[i], $"c{i.ToString(CultureInfo.InvariantCulture)}"));
        }

        // One source scan instead of one per column: a single batch samples inline; several batches over a
        // bounded sample materialize it once so every batch profiles identical rows. An unbounded profile
        // (SampleSize <= 0) reads the whole table per batch, which is exact by definition, and a table too
        // wide to materialize falls back to independent per-batch samples (the pre-existing exposure of the
        // per-column queries, which each sampled independently).
        var materialize = batches.Count > 1 && policy.SampleSize > 0 && columns.Count <= MaxSampleTableColumns;
        if (materialize)
        {
            var projections = string.Join(",\n           ", columns.Select((name, i) => Projection(name, $"c{i.ToString(CultureInfo.InvariantCulture)}")));
            var materializeSql = $"SELECT {top}{projections}\nINTO {SampleTableName}\nFROM {qualified};";
            await using var materializeCommand = new SqlCommand(materializeSql, connection) { CommandTimeout = 0 };
            await materializeCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var profiles = new List<ColumnProfile>(columns.Count);
        foreach (var batch in batches)
        {
            var sql = BuildBatchSql(qualified, top, batch, materialize, dateTimeStyles, dateStyles, numericFormats, locale);

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                continue;
            }

            long Long(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
            int Int(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
            long? NullableLong(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);

            var total = Long("Total");
            foreach (var (columnName, key) in batch)
            {
                profiles.Add(new ColumnProfile
                {
                    ColumnName = columnName,
                    Total = total,
                    NonNull = Long($"{key}_NonNull"),
                    AsBigInt = Long($"{key}_AsBigInt"),
                    AsGuid = Long($"{key}_AsGuid"),
                    AsBitTokens = Long($"{key}_AsBitTokens"),
                    LeadingZeroInts = Long($"{key}_LeadingZeroInts"),
                    MaxLen = Int($"{key}_MaxLen"),
                    MinValue = NullableLong($"{key}_MinVal"),
                    MaxValue = NullableLong($"{key}_MaxVal"),
                    DateTimeCandidates = dateTimeStyles
                        .Select(s => new DateConversionCandidate { Style = s, Count = Long($"{key}_T{s.ToString(CultureInfo.InvariantCulture)}") })
                        .ToList(),
                    DateCandidates = dateStyles
                        .Select(s => new DateConversionCandidate { Style = s, Count = Long($"{key}_D{s.ToString(CultureInfo.InvariantCulture)}") })
                        .ToList(),
                    NumericCandidates = numericFormats
                        .Select(f => new NumericConversionCandidate
                        {
                            Format = f,
                            AsDecimal = Long($"{key}_Dec{f}"),
                            AsFloat = Long($"{key}_Flt{f}"),
                            MaxScale = Int($"{key}_Scl{f}"),
                            MaxIntegerDigits = Int($"{key}_Int{f}"),
                        })
                        .ToList(),
                });
            }
        }

        if (materialize)
        {
            // Session-scoped, so connection disposal would reclaim it anyway; dropping eagerly keeps the
            // session tidy while the connection is still open.
            await using var drop = new SqlCommand($"DROP TABLE {SampleTableName};", connection) { CommandTimeout = 0 };
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return profiles;
    }

    private static string BuildBatchSql(
        string qualified,
        string top,
        IReadOnlyList<(string Name, string Key)> batch,
        bool materialized,
        IReadOnlyList<int> dateTimeStyles,
        IReadOnlyList<int> dateStyles,
        IReadOnlyList<NumericFormat> numericFormats,
        ServerLocale locale)
    {
        var aggregates = new List<string> { "COUNT_BIG(*) AS Total" };
        foreach (var (_, key) in batch)
        {
            AppendColumnAggregates(aggregates, key, dateTimeStyles, dateStyles, numericFormats, locale);
        }

        var select = string.Join(",\n    ", aggregates);
        if (materialized)
        {
            return $"""
                SELECT
                    {select}
                FROM {SampleTableName};
                """;
        }

        var projections = string.Join(",\n           ", batch.Select(c => Projection(c.Name, c.Key)));
        return $"""
            WITH v AS (
                SELECT {top}{projections}
                FROM {qualified}
            )
            SELECT
                {select}
            FROM v;
            """;
    }

    private static void AppendColumnAggregates(
        List<string> aggregates,
        string key,
        IReadOnlyList<int> dateTimeStyles,
        IReadOnlyList<int> dateStyles,
        IReadOnlyList<NumericFormat> numericFormats,
        ServerLocale locale)
    {
        var x = $"[{key}]";
        aggregates.Add($"COUNT_BIG({x})                                                        AS [{key}_NonNull]");
        aggregates.Add($"COUNT_BIG(TRY_CONVERT(bigint, {x}))                                   AS [{key}_AsBigInt]");
        aggregates.Add($"COUNT_BIG(TRY_CONVERT(uniqueidentifier, {x}))                         AS [{key}_AsGuid]");
        aggregates.Add($"COUNT_BIG(CASE WHEN LOWER({x}) IN ('0','1','true','false','yes','no','y','n') THEN 1 END) AS [{key}_AsBitTokens]");
        aggregates.Add($"COUNT_BIG(CASE WHEN {x} LIKE '0%' AND LEN({x}) > 1 AND TRY_CONVERT(bigint, {x}) IS NOT NULL THEN 1 END) AS [{key}_LeadingZeroInts]");
        aggregates.Add($"ISNULL(MAX(LEN({x})), 0)                                              AS [{key}_MaxLen]");
        aggregates.Add($"MIN(TRY_CONVERT(bigint, {x}))                                         AS [{key}_MinVal]");
        aggregates.Add($"MAX(TRY_CONVERT(bigint, {x}))                                         AS [{key}_MaxVal]");

        // A genuine date carries a 4-digit year. This guard rejects short hyphen/slash codes such as
        // "11-800" that SQL Server's lenient parser would otherwise silently accept as a date.
        foreach (var style in dateTimeStyles)
        {
            var s = style.ToString(CultureInfo.InvariantCulture);
            aggregates.Add($"COUNT_BIG(CASE WHEN {DateShapeGuard(x)} AND TRY_CONVERT(datetime2, {x}, {s}) IS NOT NULL THEN 1 END) AS [{key}_T{s}]");
        }

        foreach (var style in dateStyles)
        {
            var s = style.ToString(CultureInfo.InvariantCulture);
            aggregates.Add($"COUNT_BIG(CASE WHEN {DateShapeGuard(x)} AND TRY_CONVERT(date, {x}, {s}) IS NOT NULL THEN 1 END) AS [{key}_D{s}]");
        }

        foreach (var format in numericFormats)
        {
            var n = LocaleConversion.NumericInput(x, format, locale);
            aggregates.Add($"COUNT_BIG(TRY_CONVERT(decimal(38,10), {n})) AS [{key}_Dec{format}]");
            aggregates.Add($"COUNT_BIG(TRY_CONVERT(float, {n})) AS [{key}_Flt{format}]");
            aggregates.Add(
                $"ISNULL(MAX(CASE WHEN CHARINDEX('.', {n}) > 0 AND TRY_CONVERT(decimal(38,10), {n}) IS NOT NULL " +
                $"THEN LEN({n}) - CHARINDEX('.', {n}) ELSE 0 END), 0) AS [{key}_Scl{format}]");
            aggregates.Add(
                $"ISNULL(MAX(CASE WHEN TRY_CONVERT(decimal(38,10), {n}) IS NOT NULL " +
                $"THEN LEN(REPLACE(REPLACE(CASE WHEN CHARINDEX('.', {n}) > 0 THEN LEFT({n}, CHARINDEX('.', {n}) - 1) ELSE {n} END, '-', ''), '+', '')) " +
                $"ELSE 0 END), 0) AS [{key}_Int{format}]");
        }
    }

    // The shared normalization every aggregate reads: trimmed, empty-to-NULL nvarchar under a stable key alias.
    private static string Projection(string columnName, string key)
        => $"NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), [{Escape(columnName)}]))), '') AS [{key}]";

    // A date must contain four consecutive digits (a year); guards against short codes being read as dates.
    private static string DateShapeGuard(string x) => $"{x} LIKE '%[0-9][0-9][0-9][0-9]%'";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
