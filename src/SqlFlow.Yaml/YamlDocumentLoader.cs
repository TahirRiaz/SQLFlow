using SqlFlow.Core;
using SqlFlow.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>A loaded flow document: a file flow (CSV/JSON/XML/XLS/Parquet into SQL), a relational
/// table-to-table ingestion flow, a file export, a stored-procedure flow, a standalone invoke flow, or an ML
/// health check, discriminated by the document's <c>flowType</c> key.</summary>
public abstract record FlowDocument
{
    /// <summary>The flow's declared schedule from its top-level <c>schedule:</c> block, or null when it declares
    /// none. Captured at the document envelope so every flow kind carries a schedule the same way; the control
    /// plane turns it into actual runs (the engine itself never schedules anything).</summary>
    public ScheduleSpec? Schedule { get; init; }

    /// <summary>The flow's declared execution mode from its top-level <c>mode:</c> key (auto | manual; absent is
    /// auto). Captured at the document envelope so every flow kind carries it the same way: a <c>mode: manual</c>
    /// pipeline is excluded from every group expansion (a schedule's member set, and a Node run's descendant
    /// set), and runs only when named directly, which IS the manual trigger. This is how a deactivated pipeline
    /// (a retired source, a run-once replay) is kept out of automatic execution while staying runnable by hand.
    /// A health-check document reads the same key into its own flow model as well; the two never disagree
    /// because they bind the same YAML scalar.</summary>
    public Core.Runs.ExecutionMode Mode { get; init; }
}

/// <summary>A file-source flow document (no <c>flowType</c> key, the original document shape).</summary>
public sealed record FileFlowDocument : FlowDocument
{
    public required FlowDefinition Flow { get; init; }
}

/// <summary>A relational ingestion flow document (<c>flowType: ing</c>).</summary>
public sealed record IngestionFlowDocument : FlowDocument
{
    public required IngestionDocument Document { get; init; }
}

/// <summary>An export flow document (<c>flowType: exp</c>).</summary>
public sealed record ExportFlowDocument : FlowDocument
{
    public required ExportDocument Document { get; init; }
}

/// <summary>A stored-procedure flow document (<c>flowType: sp</c>).</summary>
public sealed record StoredProcedureFlowDocument : FlowDocument
{
    public required StoredProcedureDocument Document { get; init; }
}

/// <summary>A standalone invoke flow document (<c>flowType: inv</c>).</summary>
public sealed record InvokeFlowDocument : FlowDocument
{
    public required InvokeDocument Document { get; init; }
}

/// <summary>An ML health-check flow document (<c>flowType: hc</c>).</summary>
public sealed record HealthCheckFlowDocument : FlowDocument
{
    public required HealthCheckDocument Document { get; init; }
}

/// <summary>A source-control flow document (<c>flowType: scm</c>).</summary>
public sealed record SourceControlFlowDocument : FlowDocument
{
    public required SourceControlDocument Document { get; init; }
}

/// <summary>A batch flow document (<c>flowType: batch</c>).</summary>
public sealed record BatchFlowDocument : FlowDocument
{
    public required BatchDocument Document { get; init; }
}

/// <summary>A generic acquisition flow document (<c>flowType: api</c>): fetch from a third-party system over any
/// transport (HTTP, SFTP, S3, Azure Table) and land the raw payloads in the lake.</summary>
public sealed record AcquireFlowDocument : FlowDocument
{
    public required SqlFlow.Core.Acquire.AcquireFlow Flow { get; init; }
}

/// <summary>A file-copy flow document (<c>flowType: cpy</c>): copy files byte-for-byte between storage endpoints
/// (local disk, Azure Blob/ADLS) in any direction, with optional zip/unzip.</summary>
public sealed record CopyFlowDocument : FlowDocument
{
    public required SqlFlow.Core.Copy.CopyFlow Flow { get; init; }
}

/// <summary>An SFTP transfer flow document (<c>flowType: sftp</c>): download files from an SFTP server into the
/// lake/local, or upload the other way.</summary>
public sealed record SftpFlowDocument : FlowDocument
{
    public required SqlFlow.Core.Sftp.SftpFlow Flow { get; init; }
}

/// <summary>A calendar-dimension flow document (<c>flowType: cal</c>): generate a date dimension for a declared
/// range and merge it into a table. The only flow kind with no data source: its rows are computed.</summary>
public sealed record CalendarFlowDocument : FlowDocument
{
    public required CalendarDocument Document { get; init; }
}

/// <summary>A translation flow document (<c>flowType: trl</c>): map a SQL query result through a declared JSON
/// template into shaped documents, write them to a destination, and optionally deliver them to a remote API.</summary>
public sealed record TranslateFlowDocument : FlowDocument
{
    public required TranslateDocument Document { get; init; }
}

/// <summary>
/// The single entry point for loading any flow document: it sniffs the root <c>flowType</c> key with a cheap
/// probe pass, then delegates to the matching loader. No key (the long-standing default) means a file flow;
/// <c>ing</c> means a relational ingestion flow, <c>exp</c> a file export, <c>sp</c> a stored-procedure flow,
/// <c>inv</c> a standalone invoke, <c>hc</c> an ML health check; anything else is a clear error rather than a
/// confusing downstream validation failure.
/// </summary>
public sealed class YamlDocumentLoader
{
    private sealed class DocumentKindYaml
    {
        public string? FlowType { get; set; }

        public ScheduleYaml? Schedule { get; set; }

        public string? Mode { get; set; }
    }

    private sealed class ScheduleYaml
    {
        /// <summary>Set when the key was a bare scalar (<c>schedule: nightly</c>) or a sequence
        /// (<c>schedule: [nightly, hourly]</c>): the named schedules this flow joins.</summary>
        public List<string> Refs { get; set; } = [];

        public string? Name { get; set; }

        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>The mapping shape the converter delegates an inline <c>schedule:</c> block to: the same inline fields
    /// as <see cref="ScheduleYaml"/> minus the reference-only <c>Refs</c>. It is deliberately NOT accepted by
    /// <see cref="ScheduleYamlConverter"/>, so the normal object deserializer binds it (camelCase, coercion,
    /// unknown-key tolerance) without recursing back into the converter.</summary>
    private sealed class InlineScheduleYaml
    {
        public string? Name { get; set; }

        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>
    /// The <c>schedule:</c> key is written three ways: a bare scalar (joining one shared schedule by name, e.g.
    /// <c>schedule: nightly</c>), a sequence (joining several, e.g. <c>schedule: [nightly, hourly]</c>, so one flow
    /// can sit in a nightly full refresh and an hourly subset), or a mapping (an inline cadence, optionally carrying
    /// a <c>name:</c> to publish it for others to join). YamlDotNet cannot bind all three shapes to one type, so this
    /// converter reads the scalar and sequence forms itself and hands the mapping form to the normal deserializer.
    /// </summary>
    private sealed class ScheduleYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ScheduleYaml);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            // schedule: nightly
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return string.IsNullOrWhiteSpace(scalar.Value)
                    ? null
                    : new ScheduleYaml { Refs = [scalar.Value.Trim()] };
            }

            // schedule: [nightly, hourly]
            if (parser.Current is SequenceStart)
            {
                var names = rootDeserializer(typeof(List<string>)) as List<string> ?? [];
                var refs = names
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return refs.Count == 0 ? null : new ScheduleYaml { Refs = refs };
            }

            if (rootDeserializer(typeof(InlineScheduleYaml)) is not InlineScheduleYaml inline)
            {
                return null;
            }

            return new ScheduleYaml
            {
                Name = inline.Name,
                Cron = inline.Cron,
                IntervalSeconds = inline.IntervalSeconds,
                Timezone = inline.Timezone,
                Enabled = inline.Enabled,
                Catchup = inline.Catchup,
                MaxConcurrency = inline.MaxConcurrency,
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => throw new NotSupportedException("The schedule probe is read-only.");
    }

    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ScheduleYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly YamlFlowLoader _fileFlows;
    private readonly YamlIngestionFlowLoader _ingestionFlows;
    private readonly YamlExportFlowLoader _exportFlows;
    private readonly YamlStoredProcedureFlowLoader _storedProcedureFlows;
    private readonly YamlInvokeFlowLoader _invokeFlows;
    private readonly YamlHealthCheckFlowLoader _healthCheckFlows;
    private readonly YamlSourceControlFlowLoader _sourceControlFlows;
    private readonly YamlBatchFlowLoader _batchFlows;
    private readonly YamlAcquireFlowLoader _acquireFlows;
    private readonly YamlCopyFlowLoader _copyFlows;
    private readonly YamlSftpFlowLoader _sftpFlows;
    private readonly YamlCalendarFlowLoader _calendarFlows;
    private readonly YamlTranslateFlowLoader _translateFlows;

    public YamlDocumentLoader(
        YamlFlowLoader fileFlows,
        YamlIngestionFlowLoader ingestionFlows,
        YamlExportFlowLoader exportFlows,
        YamlStoredProcedureFlowLoader storedProcedureFlows,
        YamlInvokeFlowLoader invokeFlows,
        YamlHealthCheckFlowLoader healthCheckFlows,
        YamlSourceControlFlowLoader sourceControlFlows,
        YamlBatchFlowLoader batchFlows,
        YamlAcquireFlowLoader acquireFlows,
        YamlCopyFlowLoader copyFlows,
        YamlSftpFlowLoader sftpFlows,
        YamlCalendarFlowLoader calendarFlows,
        YamlTranslateFlowLoader translateFlows)
    {
        ArgumentNullException.ThrowIfNull(fileFlows);
        ArgumentNullException.ThrowIfNull(ingestionFlows);
        ArgumentNullException.ThrowIfNull(exportFlows);
        ArgumentNullException.ThrowIfNull(storedProcedureFlows);
        ArgumentNullException.ThrowIfNull(invokeFlows);
        ArgumentNullException.ThrowIfNull(healthCheckFlows);
        ArgumentNullException.ThrowIfNull(sourceControlFlows);
        ArgumentNullException.ThrowIfNull(batchFlows);
        ArgumentNullException.ThrowIfNull(acquireFlows);
        ArgumentNullException.ThrowIfNull(copyFlows);
        ArgumentNullException.ThrowIfNull(sftpFlows);
        ArgumentNullException.ThrowIfNull(calendarFlows);
        ArgumentNullException.ThrowIfNull(translateFlows);
        _fileFlows = fileFlows;
        _ingestionFlows = ingestionFlows;
        _exportFlows = exportFlows;
        _storedProcedureFlows = storedProcedureFlows;
        _invokeFlows = invokeFlows;
        _healthCheckFlows = healthCheckFlows;
        _sourceControlFlows = sourceControlFlows;
        _batchFlows = batchFlows;
        _acquireFlows = acquireFlows;
        _copyFlows = copyFlows;
        _sftpFlows = sftpFlows;
        _calendarFlows = calendarFlows;
        _translateFlows = translateFlows;
    }

    public FlowDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public FlowDocument Parse(string yaml, string source = "<inline>")
    {
        DocumentKindYaml? probe;
        try
        {
            probe = _probe.Deserialize<DocumentKindYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        var flowType = probe?.FlowType?.Trim();
        var schedule = MapSchedule(probe?.Schedule);
        var mode = YamlDocumentParts.ParseExecutionMode(probe?.Mode, "mode", source);

        if (string.IsNullOrEmpty(flowType))
        {
            return new FileFlowDocument { Flow = _fileFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "ing", StringComparison.OrdinalIgnoreCase))
        {
            return new IngestionFlowDocument { Document = _ingestionFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "exp", StringComparison.OrdinalIgnoreCase))
        {
            return new ExportFlowDocument { Document = _exportFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "sp", StringComparison.OrdinalIgnoreCase))
        {
            return new StoredProcedureFlowDocument { Document = _storedProcedureFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "inv", StringComparison.OrdinalIgnoreCase))
        {
            return new InvokeFlowDocument { Document = _invokeFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "hc", StringComparison.OrdinalIgnoreCase))
        {
            return new HealthCheckFlowDocument { Document = _healthCheckFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "scm", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceControlFlowDocument { Document = _sourceControlFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "batch", StringComparison.OrdinalIgnoreCase))
        {
            return new BatchFlowDocument { Document = _batchFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "api", StringComparison.OrdinalIgnoreCase))
        {
            return new AcquireFlowDocument { Flow = _acquireFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "cpy", StringComparison.OrdinalIgnoreCase))
        {
            return new CopyFlowDocument { Flow = _copyFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return new SftpFlowDocument { Flow = _sftpFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "cal", StringComparison.OrdinalIgnoreCase))
        {
            return new CalendarFlowDocument { Document = _calendarFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        if (string.Equals(flowType, "trl", StringComparison.OrdinalIgnoreCase))
        {
            return new TranslateFlowDocument { Document = _translateFlows.Parse(yaml, source), Schedule = schedule, Mode = mode };
        }

        throw new FlowValidationException(
            $"{source}: unknown flowType '{flowType}'. Use 'ing' for a table-to-table ingestion flow, 'exp' for a " +
            "file export, 'sp' for a stored-procedure flow, 'inv' for an ADF/Automation trigger, 'hc' for an ML " +
            "health check, 'scm' for a database source-control snapshot, 'batch' for an ordered multi-flow batch, " +
            "'api' for a generic acquisition flow (HTTP / SFTP / Azure Table), 'cpy' for a file-copy flow "
            + "(local / Azure storage / S3, with optional zip/unzip), 'cal' for a generated calendar dimension, "
            + "'trl' for a JSON translation flow (query result to shaped documents, optionally delivered to an API), "
            + "or omit flowType for a file flow.");
    }

    private static ScheduleSpec? MapSchedule(ScheduleYaml? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        // A membership declaration (schedule: nightly, or schedule: [nightly, hourly]): carry the names for the
        // repo-wide scan to bind. The cadence is deliberately NOT copied onto the flow (a single-file parse has no
        // view of the shared library anyway): the flow JOINS those schedules, and each one fires once for all of
        // its members.
        if (schedule.Refs.Count > 0)
        {
            return new ScheduleSpec { Refs = schedule.Refs };
        }

        // An inline block carrying neither a cron nor an interval declares nothing to fire; treat it as absent so
        // an empty/placeholder block is not stored as a broken schedule. The cron syntax itself is validated where
        // the schedule is armed (the catalog/control plane owns the cron library).
        if (string.IsNullOrWhiteSpace(schedule.Cron) && schedule.IntervalSeconds is null)
        {
            return null;
        }

        return new ScheduleSpec
        {
            Name = string.IsNullOrWhiteSpace(schedule.Name) ? null : schedule.Name.Trim(),
            Cron = string.IsNullOrWhiteSpace(schedule.Cron) ? null : schedule.Cron.Trim(),
            IntervalSeconds = schedule.IntervalSeconds,
            Timezone = string.IsNullOrWhiteSpace(schedule.Timezone) ? "UTC" : schedule.Timezone.Trim(),
            Enabled = schedule.Enabled ?? true,
            Catchup = schedule.Catchup ?? false,
            // Omitted takes the product default; 0 is the explicit unbounded opt-out. The probe has nowhere to
            // surface a warning, so a meaningless negative simply falls back to the default (the schedule-library
            // loader, which does have a warning channel, reports it).
            MaxConcurrency = ScheduleDefaults.Resolve(schedule.MaxConcurrency, out _),
        };
    }
}
