namespace SqlFlow.Yaml;

// Binding-only DTOs for the calendar-dimension document (flowType: cal). Every property is nullable; all
// defaults and validation live in YamlCalendarFlowLoader, which maps these into the immutable
// SqlFlow.Core.Calendar.CalendarFlow.

internal sealed class CalendarYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference) or a map with
    /// 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }

    public CalendarBlockYaml? Calendar { get; set; }
}

internal sealed class CalendarBlockYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c>; SQL Server when omitted. The dimension is written
    /// with T-SQL, so anything but mssql/azdb is rejected at parse time.</summary>
    public string? Provider { get; set; }

    /// <summary>The three-part table the dimension is generated into.</summary>
    public string? Object { get; set; }

    /// <summary>The first date in the dimension, inclusive, as yyyy-MM-dd.</summary>
    public string? From { get; set; }

    /// <summary>The last date in the dimension, inclusive, as yyyy-MM-dd.</summary>
    public string? To { get; set; }

    /// <summary>The ISO 3166-1 alpha-2 country whose observances and season names are used.</summary>
    public string? Country { get; set; }

    /// <summary>The culture day and month names come from; defaults from the country.</summary>
    public string? Culture { get; set; }

    /// <summary>The IANA time zone the daylight-saving flag follows; defaults from the country.</summary>
    public string? Timezone { get; set; }

    public int? FiscalYearStartMonth { get; set; }

    /// <summary>full, publicHolidays, or none.</summary>
    public string? Observances { get; set; }

    public bool? Rebuild { get; set; }
}
