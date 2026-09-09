namespace SqlFlow.Core;

/// <summary>
/// Thrown when a schema change cannot acquire its lock without blocking: either the object-scoped app lock
/// could not be taken within the timeout (another schema run holds the same object), or a DDL statement's
/// schema-modification lock timed out (SQL error 1222) because the table was busy. The flow fails cleanly and
/// is safely retryable; the pending lock request never queues in front of and blocks active readers.
/// </summary>
public sealed class SchemaLockTimeoutException : SqlFlowException
{
    public SchemaLockTimeoutException(string resource, int? returnCode, Exception? innerException = null)
        : base($"Could not acquire the schema lock on '{resource}'"
               + (returnCode is { } code ? $" (sp_getapplock returned {code})." : " (DDL lock timeout, SQL error 1222)."),
               innerException ?? new InvalidOperationException("schema lock timeout"))
    {
        Resource = resource;
        ReturnCode = returnCode;
    }

    public string Resource { get; }

    public int? ReturnCode { get; }
}
