using System.Text.RegularExpressions;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// Assigns every request a correlation id (honoring an inbound <c>X-Correlation-ID</c> when present, otherwise
/// minting one), echoes it on the response, and pushes it into the logging scope so every log line for the
/// request is traceable. The id is the handle a 500 ProblemDetails tells the caller to quote.
/// </summary>
public sealed partial class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private const int MaxInboundLength = 128;

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

        var correlationId = Inbound(context.Request.Headers[HeaderName].ToString()) ?? Guid.NewGuid().ToString("N");
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

    // An inbound id is kept only when it is 1 to 128 letters, digits, '-' and '_', so a hostile header cannot bloat logs
    // or smuggle a line break or control character into a log line or the response. The id kept is the header with
    // every other character removed, which for an id that passes is the header itself; null mints a fresh one.
    internal static string? Inbound(string value)
    {
        if (value.Length is 0 or > MaxInboundLength)
        {
            return null;
        }

        var kept = OutsideIdAlphabet().Replace(value, string.Empty);
        return kept.Length == value.Length ? kept : null;
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}_-]")]
    private static partial Regex OutsideIdAlphabet();
}
