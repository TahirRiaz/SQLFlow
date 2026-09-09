namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// Assigns every request a correlation id (honoring an inbound <c>X-Correlation-ID</c> when present, otherwise
/// minting one), echoes it on the response, and pushes it into the logging scope so every log line for the
/// request is traceable. The id is the handle a 500 ProblemDetails tells the caller to quote.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inbound = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafe(inbound) ? inbound : Guid.NewGuid().ToString("N");
        context.Items[HeaderName] = correlationId;
        context.TraceIdentifier = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    // Bound an inbound id so a hostile header cannot bloat logs or smuggle control characters into a response.
    private static bool IsSafe(string value)
        => value.Length is > 0 and <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
