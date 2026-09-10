using System.Globalization;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Lineage;

namespace SqlFlow.Tests;

/// <summary>
/// The dedicated lineage test harness: builds a disposable flow ESTATE on disk (documents, canonical run
/// artifacts, optional .sqlflow/env) and computes lineage through the one real entry point
/// (<see cref="LineageService"/>), so every test exercises the exact path the CLI runs: collection tiers,
/// extraction, graph assembly, and planning together. Used by the offline end-to-end tests and the live
/// integration tests alike; nothing in here mocks anything.
/// </summary>
public sealed class LineageEstateHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sqlflow-estate-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageEstateHarness()
    {
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Writes one flow document and pins its timestamp (staleness checks compare against runs).</summary>
    public LineageEstateHarness Flow(string fileName, string yaml, DateTime? writeUtc = null)
    {
        var path = Path.Combine(Root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        File.SetLastWriteTimeUtc(path, writeUtc ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        return this;
    }

    /// <summary>Writes one canonical run artifact for a flow: a run.json whose SqlTrace replays the given
    /// (step, sql) statements, exactly the shape the engine writes.</summary>
    public LineageEstateHarness Run(string flowName, DateTime writtenUtc, params (string Step, string Sql)[] trace)
    {
        var folder = Path.Combine(
            Root, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName),
            $"{writtenUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(folder);

        var entries = string.Join(",", trace.Select((t, i) => $$"""
            { "sequence": {{i + 1}}, "step": "{{t.Step}}", "sql": {{System.Text.Json.JsonSerializer.Serialize(t.Sql)}} }
            """));
        File.WriteAllText(Path.Combine(folder, "run.json"), $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{Guid.NewGuid()}}",
              "success": true,
              "writtenUtc": "{{writtenUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}}",
              "result": { "sqlTrace": [ {{entries}} ] }
            }
            """);
        return this;
    }

    /// <summary>The real computation, exactly as the CLI invokes it.</summary>
    public Task<LineageReport> ComputeAsync(bool includeObserved = true, bool includeDerived = false)
        => LineageService.ComputeAsync(new LineageOptions
        {
            FlowDirectory = Root,
            IncludeObserved = includeObserved,
            IncludeDerived = includeDerived,
        });

    /// <summary>The wave layout as plain flow-name lists, the assertion currency of the plan tests.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Waves(LineageReport report)
        => report.ExecutionPlan.Waves.Select(w => w.Flows).ToList();

    // ---- Reusable document shapes (the canonical secrets contract: env references only). ----------------

    public static string Ingestion(string name, string sourceObject, string targetObject,
        string sourceRef = "${env:SQLFLOW_CONN_SRC}", string targetRef = "${env:SQLFLOW_CONN_DWH}", string? postProcess = null) => $$"""
        flowType: ing
        name: {{name}}
        connections:
          src: {{sourceRef}}
          dwh: {{targetRef}}
        source: { server: src, object: {{sourceObject}} }
        target: { server: dwh, object: {{targetObject}} }
        load: { keyColumns: [Id] }
        {{(postProcess is null ? string.Empty : "postProcess: \"" + postProcess + "\"")}}
        """;

    public static string StoredProcedure(string name, string procedure, string serverRef = "${env:SQLFLOW_CONN_DWH}") => $$"""
        flowType: sp
        name: {{name}}
        connections:
          dwh: {{serverRef}}
        procedure: { server: dwh, object: {{procedure}} }
        """;

    public static string HealthCheck(string name, string targetObject, string dateColumn, string serverRef = "${env:SQLFLOW_CONN_DWH}") => $$"""
        flowType: hc
        name: {{name}}
        connections:
          dwh: {{serverRef}}
        target: { server: dwh, object: {{targetObject}} }
        dateColumn: {{dateColumn}}
        baseValue: COUNT(*)
        """;

    public static string Export(string name, string sourceObject, string serverRef = "${env:SQLFLOW_CONN_DWH}") => $$"""
        flowType: exp
        name: {{name}}
        connections:
          dwh: {{serverRef}}
        source:
          server: dwh
          object: {{sourceObject}}
        target:
          path: ./out
          filetype: csv
        """;
}
