using System.Data;
using System.Globalization;
using SqlFlow.Core.Calendar;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Calendar;

/// <summary>
/// The physical shape of the generated dimension: one column per <see cref="CalendarRow"/> property, in the
/// same order, plus the two audit columns every SQLFlow target carries. This type is the single place the
/// column list, its SQL types and the row-to-DataTable projection are written down, so the CREATE, the staging
/// table and the MERGE can never drift apart.
/// </summary>
internal static class CalendarTable
{
    /// <summary>A generated column: its name, its SQL type, and how a row supplies its value.</summary>
    internal sealed record Column(string Name, string SqlType, Type ClrType, Func<CalendarRow, object?> Value);

    /// <summary>The dimension's business key.</summary>
    public const string KeyColumn = "PeriodID";

    public const string InsertedAudit = "InsertedDate_DW";

    public const string UpdatedAudit = "UpdatedDate_DW";

    /// <summary>
    /// The generated columns, in contract order. The names, types and lengths reproduce the dimension the
    /// legacy generator wrote, so a view or report built on the old table binds against this one unchanged.
    /// The string widths are the old table's exact declared lengths, not round numbers: a wider column would
    /// change the type a pass-through reporting view exposes downstream.
    /// </summary>
    public static IReadOnlyList<Column> Columns { get; } =
    [
        new(KeyColumn, "int", typeof(int), r => r.PeriodId),
        new("Date", "date", typeof(DateTime), r => r.Date.ToDateTime(TimeOnly.MinValue)),
        new("DayOfMonth", "int", typeof(int), r => r.DayOfMonth),
        new("DayOfWeekName", "nvarchar(25)", typeof(string), r => r.DayOfWeekName),
        new("DayOfWeekNameShort", "nvarchar(5)", typeof(string), r => r.DayOfWeekNameShort),
        new("DayOfWeekNumber", "int", typeof(int), r => r.DayOfWeekNumber),
        new("WeekOfYear", "int", typeof(int), r => r.WeekOfYear),
        new("MonthNumber", "int", typeof(int), r => r.MonthNumber),
        new("MonthName", "nvarchar(25)", typeof(string), r => r.MonthName),
        new("MonthNameShort", "nvarchar(5)", typeof(string), r => r.MonthNameShort),
        new("MonthNumName", "nvarchar(10)", typeof(string), r => r.MonthNumName),
        new("Quarter", "nvarchar(5)", typeof(string), r => r.Quarter),
        new("Year", "int", typeof(int), r => r.Year),
        new("IsWeekend", "bit", typeof(bool), r => r.IsWeekend),
        new("IsLeapYear", "bit", typeof(bool), r => r.IsLeapYear),
        new("IsLastDayOfMonth", "bit", typeof(bool), r => r.IsLastDayOfMonth),
        new("FiscalWeekOfYear", "int", typeof(int), r => r.FiscalWeekOfYear),
        new("FiscalMonth", "int", typeof(int), r => r.FiscalMonth),
        new("FiscalQuarter", "int", typeof(int), r => r.FiscalQuarter),
        new("FiscalYear", "int", typeof(int), r => r.FiscalYear),
        new("IsHoliday", "bit", typeof(bool), r => r.IsHoliday),
        new("HolidayName", "nvarchar(50)", typeof(string), r => r.HolidayName),
        new("Season", "nvarchar(25)", typeof(string), r => r.Season),
        new("DaylightSavingTime", "bit", typeof(bool), r => r.DaylightSavingTime),
        new("ISOWeekNumber", "int", typeof(int), r => r.IsoWeekNumber),
    ];

    /// <summary>The generated columns other than the key, which is what a MERGE compares and updates.</summary>
    public static IReadOnlyList<Column> NonKeyColumns { get; } =
        Columns.Where(c => !string.Equals(c.Name, KeyColumn, StringComparison.Ordinal)).ToList();

    /// <summary>Quotes an identifier for T-SQL, doubling any embedded closing bracket.</summary>
    public static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>The CREATE TABLE for the dimension, with the key as its clustered primary key.</summary>
    public static string CreateTableSql(RelationalObject table)
    {
        var lines = Columns.Select(c =>
            $"    {Quote(c.Name)} {c.SqlType} {(string.Equals(c.Name, KeyColumn, StringComparison.Ordinal) ? "NOT NULL" : "NULL")}").ToList();
        lines.Add($"    {Quote(InsertedAudit)} datetime NULL");
        lines.Add($"    {Quote(UpdatedAudit)} datetime NULL");
        lines.Add($"    CONSTRAINT {Quote("PK_" + table.Name)} PRIMARY KEY CLUSTERED ({Quote(KeyColumn)})");

        return $"CREATE TABLE {table.QualifiedName} (\r\n{string.Join(",\r\n", lines)}\r\n);";
    }

    /// <summary>The staging table's CREATE, as a session-scoped temp table with no key.</summary>
    public static string CreateStagingSql(string stagingName)
    {
        var lines = Columns.Select(c => $"    {Quote(c.Name)} {c.SqlType} NULL");
        return $"CREATE TABLE {stagingName} (\r\n{string.Join(",\r\n", lines)}\r\n);";
    }

    /// <summary>Projects generated rows into the DataTable SqlBulkCopy streams into the staging table.</summary>
    public static DataTable ToDataTable(IReadOnlyList<CalendarRow> rows)
    {
        var table = new DataTable("calendar") { Locale = CultureInfo.InvariantCulture };
        foreach (var column in Columns)
        {
            table.Columns.Add(column.Name, column.ClrType).AllowDBNull = true;
        }

        foreach (var row in rows)
        {
            var values = new object[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                values[i] = Columns[i].Value(row) ?? DBNull.Value;
            }

            table.Rows.Add(values);
        }

        return table;
    }

    /// <summary>
    /// The MERGE that makes the target exactly the generated range: insert what is missing, update what
    /// differs, and delete anything the generator did not produce. The change test is an INTERSECT of the two
    /// row images, which treats NULL as equal to NULL; a column-by-column inequality would rewrite every row
    /// whose HolidayName is NULL on both sides, on every run.
    /// </summary>
    public static string MergeSql(RelationalObject table, string stagingName)
    {
        var target = table.QualifiedName;
        var all = Columns.Select(c => Quote(c.Name)).ToList();
        var nonKey = NonKeyColumns.Select(c => Quote(c.Name)).ToList();

        var insertColumns = string.Join(", ", all.Append(Quote(InsertedAudit)).Append(Quote(UpdatedAudit)));
        var insertValues = string.Join(", ", all.Select(c => "src." + c).Append("SYSDATETIME()").Append("SYSDATETIME()"));
        var updateSet = string.Join(",\r\n        ", nonKey.Select(c => $"tgt.{c} = src.{c}")
            .Append($"tgt.{Quote(UpdatedAudit)} = SYSDATETIME()"));

        var srcImage = string.Join(", ", all.Select(c => "src." + c));
        var tgtImage = string.Join(", ", all.Select(c => "tgt." + c));

        return $"""
            MERGE {target} AS tgt
            USING {stagingName} AS src
               ON tgt.{Quote(KeyColumn)} = src.{Quote(KeyColumn)}
            WHEN MATCHED AND NOT EXISTS (
                     SELECT {srcImage}
                     INTERSECT
                     SELECT {tgtImage})
                THEN UPDATE SET
                    {updateSet}
            WHEN NOT MATCHED BY TARGET
                THEN INSERT ({insertColumns})
                     VALUES ({insertValues})
            WHEN NOT MATCHED BY SOURCE
                THEN DELETE
            OUTPUT $action INTO @changes;
            """;
    }
}
