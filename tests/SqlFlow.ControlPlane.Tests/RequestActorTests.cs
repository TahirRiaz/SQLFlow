using System.Security.Claims;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The caller as attributed writes record it (<see cref="RequestActor"/>): the token subject first, the identity name
/// otherwise, trimmed, and nothing for a principal that carries neither.
/// </summary>
public sealed class RequestActorTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Of_PrefersTheTokenSubject_OverTheIdentityName()
    {
        var user = Principal(new Claim("sub", "alice"), new Claim(ClaimTypes.Name, "Alice Example"));

        Assert.Equal("alice", RequestActor.Of(user));
    }

    [Fact]
    public void Of_FallsBackToTheIdentityName()
    {
        Assert.Equal("bob", RequestActor.Of(Principal(new Claim(ClaimTypes.Name, "bob"))));
    }

    [Fact]
    public void Of_TrimsTheName()
    {
        Assert.Equal("carol", RequestActor.Of(Principal(new Claim("sub", "  carol "))));
    }

    [Fact]
    public void Of_APrincipalWithoutAName_IsNull()
    {
        Assert.Null(RequestActor.Of(new ClaimsPrincipal()));
        Assert.Null(RequestActor.Of(Principal(new Claim("scope", "read"))));
        Assert.Null(RequestActor.Of(Principal(new Claim("sub", "   "))));
    }

    [Fact]
    public void Of_RefusesANullPrincipal()
    {
        Assert.Throws<ArgumentNullException>(() => RequestActor.Of(null!));
    }

    [Fact]
    public void Label_NamesTheUser_OrUnknown()
    {
        Assert.Equal("user:alice", RequestActor.Label(Principal(new Claim("sub", "alice"))));
        Assert.Equal("user:unknown", RequestActor.Label(new ClaimsPrincipal()));
    }
}
