using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Core.Identity;

/// <summary>
/// Resolves a flow's stable identity. A flow's <c>FlowId</c> must be identical on every execution so
/// that logs, lineage, run history, and file-log dedup all join on it across runs (unlike the per-run
/// RunId, which changes each execution). When the author pins an explicit GUID it is used verbatim;
/// otherwise a deterministic name-based GUID (RFC 4122 version 5, SHA-1 over a fixed namespace plus
/// the flow name) is computed. The same flow name always yields the same GUID, on any machine, with
/// no database and no write-back, while different names yield different GUIDs.
/// </summary>
public static class FlowIdentity
{
    // Fixed namespace for SQLFlow flow identities. Constant forever: changing it would re-key every
    // flow that relies on the computed (unpinned) identity.
    private static readonly Guid FlowNamespace = new("4f1f6b1e-8a2d-4c3b-9e7a-2c5d8f0b1a63");

    /// <summary>
    /// Returns <paramref name="explicitId"/> when the author pinned one; otherwise the deterministic
    /// identity computed from <paramref name="flowName"/>.
    /// </summary>
    public static Guid Resolve(Guid? explicitId, string flowName)
        => explicitId is { } pinned && pinned != Guid.Empty ? pinned : FromName(flowName);

    /// <summary>Computes the deterministic (version 5) GUID for a flow name.</summary>
    public static Guid FromName(string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return NameBasedV5(FlowNamespace, flowName);
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is mandated by RFC 4122 for version 5 name-based UUIDs; it is an identity hash, not a security primitive.")]
    private static Guid NameBasedV5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        ToRfcOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        // Set the version (5) in the high nibble of octet 6 and the RFC 4122 variant in octet 8.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        ToRfcOrder(guidBytes);
        return new Guid(guidBytes);
    }

    // Swaps between .NET's mixed-endian Guid byte layout and RFC 4122 network order. The operation is
    // its own inverse, so it converts in both directions. On big-endian hosts the layouts already
    // match and no swap is needed.
    private static void ToRfcOrder(byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian)
        {
            return;
        }

        Array.Reverse(bytes, 0, 4);
        Array.Reverse(bytes, 4, 2);
        Array.Reverse(bytes, 6, 2);
    }
}
