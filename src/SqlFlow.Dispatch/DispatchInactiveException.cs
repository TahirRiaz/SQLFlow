namespace SqlFlow.Dispatch;

/// <summary>Thrown when a node call reaches a dispatcher that does not hold the ownership lease (a passive replica,
/// or the owner before it has rebuilt from the ledger). The host answers 503 with a retry hint; the node retries and
/// lands on the owner.</summary>
public sealed class DispatchInactiveException : Exception
{
    public DispatchInactiveException()
        : base("The dispatcher on this replica is not active; retry shortly.")
    {
    }

    public DispatchInactiveException(string message)
        : base(message)
    {
    }

    public DispatchInactiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
