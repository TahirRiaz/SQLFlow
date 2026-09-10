namespace SqlFlow.Yaml;

// Binding-only DTOs for the translate document (flowType: trl). Every property is nullable; all defaults and
// validation live in YamlTranslateFlowLoader, which maps these into the immutable SqlFlow.Core.Translate models.
// The template itself binds as a raw object graph (mappings, sequences, scalars) and is compiled by the loader.

internal sealed class TranslateYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference, the back-compatible
    /// form) or a map with 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }

    public TranslateSourceYaml? Source { get; set; }
    public List<TranslateDatasetYaml>? Datasets { get; set; }
    public TranslateDocumentsYaml? Documents { get; set; }

    /// <summary>The declared JSON shape, as the raw YAML object graph the loader compiles.</summary>
    public object? Template { get; set; }

    public TranslateOutputYaml? Output { get; set; }
    public TranslateInvokeYaml? Invoke { get; set; }
}

internal sealed class TranslateSourceYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c>; SQL Server when omitted. The translation read is
    /// T-SQL, so anything but mssql/azdb is rejected at parse time.</summary>
    public string? Provider { get; set; }

    public string? Query { get; set; }
}

internal sealed class TranslateDatasetYaml
{
    public string? Name { get; set; }
    public string? Query { get; set; }
    public List<string>? Bind { get; set; }
}

internal sealed class TranslateDocumentsYaml
{
    public string? Per { get; set; }
    public string? Nulls { get; set; }
}

internal sealed class TranslateOutputYaml
{
    public string? Path { get; set; }
    public string? Mode { get; set; }
    public string? FileName { get; set; }
    public bool? AddTimestamp { get; set; }
    public bool? Indent { get; set; }
}

internal sealed class TranslateInvokeYaml
{
    public string? Url { get; set; }
    public string? Method { get; set; }
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>The exact same auth surface as an acquisition flow's <c>source.auth</c>.</summary>
    public AcquireAuthYaml? Auth { get; set; }

    public int? BatchSize { get; set; }
    public string? EnvelopeKey { get; set; }

    /// <summary>The exact same reliability surface as an acquisition flow's <c>source.reliability</c>.</summary>
    public AcquireReliabilityYaml? Reliability { get; set; }
}
