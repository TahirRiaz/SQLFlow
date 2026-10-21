using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// Ends a request whose client has gone away as the cancellation it is, whatever the work under it threw. ASP.NET Core's
/// exception handler takes only an <see cref="OperationCanceledException"/> or an <see cref="IOException"/> for an abort
/// (logged at debug, status 499); anything else it logs as an unhandled error and hands to
/// <see cref="GlobalExceptionHandler"/>, which logs it again and fails to write a 500 to the closed connection. The work
/// under a request cancels through <see cref="HttpContext.RequestAborted"/>, and a provider can report that as an error
/// of its own: SqlClient answers a command cancelled while it reads with a <c>SqlException</c> ("A severe error occurred
/// on the current command", "Operation cancelled by user"). So every page a browser leaves while it loads would read as
/// a failure in the log.
/// </summary>
/// <remarks>
/// It runs inside the exception handler, so the cancellation it throws takes the handler's own abort path. An exception
/// thrown while the client is still connected is left as it is, and reported as before.
/// </remarks>
public sealed class ClientAbortMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ClientAbortMiddleware> _logger;

    public ClientAbortMiddleware(RequestDelegate next, ILogger<ClientAbortMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException) && context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(
                ex, "Request {CorrelationId} ended after its client went away: {Message}", context.TraceIdentifier, SecretHygiene.RedactedMessage(ex));
            throw new OperationCanceledException("The client went away before the request completed.", ex, context.RequestAborted);
        }
    }
}
