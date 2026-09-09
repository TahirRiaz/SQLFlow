using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.Oracle;

/// <summary>
/// Maps Oracle column types (rendered by the catalog reader from <c>DATA_TYPE</c> plus modifiers) to the SQL
/// Server types that hold them, following the SSMA default mapping. The deliberate decisions worth naming:
/// an explicit <c>NUMBER(p, s)</c> becomes <c>decimal(p, s)</c> (Oracle's precision maxes at 38, inside SQL
/// Server's range); an unconstrained <c>NUMBER</c> and Oracle <c>FLOAT</c> are floating types with no fixed
/// scale, so they map to <c>float(53)</c> (SSMA's behavior) rather than being rejected, because bare
/// <c>NUMBER</c> is Oracle's default numeric and rejecting it would strand most real tables; <c>DATE</c>
/// carries a time-of-day to whole seconds and maps to <c>datetime2(0)</c>; <c>TIMESTAMP</c> keeps its
/// fractional precision capped at SQL Server's 7 digits; a zoned <c>TIMESTAMP</c> lands in
/// <c>datetimeoffset</c>. Strict everywhere else: a type with no safe mapping throws with the column and type
/// named, never degrades.
/// </summary>
public sealed partial class OracleSourceTypeMapper : ISourceTypeMapper
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.Oracle;

    public string ToSqlServerType(CatalogColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var native = column.NativeType.Trim();
        var upper = native.ToUpperInvariant();

        // TIMESTAMP renders as TIMESTAMP(n), TIMESTAMP(n) WITH TIME ZONE, or TIMESTAMP(n) WITH LOCAL TIME ZONE.
        if (upper.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            var fsp = Math.Min(FirstNumber(upper) ?? 6, 7);
            // A zoned timestamp is an absolute instant with an offset; the local-zone variant is normalized to
            // the database time zone and carries no offset, so it lands in datetime2 like a plain timestamp.
            return upper.Contains("WITH TIME ZONE", StringComparison.Ordinal) && !upper.Contains("LOCAL", StringComparison.Ordinal)
                ? $"datetimeoffset({fsp.ToString(CultureInfo.InvariantCulture)})"
                : $"datetime2({fsp.ToString(CultureInfo.InvariantCulture)})";
        }

        // Both interval families (year-to-month, day-to-second) ride as text; SQL Server has no interval type.
        if (upper.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return "nvarchar(50)";
        }

        var match = TypeShape().Match(upper);
        if (!match.Success)
        {
            throw Unmappable(column);
        }

        var baseType = match.Groups["base"].Value.Trim();
        var args = match.Groups["args"].Success ? match.Groups["args"].Value : null;

        return baseType switch
        {
            "NUMBER" => MapNumber(args),
            "FLOAT" => "float(53)",
            "BINARY_FLOAT" => "real",
            "BINARY_DOUBLE" => "float(53)",
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" => MapChar(args, "nvarchar"),
            "CHAR" or "NCHAR" or "CHARACTER" => MapChar(args, "nchar"),
            "CLOB" or "NCLOB" or "LONG" => "nvarchar(max)",
            "DATE" => "datetime2(0)",
            "RAW" => MapRaw(args),
            "LONG RAW" or "BLOB" => "varbinary(max)",
            "ROWID" or "UROWID" => "nvarchar(4000)",
            "XMLTYPE" => "xml",
            _ => throw Unmappable(column),
        };
    }

    private static string MapNumber(string? args)
    {
        // Unconstrained NUMBER (and NUMBER(*, s), which reports no precision) is a floating numeric: float(53).
        if (string.IsNullOrEmpty(args))
        {
            return "float(53)";
        }

        var parts = args.Split(',', StringSplitOptions.TrimEntries);
        var precision = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var scale = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

        // Oracle allows a negative scale (round to the left of the point); the stored values are integers, so
        // decimal(p, 0) holds them. Clamp the scale into decimal's valid [0, precision] range.
        scale = Math.Clamp(scale, 0, precision);
        return $"decimal({precision.ToString(CultureInfo.InvariantCulture)}, {scale.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string MapChar(string? args, string typed)
    {
        if (string.IsNullOrEmpty(args))
        {
            return $"{typed}(1)";
        }

        var length = int.Parse(args, CultureInfo.InvariantCulture);
        return length <= 4000 ? $"{typed}({length.ToString(CultureInfo.InvariantCulture)})" : "nvarchar(max)";
    }

    private static string MapRaw(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            return "varbinary(max)";
        }

        var length = int.Parse(args, CultureInfo.InvariantCulture);
        return length <= 8000 ? $"varbinary({length.ToString(CultureInfo.InvariantCulture)})" : "varbinary(max)";
    }

    // The leading number inside the first parentheses (the fractional-seconds precision of a TIMESTAMP).
    private static int? FirstNumber(string text)
    {
        var match = FirstNumberShape().Match(text);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static SqlFlowException Unmappable(CatalogColumn column)
        => new($"Column '{column.Name}' has Oracle type '{column.NativeType}', which has no safe SQL Server mapping. Exclude it with ignoreColumns or cast it in a source view.");

    // "NUMBER(10,2)", "VARCHAR2(50)", "BINARY_DOUBLE", "LONG RAW", "RAW(16)" -> base (letters, digits, '_',
    // spaces) plus the first parenthesized args.
    [GeneratedRegex(@"^(?<base>[A-Z0-9_ ]+?)\s*(\((?<args>[^)]*)\))?$")]
    private static partial Regex TypeShape();

    [GeneratedRegex(@"\((\d+)")]
    private static partial Regex FirstNumberShape();
}
