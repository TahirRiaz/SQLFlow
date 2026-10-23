using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// The shared access signature a Data Lake request carries when its container authenticates with a signature or a key
/// rather than a token. A signature is used as given. An account key never travels: the Blob SDK signs an account
/// signature with it locally, for the Blob service (which the Data Lake endpoint serves) with every permission on the
/// service, its containers and their files, so it can do what the key itself can over the Blob endpoint (a container
/// signature could not create its own container). It is valid for an hour and renewed ten minutes before it expires, so
/// a long run never sends an expired one.
/// </summary>
internal sealed class DfsSignature
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan Renewal = TimeSpan.FromMinutes(10);

    private readonly string? _fixed;
    private readonly BlobServiceClient? _signer;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private string? _current;
    private DateTimeOffset _expiresOn;

    private DfsSignature(string? fixedSignature, BlobServiceClient? signer, TimeProvider time)
    {
        _fixed = fixedSignature;
        _signer = signer;
        _time = time;
    }

    /// <summary>A signature used as given.</summary>
    internal static DfsSignature Fixed(string signature) => new(signature.Trim().TrimStart('?'), null, TimeProvider.System);

    /// <summary>An account signature signed with the key the <paramref name="signer"/> holds.</summary>
    internal static DfsSignature SignedWith(BlobServiceClient signer, TimeProvider time)
    {
        if (!signer.CanGenerateAccountSasUri)
        {
            throw new InvalidOperationException("An account signature can only be signed by a client that holds the account key.");
        }

        return new DfsSignature(null, signer, time);
    }

    /// <summary>The signature's query string, without its leading <c>?</c>.</summary>
    internal string Current()
    {
        if (_fixed is not null)
        {
            return _fixed;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_current is null || _expiresOn - now < Renewal)
            {
                _expiresOn = now + Lifetime;
                _current = _signer!.GenerateAccountSasUri(AccountSasPermissions.All, _expiresOn, AccountSasResourceTypes.All).Query.TrimStart('?');
            }

            return _current;
        }
    }
}

/// <summary>
/// Appends a <see cref="DfsSignature"/> to a request's query. It runs once per call (a per-call policy), not on every
/// retry, so a retried request carries the signature once. The values are appended as the signature spells them: a
/// signature is already percent-encoded.
/// </summary>
internal sealed class DfsSignaturePolicy : HttpPipelineSynchronousPolicy
{
    private readonly DfsSignature _signature;

    public DfsSignaturePolicy(DfsSignature signature) => _signature = signature;

    public override void OnSendingRequest(HttpMessage message)
    {
        foreach (var pair in _signature.Current().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            message.Request.Uri.AppendQuery(name, value, escapeValue: false);
        }
    }
}

/// <summary>Reads the parts of an Azure Storage connection string the Data Lake endpoint needs.</summary>
internal static class StorageConnectionString
{
    /// <summary>The value of a connection string's setting (names are case-insensitive), or null.</summary>
    internal static string? Setting(string connectionString, string name)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(part[..equals].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                var value = part[(equals + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}
