# V3 Central Connection & Data-Source / Credential Registry - Design

> Pre-implementation design document; the as-built code has diverged in several places this
> document (and its own corrections report) describe as decided: there is no `Synapse`
> `DataSourceKind` (Synapse-ness is `DataSourceCapabilities.IsSynapse`), connections live in
> `src/SqlFlow.Core/Connections/` rather than a separate `SqlFlow.Connections` project,
> `ISecretResolver` is asynchronous (`ResolveAsync`), and `IConnectionFactory` returns `DbConnection`
> dispatched by kind rather than a bare `SqlConnection`. For the current, code-verified behavior see
> [docs/reference/concepts/connections-and-secrets.md](reference/concepts/connections-and-secrets.md)
> and [docs/reference/flow/connections.md](reference/flow/connections.md).

Status: design (red-team corrected). Target tree: `C:\Projects\SQLFlowV3`. Target platform: SQL Server by design, .NET 9. This document supersedes the four (unavailable) dimension drafts and folds in every critic finding as a resolved decision, not a list. The end of section 10 carries a per-id revision log (H1..H9, M1..M9, L1..L9) so every correction is auditable.

---

## 1. Overview and the single resolution model

### 1.1 One value type, one pipeline

Every connection in V3 is a `ConnectionRef`: a single string field. In this cut it is carried by `TargetSpec.Connection` (the SQL Server target, always present) and, when an `@alias` or `${...}` ref names a relational source, by `SourceSpec.Location`. A `ConnectionRef` is one of exactly three shapes, distinguished at parse time, resolved by ONE pipeline:

| Shape | Example | Where valid | Resolves to |
|---|---|---|---|
| `@alias` | `@AdventureWorksDW` | full mode only | a `DataSource` row in the control DB, which itself yields an inline string or a `${...}` ref |
| `${scheme:locator}` | `${keyvault:kv-prod/dw-conn}` | both modes | the secret value (a connection string) |
| inline | `Server=tcp:dw...;Authentication=Active Directory Default;...` | both modes (with a secretless constraint, see 1.4) | itself |

The single resolution pipeline, applied once per role per run, is:

```
raw string
  -> trim, classify (sigil grammar, 1.3)
  -> if @alias: IDataSourceStore.ResolveAsync(alias) -> DataSource (its ConnectionRef may itself be a ${...} ref)
  -> ${...} expansion via ISecretResolver.ResolveAsync (async, see M1)
  -> SqlConnectionStringBuilder canonicalization + ApplicationName stamp + secretless inspection (6.1)
  -> ResolvedConnection { CanonicalString, RedactedString, Kind, Capabilities, StorageContext, CredentialProfile }
```

There is exactly one resolver, `IConnectionResolver`, with one async `ResolveAsync` method. Lightweight and full mode plug a different `IDataSourceStore` (empty vs DB-backed) and otherwise share every line. This honors the architecture rule "the only difference is which providers are plugged in" and the Single Code Path Principle: there is no `if (mode == Full)` branch in the resolver.

The relational source path (an `@alias`/`${...}`/inline that names a MySQL or SQL Server source) is IN scope for this cut (locked decision 1). It uses the same resolver with `ConnectionRole.Source`, and the engine passes the resulting `ResolvedConnection` to an `AdoSourceReader` (section 4.4). There is no second resolution code path for sources.

### 1.2 How lightweight and full converge

- **Lightweight**: no control DB. `IDataSourceStore` is `NullDataSourceStore`, whose `SupportsAliases` is false and which throws a precise error for any `@alias`. YAML carries either a `${...}` ref or a secretless inline string. The `${...}` expansion plus canonicalization steps run identically.
- **Full**: `IDataSourceStore` is `SqlDataSourceStore`, a single indexed seek into `flw.DataSource` (section 5). The row's `ConnectionRef` (a `${...}` ref or an `Authentication=Active Directory ...` inline string) then flows through the SAME `${...}` plus canonicalization steps. The DB is a registry of references and capability metadata, never of secrets.

"Memory is the upgrade, not the entry fee": the only thing full mode adds here is the alias indirection plus capability metadata; the credential and pooling machinery is identical in both modes.

### 1.3 The `@` sigil grammar (disambiguated, leak-safe)

The critic flagged that `@` appears in AAD user names (`User ID=svc@contoso.onmicrosoft.com`) and inside resolved secret values. The grammar removes the ambiguity:

```
1. Trim leading/trailing whitespace.
2. If the FIRST non-whitespace character is '@' AND the remainder matches ^@[A-Za-z0-9_.-]+$
   (a strict alias charset: no ';', '=', '/', '@', or whitespace), it is an @alias.
3. Otherwise it is inline-or-${...}.
4. @alias is resolved BEFORE ${...} expansion (the alias may return a value that itself contains ${...}).
5. In lightweight mode an @alias is a HARD error:
   "Connection '@X' is an alias and requires full mode (a configured control database). Supply an
    inline connection string or a ${...} secret reference instead."
```

Because a real connection string always contains `;` and `=`, and an AAD UPN only appears inside such a string (never as the first token), `@alias` can never collide with connection content. `${...}` is never mistaken for an alias because it does not start with `@`.

### 1.4 Secretless inline strings (closes "secrets can rest in YAML")

An inline connection string is validated at the trust boundary by ONE canonicalize-then-inspect routine (section 6.1, `InspectSecretless`), which both the resolver inline path and the control-plane write path call (Single Code Path; this is M7). The routine does NOT substring-match raw text. It parses the value through `SqlConnectionStringBuilder` first, then inspects the parsed properties:

```
Let b = new SqlConnectionStringBuilder(value).  (throws on malformed input at the trust boundary)

Rule 1 (ALWAYS wins, H1 + H2): if !string.IsNullOrEmpty(b.Password) -> REJECT.
        The builder normalizes Password / Pwd / PWD and any surrounding whitespace
        ("Password =Hunter2", "Pwd\t= x") into one non-empty b.Password property, so a
        whitespace-bypass is impossible.

Rule 2 (only after Rule 1 passes): inspect the parsed b.Authentication enum value (NOT a
        string prefix). Inline is allowed ONLY for the passwordless modes:
          - SqlAuthenticationMethod.ActiveDirectoryDefault
          - SqlAuthenticationMethod.ActiveDirectoryManagedIdentity
          - SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity
        OR Integrated Security=true (b.IntegratedSecurity == true) with no Authentication.
        ActiveDirectoryPassword and ActiveDirectoryServicePrincipal are REJECTED inline
        (they carry a resting secret in Password even though they are "Active Directory *");
        they must be supplied as a whole-string ${...} ref instead.

A value that is itself a single ${...} ref is accepted without parsing (it resolves later).
```

Precedence is explicit: rule 1 wins over rule 2, so an `Active Directory Service Principal` or `Active Directory Password` string with a populated `Password` is rejected even though it matches "Active Directory". This makes architecture goal 7 ("secrets never live in YAML") enforceable, not aspirational.

Deliberate policy on embedded refs (L3): `SecretResolver.ResolveAsync` does in-place substitution, so `...;Password=${keyvault:kv/pw}` is technically resolvable. The validator still rejects it (the whole value is not a pure `${...}` ref). A developer who needs SQL auth puts the ENTIRE connection string in one `${env:...}` / `${keyvault:...}` secret. Whole-string ref is intentionally safer than embedded-ref-then-recheck: the file never contains the keyword `Password=` at all, and there is no resting-secret substring to mis-detect.

---

## 2. Trust chain and credential model

### 2.1 The chain

```
Ambient secretless trust root            (Managed Identity / federated workload identity / env SP)
        |  IAzureCredentialFactory.Create()  -> DefaultAzureCredential chain (existing)
        v
Key Vault                                 read under the ambient identity, suffix per cloud (6.6)
        |  ${keyvault:vault/secret}  -> connection string OR downstream-SP secret
        v
Connection string                         Authentication=Active Directory Default (passwordless) is the default
        |
        v
SqlConnection                             token owned by SqlClient/Azure.Identity on the connect path
```

There is ONE secret-bearing identity in the whole system: the ambient root. It never has a secret at rest in V3 (it is the platform-injected identity). Everything else is either passwordless (AAD on the connection string) or a reference resolved through that root at runtime.

### 2.2 `CredentialProfile` and the two auth paths

Two and only two ways a `ResolvedConnection` authenticates, decided by `CredentialProfile.Mode`:

1. **`ConnectionStringAuth` (default, canonical).** The canonical string carries `Authentication=Active Directory Default` (or `... Managed Identity`, `... Workload Identity`). SqlClient acquires and caches the AAD token itself. No `AccessToken` is set. This is the only path that pools cleanly (section 6.2). This is the default for `MSSQL` and `AZDB`.

2. **`InjectedToken` (cross-tenant / downstream SP only).** Used solely when a `DataSource` binds a downstream SP in a foreign tenant that the ambient identity cannot directly authenticate as. Here:
   - the ambient identity reads the downstream SP's secret (or a federated assertion) from Key Vault via `${keyvault:...}`,
   - the factory builds a per-alias `ClientSecretCredential(tenantId, clientId, secret)` and acquires a token for the cloud-appropriate SQL audience (`.default` scope, section 6.6),
   - the token is set on `SqlConnection.AccessToken` BEFORE open,
   - the canonical string for these aliases carries NO `Authentication=` keyword and no `Integrated Security=true` (combining either with `AccessToken` throws at the property-assignment site inside `OpenAsync`, before open: this is a useful fail-fast, L2),
   - this is the ONLY site in the codebase that sets `AccessToken`. The token participates in the pool key by design, so these aliases pool per-token (acceptably narrow, because there are few of them).

Also valid through the same canonical string path: `Integrated Security=true` (on-prem domain, `CredentialMode.Integrated`) and a fully secret-resolved legacy SQL-auth string supplied as a whole `${...}` ref (`CredentialMode.InlineConnectionString`).

### 2.3 Hardening legacy `flw.SysServicePrincipal`

Legacy stored `ClientSecret nvarchar(100)` in PLAINTEXT with NO masking clause (verified: `flw.SysServicePrincipal.sql` line 8 has no `MASKED WITH`). It was the most sensitive secret in the schema and was visible to any reader, in backups, and in the transaction log. The masking the original system did apply was on OTHER columns: `SysDataSource.ConnectionString`, `SysAPIKey.SecretKey`, and `SysSourceControlType.AccessToken` / `ConsumerSecret`. V3 eliminates the resting-secret class entirely:

- **`ClientSecret` column is dropped.** Disposition: "secret must never rest in the control DB." Not a silent drop, a documented one (section 5.3). Because the plaintext was unmasked, the lift is fail-closed by design (section 5.4): a row whose secret has not been moved into Key Vault is non-functional until `ClientSecretRef` is hand-authored.
- The ambient identity reads Key Vault. For path 1 there is no downstream secret at all (passwordless AAD). For path 2 the downstream secret lives only in Key Vault and is fetched transiently with a TTL and evict-on-auth-failure (section 6.4); it never touches the control DB, YAML, logs, traces, or exceptions (section 6.5).
- `flw.SysAPIKey.SecretKey` (which WAS masked) and the `flw.SysSourceControlType` secret tokens `AccessToken` and `ConsumerSecret` (both masked) get the identical treatment: dropped from the DB, replaced by a `${keyvault:...}` locator column that continues their existing `*SecretName` / `KeyVaultSecretName` siblings (these locator columns already existed; V3 continues them, it does not invent them). `SysSourceControlType.ConsumerKey` is NOT a secret (it is unmasked in the legacy SQL) and is moved to non-secret feature config. These are siblings of the same `ConnectionRef`/credential resolver, not separate code paths.

---

## 3. C# model

The pure value types live in `SqlFlow.Core` (resolved decision; see section 9 and L4). They have no non-Core dependencies, and the provider interfaces that reference them already live in Core, so this avoids a `Core -> Connections` reference edge. The resolver and store IMPLEMENTATIONS may live in their own project, but the contracts and value types are Core.

```csharp
namespace SqlFlow.Core.Connections;

/// <summary>How a resolved connection authenticates. ConnectionStringAuth is the default and the only
/// pooling-friendly path; InjectedToken is reserved for cross-tenant downstream service principals.</summary>
public enum CredentialMode
{
    /// <summary>Authentication=Active Directory * (passwordless) lives in the connection string; SqlClient owns the token.</summary>
    ConnectionStringAuth,
    /// <summary>Integrated Security=true (on-prem domain). No token, no secret.</summary>
    Integrated,
    /// <summary>A full, already-secret-resolved connection string supplied as a whole ${...} ref (e.g. legacy SQL auth).</summary>
    InlineConnectionString,
    /// <summary>A per-alias token is acquired for a foreign tenant SP and set on SqlConnection.AccessToken.</summary>
    InjectedToken,
}

/// <summary>Faithful 1:1 of the three legacy SysDataSource.SourceType values (MySQL/MSSQL/AZDB).
/// There is no Synapse value here: Synapse-ness is carried by DataSourceCapabilities.IsSynapse only (H6).</summary>
public enum DataSourceKind { MSSQL, AZDB, MySQL }

/// <summary>Capability metadata that drives code generation. All flags are non-null with a safe default
/// so generators never branch on an unknown tri-state.</summary>
public sealed record DataSourceCapabilities
{
    public bool IsSynapse { get; init; }            // legacy IsSynapse: gates READPAST vs NOLOCK, disable-index path
    public bool SupportsCrossDbRef { get; init; }   // legacy SupportsCrossDBRef: 3-part names in generated SQL
    public bool IsLocal { get; init; }              // legacy IsLocal: same-server cross-DB vs network bulk copy
    public bool ActivityMonitoring { get; init; }   // legacy ActivityMonitoring: per-connection DMV polling toggle
}

/// <summary>Storage staging context bound to the credential (legacy StorageAccountName/BlobContainer),
/// surfaced so file-source ingestion stages blobs under the same identity. Empty when not applicable.</summary>
public sealed record StorageContext
{
    public string? StorageAccountName { get; init; }  // legacy SysServicePrincipal.StorageAccountName
    public string? BlobContainer { get; init; }       // legacy SysServicePrincipal.BlobContainer
    /// <summary>NEW V3 field (no legacy origin). Sovereign-cloud aware; null = runtime-derived from the
    /// detected cloud (section 6.6). It is NOT persisted in flw.CredentialProfile (M6).</summary>
    public string? BlobEndpointSuffix { get; init; }
}

/// <summary>The downstream/cross-tenant SP binding. Null for the common passwordless case. The secret is a
/// LOCATOR (${keyvault:...}), never the secret itself.</summary>
public sealed record CredentialProfile
{
    public CredentialMode Mode { get; init; } = CredentialMode.ConnectionStringAuth;
    public string? TenantId { get; init; }          // legacy TenantId: foreign tenant for InjectedToken
    public string? ClientId { get; init; }          // legacy ApplicationId: app id for InjectedToken
    public string? ClientSecretRef { get; init; }   // ${keyvault:vault/secret}; resolved transiently, never stored
    public string? KeyVaultName { get; init; }      // legacy KeyVaultName: locator only
    public string? KeyVaultUriOverride { get; init; } // full vault URI bypasses suffix guessing (sovereign/private DNS)
}

/// <summary>A registry entry (the V3 equivalent of one flw.SysDataSource row joined to its SP). Immutable;
/// safe to cache by alias in the singleton store.</summary>
public sealed record DataSource
{
    public required string Alias { get; init; }                 // legacy Alias: the @alias key
    public required DataSourceKind Kind { get; init; }          // legacy SourceType (1:1, no Synapse)
    public string? Host { get; init; }                          // legacy DatabaseName (misnomer): display/diagnostics only
    /// <summary>Either an inline (secretless) connection string or a ${...} ref. Never a raw secret.</summary>
    public required string ConnectionRef { get; init; }         // legacy ConnectionString, converted to ref/passwordless
    public DataSourceCapabilities Capabilities { get; init; } = new();
    public CredentialProfile Credential { get; init; } = new();
    public StorageContext Storage { get; init; } = new();
}

/// <summary>A classified, not-yet-resolved connection reference.</summary>
public readonly record struct ConnectionRef
{
    public ConnectionRefKind Kind { get; }
    public string Value { get; }   // alias name (no '@'), or the inline/${...} text
    private ConnectionRef(ConnectionRefKind kind, string value) { Kind = kind; Value = value; }

    public static ConnectionRef Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var s = raw.Trim();
        if (s.Length == 0)
            throw new SqlFlowException("A connection reference must not be empty.");
        if (s[0] == '@')
        {
            var alias = s[1..];
            if (!IsValidAlias(alias))
                throw new SqlFlowException($"Invalid connection alias '{s}'. Allowed: @ followed by letters, digits, '_', '.', '-'.");
            return new ConnectionRef(ConnectionRefKind.Alias, alias);
        }
        return new ConnectionRef(ConnectionRefKind.Inline, s); // ${...} is expanded downstream
    }

    private static bool IsValidAlias(string a)
        => a.Length > 0 && a.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}

public enum ConnectionRefKind { Alias, Inline }

/// <summary>The single output of resolution. Carries the openable string (CanonicalString, treated as a
/// secret) and the always-safe-to-log RedactedString, plus capabilities and credential plan. The cache key
/// for pooling/logging is RedactedString; the open uses CanonicalString.</summary>
public sealed record ResolvedConnection
{
    public required string CanonicalString { get; init; }   // SECRET: never logged/traced/thrown
    public required string RedactedString { get; init; }    // safe: Password/User ID stripped (L1)
    public required DataSourceKind Kind { get; init; }
    public DataSourceCapabilities Capabilities { get; init; } = new();
    public CredentialProfile Credential { get; init; } = new();
    public StorageContext Storage { get; init; } = new();

    public override string ToString() => RedactedString; // makes accidental interpolation safe by construction
}
```

The `ResolvedConnection.ToString()` returning the redacted string is a deliberate safety net: any `$"... {resolved}"` interpolation that slips into a log or exception leaks nothing. The `RedactedString` removes exactly the secret-bearing keywords the builder can hold: `Password`, `Pwd`, `User ID`, `UID` (L1). Access tokens are never in the string (they live only on `SqlConnection.AccessToken`), and `Access Token` is not a `SqlConnectionStringBuilder` keyword, so there is nothing to strip for it.

---

## 4. Abstractions and DI

### 4.1 Interfaces

The contracts below live in `SqlFlow.Core.Connections`. `IConnectionFactory.OpenAsync` returns `System.Data.Common.DbConnection` (resolved decision, supports the MySQL source path of locked decision 1), so this contract stays provider-neutral and Core stays free of `Microsoft.Data.SqlClient`. `ITokenCredentialCache` (which touches `Azure.Core.AccessToken`) lives in `SqlFlow.Azure`, NOT in Core and NOT in `SqlFlow.SqlServer` (M2/M5; section 9).

```csharp
namespace SqlFlow.Core.Connections;

/// <summary>Resolves an @alias to a DataSource. Two backends: NullDataSourceStore (lightweight, throws on
/// any alias) and SqlDataSourceStore (full, one indexed seek). Caches immutable DataSource records only.</summary>
public interface IDataSourceStore
{
    /// <summary>True only in full mode; lets the resolver give a precise error for @alias in lightweight mode.</summary>
    bool SupportsAliases { get; }
    Task<DataSource> ResolveAsync(string alias, CancellationToken ct = default); // unknown alias -> hard SqlFlowException
}

/// <summary>The ONLY way to turn a raw connection reference into a ResolvedConnection. One pipeline,
/// both modes, both roles. Resolves @alias (full only) -> ${...} expansion -> canonicalization +
/// secretless inspection -> capability + credential plan.</summary>
public interface IConnectionResolver
{
    Task<ResolvedConnection> ResolveAsync(string rawReference, ConnectionRole role, CancellationToken ct = default);
}

public enum ConnectionRole { Source, Target } // stamps ApplicationName "SQLFlow Source"/"SQLFlow Target"

/// <summary>The ONLY way to obtain an open DbConnection. Opens a fresh physical/pooled connection per call;
/// NEVER caches or returns a shared connection object (stateless/reentrant invariant). For InjectedToken
/// aliases it sets AccessToken on a SqlConnection; otherwise SqlClient/MySql owns auth via the connection
/// string. Returns SqlConnection (MSSQL/AZDB) or MySqlConnection (MySQL) as a DbConnection (H5).</summary>
public interface IConnectionFactory
{
    Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default);
}
```

The async `ISecretProvider.ResolveAsync` / `ISecretResolver.ResolveAsync` (M1) feed the resolver's `${...}` step, so the await chain is real end to end and no thread-pool thread blocks on a synchronous Key Vault read (section 6.6).

The SQL Server target downcast (H5): SqlServer providers need a concrete `SqlConnection` (`SqlBulkCopy` requires it; `SqlCommand`/`(SqlTransaction)` require it). They obtain it through ONE guarded helper, not ad-hoc casts:

```csharp
namespace SqlFlow.SqlServer;

internal static class SqlServerConnectionGuard
{
    /// <summary>Single downcast site. The target is always SQL Server by design, so this is always valid;
    /// a wrong-provider connection fails fast with a clear, alias-named SqlFlowException, never a raw
    /// InvalidCastException.</summary>
    public static SqlConnection AsSqlConnection(DbConnection open, ResolvedConnection resolved)
        => open as SqlConnection
           ?? throw new SqlFlowException(
                $"Target '{resolved.RedactedString}' resolved to a '{open.GetType().Name}' but a SQL Server " +
                $"connection was required (Kind={resolved.Kind}). The SQL Server target must be MSSQL or AZDB.");
}
```

### 4.2 The provider-signature decision (firm)

**Decision: change every SQL Server provider from `string connectionString` to `ResolvedConnection connection`, and have each provider open via the injected `IConnectionFactory`, then downcast through `SqlServerConnectionGuard.AsSqlConnection` at the single guarded site.** The exact count (L5) is 10 methods across 7 interfaces:

| Interface | Methods changed |
|---|---|
| `ISchemaProvider` | 2 |
| `IBulkLoader` | 2 |
| `IIndexManager` | 2 |
| `IDesiredIndexManager` | 1 |
| `IIncrementalProbe` | 1 |
| `IColumnProfiler` (`ProfileAsync`) | 1 |
| `IInferenceValidator` (`CheckAsync`) | 1 |

`IDdlGenerator.Generate(TargetSpec, SchemaDelta)` is deliberately UNTOUCHED: it generates text and never opens a connection (L5). It still receives capability flags via the `SchemaDelta`/`TargetSpec` path it already has, and its branching is driven by `ResolvedConnection.Capabilities` passed by the caller, not by the generator opening anything.

Justification, point by point against the critic:

- **Single Code Path.** If the factory is added but providers keep `string connectionString` and `new SqlConnection(...)`, there are two ways to get a live connection (raw-string-open vs factory). Threading `ResolvedConnection` plus factory makes the factory the sole open site. Providers stop owning connection-string text.
- **Capability carrier (closes "flags are decorative").** `ResolvedConnection.Capabilities` rides into the DDL generator and index manager. `SqlServerDdlGenerator` branches on `IsSynapse`/`SupportsCrossDbRef`; `SqlServerIndexManager.DisableNonClusteredAsync` is gated off when `IsSynapse` (Synapse has no disable/rebuild-nonclustered). This is the only channel that makes the legacy flags actually drive code generation.
- **Leak safety.** Providers can only ever log `connection.RedactedString`; the openable string is reachable only inside `IConnectionFactory.OpenAsync`.
- **Token vs string auth.** Providers do not care which auth path is used; the factory hides it. `InjectedToken` aliases get their `AccessToken` set inside `OpenAsync` and nowhere else.

Rejected alternative: keep `string` and pass capabilities as a side parameter. Rejected because it leaves the raw-string open site in every provider (two pathways) and re-introduces the leak surface.

### 4.3 DI lifetimes (CLI composition root)

All singletons, matching the existing root. Safe because every cache holds only immutable artifacts:

```csharp
services.AddSingleton<IAzureCredentialFactory, AzureCredentialFactory>();       // existing (SqlFlow.Azure)
services.AddSingleton<ISecretProvider, EnvSecretProvider>();                    // existing (sync + async)
services.AddSingleton<ISecretProvider, AzureKeyVaultSecretProvider>();          // existing, hardened (6.6), async
services.AddSingleton<ISecretResolver, SecretResolver>();                       // existing, now exposes ResolveAsync

// Lightweight default. Full mode swaps the next line for SqlDataSourceStore.
services.AddSingleton<IDataSourceStore, NullDataSourceStore>();
services.AddSingleton<IConnectionResolver, ConnectionResolver>();

// Factory + InjectedToken plumbing. ITokenCredentialCache lives in SqlFlow.Azure (M2).
services.AddSingleton<ITokenCredentialCache, TokenCredentialCache>();           // SqlFlow.Azure
services.AddSingleton<IConnectionFactory, SqlConnectionFactory>();              // SqlFlow.SqlServer; opens MSSQL/AZDB
services.AddSingleton<MySqlConnectionFactory>();                                // SqlFlow.MySql; opens MySQL sources
```

`SqlConnectionFactory` is the factory the DI root binds to `IConnectionFactory`; it delegates to `MySqlConnectionFactory` when `resolved.Kind == MySQL` (one composite open site, no dead branch; see 4.4). `MySqlConnectionFactory` is registered because it is actually called by that delegation, so there is no uncalled factory (no-stub principle).

**Stateless/reentrant invariant (closes the shared-cache finding):** the singleton factory caches ONLY immutable values: `alias -> DataSource`, `locator -> Lazy<Task<string>>` (the cached secret read), `vault -> SecretClient`, `(tenant,client) -> cached token`. It NEVER caches or returns a `DbConnection`. Each `OpenAsync` creates and opens a brand-new connection that returns to the ADO.NET pool on dispose. Physical reuse comes from the pool, never from object sharing. A concurrency test runs N flows on one alias in parallel and asserts no interleaving and no shared transaction state.

### 4.4 Relational source path: `AdoSourceReader`, `MySqlConnectionFactory`, and the `ISourceReader` change (locked decision 1)

MySQL is wired now and is only ever a SOURCE (SQL Server is always the target). This brings the relational-source path fully into scope, so there is no dead `ConnectionRole.Source` branch and no uncalled MySQL factory.

What changes:

1. **`ISourceReader` gains an engine-resolved connection.** Today the file readers' `GetColumnsAsync`/`OpenAsync` take only `SourceSpec`. A relational reader needs the engine-resolved `ResolvedConnection` (resolved once, with `ConnectionRole.Source`). The minimal, single-path change is to add an optional resolved connection that the engine supplies for relational sources:

```csharp
namespace SqlFlow.Core.Sources;

public interface ISourceReader
{
    bool CanHandle(SourceSpec spec);

    /// <summary>resolved is non-null ONLY for relational sources (the engine resolved SourceSpec.Location with
    /// ConnectionRole.Source); file readers ignore it. This keeps one signature for all readers (Single Code Path).</summary>
    Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec spec, ResolvedConnection? resolved, CancellationToken ct = default);
    Task<ISourceRowStream> OpenAsync(SourceSpec spec, ResolvedConnection? resolved, CancellationToken ct = default);
}
```

   File readers (`Csv/Xls/Json/Xml/Parquet`) keep their behavior and simply ignore `resolved`. The engine resolves a source connection only when the source kind is relational (`type: ado`), so file sources never pay for resolution.

2. **`AdoSourceReader` (new, in `SqlFlow.Sources`).** It handles `type: ado`. It opens through the SAME injected `IConnectionFactory.OpenAsync(resolved, ct)` and consumes the returned `DbConnection` GENERICALLY via `DbCommand`/`DbDataReader`, so it works for both a `SqlConnection` (SQL Server source) and a `MySqlConnection` (MySQL source) with no provider-specific code:

```csharp
namespace SqlFlow.Sources;

public sealed class AdoSourceReader(IConnectionFactory factory) : ISourceReader
{
    public bool CanHandle(SourceSpec spec) => string.Equals(spec.Type, "ado", StringComparison.OrdinalIgnoreCase);

    public async Task<ISourceRowStream> OpenAsync(SourceSpec spec, ResolvedConnection? resolved, CancellationToken ct = default)
    {
        if (resolved is null)
            throw new SqlFlowException("An 'ado' source requires a resolved connection (alias, ${...} ref, or inline). None was provided.");
        var query = spec.Options.GetRequired("query");
        DbConnection conn = await factory.OpenAsync(resolved, ct).ConfigureAwait(false);   // SqlConnection or MySqlConnection
        DbCommand cmd = conn.CreateCommand();                                              // generic, provider-neutral
        cmd.CommandText = query;
        var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        return new DbDataReaderRowStream(conn, cmd, reader);                               // disposes conn -> back to pool
    }

    public async Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec spec, ResolvedConnection? resolved, CancellationToken ct = default)
    {
        await using var stream = await OpenAsync(spec, resolved, ct).ConfigureAwait(false);
        return stream.Columns; // derived from DbDataReader.GetColumnSchema()
    }
}
```

3. **`MySqlConnectionFactory` (new, in a new `SqlFlow.MySql` project).** It builds and opens a `MySqlConnection` and returns it as `DbConnection`. Per the project rule to reuse the libraries the original SQLFlow used, it uses `MySql.Data` (the legacy library), not `MySqlConnector`, unless a concrete defect forces the switch. The composite `SqlConnectionFactory` routes by kind:

```csharp
// inside SqlConnectionFactory.OpenAsync (the IConnectionFactory binding):
public async Task<DbConnection> OpenAsync(ResolvedConnection r, CancellationToken ct)
    => r.Kind switch
    {
        DataSourceKind.MySQL                          => await _mySql.OpenAsync(r, ct).ConfigureAwait(false),
        DataSourceKind.MSSQL or DataSourceKind.AZDB   => await OpenSqlServerAsync(r, ct).ConfigureAwait(false),
        _ => throw new SqlFlowException($"Unsupported connection kind '{r.Kind}' for '{r.RedactedString}'.")
    };
```

   Every arm does real work; there is no fall-through stub. MySQL canonicalization/redaction uses `MySqlConnectionStringBuilder` (the MySQL analogue of section 6.1): reject a non-empty `Password`, strip it for the redacted form.

4. **Engine wiring.** `FlowRunner` resolves `SourceSpec.Location` with `ConnectionRole.Source` ONLY when the source is relational, stores the `ResolvedConnection` in `RunContext`, and passes it to `ISourceReader.OpenAsync`/`GetColumnsAsync`. `SourceSpec.Location` becomes `required string` so it is symmetric with `TargetSpec.Connection` (the file readers already require a path). `FlowRunner.ResolveReader` now also matches `AdoSourceReader` for `type: ado`, so the section 8 `type: ado` example runs end to end.

---

## 5. Full-mode control DB schema

### 5.1 DDL

Schema `flw`. The hot path is one indexed seek per alias; capability columns are denormalized onto the same row (matching legacy locality). All capability bits are `NOT NULL DEFAULT 0` so code generation never sees a NULL tri-state. `DataSourceId`/`CredentialProfileId` stay as IDENTITY surrogates (locked decision 2); `Alias` is the public UNIQUE key.

Column widths are deliberately widened from the legacy sizes (L8): legacy `Alias nvarchar(50)`, `ServicePrincipalAlias nvarchar(70)`, `DatabaseName nvarchar(50)` become `nvarchar(128)`/`nvarchar(256)`. These widenings are safe and documented.

```sql
CREATE TABLE flw.DataSource
(
    DataSourceId       int IDENTITY(1,1) NOT NULL,           -- internal surrogate for FK targets only (kept)
    Alias              nvarchar(128)     NOT NULL,           -- legacy Alias (nvarchar 50, widened); the @alias key
    Kind               tinyint           NOT NULL,           -- 0 MSSQL, 1 AZDB, 2 MySQL (legacy SourceType, 1:1, no Synapse)
    Host               nvarchar(256)     NULL,               -- legacy DatabaseName (misnomer, nvarchar 50, widened): diagnostics only
    -- A ${...} reference OR a passwordless inline string. Already canonicalized on write. CHECK is defense-in-depth.
    ConnectionRef      nvarchar(2000)    NOT NULL,           -- legacy ConnectionString (was MASKED), converted
    CredentialAlias    nvarchar(128)     NULL,               -- legacy ServicePrincipalAlias -> flw.CredentialProfile.Alias
    IsSynapse          bit               NOT NULL CONSTRAINT DF_DataSource_IsSynapse        DEFAULT 0,
    SupportsCrossDbRef bit               NOT NULL CONSTRAINT DF_DataSource_SupportsCrossDb  DEFAULT 0,
    IsLocal            bit               NOT NULL CONSTRAINT DF_DataSource_IsLocal          DEFAULT 0,
    ActivityMonitoring bit               NOT NULL CONSTRAINT DF_DataSource_ActivityMon      DEFAULT 0,
    RowVersion         rowversion        NOT NULL,           -- optimistic concurrency for apply/sync
    CONSTRAINT PK_DataSource PRIMARY KEY CLUSTERED (DataSourceId),
    CONSTRAINT UQ_DataSource_Alias UNIQUE (Alias),           -- enforces the public alias key (legacy had none)
    -- Defense-in-depth only. The authoritative gate is InspectSecretless in code (6.1 / M7). The CHECK is
    -- whitespace- and collation-tolerant so it cannot be bypassed by "password =" or casing (H1).
    CONSTRAINT CK_DataSource_NoRestingSecret CHECK
        (ConnectionRef LIKE '${%}' OR
         (REPLACE(LOWER(ConnectionRef) COLLATE Latin1_General_CI_AS, ' ', '') NOT LIKE '%password=%' AND
          REPLACE(LOWER(ConnectionRef) COLLATE Latin1_General_CI_AS, ' ', '') NOT LIKE '%pwd=%'))
);
CREATE NONCLUSTERED INDEX IX_DataSource_Alias ON flw.DataSource (Alias)
    INCLUDE (Kind, ConnectionRef, CredentialAlias, IsSynapse, SupportsCrossDbRef, IsLocal, ActivityMonitoring);

CREATE TABLE flw.CredentialProfile
(
    CredentialProfileId int IDENTITY(1,1) NOT NULL,          -- internal surrogate (kept)
    Alias               nvarchar(128)     NOT NULL,          -- legacy ServicePrincipalAlias (nvarchar 70, widened)
    Mode                tinyint           NOT NULL DEFAULT 0, -- 0 ConnectionStringAuth, 1 Integrated, 2 InlineConnectionString, 3 InjectedToken
    TenantId            nvarchar(100)     NULL,              -- legacy TenantId (InjectedToken only)
    ClientId            nvarchar(100)     NULL,              -- legacy ApplicationId (InjectedToken only)
    ClientSecretRef     nvarchar(512)     NULL,              -- ${keyvault:vault/secret}; NEVER a secret value
    KeyVaultName        nvarchar(256)     NULL,              -- legacy KeyVaultName: locator only
    KeyVaultUriOverride nvarchar(512)     NULL,              -- sovereign-cloud / private-DNS full URI
    StorageAccountName  nvarchar(256)     NULL,              -- legacy StorageAccountName, kept bound to credential
    BlobContainer       nvarchar(256)     NULL,              -- legacy BlobContainer
    RowVersion          rowversion        NOT NULL,
    CONSTRAINT PK_CredentialProfile PRIMARY KEY CLUSTERED (CredentialProfileId),
    CONSTRAINT UQ_CredentialProfile_Alias UNIQUE (Alias),
    CONSTRAINT CK_CredentialProfile_NoRestingSecret CHECK
        (ClientSecretRef IS NULL OR ClientSecretRef LIKE '${%}')
);
CREATE NONCLUSTERED INDEX IX_CredentialProfile_Alias ON flw.CredentialProfile (Alias)
    INCLUDE (Mode, TenantId, ClientId, ClientSecretRef, KeyVaultName, KeyVaultUriOverride, StorageAccountName, BlobContainer);
```

There is no `ClientSecret`, `SecretKey`, `AccessToken`, or `ConsumerSecret` column anywhere. `BlobEndpointSuffix` is intentionally NOT a column (M6): it is a NEW V3 field with no legacy origin and is runtime-derived from the cloud detector (section 6.6). The `CK_*_NoRestingSecret` constraints make a resting secret a write-time failure in defense-in-depth (the authoritative gate is the code routine of 6.1). Dynamic data masking is unnecessary because the columns hold only references; the references are themselves not sensitive.

### 5.2 Loading strategy

The full-mode resolution query is a single keyed seek that joins the two tables by alias:

```sql
SELECT ds.Alias, ds.Kind, ds.Host, ds.ConnectionRef,
       ds.IsSynapse, ds.SupportsCrossDbRef, ds.IsLocal, ds.ActivityMonitoring,
       cp.Mode, cp.TenantId, cp.ClientId, cp.ClientSecretRef,
       cp.KeyVaultName, cp.KeyVaultUriOverride, cp.StorageAccountName, cp.BlobContainer
FROM flw.DataSource ds
LEFT JOIN flw.CredentialProfile cp ON cp.Alias = ds.CredentialAlias
WHERE ds.Alias = @alias;
```

`IX_DataSource_Alias` makes this two index seeks. `SqlDataSourceStore` caches the resulting immutable `DataSource` keyed by alias (no TTL needed; definitions change rarely, and a control-plane `apply`/`sync` evicts on write). Unknown alias yields a hard `SqlFlowException` ("Connection alias '@X' is not registered in the control database."), never a silent NULL string, closing the legacy "no FK, silent empty connection" gap.

### 5.3 Legacy-to-V3 migration mapping (column-complete)

Verified column lists drive these rows. `SourceType` valid values per the legacy extended property are exactly `MySQL`, `MSSQL`, `AZDB`; there is no `Synapse` source value (H6). Legacy masking is recorded accurately (H8): `SysServicePrincipal.ClientSecret` was PLAINTEXT (no mask); the masked columns were `SysDataSource.ConnectionString`, `SysAPIKey.SecretKey`, `SysSourceControlType.AccessToken`, `SysSourceControlType.ConsumerSecret`.

#### 5.3.1 `flw.SysDataSource` (all 11 columns)

| Legacy column | V3 destination | Disposition |
|---|---|---|
| `DataSourceID` (PK, IDENTITY) | `DataSource.DataSourceId` | kept as internal surrogate (FK targets only); aliasing is by `Alias` |
| `SourceType` (NULL nvarchar(50); valid MySQL/MSSQL/AZDB) | `DataSource.Kind` (tinyint) | 1:1 map: `MSSQL->0`, `AZDB->1`, `MySQL->2`. NULL `SourceType` is REJECTED at migration with a clear error (no default invented). Synapse is NOT a Kind (H6). |
| `DatabaseName` (NULL nvarchar(50)) | `DataSource.Host` (nvarchar(256)) | renamed (legacy misnomer documented); diagnostics/display only; widened (L8) |
| `Alias` (NULL nvarchar(50)) | `DataSource.Alias` (nvarchar(128)) | the `@alias` key; deduped/repaired and NOT-NULL enforced before insert (M9); UNIQUE enforced; widened (L8) |
| `ConnectionString` (MASKED nvarchar(2000)) | `DataSource.ConnectionRef` | transformed to passwordless/`${...}` BEFORE insert (the CHECK rejects a resting `Password=`/`Pwd=`, H9b); legacy masking does not carry over because the value is now a reference |
| `ServicePrincipalAlias` (NULL nvarchar(70)) | `DataSource.CredentialAlias` (nvarchar(128)) | keep-as-is; now points at a secretless `CredentialProfile`; widened |
| `KeyVaultSecretName` (NULL nvarchar(250)) | folded into `ConnectionRef` as `${keyvault:<vault>/<KeyVaultSecretName>}` | the vault name is NOT on `SysDataSource`; it comes from `SysServicePrincipal.KeyVaultName` resolved via `SysDataSource.ServicePrincipalAlias` (H9c). A NULL `ServicePrincipalAlias` (no vault) is a documented migration failure for that row, not a silent default |
| `SupportsCrossDBRef` (NULL bit DEFAULT 0) | `DataSource.SupportsCrossDbRef` | `COALESCE(col,0)` (M9); NOT NULL DEFAULT 0; drives code-gen |
| `IsSynapse` (NULL bit DEFAULT 0) | `DataSource.IsSynapse` (capability only) | `COALESCE(col,0)` (M9); NOT NULL DEFAULT 0; drives READPAST/NOLOCK + disable-index gate; this is the ONLY carrier of Synapse-ness (H6) |
| `IsLocal` (NULL bit DEFAULT 0) | `DataSource.IsLocal` | `COALESCE(col,0)` (M9); NOT NULL DEFAULT 0; same-server transfer optimization |
| `ActivityMonitoring` (NULL bit, NO default) | `DataSource.ActivityMonitoring` | `COALESCE(col,0)` (M9; this is the one bit with no legacy default); per-connection DMV toggle |

#### 5.3.2 `flw.SysServicePrincipal` (all 12 columns)

| Legacy column | V3 destination | Disposition |
|---|---|---|
| `ServicePrincipalID` (PK, IDENTITY) | `CredentialProfile.CredentialProfileId` | internal surrogate (kept) |
| `ServicePrincipalAlias` (NOT NULL nvarchar(70)) | `CredentialProfile.Alias` (nvarchar(128)) | keep; UNIQUE; widened (L8) |
| `TenantId` (NULL nvarchar(100)) | `CredentialProfile.TenantId` | keep (InjectedToken only) |
| `SubscriptionId` (NULL nvarchar(100)) | (none) | DROPPED. Reason: ADF/Automation orchestration is out of scope for the V3 connection registry (M5) |
| `ApplicationId` (NULL nvarchar(100)) | `CredentialProfile.ClientId` | keep (InjectedToken only) |
| `ClientSecret` (NULL nvarchar(100), PLAINTEXT, no mask) | (none) | **DROPPED. Secret must never rest in the control DB. It was plaintext, NOT masked (H8).** Replaced by `ClientSecretRef`; see the migration prerequisite (5.4) because there is no legacy locator column to migrate from (H9) |
| `ResourceGroup` (NULL nvarchar(250)) | (none) | DROPPED. Reason: ADF/Automation out of scope (M5) |
| `DataFactoryName` (NULL nvarchar(250)) | (none) | DROPPED. Reason: ADF/Automation out of scope (M5) |
| `AutomationAccountName` (NULL nvarchar(250)) | (none) | DROPPED. Reason: ADF/Automation out of scope (M5) |
| `StorageAccountName` (NULL nvarchar(250)) | `CredentialProfile.StorageAccountName` | relocated but kept bound to the credential alias |
| `BlobContainer` (NULL nvarchar(250)) | `CredentialProfile.BlobContainer` | relocated; kept bound to alias |
| `KeyVaultName` (NULL nvarchar(250)) | `CredentialProfile.KeyVaultName` | keep as locator (vault name only); also the vault for the `SysDataSource.KeyVaultSecretName` fold-in (H9c) |

#### 5.3.3 `flw.SysAPIKey` (all 7 columns)

| Legacy column | V3 destination | Disposition |
|---|---|---|
| `ApiKeyID` (PK, IDENTITY) | `CredentialProfile.CredentialProfileId` | internal surrogate (sibling kind in the same credential resolver) |
| `ServiceType` (NOT NULL nvarchar(70)) | feature config (kind discriminator) | kept as the API-kind discriminator; not a connection field (H7) |
| `ApiKeyAlias` (NULL nvarchar(70), UNIQUE index) | `CredentialProfile.Alias` | unified into one credential resolver; UNIQUE preserved |
| `AccessKey` (NULL nvarchar(250)) | `CredentialProfile.ClientId` (analogue) | the non-secret public identifier; kept |
| `SecretKey` (MASKED nvarchar(250)) | (none) | **DROPPED. Was MASKED. Replaced by a `${keyvault:...}` locator that CONTINUES the existing `KeyVaultSecretName` (not invented), H7** |
| `ServicePrincipalAlias` (NULL nvarchar(250)) | `CredentialProfile` credential link | kept: links the API key to its owning SP credential (H7) |
| `KeyVaultSecretName` (NULL nvarchar(250)) | folded into `ClientSecretRef` as `${keyvault:<vault>/<KeyVaultSecretName>}` | this is the pre-existing locator the V3 `*Ref` continues (H7); vault from the linked SP's `KeyVaultName` |

#### 5.3.4 `flw.SysSourceControlType` (all 14 columns)

| Legacy column | V3 destination | Disposition |
|---|---|---|
| `SourceControlTypeID` (PK, IDENTITY) | source-control feature config | internal surrogate; source-control is out of scope for the connection registry (M5) |
| `SourceControlType` (NULL nvarchar(50)) | source-control feature config | kept there (e.g. GitHub/BitBucket) |
| `ServicePrincipalAlias` (NULL nvarchar(70)) | source-control feature config (credential link) | kept there |
| `SCAlias` (NULL nvarchar(50)) | source-control feature config | kept there |
| `Username` (NULL nvarchar(255)) | source-control feature config | non-secret; kept there |
| `AccessToken` (MASKED nvarchar(255)) | (none) | **DROPPED. Was MASKED. Replaced by a `${keyvault:...}` ref that CONTINUES the existing `AccessTokenSecretName` (not invented), H7** |
| `AccessTokenSecretName` (NULL nvarchar(255)) | folded into the `${keyvault:...}` access-token ref | the pre-existing locator the V3 `*Ref` continues (H7) |
| `ConsumerKey` (NULL nvarchar(255), NOT masked) | source-control feature config (non-secret) | **NOT a secret (unmasked in legacy, H7); moved to non-secret feature config, not dropped-as-secret** |
| `ConsumerSecret` (MASKED nvarchar(255)) | (none) | **DROPPED. Was MASKED. Replaced by a `${keyvault:...}` ref continuing `ConsumerSecretName` (H7)** |
| `ConsumerSecretName` (NULL nvarchar(255)) | folded into the `${keyvault:...}` consumer-secret ref | the pre-existing locator the V3 `*Ref` continues (H7) |
| `WorkSpaceName` (NULL nvarchar(255)) | source-control feature config | kept there |
| `ProjectName` (NULL nvarchar(255)) | source-control feature config | kept there |
| `ProjectKey` (NULL nvarchar(25)) | source-control feature config | kept there (H7) |
| `CreateWrkProjRepo` (NULL bit DEFAULT 0) | source-control feature config | kept there (H7) |

#### 5.3.5 Remaining legacy tables

| Legacy table.column | V3 destination | Disposition |
|---|---|---|
| `SysSourceControl.*` (incl. `Server`, `DBName`, `SCAlias`, `Batch`, `RepoName`, `ScriptToPath`, `ScriptDataForTables`) | DB-to-git scripting feature config | relocate-to-feature-config; out of scope for the connection registry (M5). Its `Server` is a name, not an alias |
| `SysAlias.*` (`System`, `SysAlias`, `Description`, `Owner`, `DomainExpert`) | governance/ownership feature config | NOT a connection table (the legacy doc states it does not enforce referential integrity); do NOT merge with `DataSource.Alias` |
| `SysDataSource` grant `SELECT TO TestUser` | (none) | dropped (demo grant) |

### 5.4 Migration prerequisites (fail-closed; H9, M9)

Because the legacy `ClientSecret` was plaintext with no locator sibling, and the CHECK constraints reject resting secrets, the migration is fail-closed and has explicit prerequisites:

1. **Move each plaintext `SysServicePrincipal.ClientSecret` into Key Vault by hand**, under a chosen secret name in the SP's `KeyVaultName`, then set `CredentialProfile.ClientSecretRef = ${keyvault:<KeyVaultName>/<chosen-name>}`. Until done, the `CredentialProfile` row is non-functional by design (fail-closed), never silently defaulted. Behavior by value: a populated `ClientSecret` requires this step before the SP is usable; a NULL `ClientSecret` migrates to a NULL `ClientSecretRef` (passwordless SP, no action needed).
2. **Transform every `SysDataSource.ConnectionString` to passwordless or whole-`${...}` form BEFORE insert.** A raw lift-and-shift of a string containing `Password=`/`Pwd=` is rejected by `CK_DataSource_NoRestingSecret` and by the code gate (6.1). This is intended.
3. **For the `SysDataSource.KeyVaultSecretName` fold-in, the vault name comes from `SysServicePrincipal.KeyVaultName` joined via `SysDataSource.ServicePrincipalAlias`** (`SysDataSource` has no `KeyVaultName` of its own). A NULL `ServicePrincipalAlias` (hence no vault) is a documented per-row migration failure, surfaced with the alias name, not a silent default.
4. **Alias dedupe/repair (M9).** Legacy `SysDataSource.Alias` is nullable with no unique constraint, so the migration dedupes and repairs aliases (and rejects NULLs) before inserting under `UQ_DataSource_Alias`.
5. **Bit COALESCE (M9).** Apply `COALESCE(col,0)` uniformly to all four legacy bits (`SupportsCrossDBRef`, `IsSynapse`, `IsLocal`, `ActivityMonitoring`), since all four are nullable in legacy.

### 5.5 Who may write the control DB (M7)

Least-privilege (section 7) gives the runtime store a SELECT-only reader. The control-plane WRITER (the account that runs `apply`/`sync` and performs INSERT/UPDATE on `flw.DataSource`/`flw.CredentialProfile`) is a SEPARATE, scoped principal with INSERT/UPDATE on exactly those two tables. The CHECK constraints exist to backstop that writer; the authoritative gate is the code routine `InspectSecretless` (6.1), which every write path calls before issuing the INSERT/UPDATE.

---

## 6. Connection pooling and runtime efficiency

### 6.1 Canonical string via `SqlConnectionStringBuilder` plus the single secretless gate (never raw string ops)

`Canonicalize` and `InspectSecretless` are the ONE place secretlessness is decided (M7). `InspectSecretless` parses through the builder and inspects the `Password` property and the parsed `Authentication` enum, never a raw substring (H1, H2):

```csharp
private (string canonical, string redacted) Canonicalize(string raw, DataSourceKind kind, ConnectionRole role)
{
    var b = new SqlConnectionStringBuilder(raw); // throws on malformed input at the trust boundary
    InspectSecretless(b);                        // H1/H2/M7: the single authoritative gate
    b.ApplicationName = role == ConnectionRole.Source ? "SQLFlow Source" : "SQLFlow Target";
    if (b.ConnectTimeout == 15) b.ConnectTimeout = 30; // deliberate, only if left at the provider default

    var canonical = b.ConnectionString; // synonyms collapsed: Server/Data Source, Database/Initial Catalog, etc.

    var safe = new SqlConnectionStringBuilder(canonical);
    safe.Remove("Password"); safe.Remove("Pwd"); safe.Remove("User ID"); safe.Remove("UID"); // L1: the full secret-bearing keyword set
    var redacted = safe.ConnectionString;
    return (canonical, redacted);
}

/// <summary>The single authoritative secretless gate. Called by the resolver inline path AND by the
/// control-plane write path (5.5). Whitespace/casing cannot bypass it (H1) because it inspects the parsed
/// builder property, not raw text.</summary>
internal static void InspectSecretless(SqlConnectionStringBuilder b)
{
    if (!string.IsNullOrEmpty(b.Password))   // Rule 1 always wins (H2): Password/Pwd/PWD + whitespace all normalize here
        throw new SqlFlowException("Inline connection strings must not contain a password. Supply the whole string as a ${...} secret reference.");

    var passwordless =
        b.Authentication is SqlAuthenticationMethod.ActiveDirectoryDefault
                         or SqlAuthenticationMethod.ActiveDirectoryManagedIdentity
                         or SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity
        || (b.Authentication == SqlAuthenticationMethod.NotSpecified && b.IntegratedSecurity);

    if (!passwordless)   // Rule 2 (H2): only passwordless AAD / Integrated may be inline
        throw new SqlFlowException(
            "Inline connection strings may use only Active Directory Default/Managed Identity/Workload Identity " +
            "or Integrated Security=true. Active Directory Password and Active Directory Service Principal carry a " +
            "resting secret and must be supplied as a whole ${...} secret reference.");
}
```

The builder collapses keyword synonyms, whitespace, and trailing semicolons, so `Server=x;Database=y` and `Data Source=x;Initial Catalog=y` produce the identical canonical pool key (a unit test asserts this). Only the builder output is ever logged; the open uses the full canonical string. A value that is itself a pure `${...}` ref is NOT parsed here; it is expanded first, then its expansion is canonicalized and inspected. The MySQL source path uses the `MySqlConnectionStringBuilder` analogue (reject non-empty `Password`, strip it for redaction).

### 6.2 Pooling vs token auth - reconciled (the central correctness fix)

The contradiction (canonical-string pooling AND a bespoke token-TTL layer) is resolved by **picking the connection-string path as canonical**:

- Default (`ConnectionStringAuth`): the canonical string carries `Authentication=Active Directory Default`. SqlClient acquires and caches the AAD token internally and keys the pool by the canonical string only. Pooling is real. **There is no bespoke per-connection token-TTL layer for SQL connections**: it would be dead code, so it does not exist.
- The TTL cache exists ONLY for Key Vault secret reads (section 6.4) and for the `InjectedToken` cross-tenant path (section 6.3).
- `InjectedToken` aliases set `SqlConnection.AccessToken`; their canonical string carries no `Authentication=` keyword and no `Integrated Security=true` (combining either throws at the property-assignment site, before open, L2). These pool per-token by design and are rare.

This makes the three reuse layers coherent: **definition** = the cached `alias -> DataSource`; **credential** = the cached Key Vault secret / cross-tenant token (TTL); **physical** = the ADO.NET pool keyed by the canonical string. No layer fights another.

### 6.3 Cross-tenant token cache (`SqlFlow.Azure`, M2)

```csharp
namespace SqlFlow.Azure;

public interface ITokenCredentialCache
{
    Task<AccessToken> GetSqlTokenAsync(string tenantId, string clientId, string clientSecretRef, CancellationToken ct);
}
```

It resolves `clientSecretRef` via `ISecretResolver.ResolveAsync` (the ambient identity reads Key Vault), builds a `ClientSecretCredential`, acquires a token for the cloud-appropriate SQL audience (`.default` scope derived from `SQLFLOW_AZURE_CLOUD`, section 6.6), and caches it keyed by `(tenant, client)` until ~5 minutes before `ExpiresOn`. On a SQL login failure for that alias the entry is evicted and re-acquired once before surfacing (bounded, see 6.4). It lives in `SqlFlow.Azure` because it touches `Azure.Identity`/`Azure.Core`; `SqlConnectionFactory` depends only on the `ITokenCredentialCache` abstraction via DI, keeping `SqlFlow.SqlServer` Azure-free for the on-prem/Integrated path. Transient secret-lifetime note (L9): `ClientSecretCredential` and the resolved secret are immutable `System.String` and cannot be zeroed; the design minimizes their lifetime (resolve, build credential, drop the reference) and never logs or stores them, but the managed-string limitation is acknowledged rather than papered over.

### 6.4 Key Vault cache contract (closes the rotation/stampede findings)

The hardened `AzureKeyVaultSecretProvider` (section 6.6) caches by **locator** with this contract:

- **Cache successes** with a bounded TTL (default 10 minutes, configurable; locked decision 4). The TTL must be `> TimeSpan.Zero`, validated at construction (M4); a zero/negative TTL throws.
- **Do not cache failures.** A `Lazy<Task<string>>` whose task faults is removed so a transient outage is not pinned (M1/M4).
- **Collapse concurrent misses** for one key with a per-locator `Lazy<Task<string>>` so N concurrent misses fire ONE async Key Vault call (no stampede / 429 storm).
- **Invalidate on auth failure:** when a SQL open fails with login-failed (18456) or Key Vault returns 401/403, the factory evicts that locator and re-fetches once before surfacing. This is a BOUNDED loop with an explicit attempt counter (at most one re-fetch); after the second failure a distinct exception surfaces. A test asserts at most two Key Vault calls and at most two opens (M4). This bounds the post-rotation stale window to a single failed open, not a full TTL outage.

### 6.5 Redaction boundary (closes the leak-in-telemetry finding)

- The ONLY connection text permitted in any `ILogger` argument, `Activity` tag, `FlowEvent`, or exception message is `ResolvedConnection.RedactedString`.
- `ResolvedConnection.ToString()` returns the redacted string, so accidental interpolation is safe by construction.
- Inline lightweight-mode strings are treated as secrets for redaction too.
- **Non-connection-string secrets (M8):** raw resolved secret values used outside a `SqlConnection` (e.g. a `SysAPIKey`-style REST key, a source-control token) are NOT connection strings, so builder-based redaction does not apply to them. For these, the rule is: a raw resolved secret value is never logged, traced, or placed in an exception; any diagnostic refers to it only by its locator (`${keyvault:...}`). The redaction test scope (below) covers them.
- A test scans every thrown factory exception's full `ToString()` (including exceptions thrown deep inside `OpenAsync` and `SqlDataSourceStore`) and every emitted `FlowEvent.Message` for the literal secret and for `password=`/`pwd=`/`access token`, asserting absence (M8 widens this to the new `SqlConnectionFactory`/`SqlDataSourceStore` code).

### 6.6 Specific edit to `AzureKeyVaultSecretProvider.cs` (async, cloud-aware, bounded; M1/M3/M4)

Replace the synchronous, suffix-hardcoded (`.vault.azure.net`, verified line 39), re-fetch-every-call body with an async, cloud-aware, cached, stampede-collapsing one. The async path is end to end so no thread-pool thread blocks (M1):

```csharp
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly TokenCredential _credential;
    private readonly string _vaultSuffix;     // from SQLFLOW_AZURE_CLOUD (6.6); default ".vault.azure.net"
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, SecretClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (Lazy<Task<string>> task, DateTimeOffset expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Scheme => "keyvault";

    public AzureKeyVaultSecretProvider(TokenCredential credential, string vaultSuffix, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero) throw new SqlFlowException("Key Vault cache TTL must be greater than zero."); // M4
        _credential = credential; _vaultSuffix = vaultSuffix; _ttl = ttl;
    }

    public async Task<string> ResolveAsync(string locator, CancellationToken ct = default)
    {
        var (vaultUri, secretName) = ParseLocator(locator); // honors KeyVaultUriOverride / full URI / suffix
        // Iterative re-add (NOT recursion, M4): runs at most once on expiry.
        var entry = _cache.GetOrAdd(locator, _ => (NewRead(vaultUri, secretName, ct), DateTimeOffset.UtcNow + _ttl));
        if (entry.expires <= DateTimeOffset.UtcNow)
        {
            var fresh = (NewRead(vaultUri, secretName, ct), DateTimeOffset.UtcNow + _ttl);
            _cache.TryUpdate(locator, fresh, entry);
            entry = _cache[locator];
        }
        try { return await entry.task.Value.ConfigureAwait(false); }   // collapses concurrent misses to one call
        catch { _cache.TryRemove(locator, out _); throw; }             // M1/M4: never cache a failure
    }

    private Lazy<Task<string>> NewRead(string vaultUri, string secretName, CancellationToken ct)
        => new(() => ReadAsync(vaultUri, secretName, ct), LazyThreadSafetyMode.ExecutionAndPublication);

    private async Task<string> ReadAsync(string vaultUri, string secretName, CancellationToken ct)
    {
        var client = _clients.GetOrAdd(vaultUri, u => new SecretClient(new Uri(u), _credential));
        var secret = await client.GetSecretAsync(secretName, cancellationToken: ct).ConfigureAwait(false);
        return secret.Value.Value;
    }

    public void Invalidate(string locator) => _cache.TryRemove(locator, out _); // called by factory on auth failure (6.4)
}
```

Sovereign cloud is NET-NEW code (M3), not a reuse of any legacy detector: the legacy `AzureEnvironmentDetector.cs` actually defines `CloudEnvironmentDetector`, which only distinguishes Azure/AWS/Unknown and has no sovereign concept, no authority hosts, and no vault-suffix mapping. V3 derives the Key Vault DNS suffix and the SQL token audience from an explicit setting `SQLFLOW_AZURE_CLOUD = public | usgov | china` (default `public`):

| `SQLFLOW_AZURE_CLOUD` | Key Vault suffix | SQL token audience |
|---|---|---|
| `public` (default) | `.vault.azure.net` | `https://database.windows.net/.default` |
| `usgov` | `.vault.usgovcloudapi.net` | `https://database.usgovcloudapi.net/.default` |
| `china` | `.vault.azure.cn` | `https://database.chinacloudapi.cn/.default` |

`KeyVaultUriOverride` (or a full-URI locator) is the escape hatch for private DNS and bypasses suffix derivation entirely. There is a test per suffix (M3).

### 6.7 Specific edits to `FlowRunner.cs` and `InferenceService.cs` (resolve-once, single open site; H3)

Today `_secrets.Resolve(flow.Target.Connection)` runs twice in `FlowRunner` (line 88 in `RunAsync`, line 286 in `PlanCoreAsync`, both verified). And `InferenceService.cs` (line 95, verified) is a SECOND, independent resolve-and-open path: it calls `_secrets.Resolve(request.Connection)` and threads the raw string into `_schema.GetTableSchemaAsync`, `_profiler.ProfileAsync`, and `_validator.CheckAsync`. Both must be corrected, or the single-open-site invariant is false.

`FlowRunner.cs`:

- Add `IConnectionResolver _resolver` and `IConnectionFactory _factory` to the constructor; remove the `ISecretResolver _secrets` direct use for connections (it now lives behind the resolver).
- Resolve the target EXACTLY ONCE at the top of `RunAsync`:
  ```csharp
  var resolved = await _resolver.ResolveAsync(flow.Target.Connection, ConnectionRole.Target, ct).ConfigureAwait(false);
  ```
- When the source is relational (`type: ado`), resolve the source EXACTLY ONCE with `ConnectionRole.Source` and store it in `RunContext`; file sources skip this (section 4.4).
- Store `resolved` in the per-run `RunContext` (the per-run isolation that keeps reentrancy intact) and pass it into `PlanCoreAsync`. **Delete the `_secrets.Resolve` call on line 286**; `PlanCoreAsync` takes the already-resolved value.
- Change every downstream call from `connectionString` to `resolved`, e.g. `_schema.ExecuteDdlAsync(resolved, ...)`, `_loader.LoadAsync(resolved, ...)`, `_indexManager.DisableNonClusteredAsync(resolved, ...)`, `_incrementalProbe.GetWatermarkAsync(resolved, ...)`.
- The `catch` block's best-effort index rebuild uses the same `resolved` (already in scope), not a re-resolve.
- Capability-driven branches now read from `resolved.Capabilities` (e.g. skip `indexes.disable` when `resolved.Capabilities.IsSynapse`).

`InferenceService.cs` (added to the change list, H3):

- Replace `ISecretResolver _secrets` with `IConnectionResolver _resolver` (and inject `IConnectionFactory` indirectly through the providers).
- Resolve EXACTLY ONCE in `InferCoreAsync` with the appropriate `ConnectionRole`, store the `ResolvedConnection`, and thread it into `_schema.GetTableSchemaAsync(resolved, ...)`, `_profiler.ProfileAsync(resolved, ...)`, `_validator.CheckAsync(resolved, ...)`. Those providers open via `IConnectionFactory` (section 4.2). Only after this change is the single-open-site invariant actually true.

---

## 7. Production hardening

- **Rotation.** Passwordless AAD (default) needs no rotation. Key Vault secrets rotate freely: TTL bounds staleness; evict-on-auth-failure bounds it to one failed open (bounded loop, M4). Cross-tenant tokens auto-refresh before expiry.
- **Transient faults.** `SqlConnectionFactory.OpenAsync` retries opens on transient SQL error numbers (40613, 49918, 49919, 49920, 4060 throttling, 10928/10929, plus connection-reset 64/233/10053) with bounded exponential backoff and jitter; non-transient errors surface immediately. The MySQL path retries on the equivalent transient MySQL conditions. Key Vault calls retry via the SDK's built-in policy.
- **Fail-closed, leak-proof diagnostics.** Unknown alias, malformed connection string, resting-secret violation, `@alias` in lightweight mode, NULL `SourceType`/`ServicePrincipalAlias` at migration, and a wrong-provider connection at the SQL downcast guard are all hard errors with precise messages that contain only the alias name or `RedactedString`, never the secret. No code path returns an empty/default connection string.
- **Sovereign cloud.** Vault suffix and the SQL token audience are derived from `SQLFLOW_AZURE_CLOUD` (default `public`) and overridable via `KeyVaultUriOverride`; nothing is hardcoded to public `database.windows.net` (section 6.6).
- **Least privilege.** The ambient identity needs only `get` on the specific Key Vault secrets plus `db_datareader`/`db_ddladmin`-scoped rights on the targets it actually writes. The runtime control-plane reader that runs `SqlDataSourceStore` needs only `SELECT` on the two `flw` tables. The control-plane WRITER (`apply`/`sync`) is a separate principal with INSERT/UPDATE on exactly those two tables (section 5.5).
- **Preflight test command.** `sqlflow test-connection <alias|pipeline.yaml>` resolves the ref, opens via the factory, runs `SELECT 1` (or the MySQL equivalent `SELECT 1` for a MySQL source), and reports `Kind`, capability flags, storage context, the `RedactedString`, and round-trip time. It uses the SAME `IConnectionResolver`/`IConnectionFactory` as `run` (Single Code Path), so a green preflight guarantees `run` can connect.
- **Audit.** Full mode logs alias resolutions (alias, `RedactedString`, `RunId`, identity) to the run log; secret reads are counted but never valued. `RowVersion` on both tables gives optimistic-concurrency audit for `apply`/`sync`.

---

## 8. YAML and control-DB examples

Lightweight, passwordless inline:
```yaml
target:
  connection: "Server=tcp:dw.example.database.windows.net,1433;Initial Catalog=Silver;Authentication=Active Directory Default;Encrypt=true"
  schema: dbo
  table: Sales
```

Lightweight, secret reference (the file holds only the reference):
```yaml
target:
  connection: ${keyvault:kv-prod/dw-silver-conn}
  schema: dbo
  table: Sales
```

Full, alias, with a relational SQL Server source by alias:
```yaml
target:
  connection: "@AdventureWorksDW"
  schema: dbo
  table: Sales
source:
  type: ado
  location: "@AdventureWorksOLTP"   # relational source by alias; resolved with ConnectionRole.Source (section 4.4)
  options:
    query: "SELECT * FROM Sales.SalesOrderHeader"
```

Full, MySQL source into the SQL Server target (locked decision 1: MySQL is only ever a source):
```yaml
target:
  connection: "@WarehouseDW"        # SQL Server target
  schema: stg
  table: Orders
source:
  type: ado
  location: "@ShopMySql"            # Kind=MySQL; AdoSourceReader opens via MySqlConnectionFactory
  options:
    query: "SELECT id, total, created_at FROM orders"
```

Per-flow credential override (locked decision 3; overrides `DataSource.CredentialAlias`, resolved through the same pipeline):
```yaml
target:
  connection: "@AdventureWorksDW"
  credentialAlias: "sp-dw-elevated"   # overrides the DataSource's bound credential for this flow only
  schema: dbo
  table: Sales
```

Control-DB rows:
```sql
INSERT flw.CredentialProfile (Alias, Mode, KeyVaultName)
VALUES ('sp-dw-reader', 0, 'kv-prod');   -- ConnectionStringAuth, passwordless; KV only for the conn string

INSERT flw.DataSource (Alias, Kind, Host, ConnectionRef, CredentialAlias, IsSynapse, SupportsCrossDbRef, IsLocal, ActivityMonitoring)
VALUES ('AdventureWorksDW', 1, 'dw.example.database.windows.net',
        '${keyvault:kv-prod/dw-silver-conn}', 'sp-dw-reader', 0, 1, 0, 1);   -- Kind 1 = AZDB

INSERT flw.DataSource (Alias, Kind, Host, ConnectionRef, CredentialAlias)
VALUES ('ShopMySql', 2, 'shop.example.com', '${keyvault:kv-prod/shop-mysql-conn}', 'sp-dw-reader'); -- Kind 2 = MySQL (source only)
```

Cross-tenant downstream SP (the only `InjectedToken` case):
```sql
INSERT flw.CredentialProfile (Alias, Mode, TenantId, ClientId, ClientSecretRef, KeyVaultName)
VALUES ('sp-partner-tenant', 3, '<partner-tenant-guid>', '<app-id>',
        '${keyvault:kv-prod/partner-sp-secret}', 'kv-prod');   -- Mode 3 = InjectedToken

INSERT flw.DataSource (Alias, Kind, ConnectionRef, CredentialAlias)
VALUES ('PartnerDW', 1,
        'Server=tcp:partner.database.windows.net,1433;Initial Catalog=Shared;Encrypt=true', -- no Authentication=, no Integrated Security
        'sp-partner-tenant');
```

---

## 9. Integration plan against the existing V3 tree

**Add (value types + contracts in `SqlFlow.Core`, namespace `SqlFlow.Core.Connections`; L4, resolved decision):**
- `src/SqlFlow.Core/Connections/ConnectionRef.cs`, `DataSource.cs`, `CredentialProfile.cs`, `DataSourceCapabilities.cs`, `StorageContext.cs`, `ResolvedConnection.cs`, the enums (section 3).
- `src/SqlFlow.Core/Connections/IDataSourceStore.cs`, `IConnectionResolver.cs`, `IConnectionFactory.cs` (section 4.1). These reference only Core types (the `DbConnection` return type is in `System.Data.Common`, already available), so no `Core -> Connections` edge and no SqlClient/Azure dependency on Core.
- `src/SqlFlow.Core/Connections/ConnectionResolver.cs` (the single pipeline, async), `NullDataSourceStore.cs` (lightweight). The resolver depends on `ISecretResolver` (Core abstraction) and `IDataSourceStore`.

**Add (in `SqlFlow.SqlServer`, which already references `Microsoft.Data.SqlClient`):**
- `SqlConnectionFactory.cs` (the `IConnectionFactory` binding; the only SQL open site; transient retry; `InjectedToken` AccessToken set before open; canonicalization/`InspectSecretless`; routes `Kind==MySQL` to `MySqlConnectionFactory`).
- `SqlServerConnectionGuard.cs` (the single `DbConnection -> SqlConnection` downcast with fail-fast, H5).
- `SqlDataSourceStore.cs` (full-mode store; the section 5.2 seek; calls `InspectSecretless` on write paths via the control-plane writer).

**Add (new project `SqlFlow.MySql`, references `MySql.Data`; locked decision 1):**
- `MySqlConnectionFactory.cs` (opens a `MySqlConnection`, returns `DbConnection`; `MySqlConnectionStringBuilder` canonicalization/redaction). It is registered and actually called by `SqlConnectionFactory`, so it is not a stub.

**Add (new project `SqlFlow.Azure` additions; M2):**
- `ITokenCredentialCache.cs` / `TokenCredentialCache.cs` (Azure types; cross-tenant token cache, section 6.3).

**Add (relational source reader in `SqlFlow.Sources`; locked decision 1, H4 option B):**
- `AdoSourceReader.cs` (`type: ado`; opens via injected `IConnectionFactory`; consumes `DbConnection` generically via `DbCommand`/`DbDataReader`, section 4.4).
- `DbDataReaderRowStream.cs` (the `ISourceRowStream` over a `DbDataReader`, disposing the connection back to the pool).

**Change:**
- `src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs` - async `ResolveAsync`, `SQLFLOW_AZURE_CLOUD`-driven suffix, TTL cache with `Lazy<Task<string>>` stampede collapse, iterative (non-recursive) expiry, no-cache-on-failure, TTL `> 0` guard, `Invalidate` (sections 6.4, 6.6; M1/M3/M4).
- `src/SqlFlow.Core/Abstractions/ISecretProvider.cs` and `ISecretResolver.cs` - add `ResolveAsync` (M1).
- `src/SqlFlow.Core/Abstractions/*` - change `ISchemaProvider`, `IBulkLoader`, `IIndexManager`, `IDesiredIndexManager`, `IIncrementalProbe`, `IColumnProfiler`, `IInferenceValidator` from `string connectionString` to `ResolvedConnection connection` (10 methods across 7 interfaces, L5). `IDdlGenerator.Generate(TargetSpec, SchemaDelta)` is left UNTOUCHED (it never opens a connection, L5).
- `src/SqlFlow.Core/Sources/ISourceReader.cs` - add the optional `ResolvedConnection? resolved` parameter (section 4.4); `SourceSpec.Location` becomes `required string` for symmetry with `TargetSpec.Connection` (H4).
- `src/SqlFlow.Sources/*` (file readers) - update to the new `ISourceReader` signature; they ignore `resolved`.
- `src/SqlFlow.SqlServer/*` - every provider (`SqlServerSchemaProvider`, `SqlBulkLoader`, `SqlServerIndexManager`, `SqlServerDesiredIndexManager`, `SqlServerIncrementalProbe`, `SqlServerInferenceValidator`, `SqlServerColumnProfiler`) takes `ResolvedConnection`, opens via injected `IConnectionFactory`, and obtains its `SqlConnection` only through `SqlServerConnectionGuard.AsSqlConnection`; `SqlServerDdlGenerator` and `SqlServerIndexManager` branch on `connection.Capabilities` (`IsSynapse`, `SupportsCrossDbRef`).
- `src/SqlFlow.Core/Engine/FlowRunner.cs` - resolve target once via `IConnectionResolver`, resolve a relational source once with `ConnectionRole.Source`, store in `RunContext`, pass `ResolvedConnection` everywhere, delete the second resolve on line 286, match `AdoSourceReader` in `ResolveReader` (section 6.7, 4.4).
- `src/SqlFlow.Core/Engine/InferenceService.cs` - replace `ISecretResolver` with `IConnectionResolver`, resolve once in `InferCoreAsync`, thread `ResolvedConnection` into schema/profiler/validator (H3).
- `src/SqlFlow.Core/.../FlowDefinition.cs` - add optional `credentialAlias` to `TargetSpec`/`FlowDefinition` (locked decision 3); make `SourceSpec.Location` `required string` (H4).
- `src/SqlFlow.Cli/Program.cs` - register `IDataSourceStore`/`IConnectionResolver`/`IConnectionFactory`/`ITokenCredentialCache`/`MySqlConnectionFactory`/`AdoSourceReader` (section 4.3); add the `test-connection` command (section 7).
- `SqlFlowDb` (new for full mode) - `flw.DataSource` and `flw.CredentialProfile` DDL plus the migration script and prerequisites (section 5).

**Change (tests; L6, L7):**
- `tests/SqlFlow.Core.Tests/ConcurrencyTests.cs` - update the `FlowRunner` constructor call (the resolver/factory are added) and update `FakeSchema`/`FakeLoader`/`FakeIndexManager`/`FakeDesiredIndexManager`/`FakeIncrementalProbe` from `string connectionString` to `ResolvedConnection` (a small test helper builds a `ResolvedConnection` with its required init members).
- `tests/SqlFlow.Core.Tests/InferenceServiceTests.cs` - update for the `IConnectionResolver` constructor change (H3).
- `KeyVaultCacheTests` go in a NEW `tests/SqlFlow.Azure.Tests` project (or add a `SqlFlow.Azure` reference to the Core test project), because `AzureKeyVaultSecretProvider` lives in `SqlFlow.Azure` and `SqlFlow.Core.Tests` does not reference it (L7).

**Add tests:**
- `ConnectionRefParseTests` (sigil grammar, AAD-UPN non-collision, lightweight `@alias` error).
- `CanonicalizationTests` (`Server/Data Source` and `Database/Initial Catalog` collapse to one key).
- `SecretlessGateTests` (whitespace-bypass `Password =`/`Pwd\t=` rejected; `Active Directory Password`/`Service Principal` rejected inline; passwordless AAD and `Integrated Security=true` accepted; H1/H2).
- `RedactionTests` (scan exceptions, including deep `OpenAsync`/`SqlDataSourceStore` throws, plus `FlowEvent` for secrets/keywords; non-connection-string secret rule; M8).
- `KeyVaultCacheTests` (TTL, TTL `> 0` guard, no-cache-on-failure, single async call under N concurrent misses, evict-on-invalidate, bounded at-most-two-calls retry; M1/M4) in `SqlFlow.Azure.Tests`.
- `SovereignCloudSuffixTests` (one per `public`/`usgov`/`china`; M3).
- `ConnectionFactoryConcurrencyTests` (N parallel flows, one alias, no shared connection/transaction).
- `AdoSourceReaderTests` (SQL Server source and MySQL source open via the same factory; `DbCommand`/`DbDataReader` generic path; locked decision 1).
- `MigrationMappingTests` (NULL `SourceType` rejected, NULL `ServicePrincipalAlias` fold-in failure, alias dedupe, four-bit `COALESCE`, resting-secret rejection; H6/H9/M9).

---

## 10. Resolved decisions

### 10.1 Locked decisions (project owner, 2026-06-03) - final

1. **MySQL: wire now, source-only.** MySQL is wired in this cut and is only ever a SOURCE (SQL Server is always the target by design). This brings the relational-source path into scope (section 4.4): an `AdoSourceReader`, an `ISourceReader` change so the engine passes it an engine-resolved `ResolvedConnection` (resolved once with `ConnectionRole.Source`), and a `MySqlConnectionFactory`. Per the project rule to reuse the libraries the original SQLFlow used, the factory uses `MySql.Data`, not `MySqlConnector`, unless a concrete defect forces the switch. This resolves review finding H4 via option (B): there is NO dead `ConnectionRole.Source` branch and NO uncalled MySQL factory (no-stub principle).

2. **Surrogate keys: keep.** `flw.DataSource.DataSourceId` and `flw.CredentialProfile.CredentialProfileId` stay as IDENTITY surrogates (clean FK targets for future lineage and run-log rows); `Alias` remains the public key with its UNIQUE constraint. No DDL change from section 5.1 for this.

3. **Per-flow credential override: allow.** An optional `credentialAlias` on `FlowDefinition`/`TargetSpec` and the YAML overrides `DataSource.CredentialAlias` when present. The override resolves through the same `IConnectionResolver` pipeline, so it introduces no second code path. This preserves the legacy capability of one connection used under different identities per flow.

4. **Key Vault secret TTL: 10 minutes.** The hardened `AzureKeyVaultSecretProvider` defaults its success-cache TTL to 10 minutes (configurable; must be `> 0`, M4). The `Lazy<Task<string>>` stampede-collapse keeps traffic low and the bounded evict-on-auth-failure path bounds the post-rotation stale window to a single failed open.

### 10.2 Resolved technical decisions (answers the review's "Revised open decisions")

- **Home of the value types (review decision 2 = B; fixes L4/H5).** `ResolvedConnection`, `ConnectionRef`, `DataSource`, `CredentialProfile`, `DataSourceCapabilities`, `StorageContext`, the enums, plus `IConnectionResolver` and `IDataSourceStore`, live in `SqlFlow.Core` (namespace `SqlFlow.Core.Connections`). Rationale: they have no non-Core dependencies, and the provider interfaces that reference them already live in Core, so this avoids an awkward `Core -> Connections` reference edge. The resolver/store implementations may live in their own project, but the contracts are Core.

- **Factory return type and SQL-Server type access (review decision 3 = A; fixes the H5 downcast gap, consistent with MySQL-now).** `IConnectionFactory.OpenAsync` returns `System.Data.Common.DbConnection` so it can return either a `SqlConnection` or a `MySqlConnection`. The SQL Server target providers downcast to `SqlConnection` at the SINGLE guarded helper `SqlServerConnectionGuard.AsSqlConnection`, which fails fast with a clear `SqlFlowException` naming the alias and the expected provider (never a raw `InvalidCastException`). This is always valid because the target is always SQL Server. The `AdoSourceReader` consumes the `DbConnection` generically via `DbCommand`/`DbDataReader`, so the same reader serves SQL Server and MySQL sources.

- **Async secret read end to end (review decision 4 = B; fixes M1).** `ISecretProvider.ResolveAsync` and `ISecretResolver.ResolveAsync` use `SecretClient.GetSecretAsync`; concurrent misses collapse with `Lazy<Task<string>>`, not a blocking `Lazy<(string, DateTimeOffset)>`. Rationale: the resolver and factory are already async and Key Vault is network I/O; under the all-singleton DI root this removes a real thread-pool blocking point.

- **`ITokenCredentialCache` placement (review decision 5 = B; fixes M2).** `ITokenCredentialCache`/`TokenCredentialCache` live in `SqlFlow.Azure` (it already references `Azure.Identity`/`Azure.Core`). `SqlConnectionFactory` depends only on the `ITokenCredentialCache` abstraction via DI, keeping `SqlFlow.SqlServer` Azure-free for the on-prem/Integrated path.

- **Sovereign-cloud suffix source (review decision 6 = B; fixes M3).** Net-new code, not a reuse of the legacy `CloudEnvironmentDetector` (which only distinguishes Azure/AWS and has no sovereign concept). Derive the Key Vault DNS suffix and the SQL token audience from `SQLFLOW_AZURE_CLOUD = public | usgov | china` (default `public`), with `KeyVaultUriOverride` as the escape hatch, and a test per suffix.

- **`DataSourceKind.Synapse` (review decision 7 = A; fixes H6).** Dropped. Synapse-ness is carried ONLY by the `IsSynapse` capability flag (which drives code generation). `Kind` is a faithful 1:1 of the three real legacy `SourceType` values (`MSSQL`, `AZDB`, `MySQL`); a NULL legacy `SourceType` is rejected at migration with a clear error.

### 10.3 What survived the red team unchanged (verified solid)

These were confirmed correct and are preserved: the single-pipeline resolver model and `@`-sigil grammar; picking `ConnectionStringAuth` as canonical with `InjectedToken`/`AccessToken` confined to the rare cross-tenant case (and no bespoke per-connection token-TTL layer for SQL); `SqlConnectionStringBuilder` canonicalization for a stable pool key (and as the right place to enforce secretlessness); the stateless/reentrant factory invariant; dropping the resting secrets and replacing them with `${...}` locators with `ResolvedConnection.ToString()` as the redacted safety net; the `FlowRunner` resolve-once bug fix; the `ActivityMonitoring` NULL observation (generalized to all four bits, M9); capabilities riding into providers as the channel that makes the legacy flags drive code-gen (and `IDdlGenerator` left untouched); and locked decisions 2, 3, 4.

### 10.4 Revision log (per-id)

- **H1 (whitespace-bypass secret guard).** Fixed. The single `InspectSecretless` routine (6.1) parses through `SqlConnectionStringBuilder` and rejects on `!IsNullOrEmpty(b.Password)`, so `Password =`/`Pwd\t=` cannot bypass. The SQL CHECK (5.1) is now whitespace- and collation-tolerant defense-in-depth.
- **H2 (conflicting AAD rules).** Fixed. Explicit precedence in 1.4 and 6.1: rule 1 (non-empty `Password` rejected) always wins; only passwordless AAD (Default/Managed Identity/Workload Identity) and `Integrated Security=true` may be inline, validated against the parsed `Authentication` enum. `Active Directory Password`/`Service Principal` rejected inline.
- **H3 (`InferenceService` second open path).** Fixed. Added to the change list (6.7, 9): `InferenceService` switches to `IConnectionResolver`, resolves once, threads `ResolvedConnection` through schema/profiler/validator, which open via the factory.
- **H4 (source-by-alias dead).** Fixed via option (B) per locked decision 1: `AdoSourceReader`, the `ISourceReader` change to accept an engine-resolved `ResolvedConnection`, `MySqlConnectionFactory`, and `FlowRunner` source resolution with `ConnectionRole.Source` (4.4). No dead `Source` branch.
- **H5 (layering + `DbConnection` downcast).** Fixed. Value types and contracts in `SqlFlow.Core` (10.2); `OpenAsync` returns `DbConnection`; SQL providers downcast at the single `SqlServerConnectionGuard.AsSqlConnection` fail-fast site (4.1); `ITokenCredentialCache` moved to `SqlFlow.Azure`.
- **H6 (`SourceType -> Kind` wrong, double-counts Synapse).** Fixed. `DataSourceKind` is `{MSSQL, AZDB, MySQL}` 1:1; Synapse dropped from `Kind` and carried only by `IsSynapse`; NULL `SourceType` rejected at migration (3, 5.3.1, 10.2).
- **H7 (`SysAPIKey`/`SysSourceControlType` column-completeness).** Fixed. Sections 5.3.3 and 5.3.4 enumerate all 7 and all 14 columns: `ServiceType`/`ServicePrincipalAlias` added; `*SecretName` locators recorded as continued (not invented); `ConsumerKey` moved to non-secret feature config; `ProjectKey`/`SCAlias`/`SourceControlType`/`CreateWrkProjRepo` enumerated.
- **H8 (false masking claim).** Fixed. Section 2.3 and 5.3.2 now state `SysServicePrincipal.ClientSecret` was PLAINTEXT (no `MASKED WITH`); masking applied only to `SysDataSource.ConnectionString`, `SysAPIKey.SecretKey`, `SysSourceControlType.AccessToken`/`ConsumerSecret`.
- **H9 (no legacy locator for `ClientSecretRef`; CHECK rejects resting secret).** Fixed. Section 5.4 adds the migration prerequisites: hand-move plaintext `ClientSecret` to Key Vault then set `ClientSecretRef` (fail-closed until done), transform connection strings to passwordless/`${...}` before insert, and derive the `SysDataSource.KeyVaultSecretName` vault from `SysServicePrincipal.KeyVaultName` via `ServicePrincipalAlias` with the NULL case documented.
- **M1 (sync-over-async secret read).** Fixed. `ResolveAsync` end to end (4.1, 6.6, 9); `Lazy<Task<string>>` stampede collapse.
- **M2 (`TokenCredentialCache` placement).** Fixed. Moved to `SqlFlow.Azure` (6.3, 9, 10.2).
- **M3 (sovereign reuse not grounded).** Fixed. Net-new `SQLFLOW_AZURE_CLOUD` setting with a suffix/audience table and a test per cloud (6.6, 10.2).
- **M4 (recursive TTL eviction StackOverflow).** Fixed. TTL `> 0` guard at construction; iterative re-add (non-recursive); bounded at-most-one re-fetch with an attempt counter and a distinct second-failure exception; test asserts at most two calls/opens (6.4, 6.6).
- **M5 (relocated columns to unnamed target).** Fixed. `SubscriptionId`/`ResourceGroup`/`DataFactoryName`/`AutomationAccountName` are explicitly DROPPED with reason "ADF/Automation out of scope"; source-control non-secret columns are explicitly relocated to source-control feature config (5.3.2, 5.3.4, 5.3.5).
- **M6 (`BlobEndpointSuffix` fidelity).** Fixed. Documented as a NEW V3 field with no legacy origin, runtime-derived from the cloud detector, NOT a DDL column (3, 5.1).
- **M7 (two enforcement heuristics).** Fixed. One authoritative `InspectSecretless` routine called by the resolver and the control-plane write path; CHECK is defense-in-depth; section 5.5 names the control-plane writer (1.4, 5.5, 6.1).
- **M8 (redaction misses non-connection-string secrets).** Fixed. Section 6.5 adds a rule for raw resolved secrets used outside a `SqlConnection` and widens the redaction test scope to `SqlConnectionFactory`/`SqlDataSourceStore` and deep `OpenAsync` throws.
- **M9 (alias/bit NULL skew at migration).** Fixed. Section 5.4 dedupes/repairs aliases (rejects NULL) before insert and applies `COALESCE(col,0)` to all four bits.
- **L1 (`Access Token` not a builder keyword).** Fixed. `RedactedString` comment and 6.1 list exactly `Password`/`Pwd`/`User ID`/`UID`; access tokens are never in the string.
- **L2 (`AccessToken` + `Authentication` throws at open).** Fixed. Reworded to "throws at the property-assignment site inside `OpenAsync`, before open"; also incompatible with `Integrated Security=true` (2.2, 6.2).
- **L3 (embedded-ref foot-gun).** Fixed. Stated as deliberate policy in 1.4: whole-string ref is safer than embedded-ref-then-recheck.
- **L4 (home of value types).** Fixed. Value types defined in `SqlFlow.Core` (3, 9, 10.2).
- **L5 (provider-method count).** Fixed. Exact count stated: 10 methods across 7 interfaces, with the inference pair named (`IColumnProfiler.ProfileAsync`, `IInferenceValidator.CheckAsync`); `IDdlGenerator` left untouched (4.2).
- **L6 (existing tests/fakes).** Fixed. Section 9 "Change (tests)" updates the `FlowRunner` constructor call and every fake to `ResolvedConnection`, plus `InferenceServiceTests`.
- **L7 (`KeyVaultCacheTests` cannot reference the type).** Fixed. Tests go in a new `SqlFlow.Azure.Tests` project (or add the project reference); noted in section 9.
- **L8 (silent column widenings).** Fixed. Section 5.1 and 5.3 document the deliberate widenings (`nvarchar(50)`/`(70)` to `nvarchar(128)`/`(256)`).
- **L9 (transient SP-secret lifetime).** Fixed. Section 6.3 acknowledges the immutable-`System.String` limitation and minimizes the secret's lifetime (resolve, build credential, drop the reference; never log or store).

---

Grounded files read: V3 `FlowRunner.cs` (double-resolve at lines 88 and 286), `InferenceService.cs` (independent resolve-and-open at line 95), `FlowDefinition.cs` (`TargetSpec.Connection` required string, `SourceSpec.Location` nullable string), the file-only source readers under `SqlFlow.Sources`, `SecretResolver.cs`/`ISecretProvider.cs`/`EnvSecretProvider.cs` (synchronous `Resolve`), `AzureKeyVaultSecretProvider.cs` (hardcoded `.vault.azure.net`, re-fetch every call, synchronous), `CloudEnvironmentDetector` (Azure/AWS only, no sovereign), `AzureCredentialFactory.cs`, `Program.cs` (all-singleton DI root), the provider interfaces and `SqlBulkLoader.cs`/`SqlServerSchemaProvider.cs` (`SqlBulkCopy`/`SqlCommand`/`SqlTransaction` require a concrete `SqlConnection`), `ConcurrencyTests.cs` (12-arg `FlowRunner`, fakes on `string`), the test project references, `Directory.Packages.props` (SqlClient 6.0.2, no MySQL package). Legacy SQL read from `C:\Projects\SQLFlow\SQLFlowDb\Database\Tables`: `flw.SysDataSource.sql`, `flw.SysServicePrincipal.sql`, `flw.SysAPIKey.sql`, `flw.SysSourceControlType.sql`, `flw.SysSourceControl.sql`, `flw.SysAlias.sql`.

---

## 11. Implementation status

### 11.1 Slice 1 (delivered): the pure-SqlFlow.Core foundation

Implemented and unit-tested in `SqlFlow.Core`, namespace `SqlFlow.Core.Connections`, with no SqlClient or Azure dependency (the project still references only Logging.Abstractions and DiagnosticSource):

- Value types: `ConnectionRef` (plus `ConnectionRefKind`), `DataSource` (plus `DataSourceKind`, no Synapse), `DataSourceCapabilities`, `StorageContext`, `CredentialProfile` (plus `CredentialMode`), `ResolvedConnection`.
- Contracts: `IDataSourceStore`, `IConnectionResolver` (plus `ConnectionRole`), `IConnectionFactory` (returns `System.Data.Common.DbConnection`).
- `ConnectionResolver` (the single pipeline) and `NullDataSourceStore` (the lightweight backend).
- Async secret resolution: `ISecretResolver.ResolveAsync` and `ISecretProvider.ResolveAsync` (the default wraps the synchronous `Resolve`), implemented in `SecretResolver`.

Tests, all green (31 cases): `ConnectionRefParseTests`, `ConnectionResolverTests`, `NullDataSourceStoreTests`, `SecretResolverAsyncTests`. The full solution builds with 0 warnings and 0 errors.

### 11.2 Two refinements made while building (later slices must follow these)

1. Canonicalization sits behind a Core abstraction. Section 6.1 canonicalizes via `SqlConnectionStringBuilder`, a SqlClient type, while section 9 places `ConnectionResolver` in pure Core; both cannot hold. Resolution: a new Core contract `IConnectionStringCanonicalizer` (`bool CanHandle(DataSourceKind)`, `CanonicalConnection Canonicalize(string, ConnectionRole, SecretlessPolicy)`) that the resolver depends on and selects by `Kind`. The provider-specific implementations (SQL Server via `SqlConnectionStringBuilder`, MySQL via `MySqlConnectionStringBuilder`) land with their slices and must implement this interface, including the `InspectSecretless` gate.

2. The secretless gate is parameterized by a `SecretlessPolicy` the resolver computes, not applied uniformly. A uniform post-expansion `InspectSecretless` would wrongly reject (a) a connection string expanded from a whole `${...}` reference that legitimately carries a password, and (b) an `InjectedToken` connection that omits an auth keyword because the token is injected at open time. The resolver therefore selects the policy: `Trusted` (a whole-`${...}`-ref inline value, or a `CredentialMode.InlineConnectionString` alias) skips the gate; `RejectRestingSecret` (`CredentialMode.InjectedToken`) rejects only a resting password; `RequireSelfAuthenticating` (a literal inline value, or a `ConnectionStringAuth` / `Integrated` alias) enforces both rule H1 and rule H2. The canonicalizer enforces whatever policy it is handed.

### 11.3 Next slices (not yet built)

In dependency order: the SQL Server `SqlConnectionStringCanonicalizer` plus `SqlConnectionFactory`, `SqlServerConnectionGuard`, and `SqlDataSourceStore` (sections 4.1, 4.2, 5, 6.1); the MySQL canonicalizer plus `MySqlConnectionFactory` (4.4); the `SqlFlow.Azure` `AzureKeyVaultSecretProvider` hardening plus `TokenCredentialCache` (6.3, 6.6); the provider-signature change to `ResolvedConnection` and the `FlowRunner` / `InferenceService` resolve-once wiring (4.2, 6.7); the `AdoSourceReader` (4.4); the control-DB DDL and migration (5); and the CLI DI plus `test-connection` registration (4.3, 7).
