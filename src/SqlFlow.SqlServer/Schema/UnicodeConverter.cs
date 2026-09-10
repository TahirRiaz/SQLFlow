namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Converts a unicode SQL Server text type to its non-unicode counterpart (legacy
/// OnSyncConvertUnicodeDataType), preserving the declared length: nvarchar to varchar, nchar to char, ntext
/// to text, sysname to varchar(128). Non-text and already-non-unicode types pass through unchanged.
/// </summary>
public static class UnicodeConverter
{
    public static SqlDataType ToNonUnicode(SqlDataType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.BaseType switch
        {
            "nvarchar" => type with { BaseType = "varchar" },
            "nchar" => type with { BaseType = "char" },
            "ntext" => type with { BaseType = "text", Length = null },
            "sysname" => type with { BaseType = "varchar", Length = 128 },
            _ => type,
        };
    }
}
