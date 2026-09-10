using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlFlow.Core;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The broad family a SQL Server type belongs to, used to decide whether two types can be widened
/// into one another.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Integer, Decimal, and Guid are the correct, readable names for SQL type families; the CLR type-name overlap is harmless for an enum of SQL families.")]
public enum SqlTypeFamily
{
    Text,
    Integer,
    Decimal,
    Approximate,
    Money,
    DateTime,
    Bit,
    Binary,
    Guid,
    Other,
}

/// <summary>
/// A SQL Server data type parsed into its structured parts, so schema evolution can compare and widen types
/// instead of comparing opaque strings. <see cref="Parse"/> is bracket and whitespace tolerant and round-trips
/// through <see cref="Render"/>. Decimal is parsed correctly as <c>(precision, scale)</c> (the legacy parser
/// swapped them). A length of -1 means <c>(max)</c>.
/// </summary>
public sealed record SqlDataType
{
    /// <summary>The lowercased, canonical base type, for example <c>nvarchar</c>, <c>decimal</c>, <c>int</c>.</summary>
    public required string BaseType { get; init; }

    /// <summary>Declared length for character and binary types: a character count or byte count; -1 means max.</summary>
    public int? Length { get; init; }

    /// <summary>Numeric precision for decimal and numeric.</summary>
    public int? Precision { get; init; }

    /// <summary>Numeric scale for decimal and numeric, or the fractional-second scale for
    /// datetime2 / time / datetimeoffset.</summary>
    public int? Scale { get; init; }

    public bool IsMax => Length == -1;

    public SqlTypeFamily Family => FamilyOf(BaseType);

    public string Render()
    {
        if (Family == SqlTypeFamily.Decimal && Precision is { } precision)
        {
            return Scale is { } decimalScale
                ? $"{BaseType}({precision}, {decimalScale})"
                : $"{BaseType}({precision})";
        }

        if (IsScaleOnly(BaseType) && Scale is { } scale)
        {
            return $"{BaseType}({scale})";
        }

        if (Length is { } length)
        {
            return $"{BaseType}({(length == -1 ? "max" : length.ToString(CultureInfo.InvariantCulture))})";
        }

        return BaseType;
    }

    public static SqlDataType Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.Trim();
        if (text.Length == 0)
        {
            throw new SqlFlowException("A SQL data type must not be empty.");
        }

        string baseName;
        string? args = null;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = text.LastIndexOf(')');
            if (close < open)
            {
                throw new SqlFlowException($"Malformed SQL data type '{raw}'.");
            }

            baseName = text[..open];
            args = text[(open + 1)..close].Trim();
        }
        else
        {
            baseName = text;
        }

        baseName = Canonical(baseName.Trim().Trim('[', ']').Trim().ToLowerInvariant());
        if (baseName.Length == 0)
        {
            throw new SqlFlowException($"Malformed SQL data type '{raw}'.");
        }

        int? length = null;
        int? precision = null;
        int? scale = null;

        if (!string.IsNullOrEmpty(args))
        {
            var parts = args.Split(',', StringSplitOptions.TrimEntries);
            if (FamilyOf(baseName) == SqlTypeFamily.Decimal)
            {
                precision = ParseInt(parts[0], raw);
                scale = parts.Length > 1 ? ParseInt(parts[1], raw) : 0;
            }
            else if (IsScaleOnly(baseName))
            {
                scale = ParseInt(parts[0], raw);
            }
            else
            {
                length = ParseLength(parts[0], raw);
            }
        }

        return new SqlDataType { BaseType = baseName, Length = length, Precision = precision, Scale = scale };
    }

    internal static bool IsScaleOnly(string baseType) => baseType is "datetime2" or "time" or "datetimeoffset";

    private static string Canonical(string baseType) => baseType switch
    {
        "numeric" or "dec" => "decimal",
        "integer" => "int",
        "rowversion" => "timestamp",
        _ => baseType,
    };

    private static SqlTypeFamily FamilyOf(string baseType) => baseType switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" => SqlTypeFamily.Text,
        "tinyint" or "smallint" or "int" or "bigint" => SqlTypeFamily.Integer,
        "decimal" => SqlTypeFamily.Decimal,
        "real" or "float" => SqlTypeFamily.Approximate,
        "money" or "smallmoney" => SqlTypeFamily.Money,
        "date" or "time" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => SqlTypeFamily.DateTime,
        "bit" => SqlTypeFamily.Bit,
        "binary" or "varbinary" or "image" or "timestamp" => SqlTypeFamily.Binary,
        "uniqueidentifier" => SqlTypeFamily.Guid,
        _ => SqlTypeFamily.Other,
    };

    private static int ParseInt(string? value, string raw)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new SqlFlowException($"Malformed SQL data type '{raw}': '{value}' is not a valid number.");

    private static int ParseLength(string? value, string raw)
        => string.Equals(value?.Trim(), "max", StringComparison.OrdinalIgnoreCase) ? -1 : ParseInt(value, raw);
}
