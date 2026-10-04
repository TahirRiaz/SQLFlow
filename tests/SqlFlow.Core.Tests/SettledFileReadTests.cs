using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Events;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.State;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The engine makes the read of a file-date incremental flow settled-only on every run of it, a backfill or forced
/// full load included, because each of those writes the FileDate_DW the next run takes its watermark from; a
/// row-level watermark and a flow without an incremental block read as before. Files the read left for the next
/// run are named in the run's trace.
/// </summary>
public sealed class SettledFileReadTests
{
    [Fact]
    public async Task FileDateIncrementalRun_ReadsSettledFilesOnly()
    {
        var source = new RecordingSource();

        await BuildRunner(source).RunAsync(Flow(new IncrementalSpec()));

        Assert.Equal("true", source.SeenOption(SourceOptions.SettledFilesOnly));
    }

    [Fact]
    public async Task FullLoadOfAFileDateIncrementalFlow_ReadsSettledFilesOnly()
    {
        var source = new RecordingSource();

        await BuildRunner(source).RunAsync(Flow(new IncrementalSpec { FullLoad = true }));

        Assert.Equal("true", source.SeenOption(SourceOptions.SettledFilesOnly));
    }

    [Fact]
    public async Task RowWatermarkRun_ReadsEveryListedFile()
    {
        var source = new RecordingSource();

        await BuildRunner(source).RunAsync(Flow(new IncrementalSpec { WatermarkColumn = "Value" }));

        Assert.Null(source.SeenOption(SourceOptions.SettledFilesOnly));
    }

    [Fact]
    public async Task FlowWithoutIncremental_ReadsEveryListedFile()
    {
        var source = new RecordingSource();

        await BuildRunner(source).RunAsync(Flow(incremental: null));

        Assert.Null(source.SeenOption(SourceOptions.SettledFilesOnly));
    }

    [Fact]
    public async Task FilesLeftForTheNextRun_AreReportedInTheTrace()
    {
        var source = new RecordingSource { Deferred = 2 };
        var events = new RecordingEvents();

        var result = await BuildRunner(source, events).RunAsync(Flow(new IncrementalSpec()));

        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.Contains(events.Messages, m => m.StartsWith(
            "2 file(s) modified after this run began listing the source, or dated ahead of its clock, wait for the next run",
            StringComparison.Ordinal));
    }

    /// <summary>Each file read says when it was modified, to the millisecond: the time the file-date watermark is
    /// compared with, so a trace tells a changed file from a stale copy of it.</summary>
    [Fact]
    public async Task EachFileRead_SaysWhenTheFileWasModified()
    {
        var source = new RecordingSource { Modified = new DateTimeOffset(2026, 10, 4, 5, 51, 27, 139, TimeSpan.Zero) };
        var events = new RecordingEvents();

        await BuildRunner(source, events).RunAsync(Flow(new IncrementalSpec()));

        Assert.Contains("read 'x.csv' (1 row(s), modified 2026-10-04 05:51:27.139Z)", events.Messages);
    }

    private static FlowDefinition Flow(IncrementalSpec? incremental) => new()
    {
        Name = "t",
        Source = new SourceSpec { Type = "csv", Location = "memory" },
        Target = new TargetSpec { Connection = "memory", Schema = "dbo", Table = "T" },
        Incremental = incremental,
    };

    private static FlowRunner BuildRunner(RecordingSource source, IFlowEventSink? events = null) => new(
        [source],
        new FakeTypeMapper(),
        new FakeSchema(),
        new FakeReconciler(),
        new FakeDdl(),
        new FakeLoader(),
        new FakeIndexManager(),
        new FakeDesiredIndexManager(),
        new FakeIncrementalProbe(),
        new NullStateStore(),
        events ?? NullFlowEventSink.Instance,
        new SecretResolver([new EnvSecretProvider()]),
        new UnusedInferenceService(),
        NullLogger<FlowRunner>.Instance);

    private sealed class RecordingSource : ISourceReader
    {
        private readonly ConcurrentQueue<SourceSpec> _seen = new();

        public int Deferred { get; init; }

        public DateTimeOffset? Modified { get; init; }

        /// <summary>The value of <paramref name="key"/> on the spec the read was opened with, or null when absent.</summary>
        public string? SeenOption(string key)
        {
            var opened = _seen.LastOrDefault() ?? throw new InvalidOperationException("The source was never opened.");
            return opened.Options.TryGetValue(key, out var value) ? value : null;
        }

        public bool CanHandle(string sourceType) => true;

        public Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceColumn>>([new SourceColumn { Name = "Value", Type = typeof(string) }]);

        public Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
        {
            _seen.Enqueue(source);
            var table = new DataTable();
            table.Columns.Add("Value", typeof(string));
            table.Rows.Add("1");
            return Task.FromResult(new SourceReadResult
            {
                Reader = table.CreateDataReader(),
                ProcessedFiles = [new ProcessedFile { Name = "x.csv", Rows = 1, Modified = Modified }],
                DeferredFiles = Deferred,
            });
        }
    }

    private sealed class RecordingEvents : IFlowEventSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void Publish(FlowEvent flowEvent) => _messages.Enqueue(flowEvent.Message);
    }

    /// <summary>These flows declare no transform, so the runner must never touch inference.</summary>
    private sealed class UnusedInferenceService : IInferenceService
    {
        public Task<InferenceReport> InferAsync(InferenceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Inference is not expected in this test (the flow declares no transform).");

        public Task<InferenceReport> ValidateAsync(InferenceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Inference is not expected in this test (the flow declares no transform).");
    }

    private sealed class FakeIndexManager : IIndexManager
    {
        public Task<IReadOnlyList<string>> DisableNonClusteredAsync(string connectionString, string schema, string table, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task RebuildAsync(string connectionString, string schema, string table, IReadOnlyList<string> indexNames, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeDesiredIndexManager : IDesiredIndexManager
    {
        public Task<IReadOnlyList<IndexAction>> ApplyAsync(string connectionString, string desiredIndexScript, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IndexAction>>([]);
    }

    /// <summary>No prior watermark in either mode, so every run reads the whole selection.</summary>
    private sealed class FakeIncrementalProbe : IIncrementalProbe
    {
        public Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task<WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default)
            => Task.FromResult<WatermarkValue?>(null);
    }

    private sealed class FakeSchema : ISchemaProvider
    {
        public Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default)
            => Task.FromResult<TableSchema?>(
                new TableSchema { Schema = schema, Table = table, Columns = [new ColumnDefinition { Name = "Value", SqlType = "varchar(255)" }] });

        public Task ExecuteDdlAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeTypeMapper : ISqlTypeMapper
    {
        public ColumnDefinition Map(SourceColumn column, ColumnOverride? columnOverride, string defaultColumnType)
            => new() { Name = column.Name, SqlType = defaultColumnType, IsNullable = true };
    }

    private sealed class FakeReconciler : IColumnTypeReconciler
    {
        public string? WidenTo(string existingType, string desiredType) => null;
    }

    private sealed class FakeDdl : IDdlGenerator
    {
        public IReadOnlyList<string> Generate(TargetSpec target, SchemaDelta delta) => [];
    }

    private sealed class FakeLoader : IBulkLoader
    {
        public async Task<long> LoadAsync(string connectionString, TargetSpec target, DbDataReader data, LoadPolicy load, CancellationToken ct = default)
        {
            long rows = 0;
            while (await data.ReadAsync(ct))
            {
                rows++;
            }

            return rows;
        }

        public Task TruncateAsync(string connectionString, TargetSpec target, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
