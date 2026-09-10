using System.Text.RegularExpressions;

namespace SqlFlow.Core.Engine;

/// <summary>
/// The single place that understands the <c>@ColName</c> placeholder used in authored column-transform
/// expressions (the legacy pre-ingestion transform token). <c>@ColName</c> is substituted with the quoted
/// reference to the transform's source column, so an author writes <c>CAST(@ColName AS varchar(50))</c> once and
/// a rename only touches the column's name. Matching is case-insensitive and token-aware: <c>@ColNameX</c> or a
/// longer variable that merely starts with the text is never mistaken for the placeholder.
/// </summary>
public static partial class ColumnTransformExpression
{
    // @ColName, case-insensitive, only when it is a whole token: not preceded by an identifier char and not
    // followed by one (so @ColNameFoo, a distinct T-SQL variable, is left untouched).
    [GeneratedRegex(@"(?<![A-Za-z0-9_@])@ColName(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColNameToken();

    /// <summary>True when the expression uses the <c>@ColName</c> placeholder as a whole token.</summary>
    public static bool ReferencesColumnToken(string? expression)
        => !string.IsNullOrEmpty(expression) && ColNameToken().IsMatch(expression);

    /// <summary>
    /// Substitutes every <c>@ColName</c> placeholder in <paramref name="expression"/> with
    /// <paramref name="columnReference"/> (already a quoted identifier, e.g. <c>[vehicle_type]</c>). An
    /// expression with no placeholder is returned unchanged.
    /// </summary>
    public static string Substitute(string expression, string columnReference)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(columnReference);
        return ColNameToken().Replace(expression, columnReference.Replace("$", "$$", StringComparison.Ordinal));
    }
}
