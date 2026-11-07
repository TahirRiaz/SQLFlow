using System.Globalization;
using LibGit2Sharp;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>What one sync attempt of a git repo source reads.</summary>
public enum RepoSyncRead
{
    /// <summary>The tip of the source's branch, as every sync of a source that syncs on its own reads.</summary>
    BranchTip,

    /// <summary>The commit the source holds, again: a sync of a source that syncs only on request that no move asked for.</summary>
    HeldCommit,

    /// <summary>The commit a move asked for, checked against the branch and the commit the source holds before it is read.</summary>
    AskedCommit,
}

/// <summary>What an attempt reads (<see cref="Read"/>), the commit when it is not the tip, and why, in a line its trace shows.</summary>
public sealed record RepoSyncPlan(RepoSyncRead Read, string? Commit, string Reason);

/// <summary>
/// Where a move to an asked commit lands: <see cref="Commit"/> is the full id the sync reads (the asked commit, or the one
/// the source holds when that already contains it), or null when the move is refused; <see cref="Message"/> says which,
/// and why, in a line the sync's trace shows or the error it fails with.
/// </summary>
public sealed record RepoSyncMoveDecision(string? Commit, string Message)
{
    /// <summary>Whether the move is refused: the sync fails with <see cref="Message"/> and the source keeps what it holds.</summary>
    public bool Refused => Commit is null;
}

/// <summary>
/// Decides what a sync attempt of a git repo source reads. A source that syncs on its own reads the tip of its branch.
/// One that syncs only on request (<see cref="CatalogRepoSource.AutoSync"/> off) reads the commit it holds, unless the
/// attempt answers a move (<see cref="RepoSourceStore.AnswersMove"/>): then the tip, or the asked commit, which must be on
/// the branch and is never a step back from the held commit. A source holding no commit yet reads the tip for its first sync.
/// </summary>
public static class RepoSyncTarget
{
    /// <summary>What the attempt of <paramref name="source"/> reads, from the row its claim stamped; no git is read.</summary>
    public static RepoSyncPlan Plan(CatalogRepoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.AutoSync)
        {
            return new RepoSyncPlan(RepoSyncRead.BranchTip, null, $"Syncs on its own: reading the tip of {source.Branch}.");
        }

        if (!RepoSourceStore.IsCommitId(source.LastSyncedSha))
        {
            return new RepoSyncPlan(
                RepoSyncRead.BranchTip, null,
                $"Syncs only on request and holds no commit yet: reading the tip of {source.Branch} for its first sync.");
        }

        var held = source.LastSyncedSha!;
        if (RepoSourceStore.AnswersMove(source))
        {
            var asked = source.MoveRequestedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return source.MoveToCommit is { } commit
                ? new RepoSyncPlan(RepoSyncRead.AskedCommit, commit, $"Syncs only on request: moving from {ShortId(held)} to {ShortId(commit)}, as asked at {asked} UTC.")
                : new RepoSyncPlan(RepoSyncRead.BranchTip, null, $"Syncs only on request: moving from {ShortId(held)} to the tip of {source.Branch}, as asked at {asked} UTC.");
        }

        return new RepoSyncPlan(
            RepoSyncRead.HeldCommit, held,
            $"Syncs only on request: syncing the commit it holds, {ShortId(held)}, again. Nothing merged to {source.Branch} since is read until a trigger request or a sync-now asks.");
    }

    /// <summary>
    /// Where a move of a source on <paramref name="branch"/> holding <paramref name="held"/> to <paramref name="asked"/>
    /// lands, read from <paramref name="history"/>, a clone of <paramref name="remote"/> fetched just before:
    /// <list type="bullet">
    /// <item>refused when the asked commit is not in the remote, or not on the branch;</item>
    /// <item>the held commit when it already contains the asked one (a sync never moves back);</item>
    /// <item>the asked commit when it contains the held one, or the held one is no longer in the remote (its history was
    /// rewritten);</item>
    /// <item>refused when the two are on separate lines of history, since reading the asked one would drop what the held
    /// one has.</item>
    /// </list>
    /// </summary>
    public static RepoSyncMoveDecision Resolve(Repository history, string remote, string branch, string? held, string asked)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);
        if (!RepoSourceStore.IsCommitId(asked))
        {
            return Refuse($"'{asked}' is not a commit id (7 to 64 hexadecimal characters).");
        }

        var tip = history.Branches[$"origin/{branch}"]?.Tip;
        if (tip is null)
        {
            return Refuse($"{remote} has no branch '{branch}', the branch this source syncs.");
        }

        Commit? target;
        try
        {
            target = history.Lookup<Commit>(asked);
        }
        catch (AmbiguousSpecificationException)
        {
            return Refuse($"'{asked}' names more than one commit in {remote}; give more of the commit's id.");
        }

        if (target is null)
        {
            return Refuse($"Commit {ShortId(asked)} is not in {remote}: it was not pushed there, or it belongs to another repository.");
        }

        if (!Contains(history, tip, target))
        {
            return Refuse(
                $"Commit {ShortId(target.Sha)} is not on branch '{branch}' (its tip is {ShortId(tip.Sha)}), the branch this source syncs; "
                + "a commit is synced only from that branch, once it is merged there.");
        }

        // The held commit is a full id the sync recorded, so it names one commit or none.
        var holding = held is null ? null : history.Lookup<Commit>(held);
        if (holding is null)
        {
            return held is null
                ? new RepoSyncMoveDecision(target.Sha, $"Moving to {ShortId(target.Sha)}.")
                : new RepoSyncMoveDecision(
                    target.Sha,
                    $"The commit it held, {ShortId(held)}, is no longer in {remote} (the branch's history was rewritten): moving to {ShortId(target.Sha)}.");
        }

        if (Contains(history, holding, target))
        {
            return new RepoSyncMoveDecision(
                holding.Sha,
                holding.Sha == target.Sha
                    ? $"It already holds {ShortId(target.Sha)}: syncing it again."
                    : $"It holds {ShortId(holding.Sha)}, which already contains {ShortId(target.Sha)}: staying on {ShortId(holding.Sha)}, since a sync never moves back.");
        }

        if (!Contains(history, target, holding))
        {
            return Refuse(
                $"Commit {ShortId(target.Sha)} and the commit this source holds, {ShortId(holding.Sha)}, are on separate lines of branch '{branch}''s history, "
                + $"so syncing {ShortId(target.Sha)} would drop what {ShortId(holding.Sha)} has. Ask for a commit that contains both, such as the tip of '{branch}'.");
        }

        return new RepoSyncMoveDecision(target.Sha, $"Moving from {ShortId(holding.Sha)} to {ShortId(target.Sha)}.");
    }

    /// <summary>Whether <paramref name="descendant"/> contains <paramref name="ancestor"/>: the same commit, or one with it in its history.</summary>
    public static bool Contains(Repository repository, Commit descendant, Commit ancestor)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(descendant);
        ArgumentNullException.ThrowIfNull(ancestor);
        return descendant.Sha == ancestor.Sha || repository.ObjectDatabase.FindMergeBase(ancestor, descendant)?.Sha == ancestor.Sha;
    }

    private static RepoSyncMoveDecision Refuse(string why) => new(null, why);

    /// <summary>A commit id as a trace line names it: its first 12 characters.</summary>
    public static string ShortId(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
