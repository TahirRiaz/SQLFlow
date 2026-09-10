using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.SourceControl;
using SqlFlow.SourceControl;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// The orchestration contract, with the SMO scripter and the git workspace faked so the wiring is provable
/// without a database or a repository: a successful run scripts, writes the working tree, and commits; a dry
/// run writes but never commits; a scripting failure is returned as a failed result (so the run still records
/// an artifact) rather than thrown.
/// </summary>
public sealed class SourceControlServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scm_svc_" + Guid.NewGuid().ToString("N"));

    public SourceControlServiceTests() => Directory.CreateDirectory(_dir);

    private SourceControlFlow Flow() => new()
    {
        FlowId = 1,
        SysAlias = "scm-test",
        Server = "DW",
        Repository = new SourceControlRepository { WorkingDirectory = _dir, AuthorName = "t", AuthorEmail = "t@t" },
    };

    private static IConnectionResolver Resolver()
    {
        var dataSource = new DataSource
        {
            Alias = "DW",
            Kind = DataSourceKind.MSSQL,
            ConnectionRef = "Server=(local);Database=Warehouse;Integrated Security=true;TrustServerCertificate=true",
            Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
        };
        return WithoutDatabaseResolver.Build([dataSource], new SecretResolver([new EnvSecretProvider()]), SqlServerSourceProvider.CreateRegistry());
    }

    private static ScriptedDatabase Snapshot() => new()
    {
        DatabaseName = "Warehouse",
        Objects =
        [
            new ScriptedObject { Folder = "Table", Schema = "dbo", Name = "Customer", RelativePath = "Warehouse/Table/dbo.Customer.sql", Sql = "CREATE TABLE x;\n" },
            new ScriptedObject { Folder = "View", Schema = "dbo", Name = "vCustomer", RelativePath = "Warehouse/View/dbo.vCustomer.sql", Sql = "CREATE VIEW y AS SELECT 1;\n" },
        ],
    };

    [Fact]
    public async Task Run_Scripts_Writes_AndCommits()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = true, CommitSha = "abc123", Pushed = false, FilesChanged = 2 });
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        var result = await service.RunAsync(Flow());

        Assert.True(result.Success);
        Assert.Equal("Warehouse", result.DatabaseName);
        Assert.Equal(2, result.ObjectsScripted);
        Assert.Equal(2, result.Added);
        Assert.True(result.Committed);
        Assert.Equal("abc123", result.CommitSha);
        Assert.True(git.Ready);
        Assert.NotNull(git.LastMessage);
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
        Assert.Equal(2, result.Objects.Count);
    }

    [Fact]
    public async Task DryRun_WritesButNeverCommits()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = true, CommitSha = "should-not-be-used" });
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        var result = await service.RunAsync(Flow(), new SourceControlRunOptions { DryRun = true });

        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.False(result.Committed);
        Assert.Null(git.LastMessage); // Commit was never called
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql"))); // but the tree was written
    }

    [Fact]
    public async Task ScriptingFailure_IsReturnedAsAFailedResult()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult());
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new ThrowingScripter(), _ => git);

        var result = await service.RunAsync(Flow());

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(git.Ready); // failed before the workspace was touched
    }

    [Fact]
    public async Task Run_NarratesEveryStage_SoALongSnapshotIsNotSilent()
    {
        // The defect this pins: a snapshot walks thousands of objects and pushes a repository over minutes. With
        // no events the live trace has nothing to tail and the run looks hung, which is exactly when an operator
        // most wants to know what it is doing.
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = true, CommitSha = "abc1234567", Pushed = true, FilesChanged = 2 });
        var events = new RecordingEventSink();
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        var result = await service.RunAsync(Flow(), new SourceControlRunOptions { Events = events });

        Assert.True(result.Success);
        var stages = events.Events.Select(e => e.Stage).Distinct().ToList();
        Assert.Equal(["connect", "script", "git", "write", "commit", "done"], stages);

        // The scripter's progress reaches the stream, which is the legacy per-object reporting restored. All three
        // stages of a category render, so the multi-second enumeration of a large collection is announced rather
        // than showing as a gap before the first count.
        Assert.Contains(events.Events, e => e.Stage == "script" && e.Message == "Table: enumerating.");
        Assert.Contains(events.Events, e => e.Stage == "script" && e.Message == "Table: scripting 2 object(s).");
        Assert.Contains(events.Events, e => e.Stage == "script" && e.Message.Contains("2 of 2 scripted", StringComparison.Ordinal));

        // The last event summarizes the run on its own, so a reader who scrolls to the end learns the outcome
        // without reconstructing it from the stages above.
        var summary = events.Events[^1];
        Assert.Equal("done", summary.Stage);
        Assert.Contains("Warehouse", summary.Message, StringComparison.Ordinal);
        Assert.Contains("2 object(s) scripted", summary.Message, StringComparison.Ordinal);
        Assert.Contains("2 added", summary.Message, StringComparison.Ordinal);
        Assert.Contains("committed and pushed", summary.Message, StringComparison.Ordinal);
        Assert.Contains("abc12345", events.Events.Single(e => e.Stage == "commit" && e.Message.Contains("Committed", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_SaysNothingChanged_RatherThanGoingQuiet()
    {
        // A quiet day is the normal case once the estate settles, and "no commit" must read as the expected
        // outcome rather than as a run that failed to do anything.
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = false, Pushed = false, FilesChanged = 0 });
        var events = new RecordingEventSink();
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        // Write the snapshot once so the second run finds the working tree already identical.
        await service.RunAsync(Flow(), new SourceControlRunOptions { Events = new RecordingEventSink() });
        var result = await service.RunAsync(Flow(), new SourceControlRunOptions { Events = events });

        Assert.True(result.Success);
        Assert.False(result.Committed);
        Assert.Contains(events.Events, e => e.Stage == "commit" && e.Message.Contains("Nothing changed", StringComparison.Ordinal));
        Assert.Contains("no commit", events.Events[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PublishesTheFailure_SoTheTraceExplainsIt()
    {
        var events = new RecordingEventSink();
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new ThrowingScripter(), _ => new RecordingGitWorkspace(new GitCommitResult()));

        var result = await service.RunAsync(Flow(), new SourceControlRunOptions { Events = events });

        Assert.False(result.Success);
        var failure = Assert.Single(events.Events, e => e.Stage == "failed");
        Assert.Contains("login failed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingEventSink : IFlowEventSink
    {
        private readonly List<FlowEvent> _events = [];

        public IReadOnlyList<FlowEvent> Events => _events;

        public void Publish(FlowEvent flowEvent) => _events.Add(flowEvent);
    }

    private sealed class FakeScripter(ScriptedDatabase snapshot) : IDatabaseScripter
    {
        public ScriptedDatabase Script(
            string connectionString, string? database, SourceControlScripting scripting,
            Action<ScriptProgress>? progress = null, CancellationToken ct = default)
        {
            // Report the three shapes the real scripter emits, so a test asserting the run's narration sees the
            // whole progression: a category being enumerated (size unknown), one about to be scripted, and a
            // running tally. A warning rides the same callback.
            progress?.Invoke(new ScriptProgress { Category = "Table", Scripted = 0, Total = 0 });
            progress?.Invoke(new ScriptProgress { Category = "Table", Scripted = 0, Total = snapshot.Objects.Count });
            progress?.Invoke(new ScriptProgress
            {
                Category = "Table",
                Scripted = snapshot.Objects.Count,
                Total = snapshot.Objects.Count,
            });
            return snapshot;
        }
    }

    private sealed class ThrowingScripter : IDatabaseScripter
    {
        public ScriptedDatabase Script(
            string connectionString, string? database, SourceControlScripting scripting,
            Action<ScriptProgress>? progress = null, CancellationToken ct = default)
            => throw new InvalidOperationException("login failed for user");
    }

    private sealed class RecordingGitWorkspace(GitCommitResult result) : IGitWorkspace
    {
        public bool Ready { get; private set; }
        public string? LastMessage { get; private set; }

        public void EnsureReady(CancellationToken ct = default) => Ready = true;

        public GitCommitResult Commit(string message, CancellationToken ct = default)
        {
            LastMessage = message;
            return result;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
