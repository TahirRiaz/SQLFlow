using DuckDB.NET.Data;
using SqlFlow.Azure;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Live verification of the unified Azure auth path for DuckDB cloud reads. The ADLS test reads the table named
/// by <c>SQLFLOW_AZURE_TEST_ABFSS</c> using ONLY the ambient SQLFLOW_AZURE_AUTH intent (no hand-written secret),
/// so an operator can confirm service principal / managed identity / az-login actually authenticates ADLS in
/// their tenant: set the env vars and run. It skips when the path is not configured or libduckdb/the azure
/// extension cannot load. A second test proves a wired credential provider does not disturb ordinary local reads.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureStorageAuthIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_azauth_" + Guid.NewGuid().ToString("N"));

    public AzureStorageAuthIntegrationTests() => Directory.CreateDirectory(_dir);

    private static readonly Lazy<bool> DuckDbAvailable = new(() =>
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    [SkippableFact]
    public async Task AdlsRead_UsesUnifiedAuth_NoHandWrittenSecret()
    {
        var abfss = Environment.GetEnvironmentVariable("SQLFLOW_AZURE_TEST_ABFSS");
        Skip.If(string.IsNullOrWhiteSpace(abfss),
            "Set SQLFLOW_AZURE_TEST_ABFSS to an abfss:// parquet/Delta path (plus SQLFLOW_AZURE_AUTH + AZURE_* as needed) to verify live Azure storage auth.");
        Skip.IfNot(DuckDbAvailable.Value, "libduckdb (DuckDB.NET native) could not be loaded.");

        // type: delta when the path is a Delta table folder, else parquet; both auto-load the azure extension and
        // the reader auto-creates the storage secret from the ambient SQLFLOW_AZURE_AUTH intent.
        var type = abfss!.TrimEnd('/').EndsWith("_delta", StringComparison.OrdinalIgnoreCase) ? "delta" : "duckdb";
        var reader = new DuckDbSourceReader(new AzureStorageCredentialProvider());
        var source = new SourceSpec { Type = type, Location = abfss, Options = new Dictionary<string, string?>() };

        var columns = await reader.GetColumnsAsync(source);
        Assert.NotEmpty(columns);

        var read = await reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        // Reading the first row proves the credential authenticated the storage account.
        await data.ReadAsync();
    }

    [SkippableFact]
    public async Task LocalRead_WithProviderWired_IsUnaffected()
    {
        Skip.IfNot(DuckDbAvailable.Value, "libduckdb (DuckDB.NET native) could not be loaded.");

        var path = Path.Combine(_dir, "local.parquet").Replace('\\', '/');
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"COPY (SELECT * FROM (VALUES (1,'a'),(2,'b')) t(id,name)) TO '{path}' (FORMAT PARQUET);";
            command.ExecuteNonQuery();
        }

        // A real provider is wired, but the location is local: no Azure secret is generated and the read is normal.
        var reader = new DuckDbSourceReader(new AzureStorageCredentialProvider());
        var source = new SourceSpec { Type = "duckdb", Location = path, Options = new Dictionary<string, string?>() };

        var columns = await reader.GetColumnsAsync(source);
        Assert.Equal(["id", "name"], columns.Select(c => c.Name).ToArray());

        var read = await reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var rows = 0;
        while (await data.ReadAsync())
        {
            rows++;
        }

        Assert.Equal(2, rows);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
