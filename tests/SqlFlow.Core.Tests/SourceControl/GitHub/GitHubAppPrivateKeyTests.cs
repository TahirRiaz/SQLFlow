using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SqlFlow.Core.Secrets;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>
/// A GitHub App's private key is read in every form a deployment carries it (the .pem as GitHub hands it out, its PKCS#8
/// conversion, one line with \n escapes, quoted, base64-wrapped, from a file or a reference), and everything that is not
/// an unencrypted RSA private key of the App's size is refused with what it looks like instead. No message ever carries
/// the key material.
/// </summary>
public sealed class GitHubAppPrivateKeyTests
{
    private const string Origin = "from TEST_KEY";

    public static TheoryData<string, string> AcceptedForms()
    {
        var pkcs1 = TestKeys.Pkcs1Pem;
        return new TheoryData<string, string>
        {
            { "PKCS#1, as GitHub hands it out", pkcs1 },
            { "PKCS#8", TestKeys.Pkcs8Pem },
            { "CRLF line endings", pkcs1.ReplaceLineEndings("\r\n") },
            { "one line with \\n escapes", pkcs1.ReplaceLineEndings("\\n") },
            { "one line with \\r\\n escapes", pkcs1.ReplaceLineEndings("\\r\\n") },
            { "double quoted", "\"" + pkcs1.ReplaceLineEndings("\\n") + "\"" },
            { "single quoted", "'" + pkcs1 + "'" },
            { "surrounded by whitespace", "\n  \t" + pkcs1 + "\n\n  " },
            { "with a byte order mark", "\uFEFF" + pkcs1 },
            { "base64 of the PEM", Convert.ToBase64String(Encoding.UTF8.GetBytes(pkcs1)) },
            { "base64 of the PEM, wrapped at 76", Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(pkcs1)), 76) },
        };
    }

    [Theory]
    [MemberData(nameof(AcceptedForms))]
    public void Import_ReadsEveryFormADeploymentCarriesTheKeyIn(string form, string material)
    {
        using var key = GitHubAppPrivateKey.Import(material, Origin);

        Assert.True(key.KeySize >= 2048, form);
        Assert.Equal(TestKeys.Key.ExportParameters(false).Modulus, key.ExportParameters(false).Modulus);
    }

    [Fact]
    public void Import_RefusesAnEncryptedPkcs8Key()
    {
        var encrypted = TestKeys.Key.ExportEncryptedPkcs8PrivateKeyPem(
            "passphrase", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(encrypted, Origin));

        Assert.Contains("encrypted", ex.Message, StringComparison.Ordinal);
        AssertNoMaterial(ex.Message, encrypted);
    }

    [Fact]
    public void Import_RefusesALegacyEncryptedKey()
    {
        var body = TestKeys.Pkcs1Pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal));
        var legacy = "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,00112233445566778899AABBCCDDEEFF\n\n"
                     + string.Join('\n', body) + "\n-----END RSA PRIVATE KEY-----";

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(legacy, Origin));

        Assert.Contains("encrypted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesAPublicKey()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(TestKeys.Key.ExportSubjectPublicKeyInfoPem(), Origin));
        Assert.Contains("public key", ex.Message, StringComparison.Ordinal);

        var pkcs1 = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(TestKeys.Key.ExportRSAPublicKeyPem(), Origin));
        Assert.Contains("public key", pkcs1.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesACertificate()
    {
        var request = new CertificateRequest("CN=test", TestKeys.Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(certificate.ExportCertificatePem(), Origin));

        Assert.Contains("certificate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesAnEcKey_InEitherPemType()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var sec1 = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(ec.ExportECPrivateKeyPem(), Origin));
        Assert.Contains("EC key", sec1.Message, StringComparison.Ordinal);

        // PKCS#8 says only "PRIVATE KEY": the import finds it is no RSA key.
        var pkcs8 = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(ec.ExportPkcs8PrivateKeyPem(), Origin));
        Assert.Contains("not a valid RSA private key", pkcs8.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesAnOpenSshKey()
    {
        const string openSsh = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ==\n-----END OPENSSH PRIVATE KEY-----";

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(openSsh, Origin));

        Assert.Contains("OpenSSH", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesAKeySmallerThanGitHubIssues()
    {
        using var small = RSA.Create(1024);

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(small.ExportRSAPrivateKeyPem(), Origin));

        Assert.Contains("1024-bit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesACorruptKeyBody_WithoutQuotingIt()
    {
        var corrupt = "-----BEGIN RSA PRIVATE KEY-----\nTUlJRXBBSUJBQUtDQVFFQXRoaXNpc25vdGFrZXk=\n-----END RSA PRIVATE KEY-----";

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(corrupt, Origin));

        Assert.Contains("not a valid RSA private key", ex.Message, StringComparison.Ordinal);
        AssertNoMaterial(ex.Message, corrupt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("\"\"")]
    public void Import_RefusesAnEmptyValue(string material)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(material, Origin));
        Assert.Contains("is empty", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ghp_notakeyatall0123456789abcdef")]
    [InlineData("C:\\keys\\app.pem")]
    [InlineData("bm90IGEgcGVtIGtleQ==")]
    public void Import_RefusesWhatIsNeitherPemNorBase64OfPem_NamingTheAcceptedForms(string material)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(material, Origin));

        Assert.Contains("is not a PEM key", ex.Message, StringComparison.Ordinal);
        Assert.Contains("base64", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Origin, ex.Message, StringComparison.Ordinal);
        AssertNoMaterial(ex.Message, material);
    }

    [Fact]
    public void Import_RefusesMaterialTooLongToBeAKey()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppPrivateKey.Import(new string('A', GitHubAppKeySource.MaxMaterialLength + 1), Origin));
        Assert.Contains("too long", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeySource_FromVariable_RefusesAValueThatIsNotAKey_NamingTheVariableAndNotTheValue()
    {
        const string pasted = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz";

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppKeySource.FromVariable("SQLFLOW_GITHUB_APP_PRIVATE_KEY", pasted));

        Assert.Contains("SQLFLOW_GITHUB_APP_PRIVATE_KEY", ex.Message, StringComparison.Ordinal);
        AssertNoMaterial(ex.Message, pasted);
    }

    [Fact]
    public void KeySource_FromVariable_TakesAWholeReference_AsAReference()
    {
        var source = GitHubAppKeySource.FromVariable("SQLFLOW_GITHUB_APP_PRIVATE_KEY", "  ${keyvault:vault/github-app-key} ");

        Assert.Equal("${keyvault:vault/github-app-key}", source.Reference);
        Assert.Equal("SQLFLOW_GITHUB_APP_PRIVATE_KEY (${keyvault:vault/github-app-key})", source.Description);
    }

    [Fact]
    public void KeySource_FromVariable_DescribesAnInlineKeyByTheVariableAlone()
    {
        var source = GitHubAppKeySource.FromVariable("SQLFLOW_GITHUB_APP_PRIVATE_KEY", TestKeys.Pkcs1Pem);

        Assert.Null(source.Reference);
        Assert.Equal("SQLFLOW_GITHUB_APP_PRIVATE_KEY", source.Description);
        Assert.Equal("SQLFLOW_GITHUB_APP_PRIVATE_KEY", source.ToString());
    }

    [Fact]
    public void KeySource_FromFile_RefusesARelativePath()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppKeySource.FromFile("SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH", "keys/app.pem"));
        Assert.Contains("absolute path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeySource_FromFile_ReadsTheFile_AndSaysWhenItIsMissingOrTooLarge()
    {
        var folder = Directory.CreateTempSubdirectory("sqlflow_ghapp_key_");
        try
        {
            var path = Path.Combine(folder.FullName, "app.pem");
            await File.WriteAllTextAsync(path, TestKeys.Pkcs1Pem);
            var resolver = new SecretResolver([new EnvSecretProvider()]);

            using (var key = await GitHubAppKeySource.FromFile("KEY_PATH", path).LoadAsync("Iv23li", resolver, CancellationToken.None))
            {
                Assert.Equal(TestKeys.Key.ExportParameters(false).Modulus, key.ExportParameters(false).Modulus);
            }

            var missing = await Assert.ThrowsAsync<GitHubAppException>(
                () => GitHubAppKeySource.FromFile("KEY_PATH", Path.Combine(folder.FullName, "absent.pem")).LoadAsync("Iv23li", resolver, CancellationToken.None));
            Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);
            Assert.Contains("absent.pem", missing.Message, StringComparison.Ordinal);

            var large = Path.Combine(folder.FullName, "large.pem");
            await File.WriteAllTextAsync(large, new string('A', GitHubAppKeySource.MaxMaterialLength + 10));
            var tooLarge = await Assert.ThrowsAsync<GitHubAppException>(
                () => GitHubAppKeySource.FromFile("KEY_PATH", large).LoadAsync("Iv23li", resolver, CancellationToken.None));
            Assert.Contains("too large", tooLarge.Message, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task KeySource_FromReference_SaysWhichReferenceCouldNotBeRead()
    {
        var resolver = new SecretResolver([new TestKeyProvider(new Dictionary<string, string>())]);
        var source = GitHubAppKeySource.FromReference("${testkey:missing}", "${testkey:missing}");

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => source.LoadAsync("Iv23li", resolver, CancellationToken.None));

        Assert.Contains("could not read the private key of GitHub App Iv23li from ${testkey:missing}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testkey 'missing' is not set", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeySource_FromReference_ReadsThroughTheResolver()
    {
        var resolver = new SecretResolver([new TestKeyProvider(new Dictionary<string, string> { ["k"] = TestKeys.Pkcs8Pem })]);

        using var key = await GitHubAppKeySource.FromReference("${testkey:k}", "${testkey:k}").LoadAsync("Iv23li", resolver, CancellationToken.None);

        Assert.Equal(TestKeys.Key.ExportParameters(false).Modulus, key.ExportParameters(false).Modulus);
    }

    private static void AssertNoMaterial(string message, string material)
    {
        // No line of the material longer than a few characters may appear in the message.
        foreach (var line in material.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length >= 16 && !line.StartsWith("-----", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(line, message, StringComparison.Ordinal);
            }
        }
    }

    private static string Wrap(string text, int width)
        => string.Join('\n', Enumerable.Range(0, (text.Length + width - 1) / width).Select(i => text.Substring(i * width, Math.Min(width, text.Length - (i * width)))));
}
