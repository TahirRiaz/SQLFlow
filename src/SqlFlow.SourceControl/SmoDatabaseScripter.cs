using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.SourceControl;

// Only the Urn type is taken from Sfc: importing the namespace whole collides its IReadOnlyList<T> with the BCL's.
using Urn = Microsoft.SqlServer.Management.Sdk.Sfc.Urn;

namespace SqlFlow.SourceControl;

/// <summary>
/// Scripts a SQL Server database's objects with SMO, the most faithful generator available: every supported
/// category (<see cref="SourceControlObjectTypes"/>) is enumerated and each object emitted as a CREATE script
/// into the legacy per-type folder layout (<c>&lt;database&gt;/&lt;category&gt;/&lt;schema&gt;.&lt;name&gt;.sql</c>).
/// Output is made deterministic (objects sorted, line endings normalized) so an unchanged database re-scripts to
/// byte-identical files and only real changes surface as git diffs. Schemas the flow excludes (the engine's own
/// staging schema by default) are skipped whole, so a snapshot tracks the database's definition and not the work
/// tables a run happened to be holding.
///
/// Scripting one object with full DRI, indexes, triggers, and extended properties costs SMO dozens of small
/// round trips, which is latency-bound rather than server-bound: against a remote instance a single table takes
/// about a second wall-clock while the server itself is idle. One connection per object category walked in
/// series would therefore leave a few hundred objects taking many minutes with the trace apparently frozen, so
/// the walk fans out over <see cref="SourceControlScripting.Parallelism"/> independent connections. Each lane
/// owns its own <see cref="Server"/> and <see cref="Scripter"/> (SMO objects are not thread-safe, but separate
/// instances on separate connections are), enumeration stays on the single lead connection, and every lane
/// scripts by <see cref="Urn"/>, which resolves against whichever server it is handed to. The emitted SQL is
/// identical to the serial walk's; only the wall-clock changes.
/// </summary>
public sealed class SmoDatabaseScripter : IDatabaseScripter
{
    /// <summary>
    /// Scripts the database reached by <paramref name="connectionString"/> (its default catalog unless
    /// <paramref name="database"/> overrides it), honoring the include/exclude category filter and the
    /// data-table list. Returns every scripted object plus any warnings; throws only on a connection or
    /// missing-database error, since those make a snapshot meaningless.
    /// </summary>
    public ScriptedDatabase Script(
        string connectionString, string? database, SourceControlScripting scripting,
        Action<ScriptProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripting);

        var reporter = new ProgressReporter(progress);
        var sqlConnection = new SqlConnection(connectionString);
        var serverConnection = new ServerConnection(sqlConnection);
        try
        {
            var server = new Server(serverConnection);

            // Bulk-load object properties so enumeration does not round-trip per object (the legacy prefetch).
            server.SetDefaultInitFields(true);

            var databaseName = !string.IsNullOrWhiteSpace(database)
                ? database!.Trim()
                : NullIfBlank(new SqlConnectionStringBuilder(connectionString).InitialCatalog)
                  ?? NullIfBlank(serverConnection.DatabaseName)
                  ?? throw new SqlFlowException(
                      "The source-control connection has no default database; set 'source.database' to the database to script.");

            var db = server.Databases[databaseName]
                ?? throw new SqlFlowException(
                    $"Database '{databaseName}' was not found on the server, or the login cannot access it.");

            var categories = SelectedCategories(scripting);
            var excludedSchemas = new HashSet<string>(
                scripting.ExcludeSchemas.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var dataTables = new HashSet<string>(scripting.DataTables, StringComparer.OrdinalIgnoreCase);

            var objects = new List<ScriptedObject>();
            var warnings = new List<string>();

            using (var pool = new ScripterPool(connectionString, scripting.Parallelism))
            {
                foreach (var (folder, select) in categories)
                {
                    ct.ThrowIfCancellationRequested();

                    // Enumerating a large collection is itself a multi-second call, so it announces itself
                    // before it blocks. The reporter's throttle collapses the announcements of the many empty
                    // categories that enumerate instantly, leaving only the ones that actually cost something.
                    reporter.Enumerating(folder);
                    var pending = Enumerate(select(db), excludedSchemas);
                    if (pending.Count == 0)
                    {
                        continue;
                    }

                    reporter.CategoryStarted(folder, pending.Count);
                    var category = pool.ScriptCategory(folder, pending, databaseName, reporter, ct);
                    objects.AddRange(category.Objects);
                    warnings.AddRange(category.Warnings);
                }
            }

            if (dataTables.Count > 0)
            {
                ScriptData(db, dataTables, excludedSchemas, server, databaseName, objects, warnings, reporter);
            }

            // A stable, total order so the manifest and any diff of it are deterministic.
            objects.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            return new ScriptedDatabase { DatabaseName = databaseName, Objects = objects, Warnings = warnings };
        }
        finally
        {
            if (serverConnection.IsOpen)
            {
                serverConnection.Disconnect();
            }

            sqlConnection.Dispose();
        }
    }

    /// <summary>One object the walk has found and not yet scripted: its identity for the file it lands in, and
    /// the <see cref="Urn"/> any lane's scripter can resolve it from.</summary>
    private sealed record PendingScript(string? Schema, string Name, Urn Urn);

    /// <summary>What one category's fan-out produced, already ordered so the result does not depend on which
    /// lane happened to finish first.</summary>
    private sealed record CategoryResult(IReadOnlyList<ScriptedObject> Objects, IReadOnlyList<string> Warnings);

    /// <summary>Materializes and orders a category before scripting, so the file set is identical run to run.</summary>
    private static List<PendingScript> Enumerate(IEnumerable<NamedSmoObject> source, HashSet<string> excludedSchemas)
        => source
            .Where(o => !IsInExcludedSchema(o, excludedSchemas))
            .OrderBy(o => o is ScriptSchemaObjectBase s ? s.Schema : string.Empty, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => new PendingScript(o is ScriptSchemaObjectBase ssob ? ssob.Schema : null, o.Name, o.Urn))
            .ToList();

    /// <summary>
    /// The connections that script objects concurrently. A lane is opened the first time it is asked for work
    /// and then reused for every later category, so a snapshot pays the connect cost once per lane rather than
    /// once per category. The pool is not itself thread-safe: <see cref="ScriptCategory"/> runs one category at
    /// a time and each lane index is touched by exactly one task within it.
    /// </summary>
    private sealed class ScripterPool(string connectionString, int parallelism) : IDisposable
    {
        private readonly ScriptingLane?[] _lanes = new ScriptingLane?[Math.Max(1, parallelism)];

        public CategoryResult ScriptCategory(
            string folder, IReadOnlyList<PendingScript> pending, string databaseName, ProgressReporter reporter,
            CancellationToken ct)
        {
            var state = new CategoryState(pending.Count);
            var queue = new ConcurrentQueue<PendingScript>(pending);

            // A lane with nothing to do is a connection opened for nothing, so a small category uses fewer.
            var lanes = Math.Min(_lanes.Length, pending.Count);

            // A lost connection cancels the siblings: every one of them would fail on the same object-by-object
            // error, and there is no point spending minutes discovering that.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tasks = new Task[lanes];
            for (var i = 0; i < lanes; i++)
            {
                var lane = i;
                tasks[i] = Task.Factory.StartNew(
                    () => Drain(lane, folder, databaseName, queue, state, reporter, stop),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            try
            {
                Task.WaitAll(tasks, CancellationToken.None);
            }
            catch (AggregateException ex)
            {
                // The lanes only ever fault with the fatal error recorded below; surface the cause itself rather
                // than an aggregate whose message hides it.
                throw state.Fatal ?? ex.InnerExceptions[0];
            }

            if (state.Fatal is { } fatal)
            {
                throw fatal;
            }

            ct.ThrowIfCancellationRequested();
            reporter.Scripted(folder, state.Done, state.Total, force: true);

            // Ordered rather than left in completion order: the objects are re-sorted globally anyway, but the
            // warnings are user-facing text whose order would otherwise vary from run to run.
            var objects = state.Objects.OrderBy(o => o.RelativePath, StringComparer.Ordinal).ToList();
            var warnings = state.Warnings.OrderBy(w => w, StringComparer.Ordinal).ToList();
            return new CategoryResult(objects, warnings);
        }

        private void Drain(
            int lane, string folder, string databaseName, ConcurrentQueue<PendingScript> queue, CategoryState state,
            ProgressReporter reporter, CancellationTokenSource stop)
        {
            ScriptingLane? scripting = null;
            while (!stop.IsCancellationRequested && queue.TryDequeue(out var pending))
            {
                if (scripting is null)
                {
                    try
                    {
                        // Opened on the first object only, so a category smaller than the pool leaves lanes
                        // unopened. A lane that cannot connect at all is fatal for the same reason a lost
                        // connection is, and stops its siblings rather than letting them finish a walk whose
                        // result is thrown away.
                        scripting = _lanes[lane] ??= ScriptingLane.Open(connectionString);
                    }
                    catch (Exception ex) when (ex is SmoException or SqlException or ExecutionFailureException)
                    {
                        state.Fail(new SqlFlowException(
                            "A connection to the scripted database could not be opened; the snapshot is abandoned " +
                            "rather than committed with every object it never read.",
                            ex));
                        stop.Cancel();
                        return;
                    }
                }

                try
                {
                    var statements = scripting.Scripter.Script(new[] { pending.Urn });
                    var sql = JoinBatches(statements);
                    if (sql.Length > 0)
                    {
                        state.Objects.Add(BuildObject(folder, pending.Schema, pending.Name, databaseName, sql));
                    }
                }
                catch (Exception ex) when (ex is SmoException or SqlException or ExecutionFailureException)
                {
                    // One object that cannot be scripted is a warning, not a failed snapshot, and the commonest
                    // cause is an object enumerated and then dropped by whoever owns it, which SMO reports as
                    // "Invalid object name". A lost connection raises the same exception for every remaining
                    // object, though, and scripting nothing would commit the whole database as deleted, so the
                    // connection is checked before the walk is allowed to continue.
                    if (!scripting.IsConnected)
                    {
                        state.Fail(new SqlFlowException(
                            $"The connection to the scripted database was lost while scripting {folder} " +
                            $"{Label(pending.Schema, pending.Name)}; the snapshot is abandoned rather than " +
                            "committed with every object it never read.",
                            ex));
                        stop.Cancel();
                        return;
                    }

                    var warning = $"{folder} {Label(pending.Schema, pending.Name)}: not scripted ({ex.Message}).";
                    state.Warnings.Add(warning);
                    reporter.Warn(folder, state.Done, state.Total, warning);
                }

                reporter.Scripted(folder, state.Advance(), state.Total, force: false);
            }
        }

        public void Dispose()
        {
            foreach (var lane in _lanes)
            {
                lane?.Dispose();
            }
        }
    }

    /// <summary>One category's shared, concurrently written tally.</summary>
    private sealed class CategoryState(int total)
    {
        private int _done;

        public int Total { get; } = total;

        public int Done => Volatile.Read(ref _done);

        public ConcurrentBag<ScriptedObject> Objects { get; } = [];

        public ConcurrentBag<string> Warnings { get; } = [];

        /// <summary>The first fatal error a lane hit; later ones are redundant descriptions of the same loss.</summary>
        public Exception? Fatal { get; private set; }

        private readonly Lock _fatalGate = new();

        public int Advance() => Interlocked.Increment(ref _done);

        public void Fail(Exception error)
        {
            lock (_fatalGate)
            {
                Fatal ??= error;
            }
        }
    }

    /// <summary>One scripting connection: its own SMO server and scripter, so nothing is shared across lanes.</summary>
    private sealed class ScriptingLane : IDisposable
    {
        private readonly SqlConnection _connection;
        private readonly ServerConnection _serverConnection;

        private ScriptingLane(SqlConnection connection, ServerConnection serverConnection, Scripter scripter)
        {
            _connection = connection;
            _serverConnection = serverConnection;
            Scripter = scripter;
        }

        public Scripter Scripter { get; }

        public bool IsConnected => _serverConnection.IsOpen;

        public static ScriptingLane Open(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            var serverConnection = new ServerConnection(connection);
            try
            {
                var server = new Server(serverConnection);
                server.SetDefaultInitFields(true);
                return new ScriptingLane(connection, serverConnection, new Scripter(server) { Options = SchemaOptions() });
            }
            catch
            {
                if (serverConnection.IsOpen)
                {
                    serverConnection.Disconnect();
                }

                connection.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_serverConnection.IsOpen)
            {
                _serverConnection.Disconnect();
            }

            _connection.Dispose();
        }
    }

    /// <summary>
    /// Publishes scripting progress at a readable pace. A snapshot walks hundreds to thousands of objects at
    /// roughly one a second per lane, so reporting every object would flood the trace on a big database while
    /// reporting every hundredth would go silent for minutes on a slow one. Running tallies are therefore
    /// time-based: the first goes out at once, then at most one per <see cref="MinimumIntervalMs"/>. The lines
    /// that mark a boundary are never throttled, because those are exactly the ones a dropped line would leave a
    /// silent gap in front of: a category is announced before it is enumerated (which on a large collection
    /// blocks for many seconds) and again once its size is known, and each category's closing tally is forced so
    /// it always ends complete. Warnings are never throttled either: one object that failed to script is the
    /// whole point of watching.
    /// </summary>
    private sealed class ProgressReporter(Action<ScriptProgress>? progress)
    {
        private const int MinimumIntervalMs = 1_000;

        private readonly Lock _gate = new();
        private long _lastTimestamp;
        private string? _lastCategory;
        private int _lastScripted;
        private int _lastTotal;

        /// <summary>A category is about to be enumerated, which on a large collection blocks for seconds.</summary>
        public void Enumerating(string category) => Publish(category, 0, 0, force: true);

        /// <summary>A category has been enumerated and its objects are about to be scripted.</summary>
        public void CategoryStarted(string category, int total) => Publish(category, 0, total, force: true);

        public void Scripted(string category, int scripted, int total, bool force)
            => Publish(category, scripted, total, force);

        public void Warn(string category, int scripted, int total, string warning)
        {
            if (progress is null)
            {
                return;
            }

            progress(new ScriptProgress { Category = category, Scripted = scripted, Total = total, Warning = warning });
        }

        private void Publish(string category, int scripted, int total, bool force)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                // A forced closing tally usually repeats the last throttled one (the final object both advances
                // the count and ends the category), and the same line twice reads as a stutter in the trace.
                if (_lastCategory == category && _lastScripted == scripted && _lastTotal == total)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                if (!force && _lastTimestamp != 0
                    && Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds < MinimumIntervalMs)
                {
                    return;
                }

                _lastTimestamp = now;
                _lastCategory = category;
                _lastScripted = scripted;
                _lastTotal = total;
            }

            progress(new ScriptProgress { Category = category, Scripted = scripted, Total = total });
        }
    }

    private static void ScriptData(
        Database db, HashSet<string> dataTables, HashSet<string> excludedSchemas, Server server, string databaseName,
        List<ScriptedObject> objects, List<string> warnings, ProgressReporter reporter)
    {
        var dataScripter = new Scripter(server) { Options = DataOptions() };
        var scriptedTables = 0;
        foreach (Table table in db.Tables.Cast<Table>().Where(t => !t.IsSystemObject))
        {
            var key = $"{table.Schema}.{table.Name}";
            if (!dataTables.Contains(key))
            {
                continue;
            }

            // An excluded schema is excluded outright: naming one of its tables under scripting.data would
            // otherwise version the rows of a table whose definition the same run just refused to script.
            if (excludedSchemas.Contains(table.Schema))
            {
                warnings.Add($"scripting.data names '{key}', which is in the excluded schema '{table.Schema}'; skipped.");
                continue;
            }

            try
            {
                // SELECT * has no inherent order, so sort the emitted INSERTs for a deterministic snapshot;
                // for pure INSERTs row order is immaterial to the result, only to the diff.
                var inserts = dataScripter.EnumScript(new[] { table.Urn })
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();

                var sql = WrapData(table, inserts);
                objects.Add(BuildObject(SourceControlObjectTypes.DataFolder, table.Schema, table.Name, databaseName, sql));
                scriptedTables++;
                reporter.Scripted(SourceControlObjectTypes.DataFolder, scriptedTables, dataTables.Count, force: true);
            }
            catch (Exception ex) when (ex is SmoException or SqlException)
            {
                var warning = $"Data {Label(table.Schema, table.Name)}: not scripted ({ex.Message}).";
                warnings.Add(warning);
                reporter.Warn(SourceControlObjectTypes.DataFolder, scriptedTables, dataTables.Count, warning);
            }
        }

        foreach (var missing in dataTables.Where(t => !db.Tables.Cast<Table>().Any(tbl => string.Equals($"{tbl.Schema}.{tbl.Name}", t, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"scripting.data names '{missing}', which is not a user table in {databaseName}; skipped.");
        }
    }

    /// <summary>Wraps the row inserts in SET IDENTITY_INSERT when the table has an identity column, so the
    /// scripted data restores faithfully.</summary>
    private static string WrapData(Table table, IReadOnlyList<string> inserts)
    {
        var body = string.Join("\n", inserts);
        var hasIdentity = table.Columns.Cast<Column>().Any(c => c.Identity);
        if (!hasIdentity)
        {
            return Normalize(body);
        }

        var qualified = $"[{table.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
        return Normalize(
            $"SET IDENTITY_INSERT {qualified} ON;\n{body}\nSET IDENTITY_INSERT {qualified} OFF;");
    }

    private static ScriptedObject BuildObject(string folder, string? schema, string name, string databaseName, string sql)
    {
        var baseName = schema is null ? name : $"{schema}.{name}";
        var fileName = RunHistoryWriter.SafeName(baseName) + ".sql";
        var relativePath = $"{databaseName}/{folder}/{fileName}";
        return new ScriptedObject
        {
            Folder = folder,
            Schema = schema,
            Name = name,
            RelativePath = relativePath,
            Sql = sql,
        };
    }

    /// <summary>The legacy SmoHelper.SmoScriptingOptions set: full DRI, indexes, triggers, and extended
    /// properties, no drops, no data, no permissions/owner/statistics noise. ScriptSchema on; headers off for
    /// clean diffs. A fresh instance per lane, because SMO's scripter is free to read and write its options and
    /// nothing about them is documented as safe to share across threads.</summary>
    private static ScriptingOptions SchemaOptions() => new()
    {
        AllowSystemObjects = false,
        AnsiPadding = false,
        AppendToFile = false,
        IncludeIfNotExists = false,
        ContinueScriptingOnError = false,
        ConvertUserDefinedDataTypesToBaseType = false,
        WithDependencies = false,
        IncludeHeaders = false,
        DriIncludeSystemNames = false,
        Bindings = false,
        NoCollation = false,
        Default = true,
        ScriptDrops = false,
        ExtendedProperties = true,
        LoginSid = false,
        Permissions = false,
        ScriptOwner = false,
        Statistics = false,
        ScriptSchema = true,
        ScriptData = false,
        ChangeTracking = false,
        ScriptDataCompression = false,
        DriAll = true,
        FullTextIndexes = true,
        Indexes = true,
        Triggers = true,
        SchemaQualify = true,
        NoCommandTerminator = true,
    };

    /// <summary>Data-only: rows as INSERTs, no schema. Used for the opt-in reference/seed tables.</summary>
    private static ScriptingOptions DataOptions() => new()
    {
        ScriptSchema = false,
        ScriptData = true,
        NoCommandTerminator = true,
        AllowSystemObjects = false,
    };

    /// <summary>The selected categories in canonical order, after applying include (allowlist) then exclude.</summary>
    private static IReadOnlyList<(string Folder, Func<Database, IEnumerable<NamedSmoObject>> Select)> SelectedCategories(SourceControlScripting scripting)
    {
        var include = new HashSet<string>(scripting.IncludeTypes, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(scripting.ExcludeTypes, StringComparer.OrdinalIgnoreCase);

        bool Wanted(string folder)
            => (include.Count == 0 || include.Contains(folder)) && !exclude.Contains(folder);

        return Categories.Where(c => Wanted(c.Folder)).ToList();
    }

    /// <summary>True when the object belongs to a schema the flow excludes. A schema object is judged by its own
    /// name, so excluding a schema also keeps its CREATE SCHEMA script out of the snapshot; every other category
    /// is judged by the schema it is qualified with. An object that belongs to no schema (a database DDL trigger)
    /// is never excluded this way.</summary>
    private static bool IsInExcludedSchema(NamedSmoObject obj, HashSet<string> excludedSchemas)
    {
        if (excludedSchemas.Count == 0)
        {
            return false;
        }

        return obj switch
        {
            Schema schema => excludedSchemas.Contains(schema.Name),
            ScriptSchemaObjectBase qualified => excludedSchemas.Contains(qualified.Schema),
            _ => false,
        };
    }

    private static readonly (string Folder, Func<Database, IEnumerable<NamedSmoObject>> Select)[] Categories =
    [
        ("Schema", db => db.Schemas.Cast<Schema>().Where(s => !IsSystemSchema(s.Name)).Cast<NamedSmoObject>()),
        ("UserDefinedDataType", db => db.UserDefinedDataTypes.Cast<NamedSmoObject>()),
        ("UserDefinedType", db => db.UserDefinedTypes.Cast<NamedSmoObject>()),
        ("XmlSchemaCollection", db => db.XmlSchemaCollections.Cast<NamedSmoObject>()),
        ("Sequence", db => db.Sequences.Cast<NamedSmoObject>()),
        ("PartitionFunction", db => db.PartitionFunctions.Cast<NamedSmoObject>()),
        ("PartitionScheme", db => db.PartitionSchemes.Cast<NamedSmoObject>()),
        ("Table", db => db.Tables.Cast<Table>().Where(t => !t.IsSystemObject).Cast<NamedSmoObject>()),
        ("View", db => db.Views.Cast<View>().Where(v => !v.IsSystemObject).Cast<NamedSmoObject>()),
        ("StoredProcedure", db => db.StoredProcedures.Cast<StoredProcedure>().Where(p => !p.IsSystemObject).Cast<NamedSmoObject>()),
        ("UserDefinedFunction", db => db.UserDefinedFunctions.Cast<UserDefinedFunction>().Where(f => !f.IsSystemObject).Cast<NamedSmoObject>()),
        ("UserDefinedAggregate", db => db.UserDefinedAggregates.Cast<NamedSmoObject>()),
        ("UserDefinedTableType", db => db.UserDefinedTableTypes.Cast<NamedSmoObject>()),
        ("Synonym", db => db.Synonyms.Cast<NamedSmoObject>()),
        ("Rule", db => db.Rules.Cast<NamedSmoObject>()),
        ("Default", db => db.Defaults.Cast<NamedSmoObject>()),
        ("DatabaseDdlTrigger", db => db.Triggers.Cast<NamedSmoObject>()),
        ("FullTextCatalog", db => db.FullTextCatalogs.Cast<NamedSmoObject>()),
        ("SecurityPolicy", db => db.SecurityPolicies.Cast<NamedSmoObject>()),
    ];

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo", "sys", "INFORMATION_SCHEMA", "guest",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
    };

    private static bool IsSystemSchema(string name) => SystemSchemas.Contains(name);

    private static string JoinBatches(StringCollection statements)
    {
        if (statements.Count == 0)
        {
            return string.Empty;
        }

        var batches = statements.Cast<string>()
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => $"{s}\nGO");
        return Normalize(string.Join("\n", batches));
    }

    /// <summary>Normalizes line endings to LF and guarantees exactly one trailing newline, so re-scripting an
    /// unchanged object yields byte-identical content regardless of the server's line-ending conventions.</summary>
    private static string Normalize(string text)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return lf.TrimEnd('\n') + "\n";
    }

    private static string Label(string? schema, string name)
        => schema is null ? name : $"{schema}.{name}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
