using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.MySql;

/// <summary>
/// Maps MySQL column types (the full <c>COLUMN_TYPE</c> rendering, including <c>unsigned</c> and length /
/// precision) to the SQL Server types that hold them, following the SSMA default mapping with two deliberate
/// deviations: <c>bigint unsigned</c> maps to <c>decimal(20, 0)</c> (SSMA's bigint overflows above 2^63-1)
/// and <c>timestamp</c> maps to <c>datetime2</c> (not the legacy datetime). Strict: a type with no safe
/// mapping throws with the column and type named, never degrades.
/// </summary>
public sealed partial class MySqlSourceTypeMapper : ISourceTypeMapper
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public string ToSqlServerType(CatalogColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var native = column.NativeType.Trim().ToLowerInvariant();
        var match = TypeShape().Match(native);
        if (!match.Success)
        {
            throw Unmappable(column);
        }

        var baseType = match.Groups["base"].Value;
        var args = match.Groups["args"].Success ? match.Groups["args"].Value : null;
        var unsigned = native.Contains("unsigned", StringComparison.Ordinal);

        return baseType switch
        {
            // tinyint(1) is MySQL's boolean idiom; other tinyints are signed (-128..127), unlike SQL Server's.
            "tinyint" when args == "1" && !unsigned => "bit",
            "tinyint" => unsigned ? "tinyint" : "smallint",
            "bool" or "boolean" => "bit",
            "smallint" => unsigned ? "int" : "smallint",
            "mediumint" => "int",
            "int" or "integer" => unsigned ? "bigint" : "int",
            "bigint" => unsigned ? "decimal(20, 0)" : "bigint",
            "decimal" or "numeric" or "fixed" => MapDecimal(args, column),
            "float" => "real",
            "double" or "real" => "float(53)",
            "bit" => MapBit(args),
            "date" => "date",
            "datetime" => $"datetime2({Fsp(args)})",
            "timestamp" => $"datetime2({Fsp(args)})",
            "time" => $"time({Fsp(args)})",
            "year" => "smallint",
            "char" => MapChar(args, max: 4000, "nchar", "nvarchar(max)"),
            "varchar" => MapChar(args, max: 4000, "nvarchar", "nvarchar(max)"),
            "tinytext" => "nvarchar(255)",
            "text" or "mediumtext" or "longtext" => "nvarchar(max)",
            "binary" => MapChar(args, max: 8000, "binary", "varbinary(max)"),
            "varbinary" => MapChar(args, max: 8000, "varbinary", "varbinary(max)"),
            "tinyblob" => "varbinary(255)",
            "blob" or "mediumblob" or "longblob" => "varbinary(max)",
            "enum" => "nvarchar(255)",
            "set" => "nvarchar(max)",
            "json" => "nvarchar(max)",
            "geometry" or "point" or "linestring" or "polygon"
                or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection" => "varbinary(max)",
            _ => throw Unmappable(column),
        };
    }

    private static string MapDecimal(string? args, CatalogColumn column)
    {
        if (string.IsNullOrEmpty(args))
        {
            return "decimal(10, 0)"; // the MySQL default precision
        }

        var parts = args.Split(',', StringSplitOptions.TrimEntries);
        var precision = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var scale = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        if (precision > 38)
        {
            // MySQL allows precision up to 65; SQL Server caps at 38. Silent capping would corrupt data.
            throw new SqlFlowException(
                $"Column '{column.Name}' is decimal({precision}, {scale}); SQL Server supports at most precision 38. Reduce the source precision or exclude the column.");
        }

        return $"decimal({precision}, {scale})";
    }

    private static string MapBit(string? args)
    {
        var bits = string.IsNullOrEmpty(args) ? 1 : int.Parse(args, CultureInfo.InvariantCulture);
        return bits == 1 ? "bit" : $"binary({(bits + 7) / 8})";
    }

    private static string MapChar(string? args, int max, string typed, string overflow)
    {
        if (string.IsNullOrEmpty(args))
        {
            return $"{typed}(1)";
        }

        var length = int.Parse(args, CultureInfo.InvariantCulture);
        return length <= max ? $"{typed}({length})" : overflow;
    }

    // The fractional-second precision (0..6) is valid for datetime2/time directly.
    private static int Fsp(string? args)
        => string.IsNullOrEmpty(args) ? 0 : int.Parse(args, CultureInfo.InvariantCulture);

    private static SqlFlowException Unmappable(CatalogColumn column)
        => new($"Column '{column.Name}' has MySQL type '{column.NativeType}', which has no safe SQL Server mapping. Exclude it with ignoreColumns or convert it in the source.");

    // "varchar(50)", "decimal(10,2)", "int unsigned", "enum('a','b')" -> base + first parenthesized args.
    [GeneratedRegex(@"^(?<base>[a-z]+)\s*(\((?<args>[^)]*)\))?")]
    private static partial Regex TypeShape();
}
