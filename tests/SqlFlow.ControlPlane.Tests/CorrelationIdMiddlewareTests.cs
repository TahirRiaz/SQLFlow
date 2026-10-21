using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.ControlPlane.Infrastructure;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The correlation id a request carries: an inbound <c>X-Correlation-ID</c> made only of letters, digits, '-' and '_'
/// (at most 128 of them) is kept as sent, and any other is replaced by a minted id, so a header cannot put a line break
/// or a control character into a log line or the response.
/// </summary>
public sealed class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData("corr-search-1")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("req_2026-10-21")]
    [InlineData("Lauf-über-Nacht")]
    public async Task An_inbound_id_of_letters_digits_hyphens_and_underscores_is_kept(string inbound)
        => Assert.Equal(inbound, await CorrelationIdFor(inbound));

    [Theory]
    [InlineData("")]
    [InlineData("forged\r\nINFO admin signed in")]
    [InlineData("tab\there")]
    [InlineData("a b")]
    [InlineData("id;drop")]
    [InlineData("\u202Eevil")]
    public async Task Any_other_inbound_id_is_replaced_by_a_minted_one(string inbound)
    {
        var id = await CorrelationIdFor(inbound);
        Assert.NotEqual(inbound, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task An_inbound_id_longer_than_128_characters_is_replaced()
    {
        Assert.Equal(new string('a', 128), await CorrelationIdFor(new string('a', 128)));
        Assert.Matches("^[0-9a-f]{32}$", await CorrelationIdFor(new string('a', 129)));
    }

    private static async Task<string> CorrelationIdFor(string inbound)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = inbound;
        string? seen = null;
        var middleware = new CorrelationIdMiddleware(
            next =>
            {
                seen = next.TraceIdentifier;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(seen, context.Items[CorrelationIdMiddleware.HeaderName]);
        return seen!;
    }
}
