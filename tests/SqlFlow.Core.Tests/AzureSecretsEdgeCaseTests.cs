using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping robustness edge cases for the AzureSecrets feature area: the secret-reference grammar
/// (scheme/locator boundaries, empty and adjacent references), the bare-alias to SQLFLOW_CONN_ convention
/// (ASCII folding of non-alphanumerics, including non-ASCII letters), connection-reference classification,
/// embedded-secret detection and credential redaction, and the Azure storage account parser plus its
/// SQLFLOW_AZURE_AUTH mode mapping. Every test is pure and in-memory: it injects an environment accessor or
/// passes fixed inputs, so it is deterministic and never touches the process environment unless it scopes and
/// restores that state itself.
/// </summary>
public sealed class AzureSecretsEdgeCaseTests
{
    private static readonly ISecretResolver Resolver = new SecretResolver([new EnvSecretProvider()]);

    /// <summary>Injects a fixed environment so the Azure mapping is exercised without process state.</summary>
    private static AzureStorageCredentialProvider StorageWithEnv(params (string Key, string? Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return new AzureStorageCredentialProvider(k => map.TryGetValue(k, out var v) ? v : null);
    }

    /// <summary>A fixed environment accessor over the supplied pairs (case-insensitive, like the real one).</summary>
    private static Func<string, string?> EnvOf(params (string Key, string? Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    /// <summary>Sets one process variable, runs the action, then always clears it. Used only where the
    /// behavior under test reads the real process environment (the env-secret provider).</summary>
    private static void WithProcessVariable(string name, string value, Action body)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // --- secret-reference grammar: scheme and locator boundaries ---

    [Fact]
    public void Resolve_EmptyLocator_IsNotAReference_LeftVerbatim()
    {
        // The locator group is [^}]+ (one or more), so ${env:} does not match and is plain text.
        const string raw = "prefix-${env:}-suffix";
        Assert.Equal(raw, Resolver.Resolve(raw));
    }

    [Fact]
    public void Resolve_SchemeWithDigit_IsNotAReference_LeftVerbatim()
    {
        // The scheme group is [a-zA-Z]+, so a digit in the scheme means it is not a reference at all.
        const string raw = "x=${env2:NAME};";
        Assert.Equal(raw, Resolver.Resolve(raw));
    }

    [Fact]
    public void Resolve_DollarWithoutBrace_IsLiteral()
        => Assert.Equal("price is $5 and $env:X", Resolver.Resolve("price is $5 and $env:X"));

    [Fact]
    public void Resolve_UppercaseScheme_MatchesProvider_CaseInsensitively()
    {
        WithProcessVariable("SQLFLOW_EDGE_UPPER_SCHEME", "ok", () =>
            Assert.Equal("v=ok", Resolver.Resolve("v=${ENV:SQLFLOW_EDGE_UPPER_SCHEME}")));
    }

    [Fact]
    public void Resolve_LocatorWithColon_KeepsEverythingUpToBrace()
    {
        // [^}]+ greedily takes the colon-bearing remainder, so the locator is the full "a:b:c" path. The
        // env provider then reports that exact (missing) variable name.
        var ex = Assert.Throws<SqlFlowException>(() => Resolver.Resolve("${env:SQLFLOW_EDGE_A:B:C_MISSING}"));
        Assert.Contains("SQLFLOW_EDGE_A:B:C_MISSING", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhitespaceInsideBraces_IsPartOfTheLocator()
    {
        // Spaces are part of [^}]+, so the variable name carries them verbatim and is not found.
        var ex = Assert.Throws<SqlFlowException>(() => Resolver.Resolve("${env: SQLFLOW_EDGE_SPACED }"));
        Assert.Contains(" SQLFLOW_EDGE_SPACED ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AdjacentReferences_NoSeparator_BothExpand()
    {
        WithProcessVariable("SQLFLOW_EDGE_ADJ_A", "AA", () =>
            WithProcessVariable("SQLFLOW_EDGE_ADJ_B", "BB", () =>
                Assert.Equal("AABB", Resolver.Resolve("${env:SQLFLOW_EDGE_ADJ_A}${env:SQLFLOW_EDGE_ADJ_B}"))));
    }

    [Fact]
    public void Resolve_ReferenceSpanningNewlinesAround_IsExpanded()
    {
        WithProcessVariable("SQLFLOW_EDGE_MULTILINE", "R", () =>
            Assert.Equal("line1\nR\nline3", Resolver.Resolve("line1\n${env:SQLFLOW_EDGE_MULTILINE}\nline3")));
    }

    [Fact]
    public void Resolve_EmptyString_ReturnsEmpty()
        => Assert.Equal(string.Empty, Resolver.Resolve(string.Empty));

    [Fact]
    public void Resolve_UnknownScheme_ErrorNamesSchemeAndOriginalValue()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Resolver.Resolve("conn=${vault:kv/dw};opt=1"));
        Assert.Contains("vault", ex.Message, StringComparison.Ordinal);
        Assert.Contains("conn=${vault:kv/dw};opt=1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_EmbeddedReference_KeepsSurroundingLiteralText()
    {
        await WithProcessVariableAsync("SQLFLOW_EDGE_ASYNC_EMBED", "MID", async () =>
            Assert.Equal(
                "head;MID;tail",
                await Resolver.ResolveAsync("head;${env:SQLFLOW_EDGE_ASYNC_EMBED};tail")));
    }

    [Fact]
    public async Task ResolveAsync_NoReferences_ReturnsSameInstanceContent()
        => Assert.Equal("Server=localhost;Trusted_Connection=True", await Resolver.ResolveAsync("Server=localhost;Trusted_Connection=True"));

    [Fact]
    public void Resolve_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => Resolver.Resolve(null!));

    /// <summary>Async sibling of <see cref="WithProcessVariable"/>; restores the variable on every path.</summary>
    private static async Task WithProcessVariableAsync(string name, string value, Func<Task> body)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // --- bare-alias to SQLFLOW_CONN_ convention: ASCII folding ---

    [Theory]
    [InlineData("dwh", "SQLFLOW_CONN_DWH")]
    [InlineData("DWH", "SQLFLOW_CONN_DWH")]
    [InlineData("Dw9", "SQLFLOW_CONN_DW9")]
    [InlineData("a", "SQLFLOW_CONN_A")]
    [InlineData("shop.prod.eu", "SQLFLOW_CONN_SHOP_PROD_EU")]
    [InlineData("a b\tc", "SQLFLOW_CONN_A_B_C")]
    [InlineData("...", "SQLFLOW_CONN____")]
    [InlineData("9lead", "SQLFLOW_CONN_9LEAD")]
    public void Convention_FoldsEveryNonAlphanumericToUnderscore(string name, string expected)
        => Assert.Equal(expected, ConnectionConvention.EnvironmentVariable(name));

    [Theory]
    [InlineData("ærø", "SQLFLOW_CONN__R_")]
    [InlineData("café", "SQLFLOW_CONN_CAF_")]
    [InlineData("naïve.db", "SQLFLOW_CONN_NA_VE_DB")]
    public void Convention_FoldsNonAsciiLetters_BecauseTheRuleIsAsciiOnly(string name, string expected)
    {
        // The convention uses IsAsciiLetterOrDigit: non-ASCII letters are NOT alphanumeric here, so they
        // fold to underscore. This keeps the variable name portable across case-sensitive Linux shells.
        Assert.Equal(expected, ConnectionConvention.EnvironmentVariable(name));
    }

    [Fact]
    public void Convention_TrimsBeforeFolding()
        => Assert.Equal("SQLFLOW_CONN_DWH", ConnectionConvention.EnvironmentVariable("   dwh   "));

    [Fact]
    public void Convention_ReferenceWrapsTheEnvVariableName()
    {
        Assert.Equal("${env:SQLFLOW_CONN_SHOP_PROD_EU}", ConnectionConvention.Reference("shop.prod.eu"));
        Assert.Equal("SQLFLOW_CONN_SHOP_PROD_EU", ConnectionConvention.EnvironmentVariable("shop.prod.eu"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Convention_BlankName_Throws(string name)
        => Assert.Throws<ArgumentException>(() => ConnectionConvention.EnvironmentVariable(name));

    [Fact]
    public void Convention_NullName_Throws()
        => Assert.Throws<ArgumentNullException>(() => ConnectionConvention.EnvironmentVariable(null!));

    // --- connection-reference classification: alias charset and inline trimming ---

    [Theory]
    [InlineData("@a.")]
    [InlineData("@a..b")]
    [InlineData("@-")]
    [InlineData("@_")]
    [InlineData("@.")]
    [InlineData("@a-b.c_d")]
    public void ConnectionRef_AliasCharset_AcceptsDotsDashesUnderscores(string raw)
    {
        var reference = ConnectionRef.Parse(raw);
        Assert.Equal(ConnectionRefKind.Alias, reference.Kind);
        Assert.Equal(raw.Trim()[1..], reference.Value);
    }

    [Theory]
    [InlineData("@a@b")]      // an interior '@' is not in the alias charset
    [InlineData("@a:b")]      // colon
    [InlineData("@a b")]      // interior space
    [InlineData("@a\tb")]     // interior tab
    [InlineData("@a#b")]      // hash
    public void ConnectionRef_AliasWithIllegalCharacter_Throws(string raw)
        => Assert.Throws<SqlFlowException>(() => ConnectionRef.Parse(raw));

    [Fact]
    public void ConnectionRef_AliasTrimsTabsAndNewlines()
    {
        var reference = ConnectionRef.Parse("\t @dwh \n");
        Assert.Equal(ConnectionRefKind.Alias, reference.Kind);
        Assert.Equal("dwh", reference.Value);
    }

    [Fact]
    public void ConnectionRef_InlineValue_IsTrimmed()
    {
        var reference = ConnectionRef.Parse("   Server=localhost;Integrated Security=True   ");
        Assert.Equal(ConnectionRefKind.Inline, reference.Kind);
        Assert.Equal("Server=localhost;Integrated Security=True", reference.Value);
    }

    [Fact]
    public void ConnectionRef_BarewordWithoutSigil_IsInline_NotAlias()
    {
        // No '@' sigil, so even a single bare word is an inline value (classification, not validation).
        var reference = ConnectionRef.Parse("plainword");
        Assert.Equal(ConnectionRefKind.Inline, reference.Kind);
        Assert.Equal("plainword", reference.Value);
    }

    [Fact]
    public void ConnectionRef_SecretReference_WithColonLocator_IsInline()
        => Assert.Equal(ConnectionRefKind.Inline, ConnectionRef.Parse("${keyvault:vault:dw-conn}").Kind);

    // --- embedded-secret detection ---

    [Theory]
    [InlineData("Server=x;SharedAccessKey=abc==", true)]
    [InlineData("Server=x;Client Secret=zzz", true)]
    [InlineData("Server=x;ClientSecret=zzz", true)]
    [InlineData("Server=x;Access Key=zzz", true)]
    [InlineData("server=x;PASSWORD=UPPER", true)]               // keyword match is case-insensitive
    [InlineData("Server=x;Database=passwordless_db", false)]    // 'password' without '=' is not a keyword
    [InlineData("Server=x;Encrypt=True", false)]                // unrelated keyword
    [InlineData("Server=x;Column Encryption Setting=Enabled", false)]
    public void Hygiene_EmbeddedSecret_KeywordMatchingIsPreciseAndCaseInsensitive(string reference, bool expected)
        => Assert.Equal(expected, SecretHygiene.LooksLikeEmbeddedSecret(reference));

    [Theory]
    [InlineData("   ${env:SQLFLOW_CONN_DWH}")]   // leading whitespace before a reference
    [InlineData("\t@dwh")]                        // leading tab before an alias
    [InlineData("  @alias-with-pwd")]             // alias name contains 'pwd' but is not 'pwd='
    public void Hygiene_LeadingWhitespaceBeforeReferenceOrAlias_IsNotFlagged(string reference)
        => Assert.False(SecretHygiene.LooksLikeEmbeddedSecret(reference));

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Hygiene_WhitespaceOnly_IsNotFlagged(string reference)
        => Assert.False(SecretHygiene.LooksLikeEmbeddedSecret(reference));

    // --- credential redaction ---

    [Fact]
    public void Redact_SingleKeyword_CollapsesValueUpToSemicolon()
        => Assert.Equal(
            "Server=x;Password=[redacted];Database=y",
            SecretHygiene.RedactedMessage("Server=x;Password=hunter2;Database=y"));

    [Fact]
    public void Redact_KeywordAtEnd_NoTrailingSemicolon_CollapsesToEnd()
        => Assert.Equal(
            "Server=x;Pwd=[redacted]",
            SecretHygiene.RedactedMessage("Server=x;Pwd=hunter2"));

    [Fact]
    public void Redact_MultipleDistinctKeywords_AllCollapsed()
    {
        var redacted = SecretHygiene.RedactedMessage("User ID=u;Password=p1;AccountEndpoint=e;AccessKey=k1;");
        Assert.Contains("Password=[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("AccessKey=[redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p1", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("k1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RepeatedSameKeyword_CollapsesEveryOccurrence()
    {
        var redacted = SecretHygiene.RedactedMessage("password=a;x=1;password=b;y=2");
        Assert.Equal("password=[redacted];x=1;password=[redacted];y=2", redacted);
    }

    [Fact]
    public void Redact_CaseInsensitiveKeyword_PreservesOriginalKeywordCasing()
        => Assert.Equal("PassWord=[redacted];keep", SecretHygiene.RedactedMessage("PassWord=secretvalue;keep"));

    [Fact]
    public void Redact_NoSecretKeyword_ReturnsMessageUnchanged()
    {
        const string message = "Login failed for user 'svc'. Server was not found.";
        Assert.Equal(message, SecretHygiene.RedactedMessage(message));
    }

    [Fact]
    public void Redact_EmptyValueBetweenKeywordAndSemicolon_StillReplacedWithMarker()
        => Assert.Equal("Password=[redacted];next", SecretHygiene.RedactedMessage("Password=;next"));

    [Fact]
    public void Redact_NullMessage_Throws()
        => Assert.Throws<ArgumentNullException>(() => SecretHygiene.RedactedMessage((string)null!));

    [Fact]
    public void Redact_NullException_Throws()
        => Assert.Throws<ArgumentNullException>(() => SecretHygiene.RedactedMessage((Exception)null!));

    [Fact]
    public void Redact_Exception_JoinsTheInnerChainAndSkipsMessagesTheWrapperAlreadyQuotes()
    {
        var inner = new InvalidOperationException("Invalid column name 'Hash'.");
        var outer = new InvalidOperationException("An error occurred while saving the entity changes. See the inner exception for details.", inner);

        Assert.Equal(
            "An error occurred while saving the entity changes. See the inner exception for details. -> Invalid column name 'Hash'.",
            SecretHygiene.RedactedMessage(outer));

        var quoting = new InvalidOperationException("wrapper says: Invalid column name 'Hash'.", inner);
        Assert.Equal("wrapper says: Invalid column name 'Hash'.", SecretHygiene.RedactedMessage(quoting));
    }

    [Fact]
    public void Redact_Exception_FlattensAggregatesAndRedactsInnerSecrets()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("branch one failed"),
            new AggregateException(new InvalidOperationException("login failed for Server=x;Password=hunter2")));

        Assert.Equal(
            "branch one failed -> login failed for Server=x;Password=[redacted]",
            SecretHygiene.RedactedMessage(aggregate));
    }

    [Fact]
    public void Hygiene_Warning_SanitizesTheNameIntoTheNamedVariable()
    {
        var warning = SecretHygiene.Warning("shop.prod.eu", "flows/shop.flow.yaml");
        Assert.Contains("${env:SQLFLOW_CONN_SHOP_PROD_EU}", warning, StringComparison.Ordinal);
        Assert.Contains("flows/shop.flow.yaml", warning, StringComparison.Ordinal);
        Assert.Contains(".sqlflow/env", warning, StringComparison.Ordinal);
    }

    // --- Azure storage account parsing ---

    [Theory]
    [InlineData("wasb://c@acct.blob.core.windows.net/p", "acct")]                 // wasb without trailing 's'
    [InlineData("azure://acct.dfs.core.windows.net/c/p", "acct")]                 // 'azure' scheme, bare well-known host
    [InlineData("wasb://c@acct.blob.core.windows.net:8080/p", "acct")]            // wasb with a port to strip
    [InlineData("abfss://c@acct.dfs.core.windows.net", "acct")]                   // authority only, no path
    [InlineData("AZ://ACCT.DFS.CORE.WINDOWS.NET/c", "ACCT")]                      // uppercase host, well-known match is case-insensitive
    [InlineData("abfss://c@acct123.dfs.core.windows.net/p", "acct123")]           // digits in the account label
    public void Storage_ParsesAccount_AcrossSchemesPortsAndBareHosts(string location, string expectedAccount)
    {
        var credential = StorageWithEnv().ResolveAzureStorage(location);
        Assert.NotNull(credential);
        Assert.Equal(expectedAccount, credential!.AccountName);
    }

    [Theory]
    [InlineData("abfss://c@my-acct.dfs.core.windows.net/p")]   // dash in account label fails the alnum gate
    [InlineData("abfss://c@.dfs.core.windows.net/p")]          // empty account label before the first dot
    [InlineData("abfss://plainhost/path")]                     // no '@' and not a well-known host: ambiguous
    [InlineData("://acct.dfs.core.windows.net/p")]             // empty scheme (schemeEnd <= 0)
    [InlineData("abfss://c@acct_underscore.dfs.core.windows.net/p")] // underscore is not alphanumeric
    [InlineData("   ")]                                        // whitespace-only location
    public void Storage_ReturnsNull_ForMalformedAmbiguousOrNonAlnumAccounts(string location)
        => Assert.Null(StorageWithEnv().ResolveAzureStorage(location));

    [Fact]
    public void Storage_NullLocation_ReturnsNull()
        => Assert.Null(StorageWithEnv().ResolveAzureStorage(null!));

    // --- SQLFLOW_AZURE_AUTH mode mapping through the storage credential ---

    [Fact]
    public void Storage_MsiModeAlias_MapsToManagedIdentity()
    {
        var credential = StorageWithEnv(("SQLFLOW_AZURE_AUTH", "msi"))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.ManagedIdentity, credential!.Mode);
    }

    [Fact]
    public void Storage_ModeValue_IsTrimmedAndCaseFolded()
    {
        var credential = StorageWithEnv(
                ("SQLFLOW_AZURE_AUTH", "  SP  "),
                ("AZURE_TENANT_ID", "t"),
                ("AZURE_CLIENT_ID", "c"),
                ("AZURE_CLIENT_SECRET", "s"))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.ServicePrincipal, credential!.Mode);
    }

    [Fact]
    public void Storage_UnknownMode_FallsBackToDefaultChain()
    {
        var credential = StorageWithEnv(("SQLFLOW_AZURE_AUTH", "okta"))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.DefaultChain, credential!.Mode);
        Assert.Null(credential.ClientId);
        Assert.Null(credential.ClientSecret);
        Assert.Null(credential.TenantId);
    }

    [Fact]
    public void Storage_ManagedIdentity_BlankClientId_IsNormalizedToNull()
    {
        var credential = StorageWithEnv(("SQLFLOW_AZURE_AUTH", "mi"), ("AZURE_CLIENT_ID", "   "))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.ManagedIdentity, credential!.Mode);
        Assert.Null(credential.ClientId);
    }

    [Fact]
    public void Storage_ServicePrincipal_MissingOnlyClientSecret_ThrowsNamingThatVariable()
    {
        var provider = StorageWithEnv(
            ("SQLFLOW_AZURE_AUTH", "sp"),
            ("AZURE_TENANT_ID", "t"),
            ("AZURE_CLIENT_ID", "c")); // AZURE_CLIENT_SECRET deliberately absent
        var ex = Assert.Throws<SqlFlowException>(
            () => provider.ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p"));
        Assert.Contains("AZURE_CLIENT_SECRET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_ServicePrincipal_BlankTenant_IsTreatedAsMissing()
    {
        var provider = StorageWithEnv(
            ("SQLFLOW_AZURE_AUTH", "sp"),
            ("AZURE_TENANT_ID", "   "),  // whitespace counts as missing (IsNullOrWhiteSpace)
            ("AZURE_CLIENT_ID", "c"),
            ("AZURE_CLIENT_SECRET", "s"));
        var ex = Assert.Throws<SqlFlowException>(
            () => provider.ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p"));
        Assert.Contains("AZURE_TENANT_ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_RunningInAzure_FromFunctionsSignal_IsCarriedOntoCredential()
    {
        var credential = StorageWithEnv(("FUNCTIONS_WORKER_RUNTIME", "dotnet"))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.True(credential!.RunningInAzure);
    }

    // --- SQLFLOW_AZURE_AUTH mode mapping, exercised directly ---

    [Theory]
    [InlineData("serviceprincipal", CloudAuthMode.ServicePrincipal)]
    [InlineData("sp", CloudAuthMode.ServicePrincipal)]
    [InlineData("managedidentity", CloudAuthMode.ManagedIdentity)]
    [InlineData("mi", CloudAuthMode.ManagedIdentity)]
    [InlineData("msi", CloudAuthMode.ManagedIdentity)]
    [InlineData("azurecli", CloudAuthMode.AzureCli)]
    [InlineData("cli", CloudAuthMode.AzureCli)]
    [InlineData("azlogin", CloudAuthMode.AzureCli)]
    [InlineData("", CloudAuthMode.DefaultChain)]
    [InlineData("anything-else", CloudAuthMode.DefaultChain)]
    public void AzureAuth_Mode_MapsEveryAlias(string raw, CloudAuthMode expected)
        => Assert.Equal(expected, AzureAuth.Mode(EnvOf(("SQLFLOW_AZURE_AUTH", raw))));

    [Fact]
    public void AzureAuth_Mode_Unset_IsDefaultChain()
        => Assert.Equal(CloudAuthMode.DefaultChain, AzureAuth.Mode(EnvOf()));

    [Fact]
    public void AzureAuth_Mode_MixedCaseAndPadding_IsNormalized()
        => Assert.Equal(CloudAuthMode.AzureCli, AzureAuth.Mode(EnvOf(("SQLFLOW_AZURE_AUTH", "  AzLogin "))));
}
