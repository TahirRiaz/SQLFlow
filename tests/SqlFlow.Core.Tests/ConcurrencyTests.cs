using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.State;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task HundredConcurrentRuns_AreIsolated()
    {
        const int count = 100;
        var sink = new CollectingEventSink();
        var runner = BuildRunner(sink);

        // Each flow loads a distinct number of rows (1..100). If any per-run state leaked between
        // concurrent runs, the row counts, traces, or events would cross-contaminate.
        var flows = Enumerable.Range(1, count).Select(i => new FlowDefinition
        {
            Name = $"flow_{i}",
            Source = new SourceSpec
            {
                Type = "csv",
                Location = "memory",
                Options = new Dictionary<string, string?> { ["rows"] = i.ToString(CultureInfo.InvariantCulture) },
            },
            Target = new TargetSpec { Connection = "memory", Schema = "dbo", Table = $"T{i}" },
        }).ToList();

        var results = await Task.WhenAll(flows.Select(f => runner.RunAsync(f)));

        // Every run succeeded with its own correct row count.
        Assert.All(results, r => Assert.Equal(FlowStatus.Success, r.Status));
        foreach (var flow in flows)
        {
            var expected = int.Parse(flow.Source.Options["rows"]!, CultureInfo.InvariantCulture);
            var result = Assert.Single(results, r => r.FlowName == flow.Name);
            Assert.Equal(expected, result.RowsLoaded);
            Assert.Contains(result.Trace, t => t.Operation == "target.load" && t.Rows == expected);
        }

        // Every run has a unique RunId.
        Assert.Equal(count, results.Select(r => r.RunId).Distinct().Count());

        // FlowId is the stable per-definition identity: distinct per flow name, and computed
        // deterministically from that name (not the per-run RunId).
        Assert.Equal(count, results.Select(r => r.FlowId).Distinct().Count());
        Assert.All(results, r => Assert.Equal(FlowIdentity.FromName(r.FlowName), r.FlowId));

        // Every event is attributable to exactly one run, and the load event carries that run's count.
        foreach (var result in results)
        {
            var events = sink.For(result.RunId);
            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.Equal(result.RunId, e.RunId));
            Assert.All(events, e => Assert.Equal(result.FlowName, e.FlowName));
            Assert.Contains(events, e => e.Stage == "target.load" && e.Rows == result.RowsLoaded);
        }
    }

    private static FlowRunner BuildRunner(IFlowEventSink sink) => new(
        [new FakeSource()],
        new FakeTypeMapper(),
        new FakeSchema(),
        new FakeReconciler(),
        new FakeDdl(),
        new FakeLoader(),
        new FakeIndexManager(),
        new FakeDesiredIndexManager(),
        new FakeIncrementalProbe(),
        new NullStateStore(),
        sink,
        new SecretResolver([new EnvSecretProvider()]),
        new UnusedInferenceService(),
        NullLogger<FlowRunner>.Instance);

    /// <summary>These flows declare no transform, so the runner must never touch inference; throwing on any call
    /// turns an unexpected invocation into a loud test failure instead of a silently-passing fake.</summary>
    private sealed class UnusedInferenceService : IInferenceService
    {
        public Task<InferenceReport> InferAsync(InferenceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Inference is not expected in this test (the flow declares no transform).");

        public Task<InferenceReport> ValidateAsync(InferenceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Inference is not expected in this test (the flow declares no transform).");
    }

    private sealed class CollectingEventSink : IFlowEventSink
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<FlowEvent>> _byRun = new();

        public void Publish(FlowEvent flowEvent)
            => _byRun.GetOrAdd(flowEvent.RunId, _ => new ConcurrentQueue<FlowEvent>()).Enqueue(flowEvent);

        public IReadOnlyCollection<FlowEvent> For(Guid runId)
            => _byRun.TryGetValue(runId, out var queue) ? queue.ToArray() : [];
    }

    private sealed class FakeSource : ISourceReader
    {
        public bool CanHandle(string sourceType) => true;

        public Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceColumn>>([new SourceColumn { Name = "Value", Type = typeof(string) }]);

        public async Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
        {
            await Task.Delay(Random.Shared.Next(1, 5), ct); // force interleaving across runs
            var rows = int.Parse(source.Options["rows"]!, CultureInfo.InvariantCulture);
            var table = new DataTable();
            table.Columns.Add("Value", typeof(string));
            for (var i = 0; i < rows; i++)
            {
                table.Rows.Add(i.ToString(CultureInfo.InvariantCulture));
            }

            return new SourceReadResult
            {
                Reader = table.CreateDataReader(),
                ProcessedFiles = [new ProcessedFile { Name = $"{source.Options["rows"]}.csv", Rows = rows }],
            };
        }
    }

    private sealed class FakeSchema : ISchemaProvider
    {
        public Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default)
            => Task.FromResult<TableSchema?>(new TableSchema
            {
                Schema = schema,
                Table = table,
                Columns = [new ColumnDefinition { Name = "Value", SqlType = "varchar(255)" }],
            });

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
            await Task.Delay(Random.Shared.Next(1, 5), ct); // force interleaving across runs
            long rows = 0;
            while (data.Read())
            {
                rows++;
            }

            return rows;
        }

        public Task TruncateAsync(string connectionString, TargetSpec target, CancellationToken ct = default)
            => Task.CompletedTask;
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

    private sealed class FakeIncrementalProbe : IIncrementalProbe
    {
        public Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task<SqlFlow.Core.Model.WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default)
            => Task.FromResult<SqlFlow.Core.Model.WatermarkValue?>(null);
    }
}
