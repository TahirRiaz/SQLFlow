using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.Postgres;

/// <summary>
/// Maps PostgreSQL column types (rendered by the catalog reader from <c>udt_name</c> plus modifiers) to the
/// SQL Server types that hold them. There is no vendor mapping table for this direction; the mapping follows
/// the Npgsql provider semantics and the two engines' documented ranges: <c>timestamptz</c> is a UTC instant
/// and lands in <c>datetimeoffset</c>, unbounded <c>numeric</c> exceeds SQL Server's precision 38 and is
/// rejected rather than silently truncated, arrays and exotic types (inet, interval, ranges) ride as text.
/// Strict: a type with no safe mapping throws with the column and type named.
/// </summary>
public sealed partial class PostgresSourceTypeMapper : ISourceTypeMapper
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.PostgreSQL;

    public string ToSqlServerType(CatalogColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var native = column.NativeType.Trim().ToLowerInvariant();

        // Arrays (udt_name leads with '_') and other compose-as-text types ride as nvarchar(max); the reader
        // selects them with ::text so the value stream is well-defined.
        if (native.StartsWith('_'))
        {
            return "nvarchar(max)";
        }

        var match = TypeShape().Match(native);
        if (!match.Success)
        {
            throw Unmappable(column);
        }

        var baseType = match.Groups["base"].Value;
        var args = match.Groups["args"].Success ? match.Groups["args"].Value : null;

        return baseType switch
        {
            "bool" or "boolean" => "bit",
            "int2" or "smallint" or "smallserial" => "smallint",
            "int4" or "int" or "integer" or "serial" => "int",
            "int8" or "bigint" or "bigserial" => "bigint",
            "oid" => "bigint",
            "numeric" or "decimal" => MapNumeric(args, column),
            "float4" or "real" => "real",
            "float8" or "double precision" => "float(53)",
            "money" => "decimal(19, 2)",
            "date" => "date",
            "timestamp" => $"datetime2({Fsp(args, 6)})",
            "timestamptz" => $"datetimeoffset({Fsp(args, 6)})",
            "time" => $"time({Fsp(args, 6)})",
            "timetz" or "interval" => "nvarchar(50)",
            "bpchar" or "char" or "character" => MapChar(args, "nchar"),
            "varchar" or "character varying" => MapChar(args, "nvarchar"),
            "text" or "citext" or "name" => "nvarchar(max)",
            "bytea" => "varbinary(max)",
            "uuid" => "uniqueidentifier",
            "json" or "jsonb" => "nvarchar(max)",
            "xml" => "xml",
            "inet" or "cidr" or "macaddr" or "macaddr8" or "bit" or "varbit" or "tsvector" or "tsquery"
                or "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle"
                or "int4range" or "int8range" or "numrange" or "tsrange" or "tstzrange" or "daterange" => "nvarchar(max)",
            _ => throw Unmappable(column),
        };
    }

    private static string MapNumeric(string? args, CatalogColumn column)
    {
        if (string.IsNullOrEmpty(args))
        {
            // Unbounded numeric allows up to 131072 digits before the point; capping at 38 silently corrupts.
            throw new SqlFlowException(
                $"Column '{column.Name}' is an unbounded PostgreSQL numeric; SQL Server needs an explicit precision (max 38). Declare numeric(p, s) in the source or exclude the column.");
        }

        var parts = args.Split(',', StringSplitOptions.TrimEntries);
        var precision = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var scale = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        if (precision > 38)
        {
            throw new SqlFlowException(
                $"Column '{column.Name}' is numeric({precision}, {scale}); SQL Server supports at most precision 38. Reduce the source precision or exclude the column.");
        }

        return $"decimal({precision}, {scale})";
    }

    private static string MapChar(string? args, string typed)
    {
        if (string.IsNullOrEmpty(args))
        {
            // PostgreSQL varchar/char without a modifier is unlimited.
            return "nvarchar(max)";
        }

        var length = int.Parse(args, CultureInfo.InvariantCulture);
        return length <= 4000 ? $"{typed}({length})" : "nvarchar(max)";
    }

    private static int Fsp(string? args, int fallback)
        => string.IsNullOrEmpty(args) ? fallback : int.Parse(args, CultureInfo.InvariantCulture);

    private static SqlFlowException Unmappable(CatalogColumn column)
        => new($"Column '{column.Name}' has PostgreSQL type '{column.NativeType}', which has no safe SQL Server mapping. Exclude it with ignoreColumns or cast it in a source view.");

    [GeneratedRegex(@"^(?<base>[a-z0-9 ]+?)\s*(\((?<args>[^)]*)\))?$")]
    private static partial Regex TypeShape();
}
