using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Dataverse;

/// <summary>
/// The validated settings of a <c>source.type: dataverse</c> file flow: which Dataverse environment and table it reads,
/// which columns, how it pages, how it signs in, and how patient it is with the service. Pure (no HTTP), so the YAML
/// loader validates an authored document offline with <see cref="ValidateAuthored"/> and the reader parses the same
/// options at run time with <see cref="Parse"/>, after the engine has resolved a <c>${...}</c> location. Every problem
/// is reported with the option it is about, before anything is read or written.
/// </summary>
public sealed partial record DataverseSourceOptions
{
    /// <summary>The <c>source.type</c> token that selects the Dataverse reader.</summary>
    public const string SourceType = "dataverse";

    /// <summary>The table's logical name (<c>account</c>, <c>cr6b2_project</c>). Required.</summary>
    public const string TableOption = "table";

    /// <summary>Comma-separated logical names to read; absent reads every readable column.</summary>
    public const string ColumnsOption = "columns";

    /// <summary>Comma-separated logical names to leave out of an otherwise full read.</summary>
    public const string ExcludeColumnsOption = "excludeColumns";

    /// <summary>An OData <c>$filter</c> expression AND-combined with the engine's watermark bound.</summary>
    public const string FilterOption = "filter";

    /// <summary>Rows per page (the <c>odata.maxpagesize</c> preference), 1 to 5000.</summary>
    public const string PageSizeOption = "pageSize";

    /// <summary>When true, each choice, yes/no and lookup column gets a sibling <c>&lt;column&gt;name</c> label column.</summary>
    public const string FormattedValuesOption = "formattedValues";

    /// <summary>The Microsoft Entra tenant of an explicit service principal.</summary>
    public const string TenantIdOption = "tenantId";

    /// <summary>The application (client) id of an explicit service principal.</summary>
    public const string ClientIdOption = "clientId";

    /// <summary>The client secret of an explicit service principal: a whole <c>${...}</c> reference, never a literal.</summary>
    public const string ClientSecretOption = "clientSecret";

    /// <summary>The Microsoft Entra authority host, for a sovereign cloud (<c>https://login.microsoftonline.us</c>).</summary>
    public const string AuthorityHostOption = "authorityHost";

    /// <summary>The Web API version (<c>9.2</c>).</summary>
    public const string ApiVersionOption = "apiVersion";

    /// <summary>Seconds one request may take before it is retried as a transient failure.</summary>
    public const string TimeoutSecondsOption = "timeoutSeconds";

    /// <summary>Total attempts per request, the first included.</summary>
    public const string MaxAttemptsOption = "maxAttempts";

    /// <summary>The longest single wait between attempts, which also bounds an honored <c>Retry-After</c>.</summary>
    public const string MaxRetryDelaySecondsOption = "maxRetryDelaySeconds";

    /// <summary>The largest response page held in memory, in megabytes.</summary>
    public const string MaxPageMegabytesOption = "maxPageMegabytes";

    /// <summary>The engine's injected option carrying the flow's identity (always present, never authored).</summary>
    private const string FlowIdOption = "flowId";

    /// <summary>The largest page Dataverse returns.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>The Web API version read when <see cref="ApiVersionOption"/> is not set.</summary>
    public const string DefaultApiVersion = "9.2";

    /// <summary>Every option an author may write on a dataverse source.</summary>
    public static readonly IReadOnlyList<string> AuthoredOptions =
    [
        TableOption, ColumnsOption, ExcludeColumnsOption, FilterOption, PageSizeOption, FormattedValuesOption,
        TenantIdOption, ClientIdOption, ClientSecretOption, AuthorityHostOption, ApiVersionOption,
        TimeoutSecondsOption, MaxAttemptsOption, MaxRetryDelaySecondsOption, MaxPageMegabytesOption,
    ];

    // Options the engine injects for file selection or a run's parameters. A Dataverse table has no files, so each one
    // asks for something this source cannot honour, and silently reading the whole table instead would load the wrong
    // rows. The message says what to do instead.
    private static readonly (string Option, string Meaning)[] FileOnlyRunOptions =
    [
        ("incrementalAfterDate", "a file-date incremental watermark"),
        ("initFromFileDate", "a backfill window"),
        ("initToFileDate", "a backfill window"),
        ("srcFile", "a file pattern"),
    ];

    /// <summary>The environment's origin (<c>https://contoso.crm4.dynamics.com</c>), always without a path.</summary>
    public required Uri Environment { get; init; }

    /// <summary>The table's logical name, lower case.</summary>
    public required string Table { get; init; }

    /// <summary>The logical names to read, lower case, in the order written; empty reads every readable column.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The logical names to leave out, lower case.</summary>
    public IReadOnlyList<string> ExcludeColumns { get; init; } = [];

    /// <summary>The authored OData filter, or null.</summary>
    public string? Filter { get; init; }

    public int PageSize { get; init; } = MaxPageSize;

    public bool FormattedValues { get; init; }

    /// <summary>The explicit service principal, or null to sign in with the host's ambient Azure identity.</summary>
    public DataverseServicePrincipal? ServicePrincipal { get; init; }

    /// <summary>The Microsoft Entra authority host, or null for the public cloud.</summary>
    public Uri? AuthorityHost { get; init; }

    public string ApiVersion { get; init; } = DefaultApiVersion;

    public int TimeoutSeconds { get; init; } = 300;

    public int MaxAttempts { get; init; } = 8;

    public int MaxRetryDelaySeconds { get; init; } = 300;

    public int MaxPageMegabytes { get; init; } = 256;

    /// <summary>The engine's row-level watermark bound for this run, or null when the run reads every row.</summary>
    public DataverseWatermarkBound? WatermarkBound { get; init; }

    /// <summary>The flow's declared watermark column the rows are read in order of, or null when none is declared.</summary>
    public string? OrderColumn { get; init; }

    /// <summary>The Web API root every request is made under (<c>https://host/api/data/v9.2/</c>).</summary>
    public Uri ApiRoot => new(Environment, $"api/data/v{ApiVersion}/");

    /// <summary>The OAuth scope that grants access to this environment.</summary>
    public string Scope => Environment.GetLeftPart(UriPartial.Authority) + "/.default";

    /// <summary>True when <paramref name="sourceType"/> selects the Dataverse reader (case-insensitive, trimmed).</summary>
    public static bool IsDataverse(string? sourceType)
        => sourceType is not null && sourceType.Trim().Equals(SourceType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lineage identity of the table a dataverse source reads: its location (resolved or still a reference) and its
    /// table, so two flows reading different tables of one environment are two nodes, and the same table one.
    /// </summary>
    public static string Identity(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var location = (source.Location ?? string.Empty).Trim().TrimEnd('/');
        var table = Option(source.Options, TableOption)?.Trim().ToLowerInvariant() ?? string.Empty;
        return $"{location}/{table}";
    }

    /// <summary>How a person reads what a dataverse source reads: <c>the Dataverse table 'account' in https://...</c>.</summary>
    public static string Describe(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var table = Option(source.Options, TableOption)?.Trim().ToLowerInvariant() ?? "(no table)";
        return $"the Dataverse table '{table}' in {source.Location}";
    }

    /// <summary>
    /// Validates a dataverse source as written, offline: every authored option, the location unless it names a reference
    /// (resolved only when the flow runs), and the incremental block, which for a table must be a row-level watermark.
    /// </summary>
    /// <exception cref="SqlFlowException">The first problem found, naming the option.</exception>
    public static void ValidateAuthored(SourceSpec source, IncrementalSpec? incremental)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var key in source.Options.Keys)
        {
            if (!key.Equals(FlowIdOption, StringComparison.OrdinalIgnoreCase)
                && !AuthoredOptions.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException(
                    $"A dataverse source does not take the option '{key}'. Its options are: {string.Join(", ", AuthoredOptions)}.");
            }
        }

        if (incremental is not null && string.IsNullOrWhiteSpace(incremental.WatermarkColumn))
        {
            throw new SqlFlowException(
                "A dataverse source reads a table, not files, so its incremental block needs 'watermarkColumn' "
                + "(versionnumber or modifiedon); 'dateColumn' and 'overlapDays' bound files.");
        }

        if (incremental?.WatermarkColumn is { } watermark)
        {
            LogicalName(watermark, "incremental.watermarkColumn");
        }

        RejectFileOnlyRunOptions(source.Options);
        ReadOptions(source.Options);

        // A location that names a reference is resolved only when the flow runs, by the run's own resolver.
        if (!SourceLocations.NamesReference(source.Location))
        {
            EnvironmentUri(source.Location);
        }
    }

    /// <summary>
    /// Parses the options of a dataverse source whose location is already resolved, together with whatever bound and
    /// order the engine injected for this run.
    /// </summary>
    /// <exception cref="SqlFlowException">An option is missing, malformed or out of range, or the engine asked for a
    /// file-only selection this source cannot honour.</exception>
    public static DataverseSourceOptions Parse(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RejectFileOnlyRunOptions(source.Options);
        var environment = EnvironmentUri(source.Location);
        var read = ReadOptions(source.Options);
        return read with { Environment = environment };
    }

    private static void RejectFileOnlyRunOptions(IReadOnlyDictionary<string, string?> options)
    {
        foreach (var (option, meaning) in FileOnlyRunOptions)
        {
            if (!string.IsNullOrWhiteSpace(Option(options, option)))
            {
                throw new SqlFlowException(
                    $"A dataverse source reads a table, not files, so {meaning} ('{option}') does not apply. "
                    + "Declare incremental.watermarkColumn for an incremental read, or run with --full to read every row.");
            }
        }
    }

    /// <summary>Every option but the location, read and checked. The environment is left at the Dataverse default host
    /// form until <see cref="Parse"/> sets the resolved one; nothing reads it before then.</summary>
    private static DataverseSourceOptions ReadOptions(IReadOnlyDictionary<string, string?> options)
    {
        var table = LogicalName(Option(options, TableOption)
            ?? throw new SqlFlowException("A dataverse source needs the option 'table': the table's logical name, such as account."), TableOption);

        var columns = Names(Option(options, ColumnsOption), ColumnsOption);
        var exclude = Names(Option(options, ExcludeColumnsOption), ExcludeColumnsOption);
        if (columns.Count > 0 && exclude.Count > 0)
        {
            throw new SqlFlowException(
                $"A dataverse source takes '{ColumnsOption}' (the columns to read) or '{ExcludeColumnsOption}' (the columns to leave out), not both.");
        }

        var servicePrincipal = ServicePrincipalOf(options);
        var authorityHost = AuthorityHostOf(options);
        if (authorityHost is not null && servicePrincipal is null)
        {
            throw new SqlFlowException(
                $"'{AuthorityHostOption}' applies to the service principal named by '{TenantIdOption}', '{ClientIdOption}' and "
                + $"'{ClientSecretOption}'. The host's own Azure identity takes its authority from AZURE_AUTHORITY_HOST.");
        }

        return new DataverseSourceOptions
        {
            Environment = UnresolvedEnvironment,
            Table = table,
            Columns = columns,
            ExcludeColumns = exclude,
            Filter = NullIfBlank(Option(options, FilterOption)),
            PageSize = Int(options, PageSizeOption, MaxPageSize, 1, MaxPageSize),
            FormattedValues = Bool(options, FormattedValuesOption),
            ServicePrincipal = servicePrincipal,
            AuthorityHost = authorityHost,
            ApiVersion = ApiVersionOf(options),
            TimeoutSeconds = Int(options, TimeoutSecondsOption, 300, 10, 3600),
            MaxAttempts = Int(options, MaxAttemptsOption, 8, 1, 20),
            MaxRetryDelaySeconds = Int(options, MaxRetryDelaySecondsOption, 300, 1, 3600),
            MaxPageMegabytes = Int(options, MaxPageMegabytesOption, 256, 1, 2048),
            WatermarkBound = BoundOf(options),
            OrderColumn = OrderOf(options),
        };
    }

    // The environment of options read before the location is resolved: an address in the reserved .invalid domain
    // (RFC 6761), which no request can reach. Parse replaces it before the options leave this type.
    private static readonly Uri UnresolvedEnvironment = new("https://unresolved.invalid/");

    private static Uri EnvironmentUri(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new SqlFlowException(
                "A dataverse source needs 'location': the environment URL, such as https://contoso.crm4.dynamics.com.");
        }

        var trimmed = location.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            throw new SqlFlowException(
                $"The dataverse source location '{trimmed}' is not an https environment URL, such as https://contoso.crm4.dynamics.com.");
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SqlFlowException(
                $"The dataverse source location '{trimmed}' must be the environment URL alone (https://{uri.Host}); "
                + $"the reader adds the Web API path itself. Set '{ApiVersionOption}' to read another Web API version.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    /// <summary>A Dataverse logical name: letters, digits and underscores, starting with a letter. Returned lower case,
    /// which is how Dataverse spells every logical name.</summary>
    private static string LogicalName(string value, string option)
    {
        var trimmed = value.Trim();
        if (!LogicalNamePattern().IsMatch(trimmed))
        {
            throw new SqlFlowException(
                $"'{option}' value '{trimmed}' is not a Dataverse logical name (letters, digits and underscores, starting with a letter, at most 128 characters).");
        }

        return trimmed.ToLowerInvariant();
    }

    private static List<string> Names(string? raw, string option)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = LogicalName(part, option);
            if (!seen.Add(name))
            {
                throw new SqlFlowException($"'{option}' names the column '{name}' twice.");
            }

            names.Add(name);
        }

        return names;
    }

    private static DataverseServicePrincipal? ServicePrincipalOf(IReadOnlyDictionary<string, string?> options)
    {
        var tenant = NullIfBlank(Option(options, TenantIdOption));
        var client = NullIfBlank(Option(options, ClientIdOption));
        var secret = NullIfBlank(Option(options, ClientSecretOption));
        if (tenant is null && client is null && secret is null)
        {
            return null;
        }

        if (tenant is null || client is null || secret is null)
        {
            throw new SqlFlowException(
                $"A dataverse service principal needs all of '{TenantIdOption}', '{ClientIdOption}' and '{ClientSecretOption}'; "
                + "leave all three out to sign in with the host's Azure identity (SQLFLOW_AZURE_AUTH).");
        }

        if (!SuppliedReferences.IsWholeReference(secret))
        {
            throw new SqlFlowException(
                $"'{ClientSecretOption}' must be a ${{env:NAME}} or ${{keyvault:vault/secret}} reference, never a literal.");
        }

        return new DataverseServicePrincipal(tenant, client, secret);
    }

    private static Uri? AuthorityHostOf(IReadOnlyDictionary<string, string?> options)
    {
        var raw = NullIfBlank(Option(options, AuthorityHostOption));
        if (raw is null)
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SqlFlowException($"'{AuthorityHostOption}' value '{raw}' is not an https URL, such as https://login.microsoftonline.us.");
        }

        return uri;
    }

    private static string ApiVersionOf(IReadOnlyDictionary<string, string?> options)
    {
        var raw = NullIfBlank(Option(options, ApiVersionOption));
        if (raw is null)
        {
            return DefaultApiVersion;
        }

        var version = raw.TrimStart('v', 'V');
        if (!ApiVersionPattern().IsMatch(version))
        {
            throw new SqlFlowException($"'{ApiVersionOption}' value '{raw}' is not a Web API version such as 9.2.");
        }

        return version;
    }

    private static DataverseWatermarkBound? BoundOf(IReadOnlyDictionary<string, string?> options)
    {
        var column = NullIfBlank(Option(options, WatermarkPredicate.ColumnOption));
        if (column is null)
        {
            return null;
        }

        var kindName = NullIfBlank(Option(options, WatermarkPredicate.KindOption))
            ?? throw new SqlFlowException($"The engine's watermark bound for '{column}' carries no kind.");
        return new DataverseWatermarkBound(
            LogicalName(column, "incremental.watermarkColumn"),
            Option(options, WatermarkPredicate.ValueOption) ?? string.Empty,
            WatermarkPredicate.ParseKind(kindName));
    }

    private static string? OrderOf(IReadOnlyDictionary<string, string?> options)
    {
        var column = NullIfBlank(Option(options, WatermarkPredicate.OrderColumnOption))
            ?? NullIfBlank(Option(options, WatermarkPredicate.ColumnOption));
        return column is null ? null : LogicalName(column, "incremental.watermarkColumn");
    }

    private static int Int(IReadOnlyDictionary<string, string?> options, string option, int fallback, int min, int max)
    {
        var raw = NullIfBlank(Option(options, option));
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new SqlFlowException($"'{option}' value '{raw}' is not a whole number from {min} to {max}.");
        }

        return value;
    }

    private static bool Bool(IReadOnlyDictionary<string, string?> options, string option)
    {
        var raw = NullIfBlank(Option(options, option));
        return raw is null
            ? false
            : bool.TryParse(raw, out var value)
                ? value
                : throw new SqlFlowException($"'{option}' value '{raw}' is not true or false.");
    }

    private static string? Option(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) ? value : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalNamePattern();

    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();
}

/// <summary>
/// An explicit service principal for a dataverse source. <see cref="TenantId"/> and <see cref="ClientId"/> are written
/// as values or references; <see cref="ClientSecretReference"/> is always a whole <c>${...}</c> reference, resolved only
/// when the flow runs and never held anywhere else.
/// </summary>
public sealed record DataverseServicePrincipal(string TenantId, string ClientId, string ClientSecretReference);

/// <summary>The engine's row-level watermark bound as the reader receives it: the column, its canonical value and kind.</summary>
public sealed record DataverseWatermarkBound(string Column, string CanonicalValue, WatermarkKind Kind);
