namespace SqlFlow.Core.Secrets;

/// <summary>
/// The tool-boundary guard against the accident every enterprise eventually has: a real credential pasted
/// into a flow document that lives under source control. A connection value in YAML is fine when it is a
/// reference (<c>${env:...}</c>, <c>${keyvault:...}</c>, <c>@alias</c>) or a passwordless string (Integrated
/// Security, Active Directory Default); it is a finding when it embeds a secret-bearing keyword. The CLI
/// warns on validate AND run, so the mistake surfaces on the first local check, before the commit.
/// </summary>
public static class SecretHygiene
{
    /// <summary>The secret-bearing keywords of the supported providers' connection strings (SQL Server,
    /// MySQL, PostgreSQL), compared without case.</summary>
    private static readonly string[] SecretKeywords =
    [
        "password=", "pwd=", "client secret=", "clientsecret=", "secret=", "accesskey=", "access key=", "sharedaccesskey=",
    ];

    /// <summary>True when <paramref name="reference"/> is a literal that embeds a credential: not a
    /// <c>${...}</c> reference, not an <c>@alias</c>, and carrying a secret-bearing keyword.</summary>
    public static bool LooksLikeEmbeddedSecret(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmed = reference.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) || trimmed.StartsWith('@'))
        {
            return false;
        }

        return SecretKeywords.Any(keyword => trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Scrubs credential-bearing fragments from a message that may quote external input (an
    /// exception wrapping a connection string): every secret keyword's value collapses to [redacted].
    /// Applied wherever third-party error text reaches a report or log.</summary>
    public static string RedactedMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var result = message;
        foreach (var keyword in SecretKeywords)
        {
            var searchFrom = 0;
            int index;
            while (searchFrom < result.Length
                   && (index = result.IndexOf(keyword, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var valueStart = index + keyword.Length;
                var valueEnd = result.IndexOf(';', valueStart);
                if (valueEnd < 0)
                {
                    valueEnd = result.Length;
                }

                result = result[..valueStart] + "[redacted]" + result[valueEnd..];
                searchFrom = valueStart + "[redacted]".Length;
            }
        }

        return result;
    }

    /// <summary>
    /// The redacted, full causal chain of an exception's messages, for persisting or reporting a failure. Many
    /// framework wrappers carry no diagnostic value themselves (EF Core's DbUpdateException says only "See the
    /// inner exception for details" while the SqlException under it names the actual problem), so recording just
    /// the outer <see cref="Exception.Message"/> loses the reason. This walks the chain outer-to-inner, flattens
    /// AggregateException branches, skips an inner message a wrapper already quotes, and joins the rest with
    /// " -> " before redacting.
    /// </summary>
    public static string RedactedMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var messages = new List<string>();
        AppendMessageChain(exception, messages);
        return RedactedMessage(messages.Count > 0 ? string.Join(" -> ", messages) : exception.GetType().Name);
    }

    private static void AppendMessageChain(Exception exception, List<string> messages)
    {
        if (exception is AggregateException aggregate)
        {
            // The aggregate's own message just parenthesizes its children; recurse into the flattened branches.
            foreach (var branch in aggregate.Flatten().InnerExceptions)
            {
                AppendMessageChain(branch, messages);
            }

            return;
        }

        var message = exception.Message.Trim();
        if (message.Length > 0 && !messages.Any(m => m.Contains(message, StringComparison.Ordinal)))
        {
            messages.Add(message);
        }

        if (exception.InnerException is { } inner)
        {
            AppendMessageChain(inner, messages);
        }
    }

    /// <summary>The warning line for one offending connection. The value itself is never echoed.</summary>
    public static string Warning(string connectionName, string source)
        => $"WARN  {source}: connection '{connectionName}' embeds a credential in the document. Files under " +
           $"source control must carry references instead: use ${{env:NAME}}, ${{keyvault:vault/secret}}, or a " +
           $"bare '{connectionName}:' (which resolves ${{env:{Connections.ConnectionConvention.EnvironmentVariable(connectionName)}}}); " +
           "put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.";
}
