using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The pure projections of a pipeline's pre-ingestion transforms into catalog rows: authored YAML transforms into
/// declared rows (the source of truth) and an inference report into detected rows. No EF, no database.
/// </summary>
public sealed class CatalogPipelineColumnProjectionTests
{
    private static readonly Guid Repo = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Pipeline = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Declared_ResolvesColNameAndCastAndFlags()
    {
        var policy = new TypeInferencePolicy
        {
            Columns =
            [
                new ColumnTransform { Name = "vehicle_type", Expression = "CAST(@ColName AS varchar(50))", Alias = "vehicle_type_clean", Type = "varchar(50)", SortOrder = 10 },
                new ColumnTransform { Name = "line_total", Type = "decimal(18,2)" },
                new ColumnTransform { Name = "loaded_at", Expression = "SYSUTCDATETIME()", Virtual = true },
                new ColumnTransform { Name = "scratch", Type = "int", ExcludeFromView = true },
            ],
        };

        var rows = CatalogProjection.PipelineColumnsDeclared(Repo, Pipeline, policy);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(PipelineColumnKinds.Declared, r.Kind));
        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.Ordinal));

        var vehicle = rows[0];
        Assert.Equal("vehicle_type_clean", vehicle.ColumnName);
        Assert.Equal("vehicle_type", vehicle.SourceColumn);
        Assert.Equal("CAST([vehicle_type] AS varchar(50))", vehicle.Expression);
        Assert.Equal("varchar(50)", vehicle.DataType);
        Assert.Equal(10, vehicle.SortOrder);
        Assert.True(vehicle.Converted);

        // Type-only transform becomes a straight CAST of the source column.
        Assert.Equal("CAST([line_total] AS decimal(18,2))", rows[1].Expression);

        // Virtual column has no source counterpart and uses its expression verbatim.
        var loaded = rows[2];
        Assert.True(loaded.IsVirtual);
        Assert.Null(loaded.SourceColumn);
        Assert.Equal("SYSUTCDATETIME()", loaded.Expression);

        Assert.True(rows[3].ExcludeFromView);
    }

    [Fact]
    public void Declared_NoColumns_YieldsNoRows()
    {
        Assert.Empty(CatalogProjection.PipelineColumnsDeclared(Repo, Pipeline, new TypeInferencePolicy()));
    }

    [Fact]
    public void Detected_MapsRunTransformViewColumnsInOrder()
    {
        var root = JsonDocument.Parse("""
            {
              "runId": "11111111-1111-1111-1111-111111111111",
              "flowName": "orders",
              "flowKind": "file",
              "result": {
                "transformView": {
                  "viewName": "vOrders",
                  "ddl": "CREATE OR ALTER VIEW ...",
                  "columns": [
                    { "columnName": "id", "dataType": "bigint", "selectExpression": "TRY_CONVERT(bigint, [id])", "converted": true },
                    { "columnName": "note", "dataType": "", "selectExpression": "[note]", "converted": false }
                  ]
                }
              }
            }
            """).RootElement;

        var rows = CatalogProjection.PipelineColumnsDetected(root, Repo, Pipeline);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(PipelineColumnKinds.Detected, r.Kind));
        Assert.Equal([1, 2], rows.Select(r => r.Ordinal));
        Assert.Equal("id", rows[0].ColumnName);
        Assert.Equal("bigint", rows[0].DataType);
        Assert.Equal("TRY_CONVERT(bigint, [id])", rows[0].Expression);
        Assert.True(rows[0].Converted);
        Assert.Null(rows[1].DataType); // an untyped pass-through keeps a null type, not an empty string
        Assert.False(rows[1].Converted);
    }

    [Fact]
    public void Detected_NoTransformViewInResult_YieldsNoRows()
    {
        var root = JsonDocument.Parse("""{ "result": { "rowsLoaded": 42 } }""").RootElement;
        Assert.Empty(CatalogProjection.PipelineColumnsDetected(root, Repo, Pipeline));
    }
}
