namespace SqlFlow.Core.Model;

/// <summary>How to handle values that don't convert to the inferred type.</summary>
public enum ConvertErrorMode
{
    /// <summary>Bad values become NULL (uses <c>TRY_CONVERT</c>). Lenient.</summary>
    SilentNull,

    /// <summary>Bad values raise an error (uses <c>CONVERT</c>). Strict / fail loudly. The default.</summary>
    Fail,

    /// <summary>Only convert columns that are 100% convertible; otherwise keep the raw string.</summary>
    KeepString,
}

/// <summary>Day/month/year ordering of a locale's short date format, used to order date-style candidates.</summary>
public enum DateOrder
{
    /// <summary>year, month, day (ISO-leaning; e.g. en-CA, sv-SE).</summary>
    Ymd,

    /// <summary>day, month, year (most of Europe; e.g. nb-NO, en-GB).</summary>
    Dmy,

    /// <summary>month, day, year (e.g. en-US).</summary>
    Mdy,
}

/// <summary>Which numeric convention a candidate normalizes from before converting.</summary>
public enum NumericFormat
{
    /// <summary>The server (or override) locale's decimal/grouping convention.</summary>
    Locale,

    /// <summary>Invariant: '.' decimal separator, ',' grouping. The universal fallback.</summary>
    Invariant,
}

/// <summary>
/// The single locale that an inference run binds to: the server's configured locale (or an explicit
/// override). It is resolved once per run and applied to every column, so ambiguous dates and numbers
/// are interpreted consistently across the whole file instead of flipping per column.
/// </summary>
public sealed record ServerLocale
{
    /// <summary>BCP-47 culture name (e.g. "nb-NO"), or "invariant" for the fallback.</summary>
    public required string Culture { get; init; }

    /// <summary>The locale's date-component ordering, used to prioritise date-style candidates.</summary>
    public required DateOrder DateOrder { get; init; }

    /// <summary>The locale's decimal separator ('.' or ',').</summary>
    public required char DecimalSeparator { get; init; }

    /// <summary>The conventional thousands separator paired with the decimal separator.</summary>
    public required char GroupSeparator { get; init; }

    /// <summary>The neutral fallback: ISO date order and invariant ('.'-decimal) numbers.</summary>
    public static ServerLocale Invariant { get; } = new()
    {
        Culture = "invariant",
        DateOrder = DateOrder.Ymd,
        DecimalSeparator = '.',
        GroupSeparator = ',',
    };
}

/// <summary>User-controlled policy for the datatype-inference / transform layer.</summary>
public sealed record TypeInferencePolicy
{
    /// <summary>Enable datatype inference (the SQLFlow <c>FetchDataTypes</c> behavior).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// What to do with values that don't fit the inferred type. Defaults to <see cref="ConvertErrorMode.Fail"/>
    /// so a value that diverges from the profiled sample halts the load instead of being silently nulled.
    /// </summary>
    public ConvertErrorMode OnConvertError { get; init; } = ConvertErrorMode.Fail;

    /// <summary>Fraction of non-null values that must convert before a type is chosen (1.0 = all).</summary>
    public double Threshold { get; init; } = 1.0;

    /// <summary>Rows to sample when profiling; 0 = full scan.</summary>
    public int SampleSize { get; init; }

    /// <summary>Keep integer-looking values with significant leading zeros (e.g. zip codes, IDs) as string.</summary>
    public bool PreserveLeadingZeros { get; init; } = true;

    /// <summary>
    /// Explicit BCP-47 culture (e.g. "nb-NO") that overrides the server-resolved locale for date and
    /// numeric interpretation; null means use the locale configured on the server.
    /// </summary>
    public string? Culture { get; init; }

    /// <summary>
    /// Generate the typed transformation view (<c>[schema].[v&lt;Table&gt;]</c>) over the flow's just-loaded
    /// table as a post-process of the load - the V3 form of the SQLFlow pre-ingestion transform view. The
    /// downstream (chained) ingestion flow reads the view as its source, which is how the raw/target tables get
    /// correct data types and how dynamic schema evolution propagates. On by default; set
    /// <c>transform.generateView: false</c> to skip it. Generates nothing when neither <see cref="Enabled"/> nor
    /// <see cref="Columns"/> asks for any typing.
    /// </summary>
    public bool GenerateView { get; init; } = true;

    /// <summary>True when the flow actually generates the transformation view this run: the post-process is
    /// enabled AND there is something to project (inference on, or authored transforms declared).</summary>
    public bool GeneratesView => GenerateView && (Enabled || Columns.Count > 0);

    /// <summary>
    /// Explicit per-column transforms authored in YAML (the source of truth), one entry per column that is cast,
    /// renamed, computed, or dropped. A faithful port of the legacy <c>flw.PreIngestionTransform</c> rows: an
    /// authored transform overrides what inference would pick for the same column, and inference (when
    /// <see cref="Enabled"/>) fills in the columns no entry names. Empty means "infer everything or pass through".
    /// </summary>
    public IReadOnlyList<ColumnTransform> Columns { get; init; } = [];
}

/// <summary>
/// One authored column transform in the pre-ingestion transformation view - a modernized port of a
/// <c>flw.PreIngestionTransform</c> row. The transform applies to a single raw column (or, when
/// <see cref="Virtual"/>, produces a computed column that has no raw counterpart). YAML is the source of truth
/// for these; the same shape is projected into the catalog so the estate can be queried for "which
/// transformations are set on a pipeline".
/// </summary>
public sealed record ColumnTransform
{
    /// <summary>
    /// The raw source column the transform applies to (legacy <c>ColumnName</c>). For a <see cref="Virtual"/>
    /// column there is no source column, so this is the name of the computed column itself.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The SQL expression producing the value (legacy <c>SelectExp</c>). The token <c>@ColName</c>
    /// (case-insensitive) is substituted with the quoted reference to <see cref="Name"/>, so a rename of the
    /// source column only touches <see cref="Name"/>. Optional for a plain typed column (then the transform is
    /// <c>CAST(@ColName AS <see cref="Type"/>)</c>); required for a <see cref="Virtual"/> column.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// The output column name in the view (legacy <c>ColumnAlias</c>). Null keeps <see cref="Name"/>. A rename:
    /// the raw table keeps <see cref="Name"/>, the typed view/target exposes the alias.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// The declared target SQL type (legacy <c>DataType</c>, e.g. <c>varchar(50)</c>, <c>decimal(18,2)</c>). When
    /// set without an <see cref="Expression"/>, the transform is <c>CAST(@ColName AS Type)</c>; when both are set
    /// the expression is used verbatim and the type is recorded as its declared result type.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>The column's position in the generated view (legacy <c>ColumnSortOrder</c>). Lower first; entries
    /// without a sort order keep declaration order after the ordered ones.</summary>
    public int? SortOrder { get; init; }

    /// <summary>
    /// A computed column with no raw source counterpart (legacy <c>Virtual</c> indicator). Requires an
    /// <see cref="Expression"/>; <c>@ColName</c> has no meaning in a virtual expression (there is no source
    /// column) and is rejected by validation.
    /// </summary>
    public bool Virtual { get; init; }

    /// <summary>
    /// Compute the column so other expressions can reference it, but drop it from the view's final projection
    /// (legacy <c>ExcludeFromView</c>). Useful for an intermediate value that feeds a virtual column.
    /// </summary>
    public bool ExcludeFromView { get; init; }
}

/// <summary>One date-style candidate the profiler counted, in locale-preferred priority order.</summary>
public sealed record DateConversionCandidate
{
    /// <summary>SQL Server <c>CONVERT</c> style code (e.g. 104 dd.mm.yyyy, 103 dd/mm/yyyy, 23 ISO).</summary>
    public required int Style { get; init; }

    /// <summary>How many non-null sampled values convert under this style.</summary>
    public required long Count { get; init; }
}

/// <summary>
/// One numeric convention the profiler counted (locale or invariant), in priority order. Carries the
/// counts plus the precision/scale measured against the normalized ('.'-decimal) form.
/// </summary>
public sealed record NumericConversionCandidate
{
    public required NumericFormat Format { get; init; }

    /// <summary>How many non-null sampled values convert to <c>decimal</c> after normalization.</summary>
    public required long AsDecimal { get; init; }

    /// <summary>How many convert to <c>float</c> after normalization (catches scientific notation).</summary>
    public required long AsFloat { get; init; }

    /// <summary>Largest number of fractional digits seen across the normalized values.</summary>
    public required int MaxScale { get; init; }

    /// <summary>Largest number of integer (pre-decimal) digits seen across the normalized values.</summary>
    public required int MaxIntegerDigits { get; init; }
}

/// <summary>
/// The result of profiling one raw (string) column against SQL Server's own conversions. Counts are
/// how many non-null sampled values <c>TRY_CONVERT</c> accepts for each candidate - so any type
/// chosen from these counts is, by construction, convertible by SQL Server. Date and numeric
/// candidates are ordered locale-preferred-first so the inferencer can pick the first that fits.
/// </summary>
public sealed record ColumnProfile
{
    public required string ColumnName { get; init; }
    public long Total { get; init; }
    public long NonNull { get; init; }
    public long AsBigInt { get; init; }
    public long AsGuid { get; init; }
    public long AsBitTokens { get; init; }
    public long LeadingZeroInts { get; init; }
    public int MaxLen { get; init; }
    public long? MinValue { get; init; }
    public long? MaxValue { get; init; }

    /// <summary><c>datetime2</c> style candidates, locale-preferred first.</summary>
    public IReadOnlyList<DateConversionCandidate> DateTimeCandidates { get; init; } = [];

    /// <summary><c>date</c> style candidates, locale-preferred first.</summary>
    public IReadOnlyList<DateConversionCandidate> DateCandidates { get; init; } = [];

    /// <summary>Numeric convention candidates (locale, then invariant), preferred first.</summary>
    public IReadOnlyList<NumericConversionCandidate> NumericCandidates { get; init; } = [];
}

/// <summary>An inferred column: the chosen SQL type and the SELECT expression that produces it.</summary>
public sealed record InferredColumn
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required string SelectExpression { get; init; }
    public bool Converted { get; init; }

    /// <summary>CONVERT/TRY_CONVERT style code (date formats); null when not applicable.</summary>
    public int? Style { get; init; }

    /// <summary>
    /// The numeric convention used to normalize the column before conversion; null for non-numeric
    /// columns. Lets validation rebuild the exact same normalization the transform applies.
    /// </summary>
    public NumericFormat? NumericFormat { get; init; }
}
