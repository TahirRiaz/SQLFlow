using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Files;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;
using SqlFlow.Yaml;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Phase one, declared tier: scans a folder of flow documents and turns each kind's endpoints into lineage
/// facts: what the author intends, available with nothing but the files. Pre/post-process hooks (raw T-SQL
/// in the documents) are extracted through the same AST extractor, so a hook that writes a side table is
/// declared lineage too. A document that fails to parse becomes a warning and the scan continues: one broken
/// file must not blind the whole estate.
/// </summary>
public sealed class FlowSetCollector
{
    private readonly YamlDocumentLoader _documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
        new YamlTranslateFlowLoader(), new YamlSchemaRegistrationFlowLoader());

    private readonly YamlScheduleLibraryLoader _scheduleLibraries = new();

    private readonly YamlSubscriberLibraryLoader _subscriberLibraries = new();

    public CollectionResult Collect(string flowDirectory) => Collect(flowDirectory, []);

    /// <summary>
    /// Scans <paramref name="flowDirectory"/>. <paramref name="storedSpecs"/> are the report specifications the
    /// semantic layer holds for this estate (uploaded ones, and the extractions an earlier sync kept); a subscriber
    /// reads its uploaded reports from them, and falls back to its kept extractions wherever this machine cannot
    /// extract a report the repository declares. The offline CLI passes none.
    /// </summary>
    public CollectionResult Collect(string flowDirectory, IReadOnlyList<StoredReportSpec> storedSpecs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowDirectory);
        ArgumentNullException.ThrowIfNull(storedSpecs);
        var root = Path.GetFullPath(flowDirectory);
        if (!Directory.Exists(root))
        {
            throw new SqlFlowException($"Flow directory not found: '{root}'.");
        }

        var result = new CollectionResult();
        // A flow document is any *.yaml under the estate; the historical .flow.yaml suffix is no longer required (it
        // still matches, so existing repos keep working). A .yaml that does not parse as a flow is a library, config,
        // or unrelated file and is silently ignored, not reported as broken. Shared-schedule libraries are handled by
        // ResolveSchedules, subscriber libraries by CollectSubscribers, and report specifications by the subscriber
        // that declares them, so all three are excluded from the flow parse here.
        var files = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(f => !IsScheduleLibraryFile(f) && !IsSubscriberLibraryFile(f) && !ReportSpecs.IsSpecFile(f))
            .OrderBy(f => f, StringComparer.Ordinal);

        // File producers (invokes that land files) and consumers (file ingestions) are gathered across the whole
        // estate, then reconciled once every document is in hand: a per-document pass cannot see the flow on the
        // other side of the file.
        var producers = new List<FileProducer>();
        var consumers = new List<FileConsumer>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            FlowDocument document;
            try
            {
                document = _documents.LoadFile(file);
            }
            catch (SqlFlowException)
            {
                // The .yaml did not parse as a flow document: under extension-based discovery it is a non-flow file
                // (a library, config, or unrelated yaml), so it is ignored rather than reported as a broken flow.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file matched but could not be read: a real problem worth surfacing, distinct from a non-flow file.
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            Collect(result, document, relative, File.GetLastWriteTimeUtc(file), root, producers, consumers);
        }

        ReconcileFileLinks(result, producers, consumers, root);

        // Shared schedules: build the repo-wide library (dedicated schedules.yaml files plus named inline blocks),
        // then resolve every `schedule: <name>` reference to a concrete cadence. Done after the whole estate is
        // collected because a reference can point at a definition in any file.
        ResolveSchedules(result, root);

        // The consumption side: who reads the warehouse the flows above just built. Collected after the flows so a
        // subscriber's read facts join a graph whose producing side is already fully known.
        CollectSubscribers(result, root, storedSpecs);

        var duplicates = result.Flows
            .GroupBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in duplicates)
        {
            result.Warnings.Add(
                $"flow name '{group.Key}' is declared by {group.Count()} documents ({string.Join(", ", group.Select(f => f.Node.File))}); " +
                "their facts merge under one flow, which is almost never intended.");
        }

        return result;
    }

    /// <summary>Whether a file is a shared-schedule library: named <c>schedules.yaml</c> or ending in
    /// <c>.schedules.yaml</c>. These are not flow documents (they are excluded from the flow parse and handled by
    /// <see cref="ResolveSchedules"/>) and never become pipelines; they only publish named schedules for flows to
    /// reference. Public because the proposal preflight must classify a proposed file exactly as this scan will
    /// classify it once the proposal merges.</summary>
    public static bool IsScheduleLibraryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("schedules.yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".schedules.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a file is a subscriber library: named <c>subscribers.yaml</c> or ending in
    /// <c>.subscribers.yaml</c>. Like a schedule library it is not a flow document and never becomes a pipeline;
    /// it declares who CONSUMES the estate, and is handled by <see cref="CollectSubscribers"/>. Public for the
    /// proposal preflight, mirroring <see cref="IsScheduleLibraryFile"/>.</summary>
    public static bool IsSubscriberLibraryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("subscribers.yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".subscribers.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects the estate's data subscribers: the reports, workbooks, notebooks, and applications that read the
    /// warehouse. Each subscriber becomes a node of its own, and each of its queries is parsed with the same
    /// extractor a stored-procedure body or a document hook goes through, so the tables and views the query names
    /// resolve to the SAME node identities the loading flows write. That is the whole point of the port: a list of
    /// dashboard names is an inventory, but a parsed query is lineage, and only the second can answer "which
    /// reports break if I change this table".
    /// <para>
    /// The read facts are attributed as MODULE facts (<c>ViaModule</c> = the subscriber's node key, no flow),
    /// which is exactly what a subscriber is to the graph: a body of SQL that reads objects but runs no pipeline.
    /// Nothing in the edge model, the execution plan, or the wave computation needed changing to hold them.
    /// </para>
    /// </summary>
    private void CollectSubscribers(CollectionResult result, string root, IReadOnlyList<StoredReportSpec> storedSpecs)
    {
        var files = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(IsSubscriberLibraryFile)
            .OrderBy(f => f, StringComparer.Ordinal);

        var storedBySubscriber = new Dictionary<string, List<StoredReportSpec>>(StringComparer.Ordinal);
        foreach (var spec in storedSpecs)
        {
            if (!storedBySubscriber.TryGetValue(spec.SubscriberKey, out var list))
            {
                list = [];
                storedBySubscriber[spec.SubscriberKey] = list;
            }

            list.Add(spec);
        }

        // Everything the consumption side is built from, in a stable order, so an unchanged estate fingerprints the
        // same on every machine and every pass.
        var fingerprint = new StringBuilder();

        // Subscriber names are the estate's identity for a consumer, so a name declared twice (across files, or in
        // one file) would merge two different reports into one node. The first wins and the collision is reported.
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            fingerprint.Append("library\n").Append(relative).Append('\n').Append(ReportSpecs.Hash(yaml)).Append('\n');

            var library = _subscriberLibraries.Parse(yaml, relative);
            result.Warnings.AddRange(library.Warnings);
            RegisterServers(result, library.Connections.Values);

            foreach (var subscriber in library.Subscribers)
            {
                if (declared.TryGetValue(subscriber.Name, out var firstFile))
                {
                    result.Warnings.Add(
                        $"{relative}: subscriber '{subscriber.Name}' is already declared in {firstFile}; the first wins.");
                    continue;
                }

                declared.Add(subscriber.Name, relative);
                var key = NodeKey.For(ServerIdentity.Subscriber, database: null, schema: null, subscriber.Name);
                var collected = CollectSubscriber(
                    result, subscriber, key, library.Connections, relative, root,
                    storedBySubscriber.TryGetValue(key, out var stored) ? stored : [], fingerprint);
                result.Subscribers.Add(collected);

                // Only now is it known whether anything links the subscriber: a report uploaded to the semantic layer
                // links one its library file leaves bare.
                if (collected.Queries.Count == 0
                    && library.UnlinkedWarnings.TryGetValue(subscriber.Name, out var unlinked))
                {
                    result.Warnings.Add(unlinked);
                }
            }
        }

        result.Subscribers.Sort((a, b) =>
            string.Compare(a.Subscriber.Name, b.Subscriber.Name, StringComparison.OrdinalIgnoreCase));
        result.SubscriberInputHash = ReportSpecs.Hash(fingerprint.ToString());
    }

    /// <summary>Parses one subscriber's queries into read facts plus the per-query evidence the catalog shows.</summary>
    private static CollectedSubscriber CollectSubscriber(
        CollectionResult result,
        Core.Subscribers.DataSubscriber subscriber,
        string subscriberKey,
        IReadOnlyDictionary<string, Core.Connections.DataSource> connections,
        string file,
        string root,
        IReadOnlyList<StoredReportSpec> stored,
        StringBuilder fingerprint)
    {
        var queries = new List<CollectedSubscriberQuery>(subscriber.Queries.Count);

        // A subscriber backed by a report contributes the queries its VISUALS ask, on top of any the document
        // declares by hand. Both kinds go through the identical parse below, so a visual's question becomes
        // lineage exactly as a transcribed query does and there is no second consumption path.
        var extracted = ReadReports(result, subscriber, subscriberKey, connections, file, root, stored, fingerprint);

        foreach (var query in subscriber.Queries.Concat(extracted.Queries))
        {
            // The loader already rejected a query whose server is not declared, so the lookup cannot miss.
            var serverRef = SubscriberServerRef(query.Server, connections);
            var label = $"subscriber '{subscriber.Name}' query '{query.Name}'";

            var deps = TSqlLineageExtractor.Extract(query.Sql, label, defaultDatabase: null);
            result.Warnings.AddRange(deps.Warnings);

            var objects = new List<ModelObjectRef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in ScriptFactBuilder.Facts(
                         deps, flow: null, viaModuleKey: subscriberKey, serverRef, LineageTier.Declared,
                         minimumParts: 1))
            {
                result.Facts.Add(fact);
                if (seen.Add(NodeKey.For(fact.ServerRef, fact.Database, fact.Schema, fact.Name)))
                {
                    objects.Add(new ModelObjectRef
                    {
                        ServerRef = fact.ServerRef,
                        Database = fact.Database,
                        Schema = fact.Schema,
                        Name = fact.Name,
                    });
                }
            }

            // A report's query is a first-class source of data-model knowledge: the joins an analyst writes are
            // the joins the business actually uses, and they carry the same weight here as a warehouse view's.
            ScriptFactBuilder.AppendModelObservations(result, deps, serverRef, LineageTier.Declared, label);

            if (objects.Count == 0)
            {
                result.Warnings.Add(
                    $"{file}: subscriber '{subscriber.Name}' query '{query.Name}' names no warehouse object that "
                    + "lineage can resolve; it contributes no consumption edge.");
            }

            queries.Add(new CollectedSubscriberQuery
            {
                Name = query.Name,
                ServerRef = serverRef,
                Sql = query.Sql,
                ReportFile = query.ReportFile,
                Objects = objects,
            });
        }

        return new CollectedSubscriber
        {
            Subscriber = subscriber,
            NodeKey = subscriberKey,
            File = file,
            Queries = queries,
            Pages = extracted.Pages,
            Models = extracted.Models,
            ExtractedSpecs = extracted.ExtractedSpecs,
            RetainedExtractedReports = extracted.RetainedExtractedReports,
        };
    }

    /// <summary>
    /// Reads every report behind a subscriber, returning the questions their visuals ask as queries plus each
    /// report's page/visual/field structure and semantic model. A report records which questions were already worth
    /// asking and how they were answered, so reading it beats asking a person to transcribe every visual; the
    /// structure additionally keeps each field's ROLE, which the SQL alone cannot express.
    /// <para>
    /// A report reaches a subscriber from one of three places, all read as the same specification:
    /// </para>
    /// <list type="bullet">
    /// <item>What <c>pbix:</c> declares (<see cref="DeclaredReports"/>): a <c>.pbix</c> the extractor reads, a
    /// committed specification, or a directory of either.</item>
    /// <item>A specification uploaded to the semantic layer for this subscriber. A label the repository already
    /// supplies wins over an upload of the same name, so the repository stays the authority on what it declares.</item>
    /// <item>The copy an earlier sync kept of a declared <c>.pbix</c>, used only where this machine cannot extract it
    /// (no extractor, or the file is not here, as in a control plane syncing a clone whose reports are
    /// git-ignored).</item>
    /// </list>
    /// <para>
    /// Every failure is a warning, never a throw: an unreadable or absent report must leave the rest of the estate's
    /// lineage intact, exactly as an unparseable flow document does.
    /// </para>
    /// </summary>
    private static ExtractedReport ReadReports(
        CollectionResult result,
        Core.Subscribers.DataSubscriber subscriber,
        string subscriberKey,
        IReadOnlyDictionary<string, Core.Connections.DataSource> connections,
        string file,
        string root,
        IReadOnlyList<StoredReportSpec> stored,
        StringBuilder fingerprint)
    {
        var declaresReport = !string.IsNullOrWhiteSpace(subscriber.Pbix);
        var uploads = stored
            .Where(s => string.Equals(s.Origin, ReportSpecOrigin.Upload, StringComparison.Ordinal))
            .OrderBy(s => s.ReportFile, StringComparer.Ordinal)
            .ToList();
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in stored.Where(s => string.Equals(s.Origin, ReportSpecOrigin.Extracted, StringComparison.Ordinal)))
        {
            kept.TryAdd(spec.ReportFile, spec.Yaml);
        }

        // A subscriber that no longer declares a report has no use for an extraction kept for it.
        IReadOnlySet<string>? noneRetained = new HashSet<string>(StringComparer.Ordinal);

        if (!declaresReport && uploads.Count == 0)
        {
            return new ExtractedReport([], [], [], [], noneRetained);
        }

        // The report's queries run against the subscriber's own connection, since a visual names model entities
        // rather than a server; a registered-source subscriber's report resolves against the registered databases.
        // Without either there is nothing to resolve them against.
        var server = subscriber.IsRegisteredSource
            ? Core.Subscribers.DataSubscriber.RegisteredSource
            : subscriber.Server
                ?? (subscriber.Queries.Count > 0 ? subscriber.Queries[0].Server : null)
                ?? (connections.Count == 1 ? connections.Keys.First() : null);
        if (server is null
            || (!subscriber.IsRegisteredSource && !connections.ContainsKey(server)))
        {
            var what = declaresReport ? $"declares 'pbix: {subscriber.Pbix}'" : "has an uploaded report";
            result.Warnings.Add(
                $"{file}: subscriber '{subscriber.Name}' {what} but no usable 'server' to resolve its visuals' tables "
                + "against; the report was not extracted. Declare the connection alias the report reads through.");
            return new ExtractedReport([], [], [], [], declaresReport ? null : noneRetained);
        }

        var inputs = new SortedDictionary<string, ReportInput>(StringComparer.Ordinal);
        var fresh = new List<ExtractedReportSpec>();
        var retained = declaresReport
            ? DeclaredReports(result, subscriber, file, root, kept, inputs, fresh)
            : noneRetained;

        foreach (var upload in uploads)
        {
            if (inputs.ContainsKey(upload.ReportFile))
            {
                result.Warnings.Add(
                    $"{file}: subscriber '{subscriber.Name}' has an uploaded report '{upload.ReportFile}', but the "
                    + "repository declares a report with the same name, which is used instead. Delete the upload or "
                    + "rename the report.");
                continue;
            }

            inputs[upload.ReportFile] = new ReportInput(
                upload.ReportFile, upload.Yaml, $"uploaded report '{upload.ReportFile}'");
        }

        var queries = new List<Core.Subscribers.SubscriberQuery>();
        var pages = new List<Core.Lineage.LineageSubscriberPage>();
        var models = new List<Core.Lineage.LineageSubscriberModel>();

        // More than one report can legitimately share a page's display name and a visual's title (two files in the
        // same directory both starting from the same report template, say), so a query name that would be
        // unambiguous for a single report is folded together with its report file once there is more than one.
        var qualifyWithReportFile = inputs.Count > 1;

        foreach (var input in inputs.Values)
        {
            fingerprint.Append("report\n").Append(subscriberKey).Append('\n').Append(input.ReportFile).Append('\n')
                .Append(ReportSpecs.Hash(input.Yaml)).Append('\n');
            ReadOneReport(result, subscriber, file, server, connections, input, qualifyWithReportFile, queries, pages, models);
        }

        return new ExtractedReport(queries, pages, models, fresh, retained);
    }

    /// <summary>
    /// Resolves what a subscriber's <c>pbix:</c> declares into <paramref name="inputs"/>: every committed
    /// specification read as it is, and every <c>.pbix</c> without one extracted now (collected into
    /// <paramref name="fresh"/>) or, where this machine cannot extract it, served from the copy an earlier sync kept.
    /// A committed specification wins over a <c>.pbix</c> of the same report so every machine reads the same thing,
    /// whether or not it has the extractor.
    /// </summary>
    /// <returns>The report labels whose kept extraction is still wanted, or null when this pass cannot tell.</returns>
    private static IReadOnlySet<string>? DeclaredReports(
        CollectionResult result,
        Core.Subscribers.DataSubscriber subscriber,
        string file,
        string root,
        IReadOnlyDictionary<string, string> kept,
        SortedDictionary<string, ReportInput> inputs,
        List<ExtractedReportSpec> fresh)
    {
        var declaration = $"{file}: subscriber '{subscriber.Name}' declares 'pbix: {subscriber.Pbix}'";
        var declaredPath = Path.GetFullPath(Path.Combine(root, subscriber.Pbix!));
        List<(string Label, string Path)> reports;
        List<(string Label, string Path)> specs;

        if (Directory.Exists(declaredPath))
        {
            reports = Directory.EnumerateFiles(declaredPath, "*.pbix", SearchOption.AllDirectories)
                .Select(p => (Label: Path.GetRelativePath(declaredPath, p).Replace('\\', '/'), Path: p))
                .ToList();
            specs = Directory.EnumerateFiles(declaredPath, "*.yaml", SearchOption.AllDirectories)
                .Where(ReportSpecs.IsSpecFile)
                .Select(p => (Label: ReportSpecs.LabelOf(Path.GetRelativePath(declaredPath, p).Replace('\\', '/')), Path: p))
                .ToList();

            if (reports.Count == 0 && specs.Count == 0)
            {
                // A folder whose reports are git-ignored still exists in a clone when anything else in it is tracked,
                // so an empty one is not proof the reports are gone: keep serving what was last extracted from it.
                if (kept.Count == 0)
                {
                    result.Warnings.Add(
                        $"{declaration}, a directory containing no .pbix files or {ReportSpecs.FileSuffix} "
                        + "specifications; no report was extracted.");
                    return null;
                }

                result.Warnings.Add(
                    $"{declaration}, a directory containing no .pbix files or {ReportSpecs.FileSuffix} specifications "
                    + $"here; serving the {kept.Count} report(s) last extracted from it.");
                UseKept(kept, kept.Keys, inputs);
                return null;
            }
        }
        else if (File.Exists(declaredPath))
        {
            var name = Path.GetFileName(declaredPath);
            var isSpec = name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
            reports = isSpec ? [] : [(name, declaredPath)];
            specs = isSpec ? [(ReportSpecs.LabelOf(name), declaredPath)] : [];
        }
        else
        {
            if (kept.Count == 0)
            {
                result.Warnings.Add(
                    $"{declaration}, which does not exist as either a file or a directory; the report was not extracted.");
                return null;
            }

            result.Warnings.Add(
                $"{declaration}, which does not exist here as either a file or a directory; serving the {kept.Count} "
                + "report(s) last extracted from it.");
            UseKept(kept, kept.Keys, inputs);
            return null;
        }

        foreach (var (label, path) in specs.OrderBy(s => s.Label, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            try
            {
                if (new FileInfo(path).Length > ReportSpecs.MaxBytes)
                {
                    result.Warnings.Add(
                        $"{declaration}: specification '{relative}' is larger than "
                        + $"{ReportSpecs.MaxBytes / (1024 * 1024)} MB; it was not read.");
                    continue;
                }

                if (!inputs.TryAdd(label, new ReportInput(label, File.ReadAllText(path), $"specification '{relative}'")))
                {
                    result.Warnings.Add($"{declaration}: more than one specification is named '{label}'; the first is used.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{declaration}: specification '{relative}' could not be read ({ex.Message}).");
            }
        }

        var pending = new List<(string Label, string Path)>();
        foreach (var report in reports.OrderBy(r => r.Label, StringComparer.Ordinal))
        {
            if (inputs.ContainsKey(report.Label))
            {
                result.Warnings.Add(
                    $"{declaration}: report '{report.Label}' has both a .pbix and a committed specification; the "
                    + "specification is used. Regenerate it with 'sqlflow powerbi extract' when the report changes.");
                continue;
            }

            pending.Add(report);
        }

        var retained = new HashSet<string>(pending.Select(p => p.Label), StringComparer.Ordinal);
        if (pending.Count == 0)
        {
            return retained;
        }

        // Extraction runs in a separate binary, which the machine running the sync may not have: it is built where
        // the .pbix files live rather than shipped inside the control plane, deliberately, so that a hostile report
        // never reaches the process holding catalog credentials. Its absence is a warning and not a failure, and a
        // report an earlier sync extracted keeps being served from the copy it kept.
        var executable = PbixExtractTool.Locate();
        if (executable is null)
        {
            var unserved = pending.Where(p => !kept.ContainsKey(p.Label)).Select(p => p.Label).ToList();
            if (unserved.Count > 0)
            {
                result.Warnings.Add(
                    $"{declaration} but the 'pbix-extract' tool was not found, so {Describe(unserved)} could not be "
                    + "extracted. Build it (make -C tools/pbix-extract) and put it on PATH, set "
                    + $"{PbixExtractTool.PathVariable} to its location, or commit a specification made with "
                    + "'sqlflow powerbi extract'.");
            }

            UseKept(kept, pending.Select(p => p.Label), inputs);
            return null;
        }

        foreach (var (label, path) in pending)
        {
            try
            {
                var yaml = ReportSpecs.Extract(executable, path, label);
                fresh.Add(new ExtractedReportSpec(label, yaml));
                inputs[label] = new ReportInput(label, yaml, $"report '{label}'");
            }
            catch (PbixExtractException ex)
            {
                var fallback = kept.ContainsKey(label)
                    ? "; serving the copy last extracted from it"
                    : "; the report was not extracted";
                result.Warnings.Add(
                    $"{file}: subscriber '{subscriber.Name}' report '{label}' could not be read ({ex.Message}){fallback}.");
                UseKept(kept, [label], inputs);
            }
        }

        return retained;
    }

    /// <summary>Adds the kept extraction of each of <paramref name="labels"/> that has one.</summary>
    private static void UseKept(
        IReadOnlyDictionary<string, string> kept, IEnumerable<string> labels, SortedDictionary<string, ReportInput> inputs)
    {
        foreach (var label in labels)
        {
            if (kept.TryGetValue(label, out var yaml))
            {
                inputs.TryAdd(label, new ReportInput(label, yaml, $"the kept extraction of report '{label}'"));
            }
        }
    }

    private static string Describe(IReadOnlyList<string> labels)
        => labels.Count == 1 ? $"report '{labels[0]}'" : $"{labels.Count} reports ({string.Join(", ", labels)})";

    /// <summary>
    /// Turns each model table the tool resolved to a physical warehouse object into one
    /// <see cref="SynonymLink"/>, which is what makes a report's consumption edge land on the same node an
    /// ingestion flow writes rather than on a name-only one.
    /// <para>
    /// A visual's synthesized SQL names the MODEL entity (<c>FROM [Sales]</c>), which the extractor resolves
    /// as a bare one-part name under the subscriber's own server. The synonym pass in
    /// <c>LineageGraphBuilder</c> then rewrites exactly that identity onto the physical object. Reusing the
    /// synonym mechanism rather than adding one is the point: a PowerBI-derived synonym and one read from
    /// <c>sys.synonyms</c> are indistinguishable to the resolution pass, so the graph builder needed no
    /// change at all.
    /// </para>
    /// <para>
    /// The server the M expression named is deliberately NOT used as the synonym's target server. A
    /// connection string in a report is not the same identity as the estate's declared connection reference,
    /// and treating them as equal would split one physical server into two nodes. The target keeps the
    /// subscriber's own server (which is what its queries already resolve against) and only the
    /// database/schema/name are taken from the model source, since those are what the bare model name lacks.
    /// </para>
    /// </summary>
    private static void EmitModelSourceSynonyms(
        CollectionResult result,
        string server,
        IReadOnlyDictionary<string, Core.Connections.DataSource> connections,
        PbixExtractResult extracted)
    {
        if (extracted.ModelSources.Count == 0
            || (server != Core.Subscribers.DataSubscriber.RegisteredSource && !connections.ContainsKey(server)))
        {
            return;
        }

        var serverRef = SubscriberServerRef(server, connections);

        foreach (var source in extracted.ModelSources)
        {
            // The "from" side is the identity a visual's bare model-entity reference actually lands on:
            // the subscriber's server with no database or schema (FlowSetCollector extracts subscriber
            // queries with defaultDatabase: null and minimumParts: 1). Empty strings key the same way a
            // null does through NodeKey.For, which is what lets this match.
            result.Synonyms.Add(new SynonymLink
            {
                ServerRef = serverRef,
                Database = string.Empty,
                Schema = string.Empty,
                Name = source.ModelTable,
                TargetDatabase = source.Database,
                TargetSchema = source.Schema,
                TargetName = source.Name,
            });
        }
    }

    /// <summary>Reads one report's specification into <paramref name="pages"/>, its visuals' synthesized queries
    /// into <paramref name="queries"/>, and its semantic model into <paramref name="models"/>, all accumulated across
    /// every report the subscriber has. See <see cref="ReadReports"/>.</summary>
    private static void ReadOneReport(
        CollectionResult result,
        Core.Subscribers.DataSubscriber subscriber,
        string file,
        string server,
        IReadOnlyDictionary<string, Core.Connections.DataSource> connections,
        ReportInput input,
        bool qualifyWithReportFile,
        List<Core.Subscribers.SubscriberQuery> queries,
        List<Core.Lineage.LineageSubscriberPage> pages,
        List<Core.Lineage.LineageSubscriberModel> models)
    {
        var reportFile = input.ReportFile;
        PbixExtractResult extracted;
        try
        {
            extracted = PbixExtractTool.Parse(input.Yaml, input.Source);
        }
        catch (PbixExtractException ex)
        {
            result.Warnings.Add(
                $"{file}: subscriber '{subscriber.Name}' report '{reportFile}' could not be read "
                + $"({ex.Message}); the report was not extracted.");
            return;
        }

        // What the tool declined to extract, and why: a visual whose filter it could not represent, a
        // projection the query does not select. These are re-emitted verbatim so a dropped question is visible
        // in the estate's own warnings rather than only in the tool's output.
        foreach (var warning in extracted.Warnings)
        {
            result.Warnings.Add($"{file}: subscriber '{subscriber.Name}' report '{reportFile}': {warning}");
        }

        EmitModelSourceSynonyms(result, server, connections, extracted);

        foreach (var page in extracted.Pages)
        {
            var visualList = page.Visuals ?? [];
            var visuals = new List<Core.Lineage.LineageSubscriberVisual>(visualList.Count);
            foreach (var visual in visualList)
            {
                if (visual.Sql is not { Length: > 0 } sql)
                {
                    // The tool does not emit a visual it could not render, and says why under its own
                    // warnings, so one arriving without SQL means the two have drifted out of step.
                    result.Warnings.Add(
                        $"{file}: subscriber '{subscriber.Name}' report '{reportFile}' page "
                        + $"'{page.Page}' visual #{visual.Ordinal} arrived with no query; it was skipped.");
                    continue;
                }

                // The query's name identifies the visual it came from, so the catalog can say WHICH chart links
                // a report to a table, and so the structure below can point back at its own SQL. Once more than
                // one report is in play the report file itself joins the name, so two reports' identically
                // titled visuals do not collide into one synthesized query.
                var visualLabel = visual.Title is { Length: > 0 } title
                    ? title
                    : $"{visual.VisualType} #{visual.Ordinal}";
                var queryName = qualifyWithReportFile
                    ? $"{reportFile} / {page.Page} / {visualLabel}"
                    : $"{page.Page} / {visualLabel}";

                queries.Add(new Core.Subscribers.SubscriberQuery
                {
                    Name = queryName,
                    Server = server,
                    Sql = sql,
                    ReportFile = reportFile,
                });

                visuals.Add(new Core.Lineage.LineageSubscriberVisual
                {
                    Ordinal = visual.Ordinal,
                    VisualType = visual.VisualType,
                    Title = visual.Title,
                    QueryName = queryName,
                    Fields = (visual.Fields ?? [])
                        .Select(f => new Core.Lineage.LineageSubscriberField
                        {
                            Role = f.Role,
                            TableName = f.Table,
                            ColumnOrMeasure = f.Field,
                            IsMeasure = f.IsMeasure,
                        })
                        .ToList(),
                });
            }

            // The page carries the label this subscriber knows the report by, not whatever label the specification
            // was written with: a specification made on another machine, or uploaded under a new name, must key its
            // pages the same way its model is keyed below.
            pages.Add(new Core.Lineage.LineageSubscriberPage
            {
                ReportFile = reportFile,
                Name = page.Name ?? page.Page,
                DisplayName = page.Page,
                Ordinal = page.Ordinal,
                Visuals = visuals,
            });
        }

        // The semantic model behind this report file: how it computes its numbers, which its visuals only name. A
        // report connected live to a published dataset carries none, and then contributes nothing here.
        var model = extracted.Model;

        // The connection identity the report reads through: the server segment a resolved source's node key carries,
        // the same one its model-source synonyms are declared under.
        var serverRef = server == Core.Subscribers.DataSubscriber.RegisteredSource || connections.ContainsKey(server)
            ? SubscriberServerRef(server, connections)
            : null;
        if (model.Tables.Count > 0 || model.Relationships.Count > 0)
        {
            models.Add(new Core.Lineage.LineageSubscriberModel
            {
                ReportFile = reportFile,
                Tables = model.Tables
                    .Select(t => new Core.Lineage.LineageSubscriberModelTable
                    {
                        Name = t.Name,
                        PowerQuery = t.PowerQuery,
                        SourceDatabase = t.SourceDatabase,
                        SourceSchema = t.SourceSchema,
                        SourceName = t.SourceName,
                        ServerRef = t.SourceName is null ? null : serverRef,
                        Fields = t.Fields
                            .Select(f => new Core.Lineage.LineageSubscriberModelField
                            {
                                Name = f.Name,
                                Kind = f.Kind,
                                DataType = f.DataType,
                                Expression = f.Expression,
                                Description = f.Description,
                            })
                            .ToList(),
                    })
                    .ToList(),
                Relationships = model.Relationships
                    .Select(r => new Core.Lineage.LineageSubscriberModelRelationship
                    {
                        FromTable = r.FromTable,
                        FromColumn = r.FromColumn,
                        ToTable = r.ToTable,
                        ToColumn = r.ToColumn,
                        Cardinality = r.Cardinality,
                        IsActive = r.IsActive,
                    })
                    .ToList(),
                Expressions = model.Expressions
                    .Select(e => new Core.Lineage.LineageSubscriberExpression { Name = e.Name, PowerQuery = e.PowerQuery })
                    .ToList(),
            });
        }
    }

    /// <summary>One report about to be read: the label the subscriber knows it by, its specification, and where the
    /// specification came from (named in any warning).</summary>
    private sealed record ReportInput(string ReportFile, string Yaml, string Source);

    /// <summary>What reading a subscriber's reports produced: its visuals as queries, their structure, the semantic
    /// model behind each report, the specifications extracted this pass, and which kept extractions are still
    /// wanted.</summary>
    private sealed record ExtractedReport(
        IReadOnlyList<Core.Subscribers.SubscriberQuery> Queries,
        IReadOnlyList<Core.Lineage.LineageSubscriberPage> Pages,
        IReadOnlyList<Core.Lineage.LineageSubscriberModel> Models,
        IReadOnlyList<ExtractedReportSpec> ExtractedSpecs,
        IReadOnlySet<string>? RetainedExtractedReports);

    /// <summary>
    /// Builds the repo's named schedules and their MEMBER SETS. A schedule is defined once (a <c>schedules.yaml</c>
    /// library entry, or an inline block on a flow) and flows join it by name with <c>schedule: &lt;name&gt;</c>; a
    /// flow may join several. Joining is membership, never a cadence copy: the schedule fires once and runs every
    /// member as a single wave-ordered group, which is what keeps a source's loads from racing the merges that read
    /// them. An unnamed inline block takes its declaring flow's name, so every schedule is named and every fire has a
    /// member set. Library entries and inline names share one namespace and the first definition of a name wins (a
    /// redefinition is warned). A reference to an unknown name leaves that flow unscheduled with a warning, never a
    /// broken schedule.
    /// </summary>
    private void ResolveSchedules(CollectionResult result, string root)
    {
        var library = new Dictionary<string, CollectedSchedule>(StringComparer.OrdinalIgnoreCase);

        void Register(string name, ScheduleSpec spec, string originFile, string? originFlow, string? libraryYaml)
        {
            var schedule = new CollectedSchedule
            {
                Name = name,
                Spec = spec with { Name = name, Refs = [] },
                OriginFile = originFile,
                OriginFlow = originFlow,
                LibraryYaml = libraryYaml,
            };
            if (!library.TryAdd(name, schedule))
            {
                result.Warnings.Add(
                    $"schedule name '{name}' is declared more than once ({schedule.Origin} redefines {library[name].Origin}); the first wins.");
            }
        }

        // 1) Dedicated library files.
        var libraryFiles = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(IsScheduleLibraryFile)
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in libraryFiles)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            var parsed = _scheduleLibraries.Parse(yaml, relative);
            result.Warnings.AddRange(parsed.Warnings);
            foreach (var named in parsed.Schedules)
            {
                Register(named.Name, named.Spec, relative, originFlow: null, libraryYaml: yaml);
            }
        }

        // 2) Inline blocks. A name: publishes the cadence for other flows to join; an unnamed block is still a
        //    schedule, named after its flow. Either way the declaring flow is a member: writing a cadence on a flow
        //    schedules that flow.
        foreach (var flow in result.Flows)
        {
            if (flow.Schedule is { IsReference: false } inline)
            {
                Register(
                    string.IsNullOrWhiteSpace(inline.Name) ? flow.Node.Name : inline.Name,
                    inline,
                    flow.Node.File,
                    flow.Node.Name,
                    libraryYaml: null);
            }
        }

        // 3) Bind membership. The declaring flow of an inline block joins its own schedule; a referencing flow joins
        //    each name it lists. A flow can appear once per schedule at most, so a repeated reference is idempotent.
        void Join(string scheduleName, string flowName)
        {
            var members = library[scheduleName].Members;
            if (!members.Contains(flowName, StringComparer.OrdinalIgnoreCase))
            {
                members.Add(flowName);
            }
        }

        foreach (var flow in result.Flows)
        {
            switch (flow.Schedule)
            {
                case { IsReference: false } inline:
                {
                    var name = string.IsNullOrWhiteSpace(inline.Name) ? flow.Node.Name : inline.Name;
                    // A losing redefinition (warned above) still joins the winning schedule of that name: the author
                    // asked for this cadence under this name, and the first definition is the one that survives.
                    Join(name, flow.Node.Name);
                    break;
                }

                case { IsReference: true } reference:
                {
                    foreach (var name in reference.Refs)
                    {
                        if (library.ContainsKey(name))
                        {
                            Join(name, flow.Node.Name);
                        }
                        else
                        {
                            result.Warnings.Add(
                                $"'{flow.Node.Name}' ({flow.Node.File}) joins schedule '{name}', which no schedules.yaml or " +
                                "named inline block defines; the flow is left unscheduled.");
                        }
                    }

                    break;
                }
            }
        }

        // 4) A library entry nothing joined never fires. That is a real authoring mistake (a renamed source, a typo
        //    on the referencing side), so it is surfaced rather than sitting in the catalog as a schedule with an
        //    empty set.
        foreach (var schedule in library.Values)
        {
            if (schedule.Members.Count == 0)
            {
                result.Warnings.Add(
                    $"schedule '{schedule.Name}' ({schedule.Origin}) has no members: no flow joins it with " +
                    $"'schedule: {schedule.Name}', so it would fire nothing.");
            }
        }

        result.Schedules.AddRange(library.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));

        // 5) Every flow that automatic dispatch could run should be attached to a schedule. A 'mode: manual' or
        //    'mode: disabled' flow opted out deliberately, so it is exempt; anything else that joined nothing will
        //    simply never run, which is almost always an oversight rather than an intent.
        var attached = new HashSet<string>(
            library.Values.SelectMany(s => s.Members), StringComparer.OrdinalIgnoreCase);
        foreach (var flow in result.Flows)
        {
            if (!attached.Contains(flow.Node.Name) && flow.Node.Mode == Core.Runs.ExecutionMode.Auto)
            {
                result.Warnings.Add(
                    $"'{flow.Node.Name}' ({flow.Node.File}) is attached to no schedule and is not 'mode: manual' " +
                    "or 'mode: disabled', so nothing will ever run it; join one with 'schedule: <name>'.");
            }
        }
    }

    /// <summary>The server identity a subscriber alias stands for: the declared connection's reference, or
    /// <see cref="ServerIdentity.Registered"/> for a registered-source subscriber.</summary>
    private static string SubscriberServerRef(
        string alias, IReadOnlyDictionary<string, Core.Connections.DataSource> connections)
        => alias == Core.Subscribers.DataSubscriber.RegisteredSource
            ? ServerIdentity.Registered
            : ServerIdentity.From(connections[alias].ConnectionRef);

    /// <summary>Registers a document's connections in the server inventory the derived tier connects to.</summary>
    private static void RegisterServers(CollectionResult result, IEnumerable<Core.Connections.DataSource> connections)
    {
        foreach (var connection in connections)
        {
            var identity = ServerIdentity.From(connection.ConnectionRef);
            if (!result.Servers.TryAdd(identity, (connection.ConnectionRef, connection.Kind))
                && result.Servers[identity].Kind != connection.Kind)
            {
                result.Warnings.Add(
                    $"server '{identity}' is declared with conflicting providers ({result.Servers[identity].Kind} vs {connection.Kind}); the first wins.");
            }
        }
    }

    private static void Collect(
        CollectionResult result, FlowDocument document, string file, DateTime fileWriteUtc, string root,
        List<FileProducer> producers, List<FileConsumer> consumers)
    {
        // The flow nodes come from the shared header projection, the single authority for what a document
        // declares (name, kind, batch, servers, mode, lifecycle, schedule), shared with the catalog's per-run
        // write-back so the two paths can never extract a document differently. An unknown document kind throws
        // there and is reported by the per-file catch in Collect(directory). The switch below contributes only what
        // lineage adds on top of the headers: the server inventory, the declared facts, and the file producers and
        // consumers reconciled after the whole estate is scanned.
        var headers = FlowDocumentHeaders.Project(document);
        foreach (var header in headers)
        {
            result.Flows.Add(new CollectedFlow
            {
                Node = new LineageFlowNode
                {
                    Name = header.Name,
                    Kind = header.Kind,
                    File = file,
                    Batch = header.Batch,
                    Mode = header.Mode,
                    Lifecycle = header.Lifecycle,
                },
                SourceServerRef = header.SourceServerRef,
                TargetServerRef = header.TargetServerRef,
                Schedule = header.Schedule,
                ParticipatesInLineage = header.ParticipatesInLineage,
                FileWriteUtc = fileWriteUtc,
            });
        }

        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var name = headers[0].Name;
                RegisterServers(result, doc.Document.Connections);
                var source = headers[0].SourceServerRef!;
                var target = headers[0].TargetServerRef;

                result.Facts.Add(ObjectFact(name, LineageRelation.Reads, source, flow.Source.Table, LineageNodeKind.Unknown));
                result.Facts.Add(ObjectFact(name, LineageRelation.Writes, target, flow.Target.Table, LineageNodeKind.Table));

                // The transformation view is a run output too (external-DB landings): the flow refreshes
                // [schema].[v<Table>] over its target, and the downstream chained flow reads THE VIEW. Declaring
                // it written here connects "landing flow -> view -> downstream flow" so waves order the chain.
                if (flow.Transform.GeneratesView)
                {
                    result.Facts.Add(ObjectFact(
                        name, LineageRelation.Writes, target,
                        flow.Target.Table with { Name = $"v_{flow.Target.Table.Name}" }, LineageNodeKind.View));
                    CollectGeneratedViewModule(
                        result, target, flow.Target.Table.Database, flow.Target.Table.Schema,
                        flow.Target.Table.Name, flow.Transform);
                }

                ExtractHook(result, name, target, flow.Process.PreProcessOnTarget, $"{file}: preProcess", flow.Target.Table.Database);
                ExtractHook(result, name, target, flow.Process.PostProcessOnTarget, $"{file}: postProcess", flow.Target.Table.Database);

                // The YAML's declared key columns are the target's business key (the update/insert match):
                // the declared tier of the interpreted data model, straight from the author.
                if (flow.Load.KeyColumns.Count > 0)
                {
                    result.KeyHints.Add(new CollectedKeyHint
                    {
                        Table = new ModelObjectRef
                        {
                            ServerRef = target,
                            Database = flow.Target.Table.Database,
                            Schema = flow.Target.Table.Schema,
                            Name = flow.Target.Table.Name,
                        },
                        Columns = flow.Load.KeyColumns,
                        Origin = LineageModelOrigin.Declared,
                        Tier = LineageTier.Declared,
                    });
                }

                // The embedded healthCheck: block became its own flow node above (the projection's derived hc
                // sibling); it READS the load's target, which is exactly the dependency that orders it after the
                // load in waves and node runs.
                if (doc.Document.HealthCheck is { } check)
                {
                    result.Facts.Add(ObjectFact(check.SysAlias, LineageRelation.Reads, target, check.Target, LineageNodeKind.Table));
                }

                break;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                result.Facts.Add(ObjectFact(headers[0].Name, LineageRelation.Reads, headers[0].TargetServerRef, flow.Source, LineageNodeKind.Unknown));
                if (!string.IsNullOrWhiteSpace(flow.TrgPath))
                {
                    result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, flow.TrgPath, root));
                }

                break;
            }

            case TranslateFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                var server = headers[0].TargetServerRef;

                // The flow's true inbound is whatever tables its declared SQL reads: the primary query and every
                // dataset query go through the same T-SQL extraction as authored hook scripts, so the graph
                // shows source table -> translate flow instead of the flow floating as an output-only root.
                ExtractHook(result, headers[0].Name, server, flow.Query, $"{file}: source.query", defaultDatabase: null);
                foreach (var dataset in flow.Datasets)
                {
                    ExtractHook(result, headers[0].Name, server, dataset.Query, $"{file}: datasets.{dataset.Name}", defaultDatabase: null);
                }

                // The saved documents are a declared file drop: the writes fact records the folder, and the
                // producer registration lets reconciliation bind any downstream file flow watching it.
                result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, flow.Output.Path, root));
                producers.Add(new FileProducer(headers[0].Name, [new FileOutput { Location = flow.Output.Path }]));

                // The optional delivery endpoint is an outbound the flow writes, mirroring how an acquisition
                // records its inbound endpoints, so the graph carries where the documents actually go.
                if (flow.Invoke is not null)
                {
                    result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, flow.Invoke.Url, root));
                }

                break;
            }

            case StoredProcedureFlowDocument doc:
            {
                RegisterServers(result, doc.Document.Connections);

                // The flow requires the procedure; the procedure's own reads/writes are its module's
                // lineage, expanded by the derived tier.
                result.Facts.Add(ObjectFact(
                    headers[0].Name, LineageRelation.Requires, headers[0].TargetServerRef, doc.Document.Flow.Procedure, LineageNodeKind.Procedure));
                break;
            }

            case HealthCheckFlowDocument doc:
            {
                RegisterServers(result, doc.Document.Connections);
                result.Facts.Add(ObjectFact(
                    headers[0].Name, LineageRelation.Reads, headers[0].TargetServerRef, doc.Document.Flow.Target, LineageNodeKind.Unknown));
                break;
            }

            case CalendarFlowDocument doc:
            {
                // The generator reads nothing: the dimension is computed, so the flow is a pure producer and
                // its table is a root of the graph that every conforming fact joins to.
                RegisterServers(result, doc.Document.Connections);
                result.Facts.Add(ObjectFact(
                    headers[0].Name, LineageRelation.Writes, headers[0].TargetServerRef, doc.Document.Flow.Table, LineageNodeKind.Table));
                break;
            }

            case FileFlowDocument doc:
            {
                var flow = doc.Flow;
                var target = headers[0].TargetServerRef;
                result.Servers.TryAdd(target, (flow.Target.Connection, Core.Connections.DataSourceKind.MSSQL));

                // The file the flow reads is its Location, or the srcPath option when no Location is given (the
                // loader accepts either). Its normalized identity is the file node; recording the same source spec
                // as a consumer lets an invoke or acquisition that lands into this folder link to THIS node.
                var readLocation = !string.IsNullOrWhiteSpace(flow.Source.Location)
                    ? flow.Source.Location!
                    : Option(flow.Source.Options, "srcPath");
                if (!string.IsNullOrWhiteSpace(readLocation))
                {
                    var fileNode = NormalizeFileIdentity(readLocation!, root);
                    result.Facts.Add(FileFact(flow.Name, LineageRelation.Reads, readLocation!, root));
                    consumers.Add(new FileConsumer(flow.Name, fileNode, new FileSelectionSpec
                    {
                        Type = flow.Source.Type,
                        Location = readLocation,
                        Glob = Option(flow.Source.Options, "srcFile"),
                        Mask = Option(flow.Source.Options, "srcPathMask"),
                    }));
                }

                result.Facts.Add(new LineageFact
                {
                    Flow = flow.Name,
                    Relation = LineageRelation.Writes,
                    ServerRef = target,
                    Schema = flow.Target.Schema,
                    Name = flow.Target.Table,
                    Tier = LineageTier.Declared,
                    KindHint = LineageNodeKind.Table,
                });

                // The pre-ingestion transform view is a run output too: the flow refreshes [schema].[v<Table>]
                // over its loaded table, and downstream chained flows read THE VIEW, not the table. Declaring the
                // view as written here is what connects "landing flow -> view -> downstream ingestion flow" in
                // the graph, so flow dependencies and execution waves order the chain correctly.
                if (flow.Inference.GeneratesView)
                {
                    result.Facts.Add(new LineageFact
                    {
                        Flow = flow.Name,
                        Relation = LineageRelation.Writes,
                        ServerRef = target,
                        Schema = flow.Target.Schema,
                        Name = $"v_{flow.Target.Table}",
                        Tier = LineageTier.Declared,
                        KindHint = LineageNodeKind.View,
                    });
                    CollectGeneratedViewModule(
                        result, target, database: null, flow.Target.Schema, flow.Target.Table, flow.Inference);
                }

                break;
            }

            case InvokeFlowDocument doc:
            {
                // An invoke triggers external compute (an ADF pipeline or Automation runbook). It moves no catalog
                // data of its own, but when the author declares the file(s) that compute lands via 'output:', the
                // invoke becomes a file producer: reconciliation connects it to the file ingestion that reads them,
                // so the graph chains invoke -> file -> landing table -> view -> downstream.
                var definition = doc.Document.Definition;
                if (definition.Outputs.Count > 0)
                {
                    producers.Add(new FileProducer(definition.InvokeAlias, definition.Outputs));
                }

                break;
            }

            case AcquireFlowDocument doc:
            {
                // An acquisition fetches from a third party and lands raw files under its landing target. Its
                // SOURCE side is the external endpoint itself: one node per item (the base URL plus the item's
                // declared request path), read by the flow - mirroring how an sftp download reads its remote
                // paths - so the acquisition carries its true inbound and the graph shows where the data
                // actually originates instead of the flow floating as an output-only root.
                var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in doc.Flow.Items)
                {
                    var baseUrl = item.Source.BaseUrl.TrimEnd('/');
                    var path = item.Source.Request?.Path;
                    var endpoint = string.IsNullOrWhiteSpace(path)
                        ? baseUrl
                        : baseUrl + (path!.StartsWith('/') ? path : "/" + path);
                    if (endpoints.Add(endpoint))
                    {
                        result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Reads, endpoint, root));
                    }
                }

                // And it is ALWAYS a file producer: one declared drop per item, each derived with engine parity
                // from that item's target + pathTemplate + the extension the landing appends, so reconciliation
                // binds each drop to the file ingestion(s) watching that landing folder (or a parent of it) and
                // the graph chains acquire -> file -> landing table -> view -> downstream, ordering the waves. A
                // multi-endpoint flow thus feeds several downstream pre flows from one pipeline. An unconsumed
                // drop still records its own node.
                producers.Add(new FileProducer(headers[0].Name, doc.Flow.Items.Select(item => AcquireDrop(item.Landing)).ToList()));
                break;
            }

            case CopyFlowDocument doc:
            {
                // A copy performs one or more steps; lineage is computed from those steps. Each step reads its source
                // (a file node chaining the upstream drop zone) and lands its target folder, which the downstream
                // ingestion reads. The copy is ALWAYS a file producer: an explicit outputs: block declares its drops,
                // otherwise each step's physical target is the drop. Reconciliation then binds every drop to the
                // ingestion(s) that read it - including a load watching the parent folder recursively while the copy
                // lands into per-dataset subfolders - and a drop nothing consumes still records its own node, so a
                // copy read by an exact-folder load or by nothing keeps its previous graph.
                foreach (var step in doc.Flow.Steps)
                {
                    result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Reads, step.Source.Location, root));
                }

                producers.Add(new FileProducer(
                    headers[0].Name,
                    doc.Flow.Outputs.Count > 0
                        ? doc.Flow.Outputs
                        : doc.Flow.Steps.Select(step => new FileOutput { Location = step.Target.Location }).ToList()));

                break;
            }

            case SftpFlowDocument doc:
            {
                // Lineage is computed from the flow's steps. Download reads each step's server path and lands each
                // step's lake target, which the downstream ingestion reads; upload reverses it. A download is ALWAYS a
                // file producer (an explicit outputs: block declares its drops, otherwise each step's local target is
                // the drop), so reconciliation binds every drop to the ingestion(s) that read it - including a load
                // watching the parent folder while the download lands into subfolders - and an unconsumed drop still
                // records its own node.
                var host = $"sftp://{doc.Flow.Server.Host}:{doc.Flow.Server.Port}";
                if (doc.Flow.Direction == Core.Sftp.SftpDirection.Download)
                {
                    foreach (var step in doc.Flow.Steps)
                    {
                        result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Reads, host + step.RemotePath, root));
                    }

                    producers.Add(new FileProducer(
                        headers[0].Name,
                        doc.Flow.Outputs.Count > 0
                            ? doc.Flow.Outputs
                            : doc.Flow.Steps.Select(step => new FileOutput { Location = step.Local }).ToList()));
                }
                else
                {
                    foreach (var step in doc.Flow.Steps)
                    {
                        result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Reads, step.Local, root));
                        result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, host + step.RemotePath, root));
                    }
                }

                break;
            }

            case SchemaRegistrationFlowDocument doc:
            {
                // A schema registration contributes no facts and no server to the derived tier (whose module
                // parsing and join inference a registration deliberately does without): it is recorded so the
                // connected pass harvests exactly its database's tables and views, attributed to the flow.
                var flow = doc.Document.Flow;
                var connection = doc.Document.Connections.First(c =>
                    string.Equals(c.Alias, flow.Server, StringComparison.OrdinalIgnoreCase));
                result.SchemaRegistrations.Add(new CollectedSchemaRegistration
                {
                    Flow = headers[0].Name,
                    ServerRef = ServerIdentity.From(connection.ConnectionRef),
                    RawReference = connection.ConnectionRef,
                    Kind = connection.Kind,
                    Scope = flow.Scope,
                });
                break;
            }

                // SourceControlFlowDocument contributes a flow node for the catalog but no facts: it reads object
                // DEFINITIONS, not data, so it declares no dependency and the graph builder drops it entirely.
                // BatchFlowDocument projects no header at all: a batch's ordering is computed FROM lineage,
                // never part of it.
        }
    }

    /// <summary>A document hook is raw author T-SQL: the same operation-wise extractor derives what it
    /// touches, attributed to the flow as declared lineage through the shared fact mapping.</summary>
    private static void ExtractHook(
        CollectionResult result, string flow, string serverRef, string? sql, string label, string? defaultDatabase)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        var deps = TSqlLineageExtractor.Extract(sql, label, defaultDatabase);
        result.Warnings.AddRange(deps.Warnings);
        result.Facts.AddRange(ScriptFactBuilder.Facts(
            deps, flow, viaModuleKey: null, serverRef, LineageTier.Declared, minimumParts: 1));
        result.ObjectArtifacts.AddRange(ScriptFactBuilder.ObjectArtifacts(deps, serverRef, LineageTier.Declared, minimumParts: 1));

        // The authored hook SQL carries data-model observations of the declared tier (joins the author
        // wrote, constraint clauses in authored DDL), attributed to the hook label as the script unit.
        ScriptFactBuilder.AppendModelObservations(result, deps, serverRef, LineageTier.Declared, label);
    }

    private static LineageFact ObjectFact(
        string flow, LineageRelation relation, string serverRef, Core.Ingestion.RelationalObject table, LineageNodeKind kind)
        => new()
        {
            Flow = flow,
            Relation = relation,
            ServerRef = serverRef,
            Database = table.Database,
            Schema = table.Schema,
            Name = table.Name,
            Tier = LineageTier.Declared,
            KindHint = kind,
        };

    /// <summary>Declared-tier module lineage of a generated transform view, driven by the view's SQL. The DDL the
    /// engine executes is synthesized offline through the same code path the run uses (the authored transform
    /// columns resolved by <see cref="Core.Engine.ColumnTransformResolver"/> into
    /// <see cref="Core.Engine.TransformViewBuilder"/>; run-time inference only adds casts and pass-throughs of the
    /// same table's columns, which reference no further objects), and the same extractor and fact mapping the
    /// derived tier applies to <c>sys.sql_modules</c> attributes what the body reads to the view as a module
    /// (flow: null). This attaches <c>v_&lt;Table&gt;</c> to every parent table the SQL references, the FROM table
    /// and any table an authored expression names, without a live connection; a downstream flow reading the view
    /// inherits those dependencies through module expansion. The module key may lack its database (a file flow does
    /// not know its target catalog); the graph builder completes it with the same identity resolution the facts
    /// get. A schema-less target is skipped: the engine itself cannot build a view there, so there is no SQL to
    /// attribute.</summary>
    private static void CollectGeneratedViewModule(
        CollectionResult result, string serverRef, string? database, string? schema, string table,
        Core.Model.TypeInferencePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        var viewName = $"v_{table}";
        var projection = Core.Engine.ColumnTransformResolver.Resolve(
            policy.Columns.Where(c => !c.Virtual).Select(c => c.Name).ToList(), policy);
        var ddl = Core.Engine.TransformViewBuilder.Build(schema, viewName, schema, table, projection);

        var moduleKey = NodeKey.For(serverRef, database, schema, viewName);
        var label = $"{serverRef}:{(database is null ? string.Empty : database + ".")}{schema}.{viewName}";
        var deps = TSqlLineageExtractor.Extract(ddl, label, defaultDatabase: database);
        result.Warnings.AddRange(deps.Warnings);

        foreach (var fact in ScriptFactBuilder.Facts(
                     deps, flow: null, viaModuleKey: moduleKey, serverRef, LineageTier.Declared, minimumParts: 1))
        {
            // The view's own CREATE statement points at itself; self-facts carry nothing.
            if (NodeKey.For(serverRef, fact.Database ?? database, fact.Schema, fact.Name) != moduleKey)
            {
                result.Facts.Add(fact with { Database = fact.Database ?? database });
            }
        }

        // The synthesized DDL is the view's declared script artifact (a live or observed definition outranks it in
        // the fold), and its joins are data-model observations attributed to the module as the script unit.
        result.ObjectArtifacts.AddRange(ScriptFactBuilder.ObjectArtifacts(deps, serverRef, LineageTier.Declared, minimumParts: 1));
        ScriptFactBuilder.AppendModelObservations(result, deps, serverRef, LineageTier.Declared, moduleKey);
    }

    private static LineageFact FileFact(string flow, LineageRelation relation, string location, string root)
        => new()
        {
            Flow = flow,
            Relation = relation,
            ServerRef = ServerIdentity.FileSystem,
            Name = NormalizeFileIdentity(location, root),
            Tier = LineageTier.Declared,
            KindHint = LineageNodeKind.File,
        };

    /// <summary>File-endpoint identity: cloud URLs verbatim; local paths normalized against the estate root
    /// (relative when inside it), so './data/x.csv' and 'data/x.csv' are one node and the identity survives
    /// a checkout moving between machines.</summary>
    private static string NormalizeFileIdentity(string location, string root)
    {
        // An Azure Storage path is one node regardless of the URI shape it was written or read in: a cpy/sftp
        // target in abfss:// form and a file ingestion reading the same folder in https://...dfs form must bind.
        if (AzureBlobLocation.CanonicalIdentity(location) is { } azure)
        {
            return azure;
        }

        if (location.Contains("://", StringComparison.Ordinal))
        {
            return location;
        }

        var full = Path.GetFullPath(Path.IsPathRooted(location) ? location : Path.Combine(root, location));
        var relative = Path.GetRelativePath(root, full);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
    }

    /// <summary>Connects each file producer (an invoke that lands files) to the file consumers (file ingestions) it
    /// feeds, with engine-parity file selection: the invoke is attributed a Writes of the SAME file node the
    /// matched ingestion reads, so the merged graph runs invoke -> file -> landing table -> view -> downstream and
    /// the execution waves order the chain. A producer nothing consumes still records its own declared output node,
    /// so the invoke is not a dangling node and links automatically once a matching ingestion is added.</summary>
    private static void ReconcileFileLinks(
        CollectionResult result, List<FileProducer> producers, List<FileConsumer> consumers, string root)
    {
        foreach (var producer in producers)
        {
            // One producer can declare several drops (an SFTP download of many file sets); each binds on its own,
            // and the invoke writes each distinct file node at most once (dedup across drops and consumers).
            var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in producer.Outputs)
            {
                var spec = output.ToSelectionSpec();
                var matched = false;
                foreach (var consumer in consumers)
                {
                    if (!FileSelection.Feeds(spec, consumer.Spec))
                    {
                        continue;
                    }

                    matched = true;
                    if (linked.Add(consumer.Node))
                    {
                        result.Facts.Add(new LineageFact
                        {
                            Flow = producer.Flow,
                            Relation = LineageRelation.Writes,
                            ServerRef = ServerIdentity.FileSystem,
                            Name = consumer.Node,
                            Tier = LineageTier.Declared,
                            KindHint = LineageNodeKind.File,
                        });
                    }
                }

                // A drop nothing consumes still records its own declared node, so it is visible and links
                // automatically once a matching ingestion is added.
                if (!matched && linked.Add(NormalizeFileIdentity(output.Location, root)))
                {
                    result.Facts.Add(FileFact(producer.Flow, LineageRelation.Writes, output.Location, root));
                }
            }
        }
    }

    /// <summary>The file drop an acquisition's landing declares, in producer form. Engine parity with the landing
    /// pipeline: a payload lands at target/pathTemplate + '.' + extension, where the extension is the declared
    /// format ('auto' derives it from the response, so any extension can land) and a gzipped landing appends '.gz'.
    /// A template token ({window.from:yyyy}, {page}, ...) renders per item, so the drop folder keeps only the
    /// template's leading static folder segments (a tokened segment and everything under it land wherever the token
    /// renders, and the matcher already treats a drop beneath the watched folder as contained), and the file segment
    /// becomes a glob with each token as a wildcard.</summary>
    private static FileOutput AcquireDrop(Core.Acquire.AcquireLanding landing)
    {
        var segments = landing.PathTemplate.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stem = segments.Length > 0 ? CollapseTemplateTokens(segments[^1]) : string.Empty;
        var staticFolders = segments.Length > 1
            ? segments[..^1].TakeWhile(s => !s.Contains('{', StringComparison.Ordinal)).ToArray()
            : [];

        var extension = string.Equals(landing.Format, "auto", StringComparison.OrdinalIgnoreCase)
            ? "*"
            : landing.Format.TrimStart('.').ToLowerInvariant();
        var suffix = landing.Compression == Core.Acquire.AcquireCompression.Gzip ? ".gz" : string.Empty;

        return new FileOutput
        {
            Location = staticFolders.Length > 0
                ? $"{landing.Target.TrimEnd('/')}/{string.Join('/', staticFolders)}"
                : landing.Target,
            SrcFile = $"{(stem.Length > 0 ? stem : "*")}.{extension}{suffix}",
        };
    }

    /// <summary>Replaces every <c>{...}</c> template token with a <c>*</c> wildcard, folding adjacent tokens into
    /// one; text outside tokens is kept verbatim (an unmatched closing brace is literal).</summary>
    private static string CollapseTemplateTokens(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '{')
            {
                if (depth++ == 0 && (builder.Length == 0 || builder[^1] != '*'))
                {
                    builder.Append('*');
                }
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string? Option(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>An invoke that declares it lands one or more file drops, awaiting reconciliation against the file
    /// ingestions.</summary>
    private sealed record FileProducer(string Flow, IReadOnlyList<FileOutput> Outputs);

    /// <summary>A file ingestion's source: the file node it reads and the selection spec a producer is matched
    /// against.</summary>
    private sealed record FileConsumer(string Flow, string Node, FileSelectionSpec Spec);
}
