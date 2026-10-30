using SqlFlow.Cli.Remote;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>The tests that point <c>SQLFLOW_CREDENTIALS_FILE</c> at a file of their own, which is process-wide.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CredentialFileSuite
{
    public const string Name = "Credential file";
}

/// <summary>The file a person's sign-in is shared through: what the command line reads of it, and what it leaves of what
/// another client wrote there.</summary>
[Collection(CredentialFileSuite.Name)]
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sqlflow-credentials-{Guid.NewGuid():N}");
    private readonly string? _previous = Environment.GetEnvironmentVariable("SQLFLOW_CREDENTIALS_FILE");

    public CredentialStoreTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("SQLFLOW_CREDENTIALS_FILE", Path.Combine(_directory, "credentials.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLFLOW_CREDENTIALS_FILE", _previous);
        Directory.Delete(_directory, recursive: true);
    }

    private static string SharedFormat()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "tools", "sqlflow-mcp", "tests", "shared-credentials.json");
            if (File.Exists(file))
            {
                return file;
            }
        }

        throw new FileNotFoundException($"Could not find tools/sqlflow-mcp/tests/shared-credentials.json above '{AppContext.BaseDirectory}'.");
    }

    [Fact]
    public void The_file_the_MCP_server_reads_is_the_file_this_reads()
    {
        File.Copy(SharedFormat(), CredentialStore.FilePath);

        var token = CredentialStore.Load(new Uri("https://CONTROL.example.com/"));
        var session = CredentialStore.Load(new Uri("http://localhost:5000"));

        Assert.Equal("sqlf_a-personal-access-token", token?.Token);
        Assert.Equal("alice", token?.Username);
        Assert.False(token?.Renews);
        Assert.Equal("a.session.token", session?.Token);
        Assert.True(session?.Renews);
        Assert.Equal("read operate author", session?.Scope);
    }

    [Fact]
    public void A_session_survives_being_written_back_with_what_makes_it_renew()
    {
        var url = new Uri("https://control.example.com");
        CredentialStore.Save(url, new StoredCredential("a", null, null, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), Renews: true, Scope: "read"));

        var held = CredentialStore.Load(url);

        Assert.True(held?.Renews);
        Assert.Equal("read", held?.Scope);
        Assert.False(File.Exists(CredentialStore.FilePath + ".new"));
    }

    [Fact]
    public void One_control_plane_in_the_file_is_the_one_a_script_means()
    {
        Assert.Null(CredentialStore.OnlyUrl());

        CredentialStore.Save(new Uri("https://a.example.com"), new StoredCredential("a", null, null, DateTime.UtcNow, null));
        Assert.Equal(new Uri("https://a.example.com"), CredentialStore.OnlyUrl());

        CredentialStore.Save(new Uri("https://b.example.com"), new StoredCredential("b", null, null, DateTime.UtcNow, null));
        Assert.Null(CredentialStore.OnlyUrl());
    }
}
