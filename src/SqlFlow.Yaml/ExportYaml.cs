namespace SqlFlow.Yaml;

// Binding-only DTOs for the export document (flowType: exp). Every property is nullable; all defaults and
// validation live in YamlExportFlowLoader, which maps these into the immutable SqlFlow.Core.Export.ExportFlow.

internal sealed class ExportYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference, the back-compatible
    /// form) or a map with 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }
    public ExportSourceYaml? Source { get; set; }
    public ExportTargetYaml? Target { get; set; }
    public ExportChunkYaml? Export { get; set; }
    public string? PostInvoke { get; set; }
    public Dictionary<string, InvokeBlockYaml>? Invokes { get; set; }
    public Dictionary<string, ServicePrincipalYaml>? ServicePrincipals { get; set; }
    public bool? OnErrorResume { get; set; }
}

internal sealed class ExportSourceYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c>; SQL Server when omitted. An export reads with
    /// T-SQL, so anything but mssql/azdb is rejected at parse time.</summary>
    public string? Provider { get; set; }

    public string? Object { get; set; }
    public string? Table { get; set; }

    /// <summary>A table hint applied to every chunk read (legacy srcWithHint), e.g. <c>WITH (NOLOCK)</c> or
    /// <c>WITH (INDEX([NCI_CalendarID]))</c>. Written as it appears in the SELECT.</summary>
    public string? WithHint { get; set; }

    /// <summary>A static predicate ANDed to every chunk's read (legacy srcFilter). A leading AND is optional.</summary>
    public string? Filter { get; set; }
}

internal sealed class ExportTargetYaml
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public string? Encoding { get; set; }
    public string? Compression { get; set; }
    public string? Delimiter { get; set; }
    public string? TextQualifier { get; set; }
    public bool? AddTimestamp { get; set; }
    public string? SubfolderPattern { get; set; }

    /// <summary>Compress each written file into a single-entry .zip in place (legacy ZipTrg).</summary>
    public bool? Zip { get; set; }

    /// <summary>How values are rendered as CSV text: <c>iso</c> (default) or <c>legacy</c>.</summary>
    public string? ValueFormat { get; set; }
}

internal sealed class ExportChunkYaml
{
    public string? By { get; set; }
    public int? Size { get; set; }
    public string? DateColumn { get; set; }
    public string? KeyColumn { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public int? Threads { get; set; }
}
