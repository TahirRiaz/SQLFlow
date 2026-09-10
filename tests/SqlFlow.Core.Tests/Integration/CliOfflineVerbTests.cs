using System.Text.Json;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The CLI verbs that need no database at all, driven through the compiled binary: usage and exit-code
/// conventions, 'validate' across the document kinds, the JSON/XML introspection trio (paths, flatten,
/// discover), offline lineage over a small estate, client-side run-parameter validation, and the guard rails
/// of the remote verbs when no control plane is configured. The invoke (inv) and source-control (scm) kinds
/// are exercised through their loader unit tests elsewhere; the CLI's validate switch is kind-agnostic through
/// DocumentLoader, so the six kinds here cover every dispatch shape it has (file, ing, exp, sp, hc, batch).
/// Skips only when the built binary is missing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliOfflineVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_cliov_" + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public CliOfflineVerbTests()
    {
        Directory.CreateDirectory(_dir);
        _dll = CliBinary.DllPath() ?? string.Empty;
    }

    private void RequireCli() => Skip.If(_dll.Length == 0, "Built CLI not found; run 'dotnet build -c Release' first.");

    // ---- usage and exit codes -----------------------------------------------------------------------------

    [SkippableFact]
    public async Task NoArguments_PrintsUsage_Exit1()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, [], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("sqlflow validate", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task HelpFlag_PrintsUsage_Exit0()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["run", "whatever.yaml", "--help"], workingDirectory: _dir);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task UnknownCommand_Exit1_NamesIt()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["frobnicate", "x.yaml"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Unknown command 'frobnicate'", result.StdErr, StringComparison.Ordinal);
    }

    // ---- validate across the document kinds ---------------------------------------------------------------

    [SkippableFact]
    public async Task Validate_FileFlow_Exit0()
    {
        RequireCli();
        var csv = WriteFile("orders.csv", "Id,Name\n1,Acme\n");
        var yaml = WriteFile("file.flow.yaml", $"""
            name: OfflineFile
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: OfflineFile
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("'OfflineFile' is valid", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_IngestionFlow_Exit0()
    {
        RequireCli();
        var yaml = WriteFile("ing.flow.yaml", """
            flowType: ing
            name: offline-ing
            connections:
              sink: ${env:SQLFLOW_OFFLINE_SINK}
            source:
              server: sink
              object: Db.dbo.Src
            target:
              server: sink
              object: Db.dbo.Trg
            load:
              keyColumns: [Id]
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("'offline-ing' is valid", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("ingestion:", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_ExportFlow_Exit0()
    {
        RequireCli();
        var yaml = WriteFile("exp.flow.yaml", $$"""
            flowType: exp
            name: offline-exp
            connections:
              sink: ${env:SQLFLOW_OFFLINE_SINK}
            source:
              server: sink
              object: Db.dbo.Src
            target:
              path: {{Fwd(_dir)}}
              fileName: out
              addTimestamp: false
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("export:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Src", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_StoredProcedureFlow_Exit0()
    {
        RequireCli();
        var yaml = WriteFile("sp.flow.yaml", """
            flowType: sp
            name: offline-sp
            connections:
              dwh: ${env:SQLFLOW_OFFLINE_SINK}
            procedure:
              server: dwh
              object: DW.dbo.usp_Refresh
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("usp_Refresh", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_HealthCheckFlow_Exit0()
    {
        RequireCli();
        var yaml = WriteFile("hc.flow.yaml", """
            flowType: hc
            name: offline-hc
            connections:
              dwh: ${env:SQLFLOW_OFFLINE_SINK}
            target: { server: dwh, object: DW.dbo.Orders }
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("health check", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("OrderDate", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_BatchFlow_Exit0()
    {
        RequireCli();
        var yaml = WriteFile("all.batch.yaml", """
            flowType: batch
            name: offline-batch
            members:
              include: ["*.flow.yaml"]
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("batch: include *.flow.yaml", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_MalformedYaml_Exit1_ExplainsWhy()
    {
        RequireCli();
        var yaml = WriteFile("broken.flow.yaml", "name: [unclosed\nsource: ::::\n");

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_MissingFile_Exit1()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["validate", Path.Combine(_dir, "nope.yaml")], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    // ---- run's client-side parameter validation (fails before anything connects) ---------------------------

    [SkippableFact]
    public async Task Run_InvertedBackfillWindow_Exit1_BeforeConnecting()
    {
        RequireCli();
        var csv = WriteFile("w.csv", "Id\n1\n");
        var yaml = WriteFile("w.flow.yaml", $"""
            name: WindowCheck
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: WindowCheck
            """);

        var result = await CliBinary.RunAsync(
            _dll, ["run", yaml, "--from", "2024-02-01", "--to", "2024-01-01"], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Run_UnparsableDate_Exit1_NamesTheFlag()
    {
        RequireCli();
        var csv = WriteFile("d.csv", "Id\n1\n");
        var yaml = WriteFile("d.flow.yaml", $"""
            name: DateCheck
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: DateCheck
            """);

        var result = await CliBinary.RunAsync(_dll, ["run", yaml, "--from", "not-a-date"], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("--from", result.StdErr, StringComparison.Ordinal);
    }

    // ---- paths / flatten / discover (JSON and XML introspection) ------------------------------------------

    private const string NestedJson = """
        {"orders":[{"id":1,"customer":{"name":"Acme"},"lines":[{"sku":"A","qty":2},{"sku":"B","qty":1}]},
                   {"id":2,"customer":{"name":"Globex"},"lines":[{"sku":"C","qty":5}]}]}
        """;

    [SkippableFact]
    public async Task Paths_Json_ListsEveryAddressablePath()
    {
        RequireCli();
        var json = WriteFile("orders.json", NestedJson);

        var result = await CliBinary.RunAsync(_dll, ["paths", json], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("customer", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("sku", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Paths_Xml_ListsEveryAddressablePath()
    {
        RequireCli();
        var xml = WriteFile("rows.xml", """
            <root>
              <row id="1"><name>Acme</name><city>Oslo</city></row>
              <row id="2"><name>Globex</name><city>Bergen</city></row>
            </root>
            """);

        var result = await CliBinary.RunAsync(_dll, ["paths", xml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("name", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("city", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Flatten_Json_EmitsARunnableFormula()
    {
        RequireCli();
        var json = WriteFile("orders.json", NestedJson);

        var result = await CliBinary.RunAsync(_dll, ["flatten", json], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        // The formula is a runnable flow stub: it must carry the source and the resolved column schema.
        Assert.Contains("type: json", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("customer", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Flatten_Data_DumpsTheFlattenedRowsAsCsv()
    {
        RequireCli();
        var json = WriteFile("orders.json", NestedJson);

        var result = await CliBinary.RunAsync(_dll, ["flatten", json, "--data"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("Acme", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Globex", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Discover_JsonFlow_ReportsTheRecordGrain()
    {
        RequireCli();
        var json = WriteFile("orders.json", NestedJson);
        var yaml = WriteFile("disc.flow.yaml", $"""
            name: Discover
            source:
              type: json
              location: {Fwd(json)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: Discover
            """);

        var result = await CliBinary.RunAsync(_dll, ["discover", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("orders", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Discover_CsvFlow_Exit1_NamesTheSupportedFormats()
    {
        RequireCli();
        var csv = WriteFile("c.csv", "Id\n1\n");
        var yaml = WriteFile("c.flow.yaml", $"""
            name: DiscCsv
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: DiscCsv
            """);

        var result = await CliBinary.RunAsync(_dll, ["discover", yaml], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("JSON and XML", result.StdErr, StringComparison.Ordinal);
    }

    // ---- lineage over a small offline estate ----------------------------------------------------------------

    private string WriteLineageEstate()
    {
        var estate = Path.Combine(_dir, "estate");
        Directory.CreateDirectory(estate);
        var csv = Path.Combine(estate, "orders.csv");
        File.WriteAllText(csv, "Id,Name\n1,Acme\n");
        // Both flows address the shared object through the SAME connection literal: lineage keys objects by
        // server reference, so mixing an inline connection with an env reference would split Db.dbo.EstateA
        // into two ambiguous keys and break the --of walk.
        const string connection = "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True";
        File.WriteAllText(Path.Combine(estate, "load-a.flow.yaml"), $"""
            name: EstateA
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "{connection}"
              schema: dbo
              table: EstateA
            """);
        File.WriteAllText(Path.Combine(estate, "a-to-b.flow.yaml"), $"""
            flowType: ing
            name: estate-a-to-b
            connections:
              sink: "{connection}"
            source:
              server: sink
              object: Db.dbo.EstateA
            target:
              server: sink
              object: Db.dbo.EstateB
            load:
              keyColumns: [Id]
            """);
        return estate;
    }

    [SkippableFact]
    public async Task Lineage_Folder_ComputesWaves_AndWritesTheCanonicalArtifact()
    {
        RequireCli();
        var estate = WriteLineageEstate();

        var result = await CliBinary.RunAsync(_dll, ["lineage", estate, "--strict"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("Lineage over", result.StdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(estate, ".sqlflow", "lineage", "lineage.json")),
            "lineage.json was not written");
    }

    [SkippableFact]
    public async Task Lineage_Json_IsParsable_AndCarriesBothFlows()
    {
        RequireCli();
        var estate = WriteLineageEstate();

        var result = await CliBinary.RunAsync(_dll, ["lineage", estate, "--json"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        using var document = JsonDocument.Parse(result.StdOut);
        var flows = document.RootElement.GetProperty("flows");
        Assert.Equal(2, flows.GetArrayLength());
    }

    [SkippableFact]
    public async Task Lineage_ImpactWalk_FollowsTheEdgeDownstream()
    {
        RequireCli();
        var estate = WriteLineageEstate();

        // The file flow writes Db.dbo.EstateA; the ingestion flow reads it, so a downstream walk from the
        // object must surface the ingestion flow.
        var result = await CliBinary.RunAsync(
            _dll, ["lineage", estate, "--of", "Db.dbo.EstateA", "--down"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("estate-a-to-b", result.StdOut, StringComparison.Ordinal);
    }

    // ---- remote verbs without a control plane configured ----------------------------------------------------

    [SkippableFact]
    public async Task Trigger_WithoutUrl_Exit1_PointsAtConfiguration()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["trigger", "--repo", "x", "--flow", "y"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("SQLFLOW_URL", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Health_WithoutUrl_Exit1_PointsAtConfiguration()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["health"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("SQLFLOW_URL", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunsList_WithoutUrl_Exit1_ButCancelKeepsTheDirectDbContract()
    {
        RequireCli();

        // list is remote-only: no URL is a configuration error.
        var list = await CliBinary.RunAsync(_dll, ["runs", "list"], workingDirectory: _dir);
        Assert.Equal(1, list.Exit);
        Assert.Contains("SQLFLOW_URL", list.StdErr, StringComparison.Ordinal);

        // cancel without a URL falls back to the direct-catalog path, whose own contract (a run id is a GUID)
        // answers before any connection is attempted.
        var cancel = await CliBinary.RunAsync(_dll, ["runs", "cancel", "not-a-guid"], workingDirectory: _dir);
        Assert.Equal(1, cancel.Exit);
        Assert.Contains("requires a run id", cancel.StdErr, StringComparison.Ordinal);
    }

    // ---- estate-wide validate, doctor, completions ----------------------------------------------------------

    [SkippableFact]
    public async Task Validate_Folder_ReportsEveryDocument_Exit1WhenAnyIsBroken()
    {
        RequireCli();
        var estate = Path.Combine(_dir, "mixed");
        Directory.CreateDirectory(estate);
        var csv = Path.Combine(estate, "ok.csv");
        File.WriteAllText(csv, "Id\n1\n");
        File.WriteAllText(Path.Combine(estate, "good.flow.yaml"), $"""
            name: EstateGood
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: EstateGood
            """);
        File.WriteAllText(Path.Combine(estate, "bad.flow.yaml"), "name: [broken\n");

        var result = await CliBinary.RunAsync(_dll, ["validate", estate], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("OK      good.flow.yaml", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("BROKEN  bad.flow.yaml", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("1 valid, 1 broken of 2", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_Folder_Json_EmitsTheMachineReadableReport()
    {
        RequireCli();
        var estate = Path.Combine(_dir, "jsonestate");
        Directory.CreateDirectory(estate);
        var csv = Path.Combine(estate, "ok.csv");
        File.WriteAllText(csv, "Id\n1\n");
        File.WriteAllText(Path.Combine(estate, "only.flow.yaml"), $"""
            name: EstateJson
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: EstateJson
            """);

        var result = await CliBinary.RunAsync(_dll, ["validate", estate, "--json"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        using var report = JsonDocument.Parse(result.StdOut);
        var entry = Assert.Single(report.RootElement.EnumerateArray());
        Assert.True(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("file", entry.GetProperty("kind").GetString());
        Assert.Equal("EstateJson", entry.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task Doctor_NothingConfigured_SkipsEverySurface_Exit0()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["doctor"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("control plane: SKIP", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("catalog db:    SKIP", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("checks out", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Completions_EachShell_Exit0_CoversTheVerbs()
    {
        RequireCli();
        foreach (var shell in new[] { "bash", "zsh", "powershell" })
        {
            var result = await CliBinary.RunAsync(_dll, ["completions", shell], workingDirectory: _dir);
            Assert.True(result.Exit == 0, $"{shell}: {result.AllOutput}");
            Assert.Contains("trigger", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("schedules", result.StdOut, StringComparison.Ordinal);
        }

        var unknown = await CliBinary.RunAsync(_dll, ["completions", "fish"], workingDirectory: _dir);
        Assert.Equal(1, unknown.Exit);
    }

    [SkippableFact]
    public async Task RunsLocal_EmptyFolder_Exit0_ReportsZero()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["runs", "local", _dir], workingDirectory: _dir);
        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("(0 of 0 local run(s)", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Logout_WithNothingStored_IsIdempotent_Exit0()
    {
        RequireCli();
        var credentialFile = Path.Combine(_dir, "credentials.json");

        var result = await CliBinary.RunAsync(
            _dll, ["logout", "--url", "http://localhost:59999"],
            env: [("SQLFLOW_CREDENTIALS_FILE", credentialFile)], workingDirectory: _dir);

        Assert.Equal(0, result.Exit);
        Assert.Contains("no stored credential", result.StdOut, StringComparison.Ordinal);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Fwd(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
