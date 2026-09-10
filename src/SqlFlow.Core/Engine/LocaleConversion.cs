using System.Globalization;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// The one place that turns a resolved <see cref="ServerLocale"/> into SQL: the ordered date-style
/// candidates to probe and the numeric-normalization expression that strips grouping and rewrites the
/// decimal separator to '.'. Profiler (counting oracle), inferencer (emitted transform), and validator
/// all build their SQL from here, so the probe, the load, and the validation are guaranteed identical.
/// Pure and static - no database, fully testable.
/// </summary>
public static class LocaleConversion
{
    /// <summary>
    /// <c>date</c> style candidates, locale-preferred first, always ending with the unambiguous ISO
    /// style so genuinely ISO columns still convert. Ambiguous values (e.g. <c>01/02/2026</c>) are
    /// claimed by the preferred order, so every column in a file interprets them the same way.
    /// </summary>
    public static IReadOnlyList<int> DateStyleCandidates(DateOrder order) => order switch
    {
        // dd.mm.yyyy, dd/mm/yyyy, dd-mm-yyyy, then ISO, then the mdy fallback.
        DateOrder.Dmy => [104, 103, 105, 23, 101],
        // mm/dd/yyyy, mm-dd-yyyy, then ISO, then the dmy fallback.
        DateOrder.Mdy => [101, 110, 23, 103],
        // yyyy-mm-dd, yyyy.mm.dd, yyyy/mm/dd, then the day/month-first fallbacks.
        _ => [23, 102, 111, 103, 101],
    };

    /// <summary>
    /// <c>datetime2</c> style candidates (styles that also accept a trailing time component),
    /// locale-preferred first with the ISO datetime styles (121/120) as the safe fallback.
    /// </summary>
    public static IReadOnlyList<int> DateTimeStyleCandidates(DateOrder order) => order switch
    {
        DateOrder.Dmy => [104, 103, 105, 121, 120, 101],
        DateOrder.Mdy => [101, 110, 121, 120, 103],
        _ => [121, 120, 102, 103, 101],
    };

    /// <summary>
    /// Wraps <paramref name="inner"/> so it becomes a '.'-decimal, no-grouping string ready for
    /// <c>CONVERT(decimal/float, ...)</c>.
    /// <para>
    /// For a '.'-decimal locale (en-US, invariant) the comma is unambiguously a thousands separator, so
    /// commas and spaces are stripped outright. For a ','-decimal locale (e.g. nb-NO) the comma is the
    /// decimal marker, so '.' is treated as grouping ONLY when a comma is present; otherwise the value is
    /// left untouched. That keeps dotted dates (<c>25.12.2026</c>) and invariant decimals (<c>12.5</c>)
    /// from being mangled into integers, while still parsing genuine Norwegian numbers (<c>1.234,56</c>).
    /// </para>
    /// </summary>
    public static string NumericInput(string inner, NumericFormat format, ServerLocale locale)
    {
        ArgumentException.ThrowIfNullOrEmpty(inner);
        ArgumentNullException.ThrowIfNull(locale);

        var dec = format == NumericFormat.Invariant ? '.' : locale.DecimalSeparator;

        if (dec == '.')
        {
            // '.' decimal: commas and spaces are grouping; strip them unconditionally.
            return $"REPLACE(REPLACE(REPLACE({inner}, ',', ''), ' ', ''), NCHAR(160), '')";
        }

        // ',' decimal: only when a comma is present do we treat '.'/spaces as grouping and rewrite the
        // comma to '.'. Without a comma the value is left as-is (so dotted dates are not seen as numbers).
        var normalized = $"REPLACE(REPLACE(REPLACE(REPLACE({inner}, '.', ''), ' ', ''), NCHAR(160), ''), ',', '.')";
        return $"CASE WHEN CHARINDEX(',', {inner}) > 0 THEN {normalized} ELSE {inner} END";
    }

    /// <summary>
    /// Reads a culture's date-component ordering from its short date pattern - used for an explicit
    /// culture override, where there is no SQL Server <c>date_format</c> to consult.
    /// </summary>
    public static DateOrder DateOrderFromCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        foreach (var ch in culture.DateTimeFormat.ShortDatePattern)
        {
            switch (char.ToLowerInvariant(ch))
            {
                case 'y': return DateOrder.Ymd;
                case 'd': return DateOrder.Dmy;
                case 'm': return DateOrder.Mdy;
            }
        }

        return DateOrder.Ymd;
    }

    /// <summary>
    /// Derives a <see cref="ServerLocale"/> from a .NET culture and a SQL Server date order. The decimal
    /// separator comes from the culture; the grouping separator is normalized to the conventional pair
    /// ('.' decimal -> ',' group, ',' decimal -> '.' group) because file data rarely uses the culture's
    /// own (often non-breaking-space) group character. Cultures whose decimal separator is neither '.'
    /// nor ',' fall back to invariant numerics.
    /// </summary>
    public static ServerLocale FromCulture(CultureInfo culture, DateOrder order)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var sep = culture.NumberFormat.NumberDecimalSeparator;
        var dec = sep.Length == 1 && (sep[0] == '.' || sep[0] == ',') ? sep[0] : '.';

        return new ServerLocale
        {
            Culture = string.IsNullOrWhiteSpace(culture.Name) ? "invariant" : culture.Name,
            DateOrder = order,
            DecimalSeparator = dec,
            GroupSeparator = dec == ',' ? '.' : ',',
        };
    }
}
