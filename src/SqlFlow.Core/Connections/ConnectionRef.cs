using SqlFlow.Core;

namespace SqlFlow.Core.Connections;

/// <summary>
/// Whether a <see cref="ConnectionRef"/> names a registry alias or carries an inline connection string
/// (which may itself contain a <c>${scheme:locator}</c> secret reference, expanded later by the resolver).
/// </summary>
public enum ConnectionRefKind
{
    Alias,
    Inline,
}

/// <summary>
/// A classified, not-yet-resolved connection reference. The single string carried by a target or a
/// relational source is one of three shapes: an <c>@alias</c> (full mode only), a <c>${scheme:locator}</c>
/// secret reference, or an inline connection string. The <c>@</c> sigil is the only disambiguator: an alias
/// is <c>@</c> followed by a strict identifier charset, so it can never collide with a real connection
/// string (which always contains ';' and '=') or an Active Directory user name (which only ever appears
/// inside a connection string, never as the first character).
/// </summary>
public readonly record struct ConnectionRef
{
    public ConnectionRefKind Kind { get; }

    /// <summary>
    /// The alias name without the leading '@' (for <see cref="ConnectionRefKind.Alias"/>), or the inline /
    /// <c>${...}</c> text (for <see cref="ConnectionRefKind.Inline"/>). Always trimmed and non-empty.
    /// </summary>
    public string Value { get; }

    private ConnectionRef(ConnectionRefKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Classifies a raw connection reference. Throws <see cref="SqlFlowException"/> for an empty
    /// value or a malformed alias.</summary>
    public static ConnectionRef Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            throw new SqlFlowException("A connection reference must not be empty.");
        }

        if (trimmed[0] == '@')
        {
            var alias = trimmed[1..];
            if (!IsValidAlias(alias))
            {
                throw new SqlFlowException(
                    $"Invalid connection alias '{trimmed}'. An alias is '@' followed by letters, digits, '_', '.', or '-'.");
            }

            return new ConnectionRef(ConnectionRefKind.Alias, alias);
        }

        // Everything else is an inline connection string. A ${scheme:locator} reference inside it is
        // expanded later by the resolver; classification does not need to look inside.
        return new ConnectionRef(ConnectionRefKind.Inline, trimmed);
    }

    private static bool IsValidAlias(string alias)
        => alias.Length > 0 && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
