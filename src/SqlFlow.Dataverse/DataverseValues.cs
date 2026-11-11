using System.Globalization;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Dataverse;

/// <summary>
/// Converts one Web API row's JSON into the typed cells the bulk load writes. Strict about meaning and lenient about
/// encoding: a number the service writes as a string (the IEEE 754 compatible form) is read as the number, a date-only
/// value is read as the date it names, and an absent or null property is a null cell; anything that cannot be the
/// column's type fails the run naming the table, the row's id, the column and an excerpt of the value.
/// </summary>
internal static class DataverseValues
{
    private const int ExcerptLength = 100;

    /// <summary>The typed cells of <paramref name="row"/> for <paramref name="columns"/>, in their order.</summary>
    /// <param name="row">One element of a page's <c>value</c> array.</param>
    /// <param name="columns">The columns the read produces.</param>
    /// <param name="table">The table's logical name, for messages.</param>
    /// <param name="primaryIdProperty">The property holding the row's id, for messages.</param>
    public static object?[] Row(JsonElement row, IReadOnlyList<DataverseColumn> columns, string table, string primaryIdProperty)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new SqlFlowException(
                $"The Dataverse table '{table}' returned a row that is not a JSON object ({row.ValueKind}).");
        }

        var cells = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!row.TryGetProperty(column.Property, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            try
            {
                cells[i] = Convert(value, column.Kind);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
            {
                var id = row.TryGetProperty(primaryIdProperty, out var idValue) && idValue.ValueKind == JsonValueKind.String
                    ? idValue.GetString()
                    : "(no id)";
                // The runtime's own parse messages quote the whole input, which may be a long text or a value nobody wants
                // copied into a run log; the excerpt above is all the message carries, and the cause stays attached.
                throw new SqlFlowException(
                    $"The Dataverse table '{table}', row {id}: column '{column.Name}' ({column.AttributeType}) holds "
                    + $"{Describe(value)}, which is not {Expected(column.Kind)}.", ex);
            }
        }

        return cells;
    }

    /// <summary>The cell for one non-null JSON value.</summary>
    /// <exception cref="FormatException">The value is not of the kind's type.</exception>
    /// <exception cref="OverflowException">The value is out of the kind's range.</exception>
    public static object Convert(JsonElement value, DataverseValueKind kind) => kind switch
    {
        DataverseValueKind.Guid => Guid.Parse(Text(value, "a GUID written as a JSON string")),
        DataverseValueKind.Text => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
        DataverseValueKind.Integer => value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out var whole) ? whole : throw new OverflowException("The number does not fit a 32-bit whole number.")
            : int.Parse(Text(value, NumberExpectation), NumberStyles.Integer, CultureInfo.InvariantCulture),
        DataverseValueKind.BigInt => value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out var big) ? big : throw new OverflowException("The number does not fit a 64-bit whole number.")
            : long.Parse(Text(value, NumberExpectation), NumberStyles.Integer, CultureInfo.InvariantCulture),
        DataverseValueKind.Decimal => value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out var fixedPoint) ? fixedPoint : throw new OverflowException("The number does not fit a decimal.")
            : decimal.Parse(Text(value, NumberExpectation), NumberStyles.Float, CultureInfo.InvariantCulture),
        DataverseValueKind.Double => Finite(value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.Parse(Text(value, NumberExpectation), NumberStyles.Float, CultureInfo.InvariantCulture)),
        DataverseValueKind.Boolean => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.Parse(Text(value, "a JSON true or false")),
        },
        DataverseValueKind.DateTime => DateTimeOffset.Parse(
            Text(value, "a date and time written as a JSON string"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime,
        DataverseValueKind.DateOnly => DateOnlyValue(Text(value, "a date written as a JSON string")),
        DataverseValueKind.Raw => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
        _ => throw new InvalidOperationException($"No conversion is defined for the value kind {kind}."),
    };

    private const string NumberExpectation = "a number written as a JSON number or string";

    /// <summary>A date-only column's value as the calendar date it names (<c>2024-05-01</c>). The service writes these
    /// without a time; a value written with one keeps the date as written, never shifted by a time zone.</summary>
    private static DateTime DateOnlyValue(string text)
    {
        var date = text.Length > 10 && text[10] == 'T' ? text[..10] : text;
        return DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    private static double Finite(double value)
        => double.IsFinite(value) ? value : throw new OverflowException("SQL Server's float holds no NaN or infinity.");

    private static string Text(JsonElement value, string expectation)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"Expected {expectation}.");

    private static string Describe(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        var excerpt = raw.Length <= ExcerptLength ? raw : raw[..ExcerptLength] + "...";
        return $"the JSON {value.ValueKind.ToString().ToLowerInvariant()} '{excerpt}'";
    }

    private static string Expected(DataverseValueKind kind) => kind switch
    {
        DataverseValueKind.Guid => "a GUID",
        DataverseValueKind.Integer => "a 32-bit whole number",
        DataverseValueKind.BigInt => "a 64-bit whole number",
        DataverseValueKind.Decimal => "a decimal number",
        DataverseValueKind.Double => "a finite floating-point number",
        DataverseValueKind.Boolean => "true or false",
        DataverseValueKind.DateTime => "a date and time",
        DataverseValueKind.DateOnly => "a date (yyyy-MM-dd)",
        _ => "text",
    };
}
