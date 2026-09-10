using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.Tests;

public sealed class FlowIdentityTests
{
    [Fact]
    public void FromName_IsDeterministic_SameNameSameId()
    {
        var first = FlowIdentity.FromName("orders");
        var second = FlowIdentity.FromName("orders");

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public void FromName_DifferentNames_DifferentIds()
        => Assert.NotEqual(FlowIdentity.FromName("orders"), FlowIdentity.FromName("customers"));

    [Fact]
    public void FromName_ProducesValidVersion5Guid()
    {
        var id = FlowIdentity.FromName("orders");

        // RFC 4122 version nibble must be 5.
        Assert.Equal(5, id.Version);

        // Variant must be the 10xx form. Octet 8 (Data4[0], index 8 in the .NET byte layout) is not
        // endian-swapped, so its two high bits encode the variant directly.
        var variantOctet = id.ToByteArray()[8];
        Assert.Equal(0x80, variantOctet & 0xC0);
    }

    [Fact]
    public void Resolve_PrefersPinnedId_FallsBackToName()
    {
        var pinned = Guid.CreateVersion7();

        Assert.Equal(pinned, FlowIdentity.Resolve(pinned, "orders"));
        Assert.Equal(FlowIdentity.FromName("orders"), FlowIdentity.Resolve(null, "orders"));
        Assert.Equal(FlowIdentity.FromName("orders"), FlowIdentity.Resolve(Guid.Empty, "orders"));
    }
}
