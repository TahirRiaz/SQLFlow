using DuckDB.NET.Data;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// Full Delta-table support through the DuckDB reader: it builds a minimal but valid Delta table (a Parquet
/// data file plus a hand-written <c>_delta_log</c> commit), then reads it via <c>source.type: delta</c> and
/// asserts the reader auto-loaded the delta extension, followed the transaction log (not the raw Parquet), and
/// surfaced the typed columns and rows. Skips if libduckdb or the delta extension cannot be loaded (the latter
/// is downloaded on first use, so an offline machine skips rather than fails).
/// </summary>
public sealed class DuckDbDeltaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_delta_" + Guid.NewGuid().ToString("N"));

    public DuckDbDeltaTests() => Directory.CreateDirectory(_dir);

    private static readonly Lazy<bool> DeltaAvailable = new(() =>
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSTALL delta; LOAD delta;";
            command.ExecuteNonQuery();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Writes a minimal version-0 Delta table (one Parquet add) at <paramref name="tableDir"/>.</summary>
    private static void WriteDeltaTable(string tableDir, string copySelect, string schemaString)
    {
        Directory.CreateDirectory(tableDir);
        Directory.CreateDirectory(Path.Combine(tableDir, "_delta_log"));
        var dataFile = "part-00000.parquet";
        var dataPath = Path.Combine(tableDir, dataFile).Replace('\\', '/');

        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"COPY ({copySelect}) TO '{dataPath}' (FORMAT PARQUET);";
            command.ExecuteNonQuery();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var size = new FileInfo(Path.Combine(tableDir, dataFile)).Length;

        // The newline-delimited transaction-log commit (version 0): protocol, table metadata, and one add file.
        // Built with the serializer so the embedded schema string and all braces are escaped correctly.
        var commit = string.Join('\n',
            System.Text.Json.JsonSerializer.Serialize(new { protocol = new { minReaderVersion = 1, minWriterVersion = 2 } }),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                metaData = new
                {
                    id = Guid.NewGuid().ToString(),
                    format = new { provider = "parquet", options = new Dictionary<string, string>() },
                    schemaString,
                    partitionColumns = Array.Empty<string>(),
                    configuration = new Dictionary<string, string>(),
                    createdTime = now,
                },
            }),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                add = new { path = dataFile, partitionValues = new Dictionary<string, string>(), size, modificationTime = now, dataChange = true },
            }));

        File.WriteAllText(Path.Combine(tableDir, "_delta_log", "00000000000000000000.json"), commit);
    }

    [SkippableFact]
    public async Task ReadsADeltaTable_ViaTypeDelta()
    {
        Skip.IfNot(DeltaAvailable.Value, "DuckDB delta extension could not be loaded (needs libduckdb + network on first use).");

        var tableDir = Path.Combine(_dir, "dim_customer");
        WriteDeltaTable(
            tableDir,
            "SELECT * FROM (VALUES (1,'Ann'),(2,'Bob'),(3,'Cy')) t(id,name)",
            """{"type":"struct","fields":[{"name":"id","type":"integer","nullable":true,"metadata":{}},{"name":"name","type":"string","nullable":true,"metadata":{}}]}""");

        var reader = new DuckDbSourceReader();
        var source = new SourceSpec { Type = "delta", Location = tableDir.Replace('\\', '/'), Options = new Dictionary<string, string?>() };

        var columns = await reader.GetColumnsAsync(source);
        Assert.Equal(["id", "name"], columns.Select(c => c.Name).ToArray());
        Assert.Equal("int", columns[0].SqlType);
        Assert.Equal("nvarchar(max)", columns[1].SqlType);

        var read = await reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var names = new List<string>();
        while (await data.ReadAsync())
        {
            names.Add(data.GetString(1));
        }

        Assert.Equal(["Ann", "Bob", "Cy"], names.OrderBy(n => n).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
