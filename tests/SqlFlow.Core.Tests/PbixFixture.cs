using System.IO.Compression;
using System.Text;

namespace SqlFlow.Tests;

/// <summary>
/// A minimal <c>.pbix</c> for the tests that run the real extractor: a zip whose <c>Report/Layout</c> part (UTF-16LE
/// JSON) holds one page with a pivot table over the model entity <c>Sales</c> and a textbox. It carries no
/// <c>DataModel</c> part (building one means XPress9-compressing an Analysis Services image), so its visuals stay on
/// the model entity name.
/// </summary>
internal static class PbixFixture
{
    /// <summary>One visual: a pivot table breaking Sales.amount down by Sales.region, titled "Revenue by Region".</summary>
    private const string VisualConfig = """
        {
          "singleVisual": {
            "visualType": "pivotTable",
            "projections": {
              "Rows": [{ "queryRef": "Sales.region" }],
              "Values": [{ "queryRef": "Sum(Sales.amount)" }]
            },
            "prototypeQuery": {
              "Version": 2,
              "From": [{ "Name": "s", "Entity": "Sales", "Type": 0 }],
              "Select": [
                { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "region" }, "Name": "Sales.region" },
                {
                  "Aggregation": {
                    "Expression": { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "amount" } },
                    "Function": 0
                  },
                  "Name": "Sum(Sales.amount)"
                }
              ]
            },
            "vcObjects": {
              "title": [{ "properties": { "text": { "expr": { "Literal": { "Value": "'Revenue by Region'" } } } } }]
            }
          }
        }
        """;

    /// <summary>A textbox, which projects nothing: decoration, not a question.</summary>
    private const string TextboxConfig = """
        { "singleVisual": { "visualType": "textbox", "projections": {} } }
        """;

    /// <summary>Writes the report to <paramref name="path"/>, creating its directory.</summary>
    public static void Write(string path)
    {
        var layout = $$"""
            {
              "id": 0,
              "sections": [
                {
                  "id": 1,
                  "name": "ReportSection1",
                  "displayName": "Revenue",
                  "ordinal": 1,
                  "filters": "[]",
                  "visualContainers": [
                    { "x": 0, "y": 0, "z": 0, "width": 100, "height": 100, "config": {{Json(VisualConfig)}}, "filters": "[]" },
                    { "x": 0, "y": 0, "z": 1, "width": 100, "height": 100, "config": {{Json(TextboxConfig)}}, "filters": "[]" }
                  ]
                }
              ]
            }
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("Report/Layout");
        using var stream = entry.Open();
        stream.Write(Encoding.Unicode.GetBytes(layout));
    }

    private static string Json(string document) => System.Text.Json.JsonSerializer.Serialize(document);
}
