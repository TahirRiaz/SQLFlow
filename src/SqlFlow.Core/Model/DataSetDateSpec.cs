using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Model;

/// <summary>
/// How <c>DataSet_DW</c> is derived for a file: from a date embedded in the file NAME when one can be detected,
/// otherwise the file's last-modified timestamp (the fallback). This ports the legacy pre-ingestion behavior
/// (the <c>flw.SysDateTimeFormat</c> lookup + <c>Functions.ExtractDateTimeFromString</c>), with the format
/// vocabulary baked into the engine and extendable per flow. <c>FileDate_DW</c> always stays the last-modified
/// timestamp (the incremental watermark); only <c>DataSet_DW</c> takes the filename date, so files load in the
/// order their data was actually produced, which is the dataset ordering a dataset-partitioned load relies on.
///
/// Detection is deterministic, not fuzzy: for each format (most specific first) a precise regex locates the
/// matching segment anywhere in the name, and <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/>
/// validates it against that same format. A segment is bounded by digit look-arounds, so a date is never matched
/// inside a longer run of digits (an id or version number).
/// </summary>
public sealed class DataSetDateSpec
{
    // The minimum year a detected date must have to be accepted; guards against a bare number parsing to year 1
    // (legacy used a 1900-01-01 sentinel for the same purpose).
    private const int MinYear = 1900;

    /// <summary>The baked-in format vocabulary. Each is a standard .NET custom date/time format. Order here is
    /// only for readability; extraction sorts by length so the most specific (longest) format wins, and for equal
    /// length the list order breaks the tie (ISO year-first forms precede day-first, and day-first precedes
    /// month-first for the European default). A flow overrides ties by supplying its own formats via
    /// <c>dataSetFormats</c>, which are tried ahead of these.</summary>
    private static readonly string[] BuiltInFormats =
    [
        // date + time (only separators valid in a file name: '-', '_', or none; ':' and '/' cannot occur, and
        // 'T'/space forms would need format-string escaping the segment tokenizer deliberately does not do)
        "yyyy-MM-dd_HH-mm-ss", "yyyy_MM_dd_HH_mm_ss", "yyyyMMdd_HHmmss", "yyyyMMdd-HHmmss", "yyyyMMddHHmmss",
        // date only, delimited
        "yyyy-MM-dd", "yyyy_MM_dd", "yyyy.MM.dd",
        "dd-MM-yyyy", "dd_MM_yyyy", "dd.MM.yyyy", "MM-dd-yyyy",
        "yyyy-M-d", "yyyy_M_d",
        // date only, compact
        "yyyyMMdd", "ddMMyyyy", "MMddyyyy",
        // Note: no year-month format ships by default. A delimited "yyyy-MM" is a prefix of "yyyy-MM-dd", so it
        // would claim the "2024-02" of an invalid "2024-02-30" and silently degrade a bad full date to a
        // year-month. A flow that genuinely lands monthly files adds "yyyy-MM" (or "yyyyMM") via dataSetFormats.
    ];

    // Format tokens, longest first, so MM is never read as two M and fff before ff before f. Case-sensitive
    // (M is month, m is minute). Anything not a token is treated as a literal separator.
    private static readonly (string Token, string Pattern)[] Tokens =
    [
        ("fffffff", "\\d{7}"), ("ffffff", "\\d{6}"), ("fffff", "\\d{5}"),
        ("yyyy", "\\d{4}"), ("ffff", "\\d{4}"),
        ("yyy", "\\d{3}"), ("fff", "\\d{3}"),
        ("yy", "\\d{2}"), ("MM", "\\d{2}"), ("dd", "\\d{2}"), ("HH", "\\d{2}"), ("hh", "\\d{2}"),
        ("mm", "\\d{2}"), ("ss", "\\d{2}"), ("ff", "\\d{2}"), ("tt", "(?:AM|PM|am|pm)"),
        ("M", "\\d{1,2}"), ("d", "\\d{1,2}"), ("H", "\\d{1,2}"), ("h", "\\d{1,2}"),
        ("m", "\\d{1,2}"), ("s", "\\d{1,2}"), ("f", "\\d"),
    ];

    private static readonly IReadOnlyList<(string Format, Regex Regex)> BuiltInCompiled = OrderBySpecificity(Compile(BuiltInFormats));

    /// <summary>The spec that never reads the file name: <c>DataSet_DW</c> is always the last-modified timestamp
    /// (the behavior for a flow that opts out with <c>dataSetFromFileName: false</c>).</summary>
    public static readonly DataSetDateSpec ModifiedOnly = new(fromFileName: false, [], null, ConventionKind.Modified);

    // The day-first vs month-first families that a single file cannot disambiguate (both components <= 12). The
    // file SET disambiguates them: an unambiguous sibling (a component > 12) forces one ordering, and that ordering
    // is applied to the ambiguous files too. Underscore/dot and ISO (year-first) forms are not ambiguous, so only
    // these two families need resolving.
    private static readonly (string DayFirst, string MonthFirst)[] AmbiguousFamilies =
    [
        ("dd-MM-yyyy", "MM-dd-yyyy"),
        ("ddMMyyyy", "MMddyyyy"),
    ];

    /// <summary>How the ambiguous day/month reading was decided, for the run's detected-convention audit line.</summary>
    private enum ConventionKind
    {
        Modified,           // DataSet_DW is the last-modified timestamp (filename detection off)
        DayFirstDefault,    // filename dates, ambiguous reads left day-first (no evidence, or day-first evidence)
        MonthFirstInferred, // filename dates, a family read month-first because the file set proved it
        DayFirstLocked,     // filename dates, day-first pinned by dataSetDayFirst
        MonthFirstLocked,   // filename dates, month-first pinned by dataSetDayFirst
    }

    private readonly IReadOnlyList<(string Format, Regex Regex)> _formats;

    // null: infer the day/month order from the file set; true/false: hard-lock day-first / month-first regardless
    // of the set (the deterministic override, dataSetDayFirst).
    private readonly bool? _dayFirstLock;

    private readonly ConventionKind _convention;

    private DataSetDateSpec(bool fromFileName, IReadOnlyList<(string Format, Regex Regex)> formats, bool? dayFirstLock, ConventionKind convention)
    {
        FromFileName = fromFileName;
        _formats = formats;
        _dayFirstLock = dayFirstLock;
        _convention = convention;
    }

    /// <summary>True when <c>DataSet_DW</c> is derived from the file name (with last-modified as the fallback).</summary>
    public bool FromFileName { get; }

    /// <summary>The effective format vocabulary, most-specific (longest) first: custom formats then built-ins.</summary>
    public IReadOnlyList<string> Formats => _formats.Select(f => f.Format).ToList();

    /// <summary>A short, human-readable statement of how <c>DataSet_DW</c> was derived for this run, for the run
    /// artifact and catalog (e.g. <c>filename dates; month-first (inferred from file set)</c> or
    /// <c>last-modified</c>). Surfaced so an operator can see what the reader detected.</summary>
    public string Convention => _convention switch
    {
        ConventionKind.Modified => "last-modified",
        ConventionKind.DayFirstDefault => "filename dates; day-first",
        ConventionKind.MonthFirstInferred => "filename dates; month-first (inferred from file set)",
        ConventionKind.DayFirstLocked => "filename dates; day-first (locked)",
        ConventionKind.MonthFirstLocked => "filename dates; month-first (locked)",
        _ => "filename dates",
    };

    /// <summary>
    /// Parses the <c>dataSet.*</c> flat options. <c>dataSetFromFileName</c> (default true) toggles filename-date
    /// derivation; <c>dataSetFormats</c> is an optional comma- or pipe-separated list of extra .NET date formats,
    /// tried ahead of the built-ins; <c>dataSetDayFirst</c> hard-locks the day-first (<c>true</c>) or month-first
    /// (<c>false</c>) reading of an ambiguous same-length date, and when absent the reading is inferred from the
    /// file set. Returns <see cref="ModifiedOnly"/> when derivation is off.
    /// </summary>
    public static DataSetDateSpec FromOptions(IReadOnlyDictionary<string, string?> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.GetBool("dataSetFromFileName", true))
        {
            return ModifiedOnly;
        }

        var extra = ParseFormats(options.GetString("dataSetFormats", string.Empty));
        var compiled = extra.Count == 0
            ? BuiltInCompiled
            : OrderBySpecificity([.. Compile(extra), .. BuiltInCompiled]);

        var dayFirstLock = ParseNullableBool(options, "dataSetDayFirst");
        var convention = ConventionKind.DayFirstDefault;
        if (dayFirstLock is { } locked)
        {
            // Apply the hard lock to every ambiguous family up front, so it holds even for a single-file read
            // that never calls ForFileSet.
            compiled = ApplyOrder(compiled, AmbiguousFamilies.Select(f => (f, preferMonthFirst: !locked)));
            convention = locked ? ConventionKind.DayFirstLocked : ConventionKind.MonthFirstLocked;
        }

        return new DataSetDateSpec(fromFileName: true, compiled, dayFirstLock, convention);
    }

    /// <summary>
    /// Resolves the day/month reading of the ambiguous same-length date families against the whole file set, then
    /// returns the spec to use for those files. When <c>dataSetDayFirst</c> is set this is a no-op (the lock was
    /// already applied). Otherwise each ambiguous family is decided independently from the set's own evidence: a
    /// file where exactly one ordering yields a valid date (a component &gt; 12) votes for that ordering; the
    /// family is read month-first only when the set gives month-first evidence and none for day-first, so a set
    /// with no evidence, or contradictory evidence, keeps the day-first default. Cheap: it scans file NAMES only,
    /// no I/O, and reuses the already-compiled formats.
    /// </summary>
    public DataSetDateSpec ForFileSet(IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (!FromFileName || _dayFirstLock is not null || _formats.Count == 0 || fileNames.Count == 0)
        {
            return this;
        }

        var decisions = AmbiguousFamilies
            .Select(family => (family, preferMonthFirst: InferMonthFirst(family, fileNames)))
            .ToList();
        if (decisions.All(d => !d.preferMonthFirst))
        {
            return this;   // every family stays day-first (the base order): nothing to rebuild
        }

        return new DataSetDateSpec(FromFileName, ApplyOrder(_formats, decisions), _dayFirstLock, ConventionKind.MonthFirstInferred);
    }

    /// <summary>The DataSet date for a file: the filename date when one is detected, else the last-modified
    /// timestamp. Equal to the last-modified timestamp when this spec does not read the file name.</summary>
    public DateTime Resolve(string fileName, DateTime fileModifiedUtc)
        => TryExtract(fileName) ?? fileModifiedUtc;

    /// <summary>The date embedded in the file name, or null when none is detected.</summary>
    public DateTime? TryExtract(string fileName)
    {
        if (!FromFileName || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        // Most specific (longest) format first; within a format, every occurrence left to right, so an earlier
        // non-date run that happens to fit the shape (an id that is not a valid date) does not stop a real date
        // later in the name from being found.
        foreach (var (format, regex) in _formats)
        {
            foreach (Match match in regex.Matches(fileName))
            {
                if (DateTime.TryParseExact(match.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                    && value.Year >= MinYear)
                {
                    return value;
                }
            }
        }

        return null;
    }

    // Whether the file set's own evidence says an ambiguous family should be read month-first: at least one file
    // where only the month-first ordering yields a valid date (a component > 12 in the day slot of the day-first
    // reading), and no file where only day-first does. Both orderings valid (a truly ambiguous name) or both
    // failing is not evidence; contradictory evidence keeps the day-first default.
    private bool InferMonthFirst((string DayFirst, string MonthFirst) family, IReadOnlyList<string> fileNames)
    {
        var day = _formats.FirstOrDefault(f => f.Format == family.DayFirst);
        var month = _formats.FirstOrDefault(f => f.Format == family.MonthFirst);
        if (day.Format is null || month.Format is null)
        {
            return false;   // the family is not in this spec's vocabulary
        }

        var dayEvidence = false;
        var monthEvidence = false;
        foreach (var name in fileNames)
        {
            var dayOk = MatchesValidly(day, name);
            var monthOk = MatchesValidly(month, name);
            if (dayOk && !monthOk)
            {
                dayEvidence = true;
            }
            else if (monthOk && !dayOk)
            {
                monthEvidence = true;
            }

            if (dayEvidence && monthEvidence)
            {
                return false;   // the set uses both orderings: keep the day-first default for ambiguous names
            }
        }

        return monthEvidence;
    }

    private static bool MatchesValidly((string Format, Regex Regex) format, string name)
    {
        foreach (Match match in format.Regex.Matches(name))
        {
            if (DateTime.TryParseExact(match.Value, format.Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                && value.Year >= MinYear)
            {
                return true;
            }
        }

        return false;
    }

    // Reorder so, for each family flagged month-first, the month-first format precedes the day-first one. They are
    // the same length, so this only changes which wins for a both-components-<=-12 date; all other formats keep
    // their order.
    private static IReadOnlyList<(string Format, Regex Regex)> ApplyOrder(
        IReadOnlyList<(string Format, Regex Regex)> formats,
        IEnumerable<((string DayFirst, string MonthFirst) Family, bool PreferMonthFirst)> decisions)
    {
        var list = formats.ToList();
        foreach (var (family, preferMonthFirst) in decisions)
        {
            if (!preferMonthFirst)
            {
                continue;
            }

            var dayIdx = list.FindIndex(f => f.Format == family.DayFirst);
            var monthIdx = list.FindIndex(f => f.Format == family.MonthFirst);
            if (dayIdx < 0 || monthIdx < 0 || monthIdx < dayIdx)
            {
                continue;   // missing, or month already first
            }

            var item = list[monthIdx];
            list.RemoveAt(monthIdx);
            list.Insert(dayIdx, item);   // dayIdx is unchanged: monthIdx > dayIdx, so the removal was after it
        }

        return list;
    }

    private static bool? ParseNullableBool(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;

    private static List<string> ParseFormats(string raw)
        => [.. raw.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<(string Format, Regex Regex)> Compile(IReadOnlyList<string> formats)
        => [.. formats.Select(f => (f, new Regex(BuildPattern(f), RegexOptions.CultureInvariant)))];

    // Stable sort longest-format-first so a full timestamp is preferred over a date-only prefix, and custom
    // formats win ties over built-ins (they precede the built-ins in the input, and OrderByDescending is stable).
    private static IReadOnlyList<(string Format, Regex Regex)> OrderBySpecificity(IReadOnlyList<(string Format, Regex Regex)> formats)
        => [.. formats.OrderByDescending(f => f.Format.Length)];

    // Convert a .NET custom date/time format into a precise regex that locates just that segment in a longer
    // string. Numeric tokens map to fixed/variable digit runs, tt to AM/PM, everything else is a literal. The
    // match is bounded by digit look-arounds so a date is never matched inside a longer run of digits (an id or
    // version number). It deliberately allows a separator-delimited neighbour (e.g. "..._2024-01-01_..."), so a
    // cleanly delimited date is still found; the built-in vocabulary carries no format that is a delimited prefix
    // of another, so this cannot degrade a full date to a partial one.
    private static string BuildPattern(string format)
    {
        var sb = new StringBuilder("(?<!\\d)");
        var i = 0;
        while (i < format.Length)
        {
            var length = MatchTokenLength(format, i, out var pattern);
            if (length > 0)
            {
                sb.Append(pattern);
                i += length;
            }
            else
            {
                sb.Append(Regex.Escape(format[i].ToString()));
                i++;
            }
        }

        sb.Append("(?!\\d)");
        return sb.ToString();
    }

    private static int MatchTokenLength(string format, int i, out string pattern)
    {
        foreach (var (token, tokenPattern) in Tokens)
        {
            if (i + token.Length <= format.Length && format.AsSpan(i, token.Length).SequenceEqual(token))
            {
                pattern = tokenPattern;
                return token.Length;
            }
        }

        pattern = string.Empty;
        return 0;
    }
}
