using System.Diagnostics;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Runs <c>tools/pbix-extract</c> over one Power BI report and reads back what it extracted.
/// <para>
/// Extraction lives in that standalone binary rather than here because a <c>.pbix</c> is
/// attacker-influenceable input: reaching its contents means running a decompressor and a SQLite reader over
/// bytes SQLFlow did not write. Keeping that parsing in a separate process, run where the report files live
/// rather than inside the control plane, keeps a hostile report away from the process holding catalog
/// credentials and warehouse reach. The only thing crossing back is YAML text.
/// </para>
/// <para>
/// It is also the ONE reader of a <c>.pbix</c> in the estate. The visual layer used to be parsed a second time
/// in C# here, which meant a fix to either reader had to be remembered twice; the tool now renders each
/// visual's question as SQL itself, so this type only locates it, runs it, and maps its output.
/// </para>
/// </summary>
internal static class PbixExtractTool
{
    /// <summary>Names the executable explicitly, for a machine that keeps it outside the search paths below.
    /// Matches the estate's other <c>SQLFLOW_*</c> settings.</summary>
    public const string PathVariable = "SQLFLOW_PBIX_EXTRACT";

    private const string ExecutableName = "pbix-extract";

    /// <summary>A report that cannot be decoded must not hang a sync, so the child is given a bounded life and
    /// killed if it outlives it. Generous enough for a large model on a slow disk.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Writes the canonical form: absent values omitted (each reads back as its default), and any string that
    /// would otherwise be read as another type or as YAML syntax quoted.</summary>
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings(quoteYaml1_1Strings: true)
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .DisableAliases()
        .Build();

    /// <summary>
    /// Locates the extractor, in the order a machine is most likely to have deliberately placed it: an explicit
    /// setting, then alongside this assembly (where packaging puts it), then on PATH. Returns null when none of
    /// them holds it, which the caller reports as a warning rather than a failure.
    /// <para>
    /// Deliberately not read from the estate's own YAML: the binary is a property of the machine running the
    /// sync, not of the repository, and putting a local path in a committed document would make the estate
    /// non-portable.
    /// </para>
    /// </summary>
    public static string? Locate()
    {
        var declared = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return File.Exists(declared) ? declared : null;
        }

        var besideAssembly = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (File.Exists(besideAssembly))
        {
            return besideAssembly;
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, ExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the tool over one report and returns the specification it wrote, unparsed.
    /// <paramref name="reportFileLabel"/> is the name each page is tagged with, which is the report's path relative
    /// to the subscriber's declared directory so two reports built from one template (each with a "Page 1") stay
    /// distinguishable.
    /// </summary>
    /// <exception cref="PbixExtractException">The tool could not be run or failed. Always carries a message naming
    /// what went wrong.</exception>
    public static string RunToYaml(string executable, string pbixPath, string reportFileLabel)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Arguments go through ArgumentList rather than a concatenated string so a report path containing a
        // space or a quote is passed through exactly as written instead of being re-split by the runtime.
        startInfo.ArgumentList.Add(pbixPath);
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(SpecNameFor(reportFileLabel));
        startInfo.ArgumentList.Add("--report-file");
        startInfo.ArgumentList.Add(reportFileLabel);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PbixExtractException($"'{executable}' could not be started ({ex.Message})", ex);
        }

        // Both pipes are drained concurrently: reading one to completion before the other deadlocks as soon as
        // the child fills the pipe it is not being read from, which a report producing many warnings will do.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new PbixExtractException(
                $"'{executable}' did not finish within {Timeout.TotalMinutes:0} minutes and was stopped");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var errors = standardError.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new PbixExtractException(
                $"'{executable}' failed with exit code {process.ExitCode}: {Summarize(errors)}");
        }

        if (Encoding.UTF8.GetByteCount(output) > ReportSpecs.MaxBytes)
        {
            throw new PbixExtractException(
                $"'{executable}' wrote a specification larger than {ReportSpecs.MaxBytes / (1024 * 1024)} MB");
        }

        return output;
    }

    /// <summary>
    /// The name the specification is written under, derived from the report's label exactly as the tool derives it
    /// from a file path (the base name without its extension, spaces as underscores). Passing it explicitly means the
    /// same report yields the same specification whatever the file on disk is called, which matters because the
    /// isolated extractor saves every upload under one fixed temporary name.
    /// </summary>
    internal static string SpecNameFor(string reportFileLabel)
    {
        var slash = reportFileLabel.LastIndexOf('/');
        var baseName = slash >= 0 ? reportFileLabel[(slash + 1)..] : reportFileLabel;
        var dot = baseName.LastIndexOf('.');
        var stem = (dot >= 0 ? baseName[..dot] : baseName).Replace(' ', '_');
        return stem.Length > 0 ? stem : "report";
    }

    /// <summary>Extracts one report and parses what the tool wrote. See <see cref="RunToYaml"/>.</summary>
    /// <exception cref="PbixExtractException">The tool could not be run, failed, or produced output this cannot
    /// read.</exception>
    public static PbixExtractResult Run(string executable, string pbixPath, string reportFileLabel)
        => Parse(RunToYaml(executable, pbixPath, reportFileLabel), $"'{executable}'");

    /// <summary>Maps a report specification onto the typed result: the report layer, the model layer, the
    /// resolved model sources, and the tool's warnings. <paramref name="source"/> names where the text came from
    /// in every error (the tool, a specification file, an upload).</summary>
    /// <exception cref="PbixExtractException">The text is not a well-formed specification.</exception>
    internal static PbixExtractResult Parse(string yaml, string source)
    {
        var (_, spec) = ReadSingle(yaml, source);
        var pages = SpecGraph.BuildPages(spec.Nodes ?? [], spec.Edges ?? [], source);
        return new PbixExtractResult(
            pages, spec.ReportWarnings ?? [], SpecGraph.ModelSources(spec.Nodes ?? []),
            SpecGraph.Model(spec.Nodes ?? [], spec.Edges ?? []));
    }

    /// <summary>
    /// Rewrites a specification into the canonical form the semantic layer stores: only the properties this reader
    /// consumes, with every free-text value passed through the same credential redaction subscriber SQL takes (an M
    /// expression or a visual's SQL can quote a connection string). Storing the canonical form rather than the text
    /// as received keeps a secret pasted into a specification out of the catalog, and means a stored specification
    /// reads back exactly as it was validated.
    /// </summary>
    /// <exception cref="PbixExtractException">The text is not a well-formed specification.</exception>
    internal static string Normalize(string yaml, string source)
    {
        var (name, spec) = ReadSingle(yaml, source);

        // Validated before anything is emitted, so a specification whose graph the reader would refuse is refused
        // here too rather than stored and then failing every sync.
        SpecGraph.BuildPages(spec.Nodes ?? [], spec.Edges ?? [], source);

        var canonical = new SpecSubscriber
        {
            Type = "PowerBI",
            Nodes = (spec.Nodes ?? []).Select(RedactNode).ToList(),
            Edges = (spec.Edges ?? []).ToList(),
            ReportWarnings = (spec.ReportWarnings ?? []).Select(Redact).OfType<string>().ToList(),
        };

        var text = Serializer.Serialize(new SpecDocument
        {
            Subscribers = new Dictionary<string, SpecSubscriber>(StringComparer.Ordinal) { [name] = canonical },
        });
        return "# A Power BI report specification in the canonical form SQLFlow stores. Generated from the output of\n"
            + "# tools/pbix-extract; regenerate it rather than editing it by hand.\n"
            + text;
    }

    private static SpecNode RedactNode(SpecNode node) => new()
    {
        Id = node.Id,
        Kind = node.Kind,
        DisplayName = node.DisplayName,
        Name = node.Name,
        ReportFile = node.ReportFile,
        Ordinal = node.Ordinal,
        VisualType = node.VisualType,
        Title = node.Title,
        Sql = Redact(node.Sql),
        SourceServer = node.SourceServer,
        SourceDatabase = node.SourceDatabase,
        SourceSchema = node.SourceSchema,
        SourceName = node.SourceName,
        PowerQuery = Redact(node.PowerQuery),
        DataType = node.DataType,
        Dax = Redact(node.Dax),
        Description = Redact(node.Description),
    };

    private static string? Redact(string? value)
        => value is null ? null : Core.Secrets.SecretHygiene.RedactedMessage(value);

    /// <summary>
    /// Reads the single report a specification holds, with the key it is written under. The tool emits exactly one
    /// subscriber keyed by the report's name; anything else means the text is not one of its specifications (or the
    /// contract between the two has drifted), which is worth saying plainly rather than silently extracting nothing.
    /// </summary>
    private static (string Name, SpecSubscriber Spec) ReadSingle(string yaml, string source)
    {
        var (name, spec) = Deserialize(yaml, source).Subscribers!.First();
        return (name, spec);
    }

    private static SpecDocument Deserialize(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        GuardShape(yaml, source);

        SpecDocument? document;
        try
        {
            document = Deserializer.Deserialize<SpecDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new PbixExtractException($"{source} is not valid YAML ({ex.Message})", ex);
        }

        if (document?.Subscribers is not { Count: 1 } subscribers
            || subscribers.Keys.First() is not { Length: > 0 }
            || subscribers.Values.First() is null)
        {
            throw new PbixExtractException($"{source} holds no single report specification");
        }

        return document;
    }

    /// <summary>
    /// Refuses a document before it is materialized when its shape could exhaust the reader: more than
    /// <see cref="ReportSpecs.MaxBytes"/> of text, more parse events than a real report produces, or any anchor or
    /// alias (the tool never writes one, and aliases are how a small YAML document expands into an enormous
    /// object graph). A specification can arrive from an upload, so this is a trust boundary.
    /// </summary>
    private static void GuardShape(string yaml, string source)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > ReportSpecs.MaxBytes)
        {
            throw new PbixExtractException(
                $"{source} is larger than {ReportSpecs.MaxBytes / (1024 * 1024)} MB");
        }

        const int maxEvents = 5_000_000;
        var events = 0;
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                if (++events > maxEvents)
                {
                    throw new PbixExtractException($"{source} has more than {maxEvents} YAML elements");
                }

                switch (parser.Current)
                {
                    case YamlDotNet.Core.Events.AnchorAlias:
                    case YamlDotNet.Core.Events.NodeEvent { Anchor.IsEmpty: false }:
                        throw new PbixExtractException(
                            $"{source} uses YAML anchors or aliases, which a report specification never contains");
                }
            }
        }
        catch (YamlException ex)
        {
            throw new PbixExtractException($"{source} is not valid YAML ({ex.Message})", ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // The child exited between the timeout and the kill, or the platform refused the signal. Either
            // way the timeout itself is what the caller is told about, so there is nothing to add here.
        }
    }

    /// <summary>Caps the tool's stderr so a runaway message cannot flood a warning.</summary>
    private static string Summarize(string errors)
    {
        var text = errors.Trim();
        if (text.Length == 0)
        {
            return "it wrote no diagnostics";
        }

        return text.Length <= 500 ? text : text[..500] + "...";
    }

    private sealed class SpecDocument
    {
        public Dictionary<string, SpecSubscriber>? Subscribers { get; set; }
    }

    /// <summary>
    /// The subscriber body the tool emits: a flat node/edge graph (<see cref="Nodes"/>/<see cref="Edges"/>)
    /// covering both the semantic model (tables, columns, measures, calculated columns, relationships) and the
    /// report layer (pages, visuals, projected fields), plus <see cref="ReportWarnings"/> as a flat diagnostics
    /// list. Both halves are reconstructed into typed records below (<see cref="SpecGraph"/>): the report layer
    /// into pages and visuals, and the model half into tables, the fields defined on them, and the relationships
    /// between them.
    /// </summary>
    private sealed class SpecSubscriber
    {
        /// <summary>Always <c>PowerBI</c> as the tool writes it; carried so the canonical form reads like the
        /// tool's own output.</summary>
        public string? Type { get; set; }

        public List<SpecNode>? Nodes { get; set; }

        public List<SpecEdge>? Edges { get; set; }

        public List<string>? ReportWarnings { get; set; }
    }

    /// <summary>
    /// One YAML graph node. This one type models every node kind the tool can emit (table, column, measure,
    /// calculatedColumn, expression, report, page, visual), since YamlDotNet has no polymorphic-by-discriminator mapping for
    /// a plain sequence item; a property a given <see cref="Kind"/> does not use is simply left null. Both the
    /// report-layer kinds and the model-layer kinds are read back out into typed records (see
    /// <see cref="SpecGraph"/>).
    /// </summary>
    internal sealed class SpecNode
    {
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string? DisplayName { get; set; }

        public string? Name { get; set; }

        public string? ReportFile { get; set; }

        public int Ordinal { get; set; }

        public string? VisualType { get; set; }

        public string? Title { get; set; }

        public string? Sql { get; set; }

        /// <summary>On a <c>table</c> node whose Power Query source named a warehouse object: the server as
        /// the M expression spelled it. A CANDIDATE identity, not an estate one, since how a connection
        /// string maps onto a declared connection reference is the estate's question, not the parser's.</summary>
        public string? SourceServer { get; set; }

        public string? SourceDatabase { get; set; }

        public string? SourceSchema { get; set; }

        /// <summary>The physical table the model entity was loaded from. Present only alongside the other
        /// three: a partial resolution is reported as unresolved rather than half-applied.</summary>
        public string? SourceName { get; set; }

        /// <summary>On a <c>table</c> node: the Power Query (M) expression that loads it; on an <c>expression</c>
        /// node: the shared query's M. As the tool emits it, with the server literal already redacted.</summary>
        public string? PowerQuery { get; set; }

        /// <summary>On a <c>column</c> node: the model's data type (<c>string</c>, <c>int64</c>, and so on).</summary>
        public string? DataType { get; set; }

        /// <summary>On a <c>measure</c> or <c>calculatedColumn</c> node: its DAX expression.</summary>
        public string? Dax { get; set; }

        /// <summary>On a <c>measure</c> node: the description its author wrote, when there is one.</summary>
        public string? Description { get; set; }
    }

    /// <summary>One YAML graph edge. <c>Role</c> is read only off a <c>projects</c> edge; the column, cardinality,
    /// and active properties only off a <c>relationship</c> edge.</summary>
    internal sealed class SpecEdge
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string? Role { get; set; }

        /// <summary>On a <c>relationship</c> edge: the column on the <see cref="From"/> table.</summary>
        public string? FromColumn { get; set; }

        /// <summary>On a <c>relationship</c> edge: the column on the <see cref="To"/> table.</summary>
        public string? ToColumn { get; set; }

        /// <summary>On a <c>relationship</c> edge: <c>1:1</c>, <c>M:1</c>, <c>1:M</c>, or <c>M:M</c>.</summary>
        public string? Cardinality { get; set; }

        /// <summary>On a <c>relationship</c> edge: false when the relationship exists but applies only where a
        /// measure invokes it (USERELATIONSHIP).</summary>
        public bool? Active { get; set; }
    }
}

/// <summary>
/// Reconstructs the report layer's <see cref="PbixPage"/>/<see cref="PbixVisual"/>/<see cref="PbixField"/>
/// records from the flat node/edge graph the tool emits, by walking exactly the edges the report layer needs
/// (<c>hasPage</c>, <c>hasVisual</c>, <c>projects</c>) and filtering nodes by <c>kind</c>. This is a
/// parsing-layer detail only: everything downstream of <see cref="PbixExtractTool.Run"/> keeps consuming the
/// same typed records it always has, so <see cref="Collection.FlowSetCollector"/> and the catalog sync path
/// needed no changes when the wire format moved from a name-keyed tree to a graph.
/// </summary>
file static class SpecGraph
{
    public static List<PbixPage> BuildPages(
        IReadOnlyList<PbixExtractTool.SpecNode> nodes, IReadOnlyList<PbixExtractTool.SpecEdge> edges,
        string source)
    {
        var byId = new Dictionary<string, PbixExtractTool.SpecNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            // A duplicate id would mean the tool emitted the same node twice, which is a real contract
            // violation between the two sides rather than something to silently paper over by keeping
            // whichever copy arrived first.
            if (node is null || string.IsNullOrEmpty(node.Id) || string.IsNullOrEmpty(node.Kind))
            {
                throw new PbixExtractException($"{source} holds a graph node with no id or kind");
            }

            if (!byId.TryAdd(node.Id, node))
            {
                throw new PbixExtractException($"{source} holds a graph with a duplicate node id '{node.Id}'");
            }
        }

        var hasPage = new Dictionary<string, List<PbixExtractTool.SpecEdge>>(StringComparer.Ordinal);
        var hasVisual = new Dictionary<string, List<PbixExtractTool.SpecEdge>>(StringComparer.Ordinal);
        var projects = new Dictionary<string, List<PbixExtractTool.SpecEdge>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (edge is null || string.IsNullOrEmpty(edge.From) || string.IsNullOrEmpty(edge.To))
            {
                throw new PbixExtractException($"{source} holds a graph edge with no endpoints");
            }

            var bucket = edge.Kind switch
            {
                "hasPage" => hasPage,
                "hasVisual" => hasVisual,
                "projects" => projects,
                _ => null,
            };
            if (bucket is null)
            {
                continue;
            }

            if (!bucket.TryGetValue(edge.From, out var list))
            {
                list = [];
                bucket[edge.From] = list;
            }

            list.Add(edge);
        }

        var pages = new List<PbixPage>();
        foreach (var pageNode in nodes.Where(n => n.Kind == "page").OrderBy(n => n.Ordinal))
        {
            var visuals = new List<PbixVisual>();
            if (hasVisual.TryGetValue(pageNode.Id, out var visualEdges))
            {
                foreach (var visualEdge in visualEdges.OrderBy(e => ResolveOrdinal(byId, e.To)))
                {
                    if (!byId.TryGetValue(visualEdge.To, out var visualNode) || visualNode.Kind != "visual")
                    {
                        continue;
                    }

                    var fields = new List<PbixField>();
                    if (projects.TryGetValue(visualNode.Id, out var projectEdges))
                    {
                        foreach (var projectEdge in projectEdges)
                        {
                            // The target column/measure node itself may not exist in the graph: the model half
                            // is absent for a report connected live to a published dataset (Section 10 of
                            // POWERAI.md), or the model reader simply could not resolve that particular field.
                            // The `projects` edge's own target id already carries everything a field needs
                            // (table, field name, and column-vs-measure via the "col:"/"measure:" tag), because
                            // the C tool derives that id the same way regardless of whether the model side
                            // resolved, so the field is read straight off the edge rather than requiring the
                            // node to be present. This matches the pre-graph behavior, where a visual's field
                            // carried its table/field/isMeasure independently of whether `tables:`/`measures:`
                            // happened to list a matching entry.
                            byId.TryGetValue(projectEdge.To, out var targetNode);
                            var (table, field, isMeasure) = SplitTarget(projectEdge.To, targetNode);
                            fields.Add(new PbixField
                            {
                                Role = projectEdge.Role ?? string.Empty,
                                Table = table,
                                Field = field,
                                IsMeasure = isMeasure,
                            });
                        }
                    }

                    visuals.Add(new PbixVisual
                    {
                        VisualType = visualNode.VisualType ?? string.Empty,
                        Ordinal = visualNode.Ordinal,
                        Title = visualNode.Title,
                        Sql = visualNode.Sql,
                        Fields = fields,
                    });
                }
            }

            pages.Add(new PbixPage
            {
                Page = pageNode.DisplayName ?? string.Empty,
                Name = pageNode.Name,
                Ordinal = pageNode.Ordinal,
                ReportFile = pageNode.ReportFile,
                Visuals = visuals.OrderBy(v => v.Ordinal).ToList(),
            });
        }

        return pages;
    }

    /// <summary>Orders a page's <c>hasVisual</c> edges by the target visual's own ordinal, since edge emission
    /// order is not itself a contract a consumer should rely on.</summary>
    private static int ResolveOrdinal(
        IReadOnlyDictionary<string, PbixExtractTool.SpecNode> byId, string nodeId)
        => byId.TryGetValue(nodeId, out var node) ? node.Ordinal : int.MaxValue;

    /// <summary>
    /// The model tables whose Power Query source resolved to a physical warehouse object. A table is included
    /// only when all four parts are present: the tool emits them together or not at all, and a partial tuple
    /// would point a report's lineage at an object it may never have read, which is worse than leaving it on
    /// the model entity name where a reader can see it is unresolved.
    /// </summary>
    public static List<PbixModelSource> ModelSources(IReadOnlyList<PbixExtractTool.SpecNode> nodes)
    {
        var sources = new List<PbixModelSource>();

        foreach (var node in nodes)
        {
            if (node.Kind != "table"
                || string.IsNullOrWhiteSpace(node.SourceServer)
                || string.IsNullOrWhiteSpace(node.SourceDatabase)
                || string.IsNullOrWhiteSpace(node.SourceSchema)
                || string.IsNullOrWhiteSpace(node.SourceName))
            {
                continue;
            }

            // A table node's id is "<subscriber>#<reportFile>#table:<name>"; the model entity name is the
            // qualifier, which is what a visual's synthesized SQL names.
            var hash = node.Id.LastIndexOf('#');
            var tag = hash >= 0 ? node.Id[(hash + 1)..] : node.Id;
            var colon = tag.IndexOf(':');
            var modelTable = colon >= 0 ? tag[(colon + 1)..] : tag;
            if (modelTable.Length == 0)
            {
                continue;
            }

            sources.Add(new PbixModelSource(
                modelTable, node.SourceServer, node.SourceDatabase, node.SourceSchema, node.SourceName));
        }

        return sources;
    }

    /// <summary>
    /// The semantic model the report was built on: each model table with the Power Query expression that loads it
    /// and its resolved warehouse source, the columns, calculated columns, and measures defined on it, and the
    /// relationships between tables with their columns, cardinality, and whether each is active.
    /// <para>
    /// A field's table is read off the graph's own <c>hasColumn</c> and <c>definedOn</c> edges rather than by
    /// splitting the field's id on a dot, since a model table's name can itself contain one. A field no edge ties
    /// to a table (a measure the model declared without a home table) is kept under an empty table name rather
    /// than dropped. A report connected live to a published dataset carries no model, so the result is then empty.
    /// </para>
    /// </summary>
    public static PbixModel Model(
        IReadOnlyList<PbixExtractTool.SpecNode> nodes, IReadOnlyList<PbixExtractTool.SpecEdge> edges)
    {
        var tableOfField = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (edge.Kind == "hasColumn" && ModelQualifier(edge.From, "table") is { } owner)
            {
                tableOfField[edge.To] = owner;
            }
            else if (edge.Kind == "definedOn" && ModelQualifier(edge.To, "table") is { } home)
            {
                tableOfField[edge.From] = home;
            }
        }

        var tableNodes = new SortedDictionary<string, PbixExtractTool.SpecNode?>(StringComparer.Ordinal);
        var fieldsByTable = new Dictionary<string, List<PbixModelField>>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(n => n.Kind == "table"))
        {
            if (ModelQualifier(node.Id, "table") is { Length: > 0 } name)
            {
                tableNodes[name] = node;
            }
        }

        foreach (var node in nodes)
        {
            var tag = node.Kind switch
            {
                "column" => "col",
                "calculatedColumn" => "calc",
                "measure" => "measure",
                _ => null,
            };
            if (tag is null || ModelQualifier(node.Id, tag) is not { Length: > 0 } qualifier)
            {
                continue;
            }

            var table = tableOfField.TryGetValue(node.Id, out var owner) ? owner : string.Empty;
            string name;
            if (table.Length > 0 && qualifier.StartsWith(table + ".", StringComparison.Ordinal))
            {
                name = qualifier[(table.Length + 1)..];
            }
            else
            {
                name = table.Length == 0 && qualifier.StartsWith('.') ? qualifier[1..] : qualifier;
            }

            if (name.Length == 0)
            {
                continue;
            }

            tableNodes.TryAdd(table, null);
            if (!fieldsByTable.TryGetValue(table, out var fields))
            {
                fields = [];
                fieldsByTable[table] = fields;
            }

            fields.Add(new PbixModelField(name, node.Kind, node.DataType, node.Dax, node.Description));
        }

        var tables = tableNodes
            .Select(entry => new PbixModelTable(
                entry.Key,
                entry.Value?.PowerQuery,
                entry.Value?.SourceDatabase,
                entry.Value?.SourceSchema,
                entry.Value?.SourceName,
                (fieldsByTable.TryGetValue(entry.Key, out var fields) ? fields : [])
                    .OrderBy(f => FieldKindRank(f.Kind))
                    .ThenBy(f => f.Name, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

        var relationships = new List<PbixModelRelationship>();
        foreach (var edge in edges.Where(e => e.Kind == "relationship"))
        {
            if (ModelQualifier(edge.From, "table") is { Length: > 0 } fromTable
                && ModelQualifier(edge.To, "table") is { Length: > 0 } toTable)
            {
                relationships.Add(new PbixModelRelationship(
                    fromTable, edge.FromColumn, toTable, edge.ToColumn, edge.Cardinality, edge.Active ?? true));
            }
        }

        var expressions = nodes
            .Where(n => n.Kind == "expression" && !string.IsNullOrEmpty(n.PowerQuery))
            .Select(n => (Name: ModelQualifier(n.Id, "expr"), n.PowerQuery))
            .Where(e => e.Name is { Length: > 0 })
            .Select(e => new PbixModelExpression(e.Name!, e.PowerQuery!))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        return new PbixModel(tables, relationships, expressions);
    }

    /// <summary>Lists a table's plain columns first, then its calculated columns, then its measures.</summary>
    private static int FieldKindRank(string kind) => kind switch
    {
        "column" => 0,
        "calculatedColumn" => 1,
        _ => 2,
    };

    /// <summary>
    /// The qualifier after a model node id's <c>#tag:</c> marker (<c>table:Sales</c> gives <c>Sales</c>,
    /// <c>col:Sales.Amount</c> gives <c>Sales.Amount</c>), or null when the id carries no such marker. Searched for
    /// as a whole marker rather than by the last '#', because a model name can contain '#' while the tag cannot.
    /// </summary>
    private static string? ModelQualifier(string id, string tag)
    {
        var marker = "#" + tag + ":";
        var at = id.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : id[(at + marker.Length)..];
    }

    /// <summary>
    /// Resolves a <c>projects</c> edge's target into (table, field, isMeasure). Table and field always come
    /// from the target id itself: the C tool derives every column/measure id as
    /// <c>&lt;subscriber&gt;#&lt;reportFile&gt;#(col|measure):&lt;table&gt;.&lt;field&gt;</c> regardless of
    /// whether a matching model node was also emitted, so the id alone is a complete, reliable source even for
    /// a field the model half did not resolve (a live-connected report with no <c>DataModel</c> part, or a
    /// field the model reader could not otherwise match). <paramref name="targetNode"/>, when present, is used
    /// only to confirm <c>isMeasure</c> from the resolved node's own `kind`; when absent, the same id's
    /// `col:`/`measure:` tag carries that distinction instead.
    /// </summary>
    private static (string Table, string Field, bool IsMeasure) SplitTarget(
        string targetId, PbixExtractTool.SpecNode? targetNode)
    {
        var hash = targetId.LastIndexOf('#');
        var tag = hash >= 0 ? targetId[(hash + 1)..] : targetId;
        var colon = tag.IndexOf(':');
        var kindTag = colon >= 0 ? tag[..colon] : tag;
        var qualifier = colon >= 0 ? tag[(colon + 1)..] : tag;
        var dot = qualifier.IndexOf('.');
        var table = dot >= 0 ? qualifier[..dot] : qualifier;
        var field = dot >= 0 ? qualifier[(dot + 1)..] : string.Empty;
        var isMeasure = targetNode?.Kind == "measure" || (targetNode is null && kindTag == "measure");

        return (table, field, isMeasure);
    }
}

/// <summary>What extracting one report produced: its pages, what the tool declined to extract, the model tables it
/// resolved to warehouse objects, and the semantic model itself.</summary>
internal sealed record PbixExtractResult(
    IReadOnlyList<PbixPage> Pages,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PbixModelSource> ModelSources,
    PbixModel Model);

/// <summary>A report's semantic model: its tables (with the fields defined on each) and the relationships between
/// them. Empty for a report connected live to a published dataset, which carries no model of its own.</summary>
internal sealed record PbixModel(
    IReadOnlyList<PbixModelTable> Tables, IReadOnlyList<PbixModelRelationship> Relationships,
    IReadOnlyList<PbixModelExpression> Expressions);

/// <summary>One shared Power Query expression: a query the model keeps without loading it as a table.</summary>
/// <param name="Name">The query's name, as a table's query refers to it.</param>
/// <param name="PowerQuery">Its M text.</param>
internal sealed record PbixModelExpression(string Name, string PowerQuery);

/// <summary>One model table.</summary>
/// <param name="Name">The model entity name, as a visual and a DAX expression refer to it.</param>
/// <param name="PowerQuery">The Power Query (M) expression that loads it, or null when the model declares none.</param>
/// <param name="SourceDatabase">The warehouse database the expression resolved to, when it resolved.</param>
/// <param name="SourceSchema">The warehouse schema the expression resolved to, when it resolved.</param>
/// <param name="SourceName">The warehouse table the expression resolved to, when it resolved.</param>
/// <param name="Fields">Its columns, calculated columns, and measures, in that order.</param>
internal sealed record PbixModelTable(
    string Name, string? PowerQuery, string? SourceDatabase, string? SourceSchema, string? SourceName,
    IReadOnlyList<PbixModelField> Fields);

/// <summary>One field defined on a model table.</summary>
/// <param name="Name">The field's name within its table.</param>
/// <param name="Kind"><c>column</c>, <c>calculatedColumn</c>, or <c>measure</c>, as the tool names the node kind.</param>
/// <param name="DataType">A column's data type.</param>
/// <param name="Expression">A calculated column's or measure's DAX expression.</param>
/// <param name="Description">A measure's author-written description.</param>
internal sealed record PbixModelField(string Name, string Kind, string? DataType, string? Expression, string? Description);

/// <summary>One relationship between two model tables.</summary>
/// <param name="FromTable">The table on the many (or first) side.</param>
/// <param name="FromColumn">Its joining column.</param>
/// <param name="ToTable">The other table.</param>
/// <param name="ToColumn">Its joining column.</param>
/// <param name="Cardinality"><c>1:1</c>, <c>M:1</c>, <c>1:M</c>, or <c>M:M</c>.</param>
/// <param name="IsActive">False when it applies only where a measure invokes it with USERELATIONSHIP.</param>
internal sealed record PbixModelRelationship(
    string FromTable, string? FromColumn, string ToTable, string? ToColumn, string? Cardinality, bool IsActive);

/// <summary>
/// One model table resolved to the physical warehouse object its Power Query expression loads from. This is
/// what lets a consumption edge extracted from a report land on the same node an ingestion flow writes: the
/// visual names the MODEL entity (<c>Sales</c>), and this says which real table that entity IS.
/// </summary>
/// <param name="ModelTable">The model entity name, as a visual's SQL refers to it.</param>
/// <param name="Server">The server as the M expression spelled it. A candidate identity, not an estate one.</param>
/// <param name="Database">The physical database.</param>
/// <param name="Schema">The physical schema.</param>
/// <param name="Name">The physical table.</param>
internal sealed record PbixModelSource(
    string ModelTable, string Server, string Database, string Schema, string Name);

/// <summary>One page of a report, as the extractor emits it.</summary>
internal sealed class PbixPage
{
    /// <summary>The page's title as a person sees it on the tab.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>The page's internal identifier, which stays stable when the title is edited.</summary>
    public string? Name { get; set; }

    public int Ordinal { get; set; }

    /// <summary>The report file this page came from, which is part of the key a catalog row uses.</summary>
    public string? ReportFile { get; set; }

    public List<PbixVisual>? Visuals { get; set; }
}

/// <summary>One visual, with the question it asks already rendered as SQL.</summary>
internal sealed class PbixVisual
{
    public string VisualType { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string? Title { get; set; }

    /// <summary>The visual's question as one SELECT, with every filter that applies to it folded into its WHERE
    /// clause. Null only for a visual the tool could not render, which it does not emit.</summary>
    public string? Sql { get; set; }

    public List<PbixField>? Fields { get; set; }
}

/// <summary>One field a visual projects, and the role it plays in the question.</summary>
internal sealed class PbixField
{
    public string Role { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public bool IsMeasure { get; set; }
}

/// <summary>Raised when a report could not be extracted, or a report specification could not be read. The collector
/// turns this into a warning, so one unreadable report leaves the rest of the estate's lineage intact; the semantic
/// layer turns it into a refused upload.</summary>
public sealed class PbixExtractException : Exception
{
    public PbixExtractException(string message)
        : base(message)
    {
    }

    public PbixExtractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
