using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

/// <summary>How a flow selects what it reads on each run: the vocabulary of <see cref="FlowLoadProfile.ReadMode"/>.</summary>
public static class FlowReadModes
{
    /// <summary>Everything the source offers is read on every run (no watermark bounds the read).</summary>
    public const string Full = "full";

    /// <summary>Only data past a high-water mark is read; the engine probes the mark on every run.</summary>
    public const string Incremental = "incremental";

    /// <summary>A fixed or rolling window is read on every run (a date range, or files modified in the last N days).</summary>
    public const string Window = "window";

    /// <summary>The rows are computed by the engine, not read from a source (the calendar dimension).</summary>
    public const string Generated = "generated";

    /// <summary>The flow hands the work to something whose reads SQLFlow does not model (a stored procedure's body,
    /// an Azure Data Factory pipeline, a batch of other flows).</summary>
    public const string External = "external";

    /// <summary>The flow reads the database but loads no data (a schema snapshot, a health check).</summary>
    public const string NotApplicable = "notApplicable";

    /// <summary>The definition could not be interpreted; <see cref="FlowLoadProfile.Summary"/> says why.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// What a flow does on every run, stated as facts rather than left for a reader to infer from the YAML: how it
/// selects what it reads, what it does to its target, and which columns key and bound the load. Every property is
/// always present; a list is empty (never absent) when the flow declares none, so "no watermark" is an explicit
/// answer instead of a missing one.
/// </summary>
public sealed record FlowLoadProfile
{
    /// <summary>One of <see cref="FlowReadModes"/>.</summary>
    public required string ReadMode { get; init; }

    /// <summary>One plain sentence combining <see cref="Read"/> and <see cref="Write"/>, the direct answer to
    /// "how is this loaded".</summary>
    public required string Summary { get; init; }

    /// <summary>How the rows or files a run processes are selected.</summary>
    public required string Read { get; init; }

    /// <summary>What a run does to the target.</summary>
    public required string Write { get; init; }

    /// <summary>The object or location the flow writes, or null when it writes none.</summary>
    public string? Target { get; init; }

    /// <summary>Whether every run empties the target before writing (a truncate, a drop and rebuild).</summary>
    public bool ReplacesTargetEachRun { get; init; }

    /// <summary>The columns rows are matched on when applied to the target; empty when the flow does not upsert.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>The columns whose high-water mark bounds the read; empty when the read is not incremental.</summary>
    public IReadOnlyList<string> WatermarkColumns { get; init; } = [];
}

/// <summary>
/// Derives a <see cref="FlowLoadProfile"/> from a flow document. It reads the typed model the engine runs, never the
/// serialized definition JSON (whose shape freezes at the serializer version that stored it), and each statement
/// mirrors the engine path it describes: the ingestion watermark precedence of <c>IncrementalWindowResolver</c>,
/// the file-flow bounds of <c>FlowRunner</c>, and the apply kinds of <c>IngestionFlowRunner</c>. Pure: no IO.
/// </summary>
public static class FlowLoadProfiles
{
    private static readonly YamlDocumentLoader Documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
        new YamlTranslateFlowLoader());

    /// <summary>Profiles a flow from its stored (secret-redacted) YAML, the same text the catalog sync parsed. A
    /// document that no longer parses yields an <see cref="FlowReadModes.Unknown"/> profile naming the parse error,
    /// so the caller reports "cannot tell" instead of guessing.</summary>
    public static FlowLoadProfile FromYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Unknown("the catalog holds no YAML for this flow");
        }

        FlowDocument document;
        try
        {
            document = Documents.Parse(yaml, "<catalog>");
        }
        catch (SqlFlowException ex)
        {
            return Unknown("the stored flow definition could not be parsed: " + ex.Message);
        }

        return Describe(document);
    }

    /// <summary>Profiles an already parsed flow document.</summary>
    public static FlowLoadProfile Describe(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document switch
        {
            IngestionFlowDocument d => Ingestion(d.Document.Flow),
            FileFlowDocument d => FileFlow(d.Flow),
            AcquireFlowDocument d => Acquire(d.Flow),
            CopyFlowDocument d => Copy(d.Flow),
            SftpFlowDocument d => Sftp(d.Flow),
            ExportFlowDocument d => Export(d.Document.Flow),
            CalendarFlowDocument d => Calendar(d.Document.Flow),
            TranslateFlowDocument d => Translate(d.Document.Flow),
            StoredProcedureFlowDocument d => Profile(
                FlowReadModes.External,
                $"runs the stored procedure {d.Document.Flow.Procedure.QualifiedName}; what it reads is defined in the procedure body, not in the flow",
                "whatever the procedure body writes (read its definition for the tables and the load logic)",
                target: d.Document.Flow.Procedure.QualifiedName),
            InvokeFlowDocument d => Profile(
                FlowReadModes.External,
                d.Document.Definition.InvokeType == Core.Invoke.InvokeType.AzureDataFactory
                    ? $"starts the Azure Data Factory pipeline '{d.Document.Definition.PipelineName}'; its reads are defined in Data Factory, not in SQLFlow"
                    : $"starts the Azure Automation runbook '{d.Document.Definition.RunbookName}'; its reads are defined in the runbook, not in SQLFlow",
                "whatever the invoked job writes; SQLFlow only starts it and waits for its outcome",
                target: null),
            BatchFlowDocument d => Profile(
                FlowReadModes.External,
                $"runs the member flows of batch '{d.Document.Flow.SysAlias}' in wave order; each member has its own load behavior",
                "nothing itself; each member flow writes its own target",
                target: null),
            SourceControlFlowDocument d => Profile(
                FlowReadModes.NotApplicable,
                $"scripts the object definitions of the database behind connection '{d.Document.Flow.Server}'",
                "commits the schema snapshot to a git repository; it loads no table",
                target: null),
            HealthCheckFlowDocument d => Profile(
                FlowReadModes.NotApplicable,
                $"computes {d.Document.Flow.Metrics.Count} metric(s) per {d.Document.Flow.DateColumn} over {d.Document.Flow.Target.QualifiedName}",
                "writes a health-check score artifact for the run; it loads no table",
                target: null),
            _ => Unknown($"no load profile is defined for the flow document type {document.GetType().Name}"),
        };
    }

    private static FlowLoadProfile Ingestion(IngestionFlow flow)
    {
        var incremental = flow.Incremental;
        var filter = flow.Source.Filter?.Trim();
        var hasFilter = !string.IsNullOrEmpty(filter);
        var replacingFilter = hasFilter && !flow.Source.FilterIsAppend;

        // Precedence mirrors IncrementalWindowResolver.AssembleWhere: a replace-filter wins, then the fullLoad flag,
        // then the incremental predicate (columns before the date column). An append-filter is added last.
        string readMode;
        string read;
        IReadOnlyList<string> watermarks = [];
        if (replacingFilter)
        {
            readMode = FlowReadModes.Full;
            read = $"reads the rows matching the fixed filter '{filter}' on every run; the filter replaces any incremental watermark";
        }
        else if (incremental.FullLoad)
        {
            readMode = FlowReadModes.Full;
            read = incremental.IsIncremental
                ? $"reads the whole source table on every run: incremental.fullLoad is set, so the declared watermark ({DescribeIngestionMarks(incremental)}) is ignored"
                : "reads the whole source table on every run (incremental.fullLoad is set)";
        }
        else if (incremental.IsIncremental)
        {
            readMode = FlowReadModes.Incremental;
            watermarks = IngestionMarks(incremental);
            read = IngestionIncrementalRead(flow, incremental);
        }
        else
        {
            readMode = FlowReadModes.Full;
            read = "reads the whole source table on every run; no incremental watermark is declared";
        }

        if (hasFilter && !replacingFilter)
        {
            read += $", narrowed by the filter '{filter}'";
        }

        if (flow.InitLoad.Enabled)
        {
            read += "; a one-time initial load (initLoad) is also declared";
        }

        var keys = flow.Load.MatchKeysInSourceAndTarget && flow.MatchKeys.KeyColumns.Count > 0
            ? flow.MatchKeys.KeyColumns
            : flow.Load.KeyColumns;
        var target = flow.Target.Table.QualifiedName;
        var write = IngestionWrite(flow, keys, target);

        return new FlowLoadProfile
        {
            ReadMode = readMode,
            Summary = Sentence(read, write),
            Read = read,
            Write = write,
            Target = target,
            ReplacesTargetEachRun = flow.Target.TruncateBeforeLoad,
            KeyColumns = keys,
            WatermarkColumns = watermarks,
        };
    }

    private static List<string> IngestionMarks(IncrementalPolicy incremental)
    {
        var marks = incremental.Columns.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (marks.Count == 0 && !string.IsNullOrWhiteSpace(incremental.DateColumn))
        {
            marks.Add(incremental.DateColumn!);
        }

        return marks;
    }

    private static string DescribeIngestionMarks(IncrementalPolicy incremental)
    {
        var parts = incremental.Columns.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (!string.IsNullOrWhiteSpace(incremental.DateColumn))
        {
            parts.Add(incremental.DateColumn!);
        }

        return string.Join(", ", parts);
    }

    private static string IngestionIncrementalRead(IngestionFlow flow, IncrementalPolicy incremental)
    {
        var columns = incremental.Columns.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var bound = incremental.FetchMinValuesFromSource
            ? "the high-water mark (or the source minimum when the source holds older data than the target)"
            : "the high-water mark";
        string read;
        if (columns.Count > 0)
        {
            read = $"reads only source rows with {string.Join(" and ", columns)} greater than {bound} of the target";
            if (incremental.Lookback > 0)
            {
                read += string.Create(CultureInfo.InvariantCulture, $", rewound by {incremental.Lookback} for a numeric column so late-committed rows are re-read");
            }

            if (!string.IsNullOrWhiteSpace(incremental.DateColumn))
            {
                read += $"; the declared dateColumn {incremental.DateColumn} is not applied while incremental.columns bound the read";
            }
        }
        else
        {
            read = string.Create(
                CultureInfo.InvariantCulture,
                $"reads only source rows with {incremental.DateColumn} greater than {bound} of the target minus {incremental.OverlapDays} day(s) of overlap");
        }

        if (!string.IsNullOrWhiteSpace(flow.Source.IncrementalClause))
        {
            read += $"; the watermark probe is scoped by '{flow.Source.IncrementalClause!.Trim()}'";
        }

        return read + "; an empty target is loaded in full";
    }

    private static string IngestionWrite(IngestionFlow flow, IReadOnlyList<string> keys, string target)
    {
        var load = flow.Load;
        var keyList = string.Join(", ", keys);
        string write;
        if (flow.Versioning.Scd2.Enabled)
        {
            write = $"keeps SCD2 history in {target} on {keyList}: a changed row closes the current version and inserts a new one";
        }
        else if (!string.IsNullOrWhiteSpace(load.ReloadColumn))
        {
            write = $"replaces per {load.ReloadColumn} in {target}: deletes the target rows of every {load.ReloadColumn} value in the batch, then inserts the batch";
        }
        else if (keys.Count == 0)
        {
            write = $"inserts every row read into {target} (no key, so nothing is matched or updated)";
        }
        else if (load.SkipUpdateExisting && load.SkipInsertNew)
        {
            write = $"writes nothing to {target}: both skipUpdateExisting and skipInsertNew are set";
        }
        else if (load.SkipUpdateExisting)
        {
            write = $"inserts rows with new {keyList} into {target}; existing rows are never updated";
        }
        else if (load.SkipInsertNew)
        {
            write = $"updates existing rows in {target} matched on {keyList}; new keys are not inserted";
        }
        else
        {
            write = $"upserts into {target} on {keyList}: matching rows are updated, new rows inserted";
        }

        if (!string.IsNullOrWhiteSpace(load.DataSetColumn))
        {
            write += $", applied one {load.DataSetColumn} dataset at a time in ascending order";
        }

        if (load.BatchUpsertToAvoidLockEscalation && keys.Count > 0)
        {
            write += string.Create(CultureInfo.InvariantCulture, $", in batches of {load.BatchUpsertRowCount} rows");
        }

        if (flow.Target.TruncateBeforeLoad)
        {
            write = $"truncates {target} first, then " + write;
        }
        else
        {
            write += "; the target is never truncated";
        }

        if (load.MatchKeysInSourceAndTarget)
        {
            write += flow.MatchKeys.Action == MatchKeyAction.Delete
                ? "; target rows whose keys are gone from the source are deleted"
                : "; target rows whose keys are gone from the source are tagged deleted (DeletedDate_DW)";
        }

        if (load.TruncateSourceWhenConsolidated)
        {
            write += "; the upstream landing table is truncated once the target has caught up with it";
        }

        return write;
    }

    private static FlowLoadProfile FileFlow(FlowDefinition flow)
    {
        var pattern = flow.Source.Options.TryGetValue("srcFile", out var file) && !string.IsNullOrWhiteSpace(file)
            ? $"'{file}' "
            : string.Empty;
        var files = $"the {flow.Source.Type} files {pattern}under {flow.Source.Location}";
        var incremental = flow.Incremental;

        string readMode;
        string read;
        IReadOnlyList<string> watermarks = [];
        if (incremental is null)
        {
            readMode = FlowReadModes.Full;
            read = $"reads every one of {files} on every run; no incremental watermark is declared";
        }
        else if (incremental.FullLoad)
        {
            readMode = FlowReadModes.Full;
            read = $"reads every one of {files} on every run (incremental.fullLoad is set, so the watermark is bypassed)";
        }
        else if (!string.IsNullOrWhiteSpace(incremental.WatermarkColumn))
        {
            readMode = FlowReadModes.Incremental;
            watermarks = [incremental.WatermarkColumn!];
            read = string.Create(
                CultureInfo.InvariantCulture,
                $"reads {files} but keeps only rows with {incremental.WatermarkColumn} greater than the target's high-water mark minus an overlap of {incremental.WatermarkOverlap}");
        }
        else
        {
            readMode = FlowReadModes.Incremental;
            watermarks = [incremental.DateColumn];
            var probe = string.IsNullOrWhiteSpace(incremental.Table) ? "the downstream table (or the target)" : incremental.Table;
            read = string.Create(
                CultureInfo.InvariantCulture,
                $"reads only {files} dated after MAX({incremental.DateColumn}) on {probe} minus {incremental.OverlapDays} day(s), never below the newest file already processed; with no prior watermark every file is read");
        }

        var target = flow.Target.QualifiedName;
        string write;
        var replaces = flow.Load.Mode == LoadMode.TruncateLoad;
        if (replaces)
        {
            write = $"truncates {target}, then loads the rows read";
        }
        else
        {
            write = $"appends the rows read to {target}";
            if (flow.Load.ResetWhenConsolidated && flow.Inference.GeneratesView)
            {
                write += "; the table is emptied at the start of a later run once every downstream flow has consolidated it";
            }
        }

        return new FlowLoadProfile
        {
            ReadMode = readMode,
            Summary = Sentence(read, write),
            Read = read,
            Write = write,
            Target = target,
            ReplacesTargetEachRun = replaces,
            WatermarkColumns = watermarks,
        };
    }

    private static FlowLoadProfile Acquire(Core.Acquire.AcquireFlow flow)
    {
        var endpoints = flow.Items.Count == 1 ? "its endpoint" : $"its {flow.Items.Count} endpoints";
        string readMode;
        string read;
        IReadOnlyList<string> watermarks = [];
        if (flow.Incremental is { } incremental)
        {
            readMode = FlowReadModes.Incremental;
            if (!string.IsNullOrWhiteSpace(incremental.Column))
            {
                watermarks = [incremental.Column!];
            }

            var from = incremental.Source switch
            {
                Core.Acquire.AcquireWatermarkSource.Lake => "what is already landed in the lake",
                Core.Acquire.AcquireWatermarkSource.Sql => "a query over the loaded data",
                _ => $"the highest {incremental.Column} seen by the previous run",
            };
            read = $"fetches from {endpoints} resuming after the watermark taken from {from}";
        }
        else
        {
            readMode = FlowReadModes.Full;
            read = $"fetches everything {endpoints} return(s) for the declared requests on every run; no incremental watermark is declared";
        }

        var landing = flow.Items.Select(i => i.Landing).ToList();
        var targets = string.Join(", ", landing.Select(l => l.Target).Distinct(StringComparer.OrdinalIgnoreCase));
        var write = $"lands the raw payloads as files under {targets}";
        if (landing.All(l => l.SkipUnchanged))
        {
            write += "; a payload identical to the one already landed is skipped";
        }

        return Profile(readMode, read, write, targets, watermarks: watermarks);
    }

    private static FlowLoadProfile Copy(Core.Copy.CopyFlow flow)
    {
        var windows = flow.Steps.Select(s => s.Source.ModifiedWithinDays).ToList();
        var readMode = windows.All(d => d > 0) ? FlowReadModes.Window : FlowReadModes.Full;
        var read = string.Join("; ", flow.Steps.Select(s => s.Source.ModifiedWithinDays > 0
            ? string.Create(CultureInfo.InvariantCulture, $"'{s.Source.Pattern}' files under {s.Source.Location} modified in the last {s.Source.ModifiedWithinDays} day(s)")
            : $"every '{s.Source.Pattern}' file under {s.Source.Location}"));
        var verb = flow.Operation switch
        {
            Core.Copy.CopyOperation.Zip => "zips them into one archive under",
            Core.Copy.CopyOperation.Unzip => "unzips them into",
            _ => "copies them to",
        };
        var targets = string.Join(", ", flow.Steps.Select(s => s.Target.Location).Distinct(StringComparer.OrdinalIgnoreCase));
        var write = $"{verb} {targets}" + (flow.Options.SkipUnchanged ? "; files already present unchanged are skipped" : string.Empty);
        return Profile(readMode, "selects " + read, write, targets);
    }

    private static FlowLoadProfile Sftp(Core.Sftp.SftpFlow flow)
    {
        var download = flow.Direction == Core.Sftp.SftpDirection.Download;
        var readMode = flow.Steps.All(s => s.ModifiedWithinDays > 0) ? FlowReadModes.Window : FlowReadModes.Full;
        var read = string.Join("; ", flow.Steps.Select(s =>
        {
            var from = download ? $"SFTP {s.RemotePath}" : s.Local;
            return s.ModifiedWithinDays > 0
                ? string.Create(CultureInfo.InvariantCulture, $"'{s.Pattern}' files in {from} modified in the last {s.ModifiedWithinDays} day(s)")
                : $"every '{s.Pattern}' file in {from}";
        }));
        var targets = string.Join(", ", flow.Steps.Select(s => download ? s.Local : "SFTP " + s.RemotePath).Distinct(StringComparer.OrdinalIgnoreCase));
        var write = (download ? "downloads them to " : "uploads them to ") + targets
            + (flow.SkipUnchanged ? "; files already present unchanged are skipped" : string.Empty);
        return Profile(readMode, "selects " + read, write, targets);
    }

    private static FlowLoadProfile Export(Core.Export.ExportFlow flow)
    {
        var source = flow.Source.QualifiedName;
        var column = string.Equals(flow.ExportBy, "K", StringComparison.OrdinalIgnoreCase) ? flow.IncrementalColumn : flow.DateColumn;
        string readMode;
        string read;
        if (flow.FromDate is not null || flow.ToDate is not null)
        {
            readMode = FlowReadModes.Window;
            read = string.Create(
                CultureInfo.InvariantCulture,
                $"reads {source} between {flow.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "the source minimum"} and {flow.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "the source maximum"} on {column} on every run");
        }
        else
        {
            readMode = FlowReadModes.Full;
            read = $"reads all of {source} on every run (no export watermark exists)";
        }

        if (!string.IsNullOrWhiteSpace(flow.SrcFilter))
        {
            read += $", narrowed by the filter '{flow.SrcFilter!.Trim()}'";
        }

        var chunk = flow.ExportBy.ToUpperInvariant() switch
        {
            "F" => "one file",
            "M" => $"one file per {flow.ExportSize} month(s) of {column}",
            "K" => $"files of {flow.ExportSize} {column} value(s) each",
            _ => $"one file per {flow.ExportSize} day(s) of {column}",
        };
        var destination = flow.TrgPath ?? flow.TargetReference ?? "the configured destination";
        var write = $"writes {flow.TrgFiletype} as {chunk} to {destination}";
        return Profile(readMode, read, write, destination);
    }

    private static FlowLoadProfile Calendar(Core.Calendar.CalendarFlow flow)
    {
        var target = flow.Table.QualifiedName;
        var read = string.Create(
            CultureInfo.InvariantCulture,
            $"generates one row per date from {flow.From:yyyy-MM-dd} to {flow.To:yyyy-MM-dd} ({flow.Country})");
        var write = flow.Rebuild
            ? $"drops and rebuilds {target} on every run"
            : $"merges the generated dates into {target}; existing rows are refreshed in place";
        return Profile(FlowReadModes.Generated, read, write, target, replaces: flow.Rebuild);
    }

    private static FlowLoadProfile Translate(Core.Translate.TranslateFlow flow)
    {
        var read = $"runs its query against connection '{flow.SrcServer}' on every run (no watermark)";
        var write = $"writes the rendered documents to {flow.Output.Path}"
            + (flow.Invoke is null ? string.Empty : ", then posts them to the declared API");
        return Profile(FlowReadModes.Full, read, write, flow.Output.Path);
    }

    private static FlowLoadProfile Profile(
        string readMode, string read, string write, string? target,
        bool replaces = false, IReadOnlyList<string>? watermarks = null)
        => new()
        {
            ReadMode = readMode,
            Summary = Sentence(read, write),
            Read = read,
            Write = write,
            Target = target,
            ReplacesTargetEachRun = replaces,
            WatermarkColumns = watermarks ?? [],
        };

    private static FlowLoadProfile Unknown(string reason)
        => new()
        {
            ReadMode = FlowReadModes.Unknown,
            Summary = "Load behavior unknown: " + reason + ".",
            Read = "unknown: " + reason,
            Write = "unknown: " + reason,
        };

    private static string Sentence(string read, string write)
        => char.ToUpperInvariant(read[0]) + read[1..] + ", and " + write + ".";
}
