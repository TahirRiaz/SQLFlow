using System.Data.Common;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;

namespace SqlFlow.DuckDb;

/// <summary>
/// A DuckDB-backed source reader (<c>source.type: duckdb</c>): the power-tier alternative to the pure-managed
/// Parquet reader. It runs a DuckDB query over Parquet/CSV/JSON files (a single path, a glob, or a partitioned
/// dataset; and, with the right extensions loaded, ADLS/S3 and Delta tables), with column projection and
/// predicate pushdown, then streams the typed result rows straight into the engine's bulk loader. The native
/// libduckdb dependency is isolated to this assembly, so the managed core stays dependency-free.
/// </summary>
public sealed class DuckDbSourceReader : ISourceReader
{
    private readonly ICloudCredentialProvider? _cloudCredentials;

    // The prepared connection (extensions installed/loaded, secrets created, relation DESCRIBEd, query built)
    // is parked here by GetColumnsAsync and consumed by OpenAsync, so one run prepares once instead of paying
    // the DESCRIBE round trip against the remote relation twice. Keyed by the SourceSpec instance the engine
    // threads through a run's stages: concurrent runs load distinct instances and never share a connection.
    // A parked connection a run never opens (a schema-only plan) is closed by CompleteAsync; entries whose
    // spec dies unconsumed are collected with it and the connection's safe handles finalize the native side.
    private readonly ConditionalWeakTable<SourceSpec, PreparedSlot> _prepared = new();

    private sealed class PreparedSlot
    {
        public Prepared? Value;
    }

    /// <summary>Constructs a reader with no cloud-credential resolution (local/explicit-init only).</summary>
    public DuckDbSourceReader()
        : this(null)
    {
    }

    /// <summary>
    /// Constructs a reader that auto-authenticates Azure object storage from the ambient SQLFLOW_AZURE_AUTH intent
    /// via <paramref name="cloudCredentials"/> (wired by the host). When null, cloud auth is left to an explicit
    /// <c>init</c> secret, exactly as before.
    /// </summary>
    public DuckDbSourceReader(ICloudCredentialProvider? cloudCredentials)
    {
        _cloudCredentials = cloudCredentials;
    }

    public bool CanHandle(string sourceType)
        => sourceType is not null
           && (sourceType.Equals("duckdb", StringComparison.OrdinalIgnoreCase)
               || sourceType.Equals("delta", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var prepared = await PrepareAsync(source, ct).ConfigureAwait(false);

        // Park the prepared connection for the run's OpenAsync instead of disposing it, so the run prepares
        // once. A prior parked connection for the same spec (a re-planned run) is displaced and closed.
        var slot = _prepared.GetOrCreateValue(source);
        var displaced = Interlocked.Exchange(ref slot.Value, prepared);
        if (displaced is not null)
        {
            await displaced.Connection.DisposeAsync().ConfigureAwait(false);
        }

        return prepared.Columns;
    }

    public async Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var prepared = TakePrepared(source) ?? await PrepareAsync(source, ct).ConfigureAwait(false);
        DbCommand? command = null;
        try
        {
            command = prepared.Connection.CreateCommand();
            command.CommandText = prepared.Query;
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // The reader owns the command and connection from here: disposing it (after the engine streams every
            // row into SqlBulkCopy) closes them, so nothing is held open longer than the read.
            var owning = new DisposingDbDataReader(reader, command, prepared.Connection);
            return new SourceReadResult
            {
                Reader = owning,
                ProcessedFiles = [new ProcessedFile { Name = source.Location ?? "duckdb-query", Path = source.Location, Columns = prepared.Columns.Count }],
            };
        }
        catch
        {
            if (command is not null)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }

            await prepared.Connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Closes a prepared connection the run parked but never opened (a plan that stopped at the schema stage,
    /// or a failed run that got no further), so the engine's end-of-run hook releases it deterministically. A
    /// normal run's connection was consumed by <see cref="OpenAsync"/> and is owned by its reader; this is a
    /// no-op then.
    /// </summary>
    public async Task CompleteAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var leftover = TakePrepared(source);
        if (leftover is not null)
        {
            await leftover.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Atomically claims the connection parked for this spec, or null when none is parked (no schema
    /// pass ran, or it was already consumed), so a parked connection is opened exactly once.</summary>
    private Prepared? TakePrepared(SourceSpec source)
        => _prepared.TryGetValue(source, out var slot) ? Interlocked.Exchange(ref slot.Value, null) : null;

    private async Task<Prepared> PrepareAsync(SourceSpec source, CancellationToken ct)
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, source, ct).ConfigureAwait(false);

            var relation = DuckDbQuery.BuildRelation(source);
            var described = await DescribeAsync(connection, relation, ct).ConfigureAwait(false);
            if (described.Count == 0)
            {
                throw new SqlFlowException($"The duckdb source produced no columns (relation: {relation}).");
            }

            var selected = SelectColumns(described, DuckDbQuery.Columns(source));
            var filter = DuckDbQuery.CombineFilters(
                DuckDbQuery.CombineFilters(DuckDbQuery.Filter(source), DuckDbQuery.IncrementalPredicate(source)),
                DuckDbQuery.FileDatePredicate(source));
            var query = BuildSelect(relation, selected, filter);
            var columns = selected.Select(c => new SourceColumn
            {
                Name = c.Name,
                Type = c.Mapping.ClrType,
                SqlType = c.Mapping.SqlType,
                Precision = c.Mapping.Precision,
                Scale = c.Mapping.Scale,
                IsNullable = true,
            }).ToList();

            return new Prepared(connection, query, columns);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConfigureAsync(DuckDBConnection connection, SourceSpec source, CancellationToken ct)
    {
        // Every extension the format and location require (delta for a Delta table; azure/httpfs for cloud
        // locations) plus any the author listed, so a Delta table on ADLS reads with no manual wiring.
        foreach (var extension in DuckDbQuery.RequiredExtensions(source))
        {
            await ExecuteAsync(connection, $"INSTALL {extension};", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, $"LOAD {extension};", ct).ConfigureAwait(false);
        }

        // Auto-authenticate Azure object storage from the ambient SQLFLOW_AZURE_AUTH intent (after the azure
        // extension is loaded, before user init so an explicit init can still override). No-op when the operator
        // manages auth themselves, the location is not Azure storage, or no cloud-credential provider is wired.
        foreach (var secret in DuckDbAzureSecret.StatementsFor(source, _cloudCredentials))
        {
            await ExecuteAsync(connection, secret, ct).ConfigureAwait(false);
        }

        foreach (var statement in DuckDbQuery.InitStatements(source))
        {
            await ExecuteAsync(connection, statement, ct).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<DescribedColumn>> DescribeAsync(DuckDBConnection connection, string relation, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM {relation};";

        var columns = new List<DescribedColumn>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // DESCRIBE returns column_name, column_type, null, key, default, extra.
            columns.Add(new DescribedColumn(reader.GetString(0), DuckDbTypeMap.Map(reader.GetString(1))));
        }

        return columns;
    }

    /// <summary>Applies the optional projection: an empty list keeps every described column in file order; a
    /// non-empty list keeps exactly those columns in the requested order, erroring on an unknown name.</summary>
    private static IReadOnlyList<DescribedColumn> SelectColumns(IReadOnlyList<DescribedColumn> described, IReadOnlyList<string> projection)
    {
        if (projection.Count == 0)
        {
            return described;
        }

        var byName = described.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<DescribedColumn>(projection.Count);
        foreach (var name in projection)
        {
            if (!byName.TryGetValue(name, out var column))
            {
                throw new SqlFlowException(
                    $"duckdb source 'columns' names '{name}', which the relation does not have. Available: {string.Join(", ", described.Select(c => c.Name))}.");
            }

            result.Add(column);
        }

        return result;
    }

    /// <summary>Builds the streamed SELECT: scalar columns pass through; a nested column (list/struct/map) is
    /// projected as JSON text so it lands in nvarchar(max), the same subtree-as-string contract the Parquet
    /// reader uses.</summary>
    private static string BuildSelect(string relation, IReadOnlyList<DescribedColumn> columns, string? filter)
    {
        var list = string.Join(", ", columns.Select(c => c.Mapping.IsNested
            ? $"to_json({Quote(c.Name)}) AS {Quote(c.Name)}"
            : Quote(c.Name)));
        var where = string.IsNullOrWhiteSpace(filter) ? string.Empty : $" WHERE {filter}";
        return $"SELECT {list} FROM {relation}{where}";
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record DescribedColumn(string Name, DuckDbColumnType Mapping);

    private sealed record Prepared(DuckDBConnection Connection, string Query, IReadOnlyList<SourceColumn> Columns);
}
