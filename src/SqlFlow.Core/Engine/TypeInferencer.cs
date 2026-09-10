using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Decides a column's SQL type from its <see cref="ColumnProfile"/>, a <see cref="TypeInferencePolicy"/>,
/// and the run's <see cref="ServerLocale"/>, and emits the SELECT expression that produces it. Pure and
/// fully testable: the candidate counts come from SQL Server's own <c>TRY_CONVERT</c>, so a chosen type
/// is always convertible. For date and numeric families it scans the locale-preferred candidates and
/// takes the FIRST that meets the threshold (not the highest count), so ambiguous values resolve to the
/// server locale consistently across every column and only genuinely foreign-formatted columns fall back.
/// The failure mode (<c>CONVERT</c> vs <c>TRY_CONVERT</c> vs keep-string) comes from the policy, which
/// defaults to fail-loud <c>CONVERT</c>.
/// </summary>
public sealed class TypeInferencer
{
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance method by design - resolved via DI and a seam for future inference configuration/state.")]
    public InferredColumn Infer(ColumnProfile profile, TypeInferencePolicy policy, ServerLocale locale)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(locale);

        if (profile.NonNull == 0)
        {
            return KeepAsString(profile);
        }

        bool Meets(long count) => (double)count / profile.NonNull >= policy.Threshold;

        // Boolean is detected by token set, NOT TRY_CONVERT(bit) - which coerces any number to 1.
        if (Meets(profile.AsBitTokens))
        {
            return Typed(profile, "bit", policy, profile.AsBitTokens);
        }

        // Integers, unless significant leading zeros mean the string is identity-like (zip/IDs).
        if (Meets(profile.AsBigInt) && !(policy.PreserveLeadingZeros && profile.LeadingZeroInts > 0))
        {
            return Typed(profile, IntegerType(profile), policy, profile.AsBigInt);
        }

        // Numerics: prefer the locale convention, fall back to invariant; take the first that fits.
        var asDecimal = profile.NumericCandidates.FirstOrDefault(c => Meets(c.AsDecimal));
        if (asDecimal is not null)
        {
            return Typed(profile, DecimalType(asDecimal), policy, asDecimal.AsDecimal, numeric: asDecimal, locale: locale);
        }

        // Float only catches what decimal can't (e.g. scientific notation).
        var asFloat = profile.NumericCandidates.FirstOrDefault(c => Meets(c.AsFloat));
        if (asFloat is not null)
        {
            return Typed(profile, "float", policy, asFloat.AsFloat, numeric: asFloat, locale: locale);
        }

        var asDateTime = profile.DateTimeCandidates.FirstOrDefault(c => Meets(c.Count));
        if (asDateTime is not null)
        {
            return Typed(profile, "datetime2", policy, asDateTime.Count, style: asDateTime.Style);
        }

        var asDate = profile.DateCandidates.FirstOrDefault(c => Meets(c.Count));
        if (asDate is not null)
        {
            return Typed(profile, "date", policy, asDate.Count, style: asDate.Style);
        }

        if (Meets(profile.AsGuid))
        {
            return Typed(profile, "uniqueidentifier", policy, profile.AsGuid);
        }

        return KeepAsString(profile);
    }

    private static string IntegerType(ColumnProfile p)
    {
        var min = p.MinValue ?? long.MinValue;
        var max = p.MaxValue ?? long.MaxValue;

        if (min >= 0 && max <= 255) return "tinyint";
        if (min >= -32768 && max <= 32767) return "smallint";
        if (min >= -2147483648 && max <= 2147483647) return "int";
        return "bigint";
    }

    private static string DecimalType(NumericConversionCandidate c)
    {
        var scale = Math.Clamp(c.MaxScale, 0, 38);
        var precision = Math.Clamp(c.MaxIntegerDigits + scale, 1, 38);
        if (scale > precision)
        {
            scale = precision;
        }

        return $"decimal({precision.ToString(CultureInfo.InvariantCulture)}, {scale.ToString(CultureInfo.InvariantCulture)})";
    }

    private static InferredColumn Typed(
        ColumnProfile p,
        string type,
        TypeInferencePolicy policy,
        long convertibleCount,
        int? style = null,
        NumericConversionCandidate? numeric = null,
        ServerLocale? locale = null)
    {
        // keep-string: only convert when every non-null value fits; otherwise preserve the raw string.
        if (policy.OnConvertError == ConvertErrorMode.KeepString && convertibleCount != p.NonNull)
        {
            return KeepAsString(p);
        }

        var function = policy.OnConvertError == ConvertErrorMode.Fail ? "CONVERT" : "TRY_CONVERT";
        var bracketed = $"[{Escape(p.ColumnName)}]";

        // Numerics are normalized (strip grouping, decimal -> '.') so the locale's convention converts;
        // every other family converts the raw column directly.
        var input = numeric is not null && locale is not null
            ? LocaleConversion.NumericInput(bracketed, numeric.Format, locale)
            : bracketed;

        var styleArgument = style is { } s ? $", {s.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        return new InferredColumn
        {
            ColumnName = p.ColumnName,
            DataType = type,
            SelectExpression = $"{function}({type}, {input}{styleArgument})",
            Converted = true,
            Style = style,
            NumericFormat = numeric?.Format,
        };
    }

    private static InferredColumn KeepAsString(ColumnProfile p) => new()
    {
        ColumnName = p.ColumnName,
        DataType = $"varchar({Math.Clamp(p.MaxLen, 1, 8000).ToString(CultureInfo.InvariantCulture)})",
        SelectExpression = $"[{Escape(p.ColumnName)}]",
        Converted = false,
    };

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
