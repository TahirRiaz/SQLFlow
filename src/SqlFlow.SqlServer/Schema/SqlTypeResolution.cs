using SqlFlow.Core;

namespace SqlFlow.SqlServer.Schema;

/// <summary>What schema evolution must do to reconcile an existing target column with the incoming type.</summary>
public enum SchemaChangeAction
{
    /// <summary>The existing target type already accommodates the incoming type; no DDL.</summary>
    Keep,

    /// <summary>The target must grow to the widened type; emit an ALTER COLUMN.</summary>
    Alter,

    /// <summary>The change crosses type families and cannot be applied safely; surface it.</summary>
    Incompatible,
}

/// <summary>The outcome of resolving a target column type against an incoming type.</summary>
public sealed record SqlTypeResolutionResult
{
    public required SchemaChangeAction Action { get; init; }

    /// <summary>The widened type to alter the column to; set only when <see cref="Action"/> is Alter.</summary>
    public SqlDataType? Merged { get; init; }

    /// <summary>Why the change is incompatible; set only when <see cref="Action"/> is Incompatible.</summary>
    public string? Reason { get; init; }

    public static readonly SqlTypeResolutionResult Keep = new() { Action = SchemaChangeAction.Keep };

    public static SqlTypeResolutionResult Alter(SqlDataType merged) =>
        new() { Action = SchemaChangeAction.Alter, Merged = merged };

    public static SqlTypeResolutionResult Incompatible(string reason) =>
        new() { Action = SchemaChangeAction.Incompatible, Reason = reason };
}

/// <summary>
/// Decides how a target column should evolve to accept an incoming type, using monotonic widening: the
/// target only ever grows, so data already present always still fits (this fixes the legacy behavior, which
/// made the target match the source exactly and would narrow a column). Two types in the same family merge
/// to the wider of the two; a cross-family change is reported Incompatible rather than forced.
/// </summary>
public static class SqlTypeResolution
{
    public static SqlTypeResolutionResult Resolve(SqlDataType existing, SqlDataType desired)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);

        if (existing == desired)
        {
            return SqlTypeResolutionResult.Keep;
        }

        var family = existing.Family;
        if (family != desired.Family)
        {
            return SqlTypeResolutionResult.Incompatible(
                $"cannot evolve [{existing.Render()}] to [{desired.Render()}]: incompatible type families ({family} vs {desired.Family})");
        }

        var merged = WidenWithinFamily(existing, desired, family);
        if (merged is null)
        {
            return SqlTypeResolutionResult.Incompatible(
                $"cannot evolve [{existing.Render()}] to [{desired.Render()}] within {family}");
        }

        return merged == existing ? SqlTypeResolutionResult.Keep : SqlTypeResolutionResult.Alter(merged);
    }

    private static SqlDataType? WidenWithinFamily(SqlDataType a, SqlDataType b, SqlTypeFamily family) => family switch
    {
        SqlTypeFamily.Text => WidenText(a, b),
        SqlTypeFamily.Binary => WidenBinary(a, b),
        SqlTypeFamily.Integer => WidenInteger(a, b),
        SqlTypeFamily.Decimal => WidenDecimal(a, b),
        SqlTypeFamily.Approximate => new SqlDataType { BaseType = a.BaseType == "float" || b.BaseType == "float" ? "float" : "real" },
        SqlTypeFamily.Money => new SqlDataType { BaseType = a.BaseType == "money" || b.BaseType == "money" ? "money" : "smallmoney" },
        SqlTypeFamily.DateTime => WidenDateTime(a, b),
        _ => null, // Bit, Guid, Other: no widening; a differing value is Incompatible
    };

    private static SqlDataType WidenText(SqlDataType a, SqlDataType b)
    {
        static bool Unicode(SqlDataType t) => t.BaseType is "nchar" or "nvarchar" or "ntext" or "sysname";
        static bool Variable(SqlDataType t) => t.BaseType is "varchar" or "nvarchar" or "text" or "ntext" or "sysname";
        static bool Huge(SqlDataType t) => t.BaseType is "text" or "ntext" || t.Length == -1;
        static int Len(SqlDataType t) => t.BaseType == "sysname" ? 128 : t.Length ?? 1;

        var unicode = Unicode(a) || Unicode(b);
        var variable = Variable(a) || Variable(b);
        var huge = Huge(a) || Huge(b);
        var baseName = (unicode ? "n" : string.Empty) + (variable ? "varchar" : "char");
        int? length = huge ? -1 : Math.Max(Len(a), Len(b));
        return new SqlDataType { BaseType = baseName, Length = length };
    }

    private static SqlDataType? WidenBinary(SqlDataType a, SqlDataType b)
    {
        // timestamp / rowversion is a fixed 8-byte system type and cannot be altered into another binary type.
        if (a.BaseType == "timestamp" || b.BaseType == "timestamp")
        {
            return null;
        }

        static bool Variable(SqlDataType t) => t.BaseType is "varbinary" or "image";
        static bool Huge(SqlDataType t) => t.BaseType == "image" || t.Length == -1;

        var variable = Variable(a) || Variable(b);
        var huge = Huge(a) || Huge(b);
        var baseName = variable ? "varbinary" : "binary";
        int? length = huge ? -1 : Math.Max(a.Length ?? 1, b.Length ?? 1);
        return new SqlDataType { BaseType = baseName, Length = length };
    }

    private static SqlDataType WidenInteger(SqlDataType a, SqlDataType b)
    {
        static int Rank(string t) => t switch { "tinyint" => 1, "smallint" => 2, "int" => 3, "bigint" => 4, _ => 0 };
        var name = Math.Max(Rank(a.BaseType), Rank(b.BaseType)) switch
        {
            1 => "tinyint",
            2 => "smallint",
            3 => "int",
            _ => "bigint",
        };
        return new SqlDataType { BaseType = name };
    }

    private static SqlDataType WidenDecimal(SqlDataType a, SqlDataType b)
    {
        var scale = Math.Max(a.Scale ?? 0, b.Scale ?? 0);
        var integerDigits = Math.Max((a.Precision ?? 18) - (a.Scale ?? 0), (b.Precision ?? 18) - (b.Scale ?? 0));
        var precision = Math.Min(38, integerDigits + scale);
        if (precision < scale)
        {
            precision = scale;
        }

        return new SqlDataType { BaseType = "decimal", Precision = precision, Scale = scale };
    }

    private static SqlDataType? WidenDateTime(SqlDataType a, SqlDataType b)
    {
        // Conservative: a differing date/time base (for example date vs datetime2) is surfaced rather than
        // promoted. Only the fractional-second scale of an identical base is widened.
        if (a.BaseType != b.BaseType)
        {
            return null;
        }

        // An unspecified scale on datetime2 / time / datetimeoffset is the implicit maximum of 7, so a bare
        // "datetime2" target already accommodates any incoming scale. Return the existing type unchanged when
        // its effective scale already covers the merged scale; the caller then short-circuits to Keep instead
        // of emitting an equivalent no-op ALTER (for example ALTER COLUMN ... datetime2(7) on a datetime2).
        var existingScale = a.Scale ?? 7;
        var scale = Math.Max(existingScale, b.Scale ?? 7);
        return scale == existingScale ? a : new SqlDataType { BaseType = a.BaseType, Scale = scale };
    }
}
