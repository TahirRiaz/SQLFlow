using SqlFlow.Core.Invoke;

namespace SqlFlow.SqlServer.Invoke;

/// <summary>
/// The without-database composition of the invoke hook runner shared by the ingestion/export/stored-procedure
/// builders: a document's declared invokes behind an in-memory store, dispatched to the executors the
/// composition root passes in (the SqlFlow.Azure ADF/Automation pair in the CLI; fakes in tests). With no
/// declared invokes there is nothing an alias could reference, so the no-op default stays in place and a set
/// Pre/PostInvokeAlias is surfaced as a clear error, the documented without-database behavior.
/// </summary>
internal static class WithoutDatabaseInvokeRunner
{
    public static IInvokeRunner Build(
        IEnumerable<InvokeDefinition>? invokes,
        IReadOnlyList<IInvokeExecutor>? executors)
    {
        var definitions = invokes?.ToList() ?? [];
        return definitions.Count == 0
            ? NullInvokeRunner.Instance
            : new DefaultInvokeRunner(new InMemoryInvokeFlowStore(definitions), new InvokeDispatcher(executors ?? []));
    }
}

/// <summary>
/// The without-database composition root for standalone invoke flows (flowType: inv): builds a fully wired
/// <see cref="InvokeFlowRunner"/> from the executors the composition root passes in, with no control database
/// anywhere. The run log stays the no-op default. Both the CLI and the tests construct through here, so YAML
/// execution has exactly one code path.
/// </summary>
public static class WithoutDatabaseInvoke
{
    public static InvokeFlowRunner BuildRunner(IReadOnlyList<IInvokeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        return new InvokeFlowRunner(new InvokeDispatcher(executors));
    }
}
