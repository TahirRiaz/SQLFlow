using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>Maps inferred CLR columns to SQL Server types. Ported and modernized from the original DataTypeMapper.</summary>
public sealed class SqlServerTypeMapper : ISqlTypeMapper
{
    public ColumnDefinition Map(SourceColumn column, ColumnOverride? columnOverride, string defaultColumnType)
    {
        ArgumentNullException.ThrowIfNull(column);

        // Precedence: an author override wins, then a type the source declared explicitly (e.g. an
        // injected hash-key column), then CLR-to-SQL inference.
        var sqlType = !string.IsNullOrWhiteSpace(columnOverride?.Type)
            ? columnOverride!.Type!.Trim()
            : !string.IsNullOrWhiteSpace(column.SqlType)
                ? column.SqlType!.Trim()
                : MapClrType(column, defaultColumnType);

        var nullable = columnOverride?.Nullable ?? column.IsNullable;

        return new ColumnDefinition { Name = column.Name, SqlType = sqlType, IsNullable = nullable };
    }

    private static string MapClrType(SourceColumn c, string defaultColumnType)
    {
        var t = Nullable.GetUnderlyingType(c.Type) ?? c.Type;

        if (t == typeof(string))
        {
            // Raw-layer columns have no inferred length -> use the metadata default (e.g. varchar(255)).
            if (c.MaxLength is > 0 and <= 4000)
            {
                return $"NVARCHAR({c.MaxLength.Value})";
            }

            return c.MaxLength is > 4000 ? "NVARCHAR(MAX)" : defaultColumnType;
        }

        if (t == typeof(bool)) return "BIT";
        if (t == typeof(byte)) return "TINYINT";
        if (t == typeof(short)) return "SMALLINT";
        if (t == typeof(int)) return "INT";
        if (t == typeof(long)) return "BIGINT";
        if (t == typeof(decimal))
            return $"DECIMAL({(c.Precision is > 0 ? c.Precision.Value : 38)}, {(c.Scale is >= 0 ? c.Scale.Value : 6)})";
        if (t == typeof(double)) return "FLOAT";
        if (t == typeof(float)) return "REAL";
        if (t == typeof(DateTime)) return "DATETIME2";
        if (t == typeof(DateTimeOffset)) return "DATETIMEOFFSET";
        if (t == typeof(TimeSpan)) return "TIME";
        if (t == typeof(Guid)) return "UNIQUEIDENTIFIER";
        if (t == typeof(byte[])) return "VARBINARY(MAX)";

        return "NVARCHAR(MAX)";
    }
}
