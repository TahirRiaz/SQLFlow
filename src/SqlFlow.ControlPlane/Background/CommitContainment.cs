using LibGit2Sharp;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>Whether one commit contains another: <see cref="Contains"/> when it was answered, or why it could not be.</summary>
public sealed record CommitContainmentResult(bool? Contains, string? Problem)
{
    public static CommitContainmentResult Yes { get; } = new(true, null);

    public static CommitContainmentResult No { get; } = new(false, null);

    public static CommitContainmentResult Unknown(string problem) => new(null, problem);
}

/// <summary>
/// Answers whether the commit a repository was synced at contains the commit a caller expected (the merge it reported):
/// the same commit, or a descendant of it, as when the branch moved on past the merge before the sync read it. Behind an
/// interface so a trigger request's processing is testable without git.
/// </summary>
public interface ICommitContainment
{
    /// <summary>Whether <paramref name="synced"/> contains <paramref name="expected"/> in <paramref name="source"/>'s repository.</summary>
    Task<CommitContainmentResult> ContainsAsync(CatalogRepoSource source, string synced, string expected, CancellationToken ct);
}

/// <summary>
/// Reads containment from the repository's history clone (the one the history pages read, fetched now so the synced
/// commit is in it), with the same credential the repository's sync reads with, resolved with the values the control
/// plane supplies with the repository's runs. A commit the clone does not hold, or a git failure, answers why, never a
/// guess.
/// </summary>
public sealed class GitCommitContainment : ICommitContainment
{
    private readonly ISecretResolver _secrets;
    private readonly RunReferences _references;

    public GitCommitContainment(ISecretResolver secrets, RunReferences references)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(references);
        _secrets = secrets;
        _references = references;
    }

    public async Task<CommitContainmentResult> ContainsAsync(CatalogRepoSource source, string synced, string expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(synced);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        if (source.RemoteUrl is not { Length: > 0 } remote)
        {
            return CommitContainmentResult.Unknown($"Repo source '{source.Name}' reads a local directory, which has no commits to compare.");
        }

        try
        {
            var references = await _references.ForAsync(SqlFlow.Core.Identity.FlowIdentity.FromName(source.Name), ct).ConfigureAwait(false);
            using var supplied = SuppliedReferences.Supply(references);
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(_secrets, source.CredentialReference, source.CredentialUsername, remote, GitAccess.Read, ct)
                .ConfigureAwait(false);

            // git and file IO block, and a cold clone is slow: keep both off the caller's thread.
            return await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer().EnsureHistoryClone(remote, source.Branch, credentials, TimeSpan.Zero, ct);
                    using var repo = new Repository(workingDir);
                    var descendant = repo.Lookup<Commit>(synced);
                    if (descendant is null)
                    {
                        return CommitContainmentResult.Unknown($"The synced commit {Short(synced)} is not in {remote}'s history.");
                    }

                    var ancestor = repo.Lookup<Commit>(expected);
                    if (ancestor is null)
                    {
                        return CommitContainmentResult.Unknown(
                            $"Commit {Short(expected)} is not in {remote}: it was not pushed there, or it belongs to another repository.");
                    }

                    if (descendant.Sha == ancestor.Sha)
                    {
                        return CommitContainmentResult.Yes;
                    }

                    var mergeBase = repo.ObjectDatabase.FindMergeBase(ancestor, descendant);
                    return mergeBase?.Sha == ancestor.Sha ? CommitContainmentResult.Yes : CommitContainmentResult.No;
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException or IOException)
        {
            return CommitContainmentResult.Unknown(
                $"Whether {Short(synced)} contains {Short(expected)} could not be read from git: {SecretHygiene.RedactedMessage(ex)}");
        }
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
