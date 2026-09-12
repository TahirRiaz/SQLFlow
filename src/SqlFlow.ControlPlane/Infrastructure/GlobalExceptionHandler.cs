using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// The single place every unhandled exception is turned into an RFC 7807 ProblemDetails response. A
/// <see cref="SqlFlowException"/> (a validation/usage error) becomes a 400 with a redacted message; anything
/// else becomes a 500 whose body never leaks internals (the message is logged, with the correlation id, server
/// side only). All messages are run through <see cref="SecretHygiene.RedactedMessage(Exception)"/> first.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetails);
        ArgumentNullException.ThrowIfNull(logger);
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false; // a client-aborted request is not an error to report.
        }

        var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var id) ? id?.ToString() : httpContext.TraceIdentifier;
        var isClientError = exception is SqlFlowException;
        // A node call reaching a replica whose dispatcher is not the owner is neither a client error nor a fault:
        // it is answered 503 with a retry hint, and the node's next attempt lands on the owner.
        var isInactiveDispatcher = exception is SqlFlow.Dispatch.DispatchInactiveException;
        var status = isInactiveDispatcher
            ? StatusCodes.Status503ServiceUnavailable
            : isClientError ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
        var redacted = SecretHygiene.RedactedMessage(exception);

        if (isInactiveDispatcher)
        {
            httpContext.Response.Headers.RetryAfter = "2";
            _logger.LogDebug("Request {CorrelationId} deferred: {Message}", correlationId, redacted);
        }
        else if (isClientError)
        {
            _logger.LogWarning("Request {CorrelationId} rejected: {Message}", correlationId, redacted);
        }
        else
        {
            _logger.LogError(exception, "Request {CorrelationId} failed: {Message}", correlationId, redacted);
        }

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = isInactiveDispatcher
                    ? "Dispatch is not active on this replica"
                    : isClientError ? "Bad request" : "An unexpected error occurred",
                // A passive replica says so, because that is exactly what a node (or an operator reading its log)
                // needs to know: retry, and the next attempt lands on the owner. A client error carries its own
                // redacted message. Never echo an internal message on a 500; the correlation id ties it to the log.
                Detail = isInactiveDispatcher || isClientError
                    ? redacted
                    : "An unexpected error occurred; quote the correlation id to support.",
                Extensions = { ["correlationId"] = correlationId },
            },
        }).ConfigureAwait(false);
    }
}
