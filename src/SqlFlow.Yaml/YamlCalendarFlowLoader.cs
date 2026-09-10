using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Calendar;
using SqlFlow.Core.Connections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed calendar document (flowType: cal): the flow itself plus the document-local connection registry it
/// declares. The connections become an in-memory data-source store, so the flow runs through the exact same
/// resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record CalendarDocument
{
    public required CalendarFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the calendar endpoint).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a calendar-dimension flow from YAML. YamlDotNet handles the grammar; this class is the mapping and
/// validation layer that turns the parsed document into a validated <see cref="CalendarDocument"/>. Everything
/// that can be wrong about a calendar (an unknown country, culture or time zone, a backwards or absurd range, a
/// fiscal month outside 1-12) is caught here, so <c>validate</c> reports it without opening a connection.
/// </summary>
public sealed class YamlCalendarFlowLoader
{
    private const string TargetConnectionName = "target";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public CalendarDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public CalendarDocument Parse(string yaml, string source = "<inline>")
    {
        CalendarYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<CalendarYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static CalendarDocument Map(CalendarYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a calendar flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var block = y.Calendar ?? throw new FlowValidationException($"{source}: 'calendar' is required.");

        var server = YamlDocumentParts.ResolveEndpointConnection(
            block.Server, block.Connection, block.Provider, "calendar", TargetConnectionName, connections, source);

        // The dimension is created and merged with T-SQL, so the target is SQL Server by design; a foreign
        // provider is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "calendar", "a calendar flow's server", source);

        var raw = YamlDocumentParts.NullIfBlank(block.Object)
            ?? throw new FlowValidationException(
                $"{source}: 'calendar.object' is required (a three-part name like Database.Schema.Table).");

        var from = ParseDate(block.From, "calendar.from", source);
        var to = ParseDate(block.To, "calendar.to", source);
        if (to < from)
        {
            throw new FlowValidationException(
                $"{source}: 'calendar.to' ({to:yyyy-MM-dd}) is before 'calendar.from' ({from:yyyy-MM-dd}).");
        }

        var years = to.Year - from.Year + 1;
        if (years > CalendarDimensionBuilder.MaxYears)
        {
            throw new FlowValidationException(
                $"{source}: the calendar range spans {years} years, more than the {CalendarDimensionBuilder.MaxYears}-year " +
                "maximum. Declare the range the facts actually reference.");
        }

        var country = (YamlDocumentParts.NullIfBlank(block.Country)
            ?? throw new FlowValidationException(
                $"{source}: 'calendar.country' is required (supported: {string.Join(", ", CountryCalendar.Supported)})."))
            .Trim().ToUpperInvariant();

        CountryCalendar calendar;
        try
        {
            calendar = CountryCalendar.For(country);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: {ex.Message}", ex);
        }

        var culture = YamlDocumentParts.NullIfBlank(block.Culture)?.Trim() ?? calendar.DefaultCulture;
        var timezone = YamlDocumentParts.NullIfBlank(block.Timezone)?.Trim() ?? calendar.DefaultTimeZone;

        // Resolving both here means a typo fails 'validate' rather than a scheduled run at three in the morning.
        try
        {
            CalendarDimensionBuilder.ResolveCulture(culture);
            CalendarDimensionBuilder.ResolveTimeZone(timezone);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: {ex.Message}", ex);
        }

        var fiscalStart = block.FiscalYearStartMonth ?? 1;
        if (fiscalStart is < 1 or > 12)
        {
            throw new FlowValidationException(
                $"{source}: 'calendar.fiscalYearStartMonth' must be 1 through 12, not {fiscalStart}.");
        }

        var flow = new CalendarFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Table = YamlDocumentParts.ParseQualifiedObject(raw, "calendar.object", source),
            From = from,
            To = to,
            Country = country,
            Culture = culture,
            TimeZone = timezone,
            FiscalYearStartMonth = fiscalStart,
            Observances = ParseObservances(block.Observances, source),
            Rebuild = block.Rebuild ?? false,
            Description = YamlDocumentParts.NullIfBlank(y.Description),
        };

        return new CalendarDocument { Flow = flow, Connections = connections.Values.ToList() };
    }

    private static DateOnly ParseDate(string? raw, string field, string source)
    {
        var text = YamlDocumentParts.NullIfBlank(raw)?.Trim()
            ?? throw new FlowValidationException($"{source}: '{field}' is required (a date as yyyy-MM-dd).");

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FlowValidationException($"{source}: '{field}' is '{text}', which is not a date in yyyy-MM-dd form.");
        }

        return date;
    }

    private static CalendarObservanceSet ParseObservances(string? raw, string source)
    {
        var text = YamlDocumentParts.NullIfBlank(raw)?.Trim();
        if (text is null)
        {
            return CalendarObservanceSet.Full;
        }

        return text.ToLowerInvariant() switch
        {
            "full" => CalendarObservanceSet.Full,
            "publicholidays" => CalendarObservanceSet.PublicHolidays,
            "none" => CalendarObservanceSet.None,
            _ => throw new FlowValidationException(
                $"{source}: 'calendar.observances' is '{text}'. Use 'full' (public holidays plus named days, " +
                "eves, advent, equinoxes and clock changes), 'publicHolidays' (statutory days only), or 'none'."),
        };
    }
}
