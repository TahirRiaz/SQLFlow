using SqlFlow.Core.Runs;

namespace SqlFlow.Orchestration;

/// <summary>
/// Spreads part of a running flow's work across member runs of the same flow, which the dispatcher hands to any node
/// serving the run's pool while the run itself keeps executing. Members are ordinary journaled runs: each is visible
/// and cancellable, carries its own parameters, and records its outcome and, for a flow kind a host registered, its
/// result object, which the run reads back through <see cref="MembersAsync"/>. A run re-executed after an interruption
/// that asks for the same operation again gets back the members it already has instead of new ones, and the members
/// end when the run does.
/// </summary>
public interface IRunFanOut
{
    /// <summary>Enqueues one member per parameter set (at least one, all performing the same operation, within the
    /// dispatcher's bound) and returns the handle to read and cancel them by.</summary>
    Task<RunFanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct);

    /// <summary>The members in slot order, as last journaled.</summary>
    Task<IReadOnlyList<RunFanOutMember>> MembersAsync(RunFanOutHandle handle, CancellationToken ct);

    /// <summary>Cancels the members still queued (outright) or executing (by a request their nodes honor).</summary>
    Task CancelAsync(RunFanOutHandle handle, CancellationToken ct);
}

/// <summary>Identifies a fan-out: its run group and the member run ids in slot order.</summary>
public sealed record RunFanOutHandle(Guid GroupId, IReadOnlyList<Guid> RunIds);

/// <summary>One fan-out member as last journaled: its run id, its slot (from 1), its lifecycle status (queued, running,
/// succeeded, failed, cancelled or skipped), the recorded error, and its result object as JSON when its kind records
/// one.</summary>
public sealed record RunFanOutMember(Guid RunId, int Slot, string Status, string? Error, string? ResultJson)
{
    /// <summary>Whether the member has finished, whatever the outcome.</summary>
    public bool IsFinished => Status is not ("queued" or "running");

    /// <summary>Whether the member finished successfully.</summary>
    public bool Succeeded => Status == "succeeded";
}
