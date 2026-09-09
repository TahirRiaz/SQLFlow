namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Classifies a single widening ALTER as <see cref="ChangeFootprint.MetadataOnly"/> or
/// <see cref="ChangeFootprint.TableRewrite"/> from the structured <see cref="SqlDataType"/> pair the planner
/// already produces. Pure, no I/O. Encodes the SQL Server in-place ALTER COLUMN rewrite rules so a genuinely
/// expensive change (a fixed-width or storage-class growth) is flagged rather than applied as a silent long lock.
/// </summary>
public static class ChangeFootprintClassifier
{
    public static ChangeFootprint Classify(SqlDataType from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return from.Family switch
        {
            // Any integer rank increase changes the fixed storage width and rewrites every row.
            SqlTypeFamily.Integer => IntRank(to.BaseType) > IntRank(from.BaseType)
                ? ChangeFootprint.TableRewrite
                : ChangeFootprint.MetadataOnly,

            // Crossing a decimal storage-byte class rewrites; growth within one class is metadata-only.
            SqlTypeFamily.Decimal => DecimalClass(to.Precision ?? 18) != DecimalClass(from.Precision ?? 18)
                ? ChangeFootprint.TableRewrite
                : ChangeFootprint.MetadataOnly,

            SqlTypeFamily.Text => TextRewrite(from, to),
            SqlTypeFamily.Binary => BinaryRewrite(from, to),

            // Crossing a datetime2/time/datetimeoffset fractional-second byte class rewrites.
            SqlTypeFamily.DateTime => TimeClass(to.Scale ?? 7) != TimeClass(from.Scale ?? 7)
                ? ChangeFootprint.TableRewrite
                : ChangeFootprint.MetadataOnly,

            _ => ChangeFootprint.MetadataOnly,
        };
    }

    private static int IntRank(string baseType) => baseType switch
    {
        "tinyint" => 1,
        "smallint" => 2,
        "int" => 3,
        "bigint" => 4,
        _ => 0,
    };

    // 5 / 9 / 13 / 17-byte storage classes for decimal precision.
    private static int DecimalClass(int precision) => precision switch
    {
        <= 9 => 0,
        <= 19 => 1,
        <= 28 => 2,
        _ => 3,
    };

    // 3 / 4 / 5-byte storage classes for datetime2/time/datetimeoffset fractional scale.
    private static int TimeClass(int scale) => scale switch
    {
        <= 2 => 0,
        <= 4 => 1,
        _ => 2,
    };

    private static ChangeFootprint TextRewrite(SqlDataType from, SqlDataType to)
    {
        if (to.IsMax && !from.IsMax)
        {
            return ChangeFootprint.TableRewrite; // (n) -> (max): moves in-row data to LOB storage
        }

        var fromFixed = from.BaseType is "char" or "nchar";
        var toFixed = to.BaseType is "char" or "nchar";
        if (fromFixed && toFixed && (to.Length ?? 1) > (from.Length ?? 1))
        {
            return ChangeFootprint.TableRewrite; // widening a fixed-width column rewrites every row
        }

        return ChangeFootprint.MetadataOnly; // varchar(n) -> varchar(m), nvarchar(n) -> nvarchar(m), etc.
    }

    private static ChangeFootprint BinaryRewrite(SqlDataType from, SqlDataType to)
    {
        if (to.IsMax && !from.IsMax)
        {
            return ChangeFootprint.TableRewrite;
        }

        if (from.BaseType == "binary" && to.BaseType == "binary" && (to.Length ?? 1) > (from.Length ?? 1))
        {
            return ChangeFootprint.TableRewrite;
        }

        return ChangeFootprint.MetadataOnly;
    }
}
