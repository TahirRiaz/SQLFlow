using SqlFlow.Core;

namespace SqlFlow.Catalog;

/// <summary>
/// Thrown when a schedule cannot be created because its repo already holds a schedule of that name (one name per repo,
/// whichever way it was made, compared as the catalog's unique index compares it). The check is taken under the repo's
/// schedule-name lock (<see cref="ScheduleStore.LockScheduleNamesAsync"/>), so it is never raced into an index failure.
/// </summary>
public sealed class ScheduleNameTakenException : SqlFlowException
{
    public ScheduleNameTakenException(Guid repoId, string name, string existingSource, Guid existingId)
        : base($"Repo '{repoId}' already has a schedule named '{name}' ({existingSource} schedule {existingId}).")
    {
        RepoId = repoId;
        Name = name;
        ExistingSource = existingSource;
        ExistingId = existingId;
    }

    public Guid RepoId { get; }

    public string Name { get; }

    /// <summary>Where the schedule holding the name comes from: <c>yaml</c> or <c>api</c>.</summary>
    public string ExistingSource { get; }

    public Guid ExistingId { get; }
}

/// <summary>
/// Thrown when a writer of a repo's schedules (a catalog sync, an API create) could not take the repo's schedule-name
/// lock within <see cref="ScheduleStore.ScheduleNamesLockTimeoutMs"/>, because another writer of the same repo held it
/// that long. Nothing was written, and the call is safe to repeat.
/// </summary>
public sealed class ScheduleNamesBusyException : SqlFlowException
{
    public ScheduleNamesBusyException(Guid repoId, int returnCode)
        : base($"The schedules of repo '{repoId}' are being written by another sync or API call; nothing was changed, " +
               $"try again (the schedule-name lock was not granted within {ScheduleStore.ScheduleNamesLockTimeoutMs / 1000} " +
               $"seconds, sp_getapplock returned {returnCode}).")
    {
        RepoId = repoId;
        ReturnCode = returnCode;
    }

    public Guid RepoId { get; }

    /// <summary>sp_getapplock's code: -1 timeout, -2 cancelled, -3 deadlock victim, -999 parameter error.</summary>
    public int ReturnCode { get; }
}
