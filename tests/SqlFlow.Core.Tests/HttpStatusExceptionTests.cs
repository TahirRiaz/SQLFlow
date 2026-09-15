using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// <see cref="HttpStatusException"/> carries the status an endpoint answered with and, when the service named one, the wait
/// it asked for (its <c>Retry-After</c>), so a caller holding the failure can schedule the next attempt no sooner.
/// </summary>
public sealed class HttpStatusExceptionTests
{
    [Fact]
    public void WithoutAWait_CarriesTheStatusOnly()
    {
        var error = new HttpStatusException(404, "HTTP 404 Not Found from https://example.com/items/1");

        Assert.Equal(404, error.StatusCode);
        Assert.Null(error.RetryAfter);
        Assert.Equal("HTTP 404 Not Found from https://example.com/items/1", error.Message);
        Assert.IsAssignableFrom<SqlFlowException>(error);
    }

    [Fact]
    public void WithAWait_CarriesIt()
    {
        var error = new HttpStatusException(429, "HTTP 429 Too Many Requests", TimeSpan.FromSeconds(30));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
    }

    [Fact]
    public void AWaitGivenAsNull_MeansTheServiceNamedNone()
        => Assert.Null(new HttpStatusException(503, "HTTP 503 Service Unavailable", retryAfter: null).RetryAfter);

    [Fact]
    public void ANegativeWait_MeansRetryAtOnce()
    {
        // A Retry-After date already in the past computes to a negative wait: the service allows the retry now.
        var error = new HttpStatusException(503, "HTTP 503 Service Unavailable", TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.Zero, error.RetryAfter);
    }
}
