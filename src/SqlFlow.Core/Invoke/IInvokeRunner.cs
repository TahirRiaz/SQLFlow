namespace SqlFlow.Core.Invoke;

/// <summary>
/// Resolves an invoke flow by its alias and runs it. This is the seam the data-flow runners hold for their
/// Pre/PostInvokeAlias hooks: a set alias is run through this, and a failure that the invoke does not tolerate
/// (OnErrorResume = false) is raised so the parent flow fails. Without-database mode wires
/// <see cref="NullInvokeRunner"/>, which cannot resolve an alias and raises a clear error; full mode wires
/// <see cref="DefaultInvokeRunner"/>.
/// </summary>
public interface IInvokeRunner
{
    Task<InvokeResult> RunByAliasAsync(string invokeAlias, CancellationToken ct = default);
}

/// <summary>
/// The without-database default: there is no flw.Invoke registry or dispatcher to resolve an alias against, so a
/// set Pre/PostInvokeAlias is surfaced as a clear configuration error rather than silently skipped (skipping a
/// requested side effect would be a correctness hazard). Full mode swaps in <see cref="DefaultInvokeRunner"/>.
/// </summary>
public sealed class NullInvokeRunner : IInvokeRunner
{
    public static readonly NullInvokeRunner Instance = new();

    private NullInvokeRunner()
    {
    }

    public Task<InvokeResult> RunByAliasAsync(string invokeAlias, CancellationToken ct = default)
        => throw new SqlFlowException(
            $"PreInvokeAlias/PostInvokeAlias '{invokeAlias}' is set, but the invoke subsystem (the flw.Invoke registry and the ADF/Automation dispatcher) is not available in this build.");
}

/// <summary>
/// The full-mode invoke runner: resolves the alias through an <see cref="IInvokeFlowLoader"/> and dispatches it.
/// A failed invoke raises (so the parent flow fails) unless the invoke flow opted into <c>OnErrorResume</c>, in
/// which case the failed result is returned and the parent continues, matching the legacy resume semantics.
/// </summary>
public sealed class DefaultInvokeRunner : IInvokeRunner
{
    private readonly IInvokeFlowLoader _loader;
    private readonly IInvokeDispatcher _dispatcher;

    public DefaultInvokeRunner(IInvokeFlowLoader loader, IInvokeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _loader = loader;
        _dispatcher = dispatcher;
    }

    public async Task<InvokeResult> RunByAliasAsync(string invokeAlias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invokeAlias);

        var definition = await _loader.LoadByAliasAsync(invokeAlias, ct).ConfigureAwait(false);
        // A pre/post-invoke hook is a nested action with its own identity; it never inherits the parent flow's
        // assigned run id, so the dispatcher mints a fresh one (assignedRunId left at its default).
        var result = await _dispatcher.DispatchAsync(definition, ct: ct).ConfigureAwait(false);

        if (!result.Success && !definition.OnErrorResume)
        {
            throw new SqlFlowException(
                $"Invoke '{invokeAlias}' (flow {definition.FlowId}, type {definition.FlowType}) failed: {result.Error}");
        }

        return result;
    }
}
