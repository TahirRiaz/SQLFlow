using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Connections;

/// <summary>
/// The single connection-resolution pipeline (resolved decision: lives in pure Core). For one raw reference
/// it: classifies the reference; resolves an <c>@alias</c> through the <see cref="IDataSourceStore"/> (full
/// mode only, else a precise error); expands <c>${...}</c> secret references through the
/// <see cref="ISecretResolver"/>; then delegates canonicalization and the secretless gate to the
/// <see cref="IConnectionStringCanonicalizer"/> for the resolved <see cref="DataSourceKind"/>. The
/// canonicalizer is the only provider-specific dependency, which keeps SqlClient out of Core.
/// </summary>
public sealed partial class ConnectionResolver : IConnectionResolver
{
    private readonly IDataSourceStore _store;
    private readonly ISecretResolver _secrets;
    private readonly IReadOnlyList<IConnectionStringCanonicalizer> _canonicalizers;

    public ConnectionResolver(
        IDataSourceStore store,
        ISecretResolver secrets,
        IEnumerable<IConnectionStringCanonicalizer> canonicalizers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(canonicalizers);

        _store = store;
        _secrets = secrets;
        _canonicalizers = canonicalizers.ToList();
    }

    public async Task<ResolvedConnection> ResolveAsync(string rawReference, ConnectionRole role, DataSourceKind? inlineKind = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawReference);

        var reference = ConnectionRef.Parse(rawReference);

        DataSourceKind kind;
        DataSourceCapabilities capabilities;
        CredentialProfile credential;
        StorageContext storage;
        string connectionText;
        SecretlessPolicy policy;

        if (reference.Kind == ConnectionRefKind.Alias)
        {
            if (!_store.SupportsAliases)
            {
                throw new SqlFlowException(
                    $"Connection '@{reference.Value}' is an alias and requires full mode (a configured control " +
                    "database). Supply an inline connection string or a ${...} secret reference instead.");
            }

            var dataSource = await _store.ResolveAsync(reference.Value, ct).ConfigureAwait(false);
            kind = dataSource.Kind;
            capabilities = dataSource.Capabilities;
            credential = dataSource.Credential;
            storage = dataSource.Storage;
            connectionText = dataSource.ConnectionRef;
            policy = PolicyForAlias(credential.Mode);
        }
        else
        {
            // An inline reference carries no registry metadata. It defaults to SQL Server; a caller that
            // knows better (the YAML connections block, the catalog CLI's --provider) passes the kind
            // explicitly. Capabilities, credential, and storage are empty either way.
            kind = inlineKind ?? DataSourceKind.MSSQL;
            capabilities = new DataSourceCapabilities();
            credential = new CredentialProfile();
            storage = new StorageContext();
            connectionText = reference.Value;

            // A whole ${...} reference yields a connection string from a secret store and is trusted; a
            // literal inline string must authenticate itself without a resting secret.
            policy = IsWholeSecretReference(connectionText)
                ? SecretlessPolicy.Trusted
                : SecretlessPolicy.RequireSelfAuthenticating;
        }

        // Expand pure and embedded ${...} references. A literal with no references is returned unchanged.
        var expanded = await _secrets.ResolveAsync(connectionText, ct).ConfigureAwait(false);

        var canonicalizer = SelectCanonicalizer(kind);
        var canonical = canonicalizer.Canonicalize(expanded, role, policy);

        return new ResolvedConnection
        {
            CanonicalString = canonical.Canonical,
            RedactedString = canonical.Redacted,
            Kind = kind,
            Capabilities = capabilities,
            Credential = credential,
            Storage = storage,
        };
    }

    private static SecretlessPolicy PolicyForAlias(CredentialMode mode) => mode switch
    {
        // The whole connection string came from a secret store, so a password in it is legitimate.
        CredentialMode.InlineConnectionString => SecretlessPolicy.Trusted,
        // A token is injected at open time, so the string legitimately omits an auth keyword.
        CredentialMode.InjectedToken => SecretlessPolicy.RejectRestingSecret,
        // ConnectionStringAuth / Integrated: the string must authenticate itself, with no resting secret.
        _ => SecretlessPolicy.RequireSelfAuthenticating,
    };

    private IConnectionStringCanonicalizer SelectCanonicalizer(DataSourceKind kind)
        => _canonicalizers.FirstOrDefault(c => c.CanHandle(kind))
           ?? throw new SqlFlowException(
                $"No connection-string canonicalizer is registered for data source kind '{kind}'.");

    /// <summary>True when the trimmed value is exactly one <c>${scheme:locator}</c> reference and nothing
    /// else, so the entire connection string is sourced from a secret store.</summary>
    private static bool IsWholeSecretReference(string value) => WholeReference().IsMatch(value);

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();
}
