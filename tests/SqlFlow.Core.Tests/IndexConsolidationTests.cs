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
/// Proves the index consolidation: a newly created table gets its declared (desired) indexes built and
/// is never disabled/rebuilt, while a pre-existing table is disabled/rebuilt and never has desired
/// indexes (re)applied. The two paths never both run, so no index work is duplicated.
/// </summary>
public sealed class IndexConsolidationTests
{
    [Fact]
    public async Task NewTable_AppliesDesiredIndexes_AndDoesNotDisable()
    {
        var indexManager = new RecordingIndexManager();
        var desired = new RecordingDesiredIndexManager();
        var runner = BuildRunner(tableExists: false, indexManager, desired);

        var result = await runner.RunAsync(Flow(manageIndexes: true, desiredIndexes: "CREATE INDEX IX_A ON dbo.T (A);"));

        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.Equal("CREATE INDEX IX_A ON dbo.T (A);", desired.AppliedScript);
        Assert.False(indexManager.DisableCalled);
        Assert.False(indexManager.RebuildCalled);
    }

    [Fact]
    public async Task ExistingTable_DisablesAndRebuilds_AndDoesNotApplyDesired()
    {
        var indexManager = new RecordingIndexManager { DisableReturns = ["IX_A"] };
        var desired = new RecordingDesiredIndexManager();
        var runner = BuildRunner(tableExists: true, indexManager, desired);

        var result = await runner.RunAsync(Flow(manageIndexes: true, desiredIndexes: "CREATE INDEX IX_A ON dbo.T (A);"));

        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.True(indexManager.DisableCalled);
        Assert.True(indexManager.RebuildCalled);
        Assert.Equal(new[] { "IX_A" }, indexManager.RebuiltNames);
        Assert.Null(desired.AppliedScript);
    }

    [Fact]
    public async Task ExistingTable_WithoutManageIndexes_DoesNothingToIndexes()
    {
        var indexManager = new RecordingIndexManager();
        var desired = new RecordingDesiredIndexManager();
        var runner = BuildRunner(tableExists: true, indexManager, desired);

        await runner.RunAsync(Flow(manageIndexes: false, desiredIndexes: "CREATE INDEX IX_A ON dbo.T (A);"));

        Assert.False(indexManager.DisableCalled);
        Assert.Null(desired.AppliedScript);
    }

    [Fact]
    public async Task NewTable_WithoutDesiredIndexes_AppliesNothing()
    {
        var indexManager = new RecordingIndexManager();
        var desired = new RecordingDesiredIndexManager();
        var runner = BuildRunner(tableExists: false, indexManager, desired);

        await runner.RunAsync(Flow(manageIndexes: true, desiredIndexes: null));

        Assert.False(indexManager.DisableCalled);
        Assert.Null(desired.AppliedScript);
    }

    private static FlowDefinition Flow(bool manageIndexes, string? desiredIndexes) => new()
    {
        Name = "t",
        Source = new SourceSpec { Type = "csv", Location = "memory" },
        Target = new TargetSpec { Connection = "memory", Schema = "dbo", Table = "T" },
        Load = new LoadPolicy { ManageIndexes = manageIndexes },
        DesiredIndexes = desiredIndexes,
    };

    private static FlowRunner BuildRunner(bool tableExists, IIndexManager indexManager, IDesiredIndexManager desired) => new(
        [new FakeSource()],
        new FakeTypeMapper(),
        new FakeSchema(tableExists),
        new FakeReconciler(),
        new FakeDdl(),
        new FakeLoader(),
        indexManager,
        desired,
        new FakeIncrementalProbe(),
        new NullStateStore(),
        NullFlowEventSink.Instance,
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

    private sealed class RecordingIndexManager : IIndexManager
    {
        public bool DisableCalled { get; private set; }
        public bool RebuildCalled { get; private set; }
        public IReadOnlyList<string> DisableReturns { get; init; } = [];
        public IReadOnlyList<string>? RebuiltNames { get; private set; }

        public Task<IReadOnlyList<string>> DisableNonClusteredAsync(string connectionString, string schema, string table, CancellationToken ct = default)
        {
            DisableCalled = true;
            return Task.FromResult(DisableReturns);
        }

        public Task RebuildAsync(string connectionString, string schema, string table, IReadOnlyList<string> indexNames, CancellationToken ct = default)
        {
            RebuildCalled = true;
            RebuiltNames = indexNames;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIncrementalProbe : IIncrementalProbe
    {
        public Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task<SqlFlow.Core.Model.WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default)
            => Task.FromResult<SqlFlow.Core.Model.WatermarkValue?>(null);
    }

    private sealed class RecordingDesiredIndexManager : IDesiredIndexManager
    {
        public string? AppliedScript { get; private set; }

        public Task<IReadOnlyList<IndexAction>> ApplyAsync(string connectionString, string desiredIndexScript, CancellationToken ct = default)
        {
            AppliedScript = desiredIndexScript;
            return Task.FromResult<IReadOnlyList<IndexAction>>(
                [new IndexAction { IndexName = "IX_A", Table = "T", Kind = IndexActionKind.Created }]);
        }
    }

    private sealed class FakeSource : ISourceReader
    {
        public bool CanHandle(string sourceType) => true;

        public Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceColumn>>([new SourceColumn { Name = "Value", Type = typeof(string) }]);

        public Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
        {
            var table = new DataTable();
            table.Columns.Add("Value", typeof(string));
            table.Rows.Add("1");
            return Task.FromResult(new SourceReadResult
            {
                Reader = table.CreateDataReader(),
                ProcessedFiles = [new ProcessedFile { Name = "x.csv", Rows = 1 }],
            });
        }
    }

    private sealed class FakeSchema(bool tableExists) : ISchemaProvider
    {
        public Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default)
            => Task.FromResult(tableExists
                ? new TableSchema { Schema = schema, Table = table, Columns = [new ColumnDefinition { Name = "Value", SqlType = "varchar(255)" }] }
                : null);

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
