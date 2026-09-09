using System.Text.Json;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Schema-evolution coverage for JSON: one process handling different versions of the same dataset. Two
/// mechanisms are exercised here (both without a database) - the additive union (added/removed fields
/// across files and records become a unified, null-filled schema) and path aliases (a field that was
/// renamed or moved across versions is coalesced into one column that is never null when any version
/// supplies a value). Cross-run column auto-growth (Widen) is covered by the integration suite.
/// </summary>
public sealed class JsonSchemaEvolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_jsonevo_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public JsonSchemaEvolutionTests() => Directory.CreateDirectory(_dir);

    private void Write(string fileName, string content) => File.WriteAllText(Path.Combine(_dir, fileName), content);

    private SourceSpec Folder(Dictionary<string, string?> options)
    {
        options["srcFile"] = options.GetValueOrDefault("srcFile") ?? "*.json";
        return new SourceSpec { Type = "json", Location = _dir, Options = options };
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadAllAsync(SourceSpec source)
    {
        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = new List<object?[]>();
        while (await data.ReadAsync())
        {
            var row = new object?[data.FieldCount];
            for (var i = 0; i < data.FieldCount; i++)
            {
                row[i] = data.IsDBNull(i) ? null : data.GetValue(i);
            }

            rows.Add(row);
        }

        return (columns.Select(c => c.Name).ToList(), rows);
    }

    private static object? Cell(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    private static List<Dictionary<string, string?>> FlattenRows(string json, JsonFlattenConfig config)
    {
        using var doc = JsonDocument.Parse(json);
        return new JsonPathFlattener(config)
            .FlattenRows(doc.RootElement.Clone())
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            .ToList();
    }

    [Fact]
    public async Task AdditiveUnion_AcrossFilesWithDifferentShapes_UnifiesAndNullFills()
    {
        Write("v1.json", """[ { "id": 1, "name": "Ann" } ]""");
        Write("v2.json", """[ { "id": 2, "name": "Bob", "email": "bob@x.io", "status": "active" } ]""");

        var (columns, rows) = await ReadAllAsync(Folder([]));

        Assert.Contains("name", columns);
        Assert.Contains("email", columns);   // a field new in v2 appears in the unified schema
        Assert.Contains("status", columns);
        Assert.Equal(2, rows.Count);

        var v1 = rows.Single(r => Equals(Cell(columns, r, "id"), "1"));
        Assert.Null(Cell(columns, v1, "email"));   // the older shape null-fills the newer fields
        var v2 = rows.Single(r => Equals(Cell(columns, r, "id"), "2"));
        Assert.Equal("bob@x.io", Cell(columns, v2, "email"));
    }

    [Fact]
    public async Task AdditiveUnion_AcrossHeterogeneousNdjsonRecords()
    {
        Write("events.ndjson", "{ \"id\": 1, \"a\": 1 }\n{ \"id\": 2, \"b\": 2 }\n");

        var (columns, rows) = await ReadAllAsync(
            new SourceSpec { Type = "ndjson", Location = Path.Combine(_dir, "events.ndjson"), Options = new Dictionary<string, string?>() });

        Assert.Contains("a", columns);
        Assert.Contains("b", columns);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task PathAliases_RenamedFieldAcrossFiles_OneColumnNeverNull()
    {
        Write("v1.json", """[ { "id": 1, "name": "Ann" }, { "id": 2, "name": "Bob" } ]""");
        Write("v2.json", """[ { "id": 3, "fullName": "Cy" }, { "id": 4, "fullName": "Di" } ]""");

        var (columns, rows) = await ReadAllAsync(Folder(new Dictionary<string, string?>
        {
            ["pathAliases"] = "person_name=$.name|$.fullName",
        }));

        Assert.Contains("person_name", columns);
        Assert.DoesNotContain("name", columns);       // the version-specific paths collapse into the alias
        Assert.DoesNotContain("fullName", columns);
        Assert.DoesNotContain("full_name", columns);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.NotNull(Cell(columns, r, "person_name")));  // never null - the gap the user flagged

        var byId = rows.ToDictionary(r => (string)Cell(columns, r, "id")!, r => (string?)Cell(columns, r, "person_name"));
        Assert.Equal("Ann", byId["1"]);
        Assert.Equal("Cy", byId["3"]);
    }

    [Fact]
    public async Task PathAliases_ThreeVersions_StillOneColumn()
    {
        Write("v1.json", """[ { "id": 1, "name": "Ann" } ]""");
        Write("v2.json", """[ { "id": 2, "fullName": "Bob" } ]""");
        Write("v3.json", """[ { "id": 3, "displayName": "Cy" } ]""");

        var (columns, rows) = await ReadAllAsync(Folder(new Dictionary<string, string?>
        {
            ["pathAliases"] = "person_name=$.name|$.fullName|$.displayName",
        }));

        Assert.Single(columns, c => c == "person_name");
        Assert.DoesNotContain("display_name", columns);
        Assert.All(rows, r => Assert.NotNull(Cell(columns, r, "person_name")));
    }

    [Fact]
    public void PathAliases_Coalesce_NonNullWinsRegardlessOfOrder()
    {
        var config = new JsonFlattenConfig
        {
            PathAliasColumns = JsonFlattenConfig.ParsePathAliases("person_name=$.name|$.fullName"),
        };

        // name explicitly null, fullName present: the present value wins.
        var a = FlattenRows("""{ "id": 1, "name": null, "fullName": "X" }""", config);
        Assert.Equal("X", a[0]["person_name"]);

        // name present, fullName explicitly null: the earlier non-null is not blanked out.
        var b = FlattenRows("""{ "id": 2, "name": "A", "fullName": null }""", config);
        Assert.Equal("A", b[0]["person_name"]);
    }

    [Fact]
    public void PathAliases_MovedField_AcrossNestingLevels()
    {
        // v1 nests the email under contact; v2 hoists it to the top. One column either way.
        var config = new JsonFlattenConfig
        {
            PathAliasColumns = JsonFlattenConfig.ParsePathAliases("email=$.contact.email|$.email"),
        };

        var v1 = FlattenRows("""{ "id": 1, "contact": { "email": "a@x.io" } }""", config);
        var v2 = FlattenRows("""{ "id": 2, "email": "b@x.io" }""", config);

        Assert.Equal("a@x.io", v1[0]["email"]);
        Assert.Equal("b@x.io", v2[0]["email"]);
        Assert.False(v1[0].ContainsKey("contact_email"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
