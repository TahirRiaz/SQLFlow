using System.Diagnostics;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.SourceControl;

namespace SqlFlow.SourceControl;

/// <summary>
/// The end-to-end source-control run, one code path for the CLI and the tests: resolve the managed database's
/// connection, script every object with SMO, write the snapshot into the git working tree (recording adds,
/// changes, and deletions), then commit and push it. Operational failures are returned as a failed
/// <see cref="SourceControlResult"/> (so the run still writes its artifact), the same contract the other flow
/// runners follow. Git credentials and the connection are resolved from references at run time and never logged.
/// </summary>
public sealed class SourceControlService
{
    private readonly IConnectionResolver _resolver;
    private readonly ISecretResolver _secrets;
    private readonly IDatabaseScripter _scripter;
    private readonly Func<GitWorkspaceConfig, IGitWorkspace> _gitFactory;

    public SourceControlService(
        IConnectionResolver resolver,
        ISecretResolver secrets,
        IDatabaseScripter scripter,
        Func<GitWorkspaceConfig, IGitWorkspace>? gitFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(scripter);
        _resolver = resolver;
        _secrets = secrets;
        _scripter = scripter;
        _gitFactory = gitFactory ?? (config => new LibGit2GitWorkspace(config));
    }

    public async Task<SourceControlResult> RunAsync(
        SourceControlFlow flow, SourceControlRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new SourceControlRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var repository = flow.Repository;
        var stopwatch = Stopwatch.StartNew();

        // The run's narration. A snapshot walks thousands of objects and pushes a repository, which takes
        // minutes; without this the live trace has nothing to show and the run looks hung. Every stage below
        // announces itself, and the last event is a one-line summary of what the run actually did.
        var events = new SourceControlEvents(options.Events, runId, flow.SysAlias);

        try
        {
            events.Info("connect", $"Resolving the connection for '{flow.Server}'.");
            var resolved = await _resolver
                .ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct)
                .ConfigureAwait(false);

            events.Info("script", $"Scripting {Describe(flow.Scripting)} from {flow.Database ?? "the connection's default database"}.");
            var scripted = _scripter.Script(
                resolved.CanonicalString, flow.Database, flow.Scripting, events.Progress, ct);
            events.Info(
                "script",
                $"Scripted {Count(scripted.Objects.Count)} object(s) from {scripted.DatabaseName}"
                + (scripted.Warnings.Count > 0 ? $"; {Count(scripted.Warnings.Count)} warning(s)." : "."),
                scripted.Objects.Count);
            foreach (var warning in scripted.Warnings)
            {
                events.Warn("script", warning);
            }

            var credentials = await BuildCredentialsAsync(repository, ct).ConfigureAwait(false);
            var config = new GitWorkspaceConfig
            {
                WorkingDirectory = repository.WorkingDirectory,
                Remote = repository.Remote,
                Branch = repository.Branch,
                AuthorName = repository.AuthorName,
                AuthorEmail = repository.AuthorEmail,
                Credentials = credentials,
                Push = options.Push,
                PathScope = scripted.DatabaseName,
            };

            var workspace = _gitFactory(config);
            events.Info(
                "git",
                repository.Remote is null
                    ? $"Preparing the local repository at {repository.WorkingDirectory} [{repository.Branch}]."
                    : $"Preparing {repository.Remote} [{repository.Branch}]: clone or fetch, then reset onto the remote.");
            workspace.EnsureReady(ct);

            events.Info("write", "Writing the snapshot into the working tree.");
            var write = SnapshotWriter.Write(repository.WorkingDirectory, scripted, ct);
            events.Info(
                "write",
                $"{Count(write.Added.Count)} added, {Count(write.Changed.Count)} changed, "
                + $"{Count(write.Deleted.Count)} deleted, {Count(write.Unchanged.Count)} unchanged.",
                write.TotalChanged);

            GitCommitResult commit;
            if (options.DryRun)
            {
                events.Info("commit", "Dry run: the working tree was written but nothing was committed.");
                commit = new GitCommitResult { Committed = false, Pushed = false, FilesChanged = 0 };
            }
            else
            {
                // Said explicitly either way, because "no commit" is the expected outcome of a quiet day and
                // must not read as a failure to anyone watching the trace.
                events.Info(
                    "commit",
                    write.TotalChanged == 0
                        ? "Nothing changed since the last snapshot, so there is nothing to commit."
                        : $"Committing {Count(write.TotalChanged)} change(s).");
                commit = workspace.Commit(BuildCommitMessage(flow.SysAlias, scripted, write), ct);
                if (commit.Committed)
                {
                    var sha = commit.CommitSha is { Length: > 0 } full ? full[..Math.Min(8, full.Length)] : "(unknown)";
                    events.Info(
                        "commit",
                        commit.Pushed
                            ? $"Committed {sha} and pushed to {repository.Remote}."
                            : $"Committed {sha} locally (not pushed).");
                }
            }

            stopwatch.Stop();
            events.Info(
                "done",
                $"Snapshot of {scripted.DatabaseName} finished in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s: "
                + $"{Count(scripted.Objects.Count)} object(s) scripted, {Count(write.Added.Count)} added, "
                + $"{Count(write.Changed.Count)} changed, {Count(write.Deleted.Count)} deleted"
                + (commit.Committed ? $", committed{(commit.Pushed ? " and pushed" : string.Empty)}." : ", no commit."),
                scripted.Objects.Count,
                stopwatch.Elapsed.TotalMilliseconds);
            return new SourceControlResult
            {
                RunId = runId,
                Success = true,
                DatabaseName = scripted.DatabaseName,
                WorkingDirectory = Path.GetFullPath(repository.WorkingDirectory),
                Remote = repository.Remote,
                Branch = repository.Branch,
                DryRun = options.DryRun,
                ObjectsScripted = scripted.Objects.Count,
                Added = write.Added.Count,
                Changed = write.Changed.Count,
                Deleted = write.Deleted.Count,
                Unchanged = write.Unchanged.Count,
                Committed = commit.Committed,
                CommitSha = commit.CommitSha,
                Pushed = commit.Pushed,
                Objects = scripted.Objects.Select(o => o.RelativePath).ToList(),
                AddedObjects = write.Added,
                ChangedObjects = write.Changed,
                DeletedObjects = write.Deleted,
                Warnings = scripted.Warnings,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            events.Error("failed", SecretHygiene.RedactedMessage(ex));
            return new SourceControlResult
            {
                RunId = runId,
                Success = false,
                Error = SecretHygiene.RedactedMessage(ex),
                WorkingDirectory = Path.GetFullPath(repository.WorkingDirectory),
                Remote = repository.Remote,
                Branch = repository.Branch,
                DryRun = options.DryRun,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            };
        }
    }

    private async Task<GitCredentials?> BuildCredentialsAsync(SourceControlRepository repository, CancellationToken ct)
    {
        // Credentials are needed to clone a private remote and to push; without a remote the history is local
        // and no credential is resolved at all.
        if (repository.Remote is null || repository.Secret is null)
        {
            return null;
        }

        var secret = await _secrets.ResolveAsync(repository.Secret, ct).ConfigureAwait(false);
        var username = repository.Username is null
            ? null
            : await _secrets.ResolveAsync(repository.Username, ct).ConfigureAwait(false);

        return new GitCredentials
        {
            Username = string.IsNullOrWhiteSpace(username) ? null : username,
            Secret = secret,
        };
    }

    /// <summary>A human count with thousands separators, so "1,204 object(s)" reads at a glance in the trace.</summary>
    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What the scripting stage is about to capture, said in the flow's own vocabulary.</summary>
    private static string Describe(Core.SourceControl.SourceControlScripting scripting)
    {
        var categories = scripting.IncludeTypes.Count > 0
            ? string.Join(", ", scripting.IncludeTypes)
            : "every object category";
        var excluded = scripting.ExcludeTypes.Count > 0
            ? $" (excluding {string.Join(", ", scripting.ExcludeTypes)})"
            : string.Empty;
        var data = scripting.DataTables.Count > 0
            ? $", plus row data for {Count(scripting.DataTables.Count)} table(s)"
            : string.Empty;
        var schemas = scripting.ExcludeSchemas.Count > 0
            ? $", skipping schema {string.Join(", ", scripting.ExcludeSchemas)}"
            : string.Empty;
        return categories + excluded + data + schemas;
    }

    private static string BuildCommitMessage(string flowName, ScriptedDatabase scripted, SnapshotWriteResult write)
        => $"{flowName}: snapshot {scripted.DatabaseName} " +
           $"({write.Added.Count} added, {write.Changed.Count} changed, {write.Deleted.Count} deleted)";
}
