namespace SqlFlow.Core.Acquire;

/// <summary>
/// One data-protection rule applied to the RAW payload at landing time, before a single byte is written to the
/// lake. It selects fields by JSON path and applies a protection transform, so personal or sensitive data the
/// upstream returns is scrubbed, pseudonymised, or generalised at the acquisition boundary rather than persisted
/// raw and cleaned downstream (by then the raw file already holds it). This is the engine capability that lets a
/// V3 <c>api</c> flow own the PII handling a legacy producer used to do, instead of mirroring the producer's
/// already-scrubbed output. Rules apply in declaration order; a rule that matches no path is a no-op (payload
/// shapes vary per record).
/// </summary>
public sealed record AcquireProtectRule
{
    /// <summary>
    /// The path selecting the field(s) to protect, interpreted per the landed format. JSON/JSONL: a JSON path with
    /// an optional leading <c>$</c>, dotted properties (<c>a.b</c>), an array wildcard (<c>[*]</c>) mapping every
    /// element, and explicit indices (<c>[3]</c>), e.g. <c>$.data[*].rider</c>. XML: an element path starting at
    /// the document root (<c>requests.request.rider</c>; a repeated element matches all occurrences), with an
    /// optional trailing <c>@name</c> selecting an attribute. CSV: the header column name.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The protection transform to apply to each matched value.</summary>
    public required AcquireProtectAction Action { get; init; }

    /// <summary>
    /// A <c>${...}</c> reference to the key material for the keyed transforms (<see cref="AcquireProtectAction.Hmac"/>,
    /// <see cref="AcquireProtectAction.Encrypt"/>, and <see cref="AcquireProtectAction.Tokenize"/> when deterministic
    /// linkability is wanted). Resolved once per run through the secret resolver. Required for HMAC and Encrypt.
    /// </summary>
    public string? Secret { get; init; }

    /// <summary>The linkability scope for the keyed transforms: how consistently the same input maps to the same
    /// output. See <see cref="AcquireProtectScope"/>.</summary>
    public AcquireProtectScope Scope { get; init; } = AcquireProtectScope.Relationship;

    /// <summary>Transform-specific knobs (mode, replacement string, mask character, output length, generalisation
    /// granularity, bucket size, token format). Keys are case-insensitive. Defaults are documented per transform on
    /// the applying engine.</summary>
    public IReadOnlyDictionary<string, string> Params { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The protection transform applied to a matched value. The families mirror the standard de-identification toolbox:
/// suppression (<see cref="Remove"/>/<see cref="Redact"/>), partial visibility (<see cref="Mask"/>), one-way
/// fingerprints (<see cref="Hash"/>/<see cref="Hmac"/>), token substitution (<see cref="Tokenize"/>), reversible
/// deterministic encryption (<see cref="Encrypt"/>), and precision reduction (<see cref="Generalize"/>).
/// </summary>
public enum AcquireProtectAction
{
    /// <summary>Delete the field entirely (drop the property, or the array element's property under a wildcard).</summary>
    Remove,

    /// <summary>Replace the value with a constant or a suppression pattern. Params: <c>mode</c> =
    /// <c>full</c> (default; <c>replacement</c>, default <c>[REDACTED]</c>) | <c>partial</c> (<c>keepFirst</c>,
    /// <c>keepLast</c>, <c>maskChar</c>) | <c>email</c> | <c>phone</c> | <c>card</c>.</summary>
    Redact,

    /// <summary>Keep the first <c>show</c> (and optional last <c>showLast</c>) characters and mask the rest with
    /// <c>maskChar</c> (default <c>*</c>).</summary>
    Mask,

    /// <summary>Unkeyed one-way SHA-256 fingerprint (hex). Deterministic and globally consistent, but not
    /// brute-force resistant for low-entropy values; use <see cref="Hmac"/> for pseudonyms that must resist
    /// dictionary attacks.</summary>
    Hash,

    /// <summary>Keyed one-way pseudonym. Params: <c>algorithm</c> = <c>hmac_sha256</c> (default) | <c>pbkdf2</c>
    /// (KDF-hardened, iteration count via <c>iterations</c>); <c>outputLength</c> truncates the hex. Requires
    /// <see cref="AcquireProtectRule.Secret"/>. Same input+key+scope maps to the same output (linkable), which is
    /// why the key must be kept to re-identify or join.</summary>
    Hmac,

    /// <summary>Substitute the value with a token. Deterministic per <see cref="AcquireProtectScope"/> when a
    /// <see cref="AcquireProtectRule.Secret"/> is given (same value maps to the same token, so joins survive);
    /// this token form is NOT reversible without an external map (use <see cref="Encrypt"/> when the original must
    /// be recoverable). Params: <c>format</c> (default <c>tok_{short}</c>; supports <c>{uuid}</c>, <c>{short}</c>,
    /// or a literal prefix).</summary>
    Tokenize,

    /// <summary>Reversible, deterministic authenticated encryption (AES-256-GCM with a synthetic, plaintext-derived
    /// nonce - the SIV construction - so the same plaintext+key yields the same ciphertext and joins survive).
    /// Base64url output. Requires <see cref="AcquireProtectRule.Secret"/>; the holder of the key can decrypt.</summary>
    Encrypt,

    /// <summary>Reduce precision. Params: <c>mode</c> = <c>year</c> | <c>month</c> | <c>quarter</c> | <c>decade</c>
    /// | <c>age_range</c> (<c>bucket</c> size, default 5) | <c>zip3</c> | <c>zip2</c> | <c>round</c> (<c>step</c>).
    /// Dates parse from ISO-8601; numbers from the invariant culture.</summary>
    Generalize,
}

/// <summary>
/// Controls how consistently a keyed transform (<see cref="AcquireProtectAction.Hmac"/>,
/// <see cref="AcquireProtectAction.Tokenize"/>, <see cref="AcquireProtectAction.Encrypt"/>) maps the same input to
/// the same output, by scoping the domain-separation salt.
/// </summary>
public enum AcquireProtectScope
{
    /// <summary>No linkability: each acquisition run salts independently, so the same value maps to different
    /// outputs across runs (a run-scoped random salt). Use when only within-payload consistency matters.</summary>
    Transaction,

    /// <summary>Linkable within a relationship boundary: the salt derives from the flow (and an optional
    /// <c>relationship</c> param), so a value is consistent across this source's runs but not across unrelated
    /// sources. The default.</summary>
    Relationship,

    /// <summary>Globally consistent per individual: no extra salt, so the same value maps to the same output
    /// everywhere the same key is used (full linkability across sources).</summary>
    Person,
}
