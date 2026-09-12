using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Translate;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Translate.Tests;

/// <summary>
/// The header/repeater vocabulary: bind-less (single-instance) datasets, the <c>$row</c> single-row block, and
/// their combinations with repeaters, driven through the real YAML compiler. The envelope case is the classic
/// interchange shape: one file per run holding a header block (extract metadata from a one-row dataset) and a
/// transactions array (every primary row).
/// </summary>
public sealed class HeaderRepeaterTests
{
    private static TranslateFlow Compile(string yaml) => new YamlTranslateFlowLoader().Parse(yaml).Flow;

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] columns)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in columns)
        {
            row[name] = value;
        }

        return row;
    }

    [Fact]
    public void Render_EnvelopeWithHeaderAndTransactions()
    {
        // One document for the whole run: the header comes from a bind-less one-row dataset, the transactions
        // from the primary result. No synthetic bind key anywhere.
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT TripId, Amount FROM edw.Trip
            datasets:
              - name: meta
                query: SELECT SYSUTCDATETIME() AS ExtractedUtc, COUNT(*) AS RecordCount FROM edw.Trip
            documents: { per: resultSet }
            template:
              header:
                $row: meta
                $item:
                  extractedAt: { $column: ExtractedUtc, $type: dateTime }
                  recordCount: "{RecordCount}"
                  format: contoso-trips-v1
              transactions:
                $forEach: rows
                $item:
                  tripId: "{TripId}"
                  amount: { $column: Amount, $type: decimal }
            output: { path: ./out, mode: array }
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["ExtractedUtc", "RecordCount"],
            [Row(("ExtractedUtc", new DateTime(2026, 8, 17, 4, 0, 0, DateTimeKind.Utc)), ("RecordCount", 2))]);
        index.SetPrimaryRows([Row(("TripId", 1), ("Amount", 10.5m)), Row(("TripId", 2), ("Amount", 20m))]);

        var document = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty, index, flow.Nulls)!.AsObject();

        var header = document["header"]!.AsObject();
        Assert.Equal("2026-08-17T04:00:00", (string?)header["extractedAt"]);
        Assert.Equal(2L, (long?)header["recordCount"]);
        Assert.Equal("contoso-trips-v1", (string?)header["format"]);
        var transactions = document["transactions"]!.AsArray();
        Assert.Equal(2, transactions.Count);
        Assert.Equal(10.5m, (decimal?)transactions[0]!["amount"]);
    }

    [Fact]
    public void Render_RowOverBoundDataset_IsAOneToOneBlock()
    {
        // A per-document extension row: shipping details keyed one-to-one by the order id.
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: shipping
                query: SELECT 1
                bind: [OrderId]
            template:
              orderId: "{OrderId}"
              shipping:
                $row: shipping
                $item:
                  carrier: "{Carrier}"
                  order: "{OrderId}"
            output: { path: ./out }
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["OrderId", "Carrier"],
        [
            Row(("OrderId", 1), ("Carrier", "Bring")),
            Row(("OrderId", 2), ("Carrier", "PostNord")),
        ]);

        var document = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("OrderId", 2))), index, flow.Nulls)!.AsObject();

        Assert.Equal("PostNord", (string?)document["shipping"]!["carrier"]);
        // The block's item still sees the enclosing document row.
        Assert.Equal(2L, (long?)document["shipping"]!["order"]);
    }

    [Fact]
    public void Render_RowWithZeroOrManyRows_FailsWithTheCount()
    {
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: meta
                query: SELECT 1
            template:
              header: { $row: meta, $item: { a: "{A}" } }
            output: { path: ./out }
            """);

        var empty = new TranslateDatasetIndex();
        empty.AddDataset(flow.Datasets[0], ["A"], []);
        var none = Assert.Throws<SqlFlowException>(() => JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("X", 1))), empty, flow.Nulls));
        Assert.Contains("found 0", none.Message, StringComparison.Ordinal);

        var many = new TranslateDatasetIndex();
        many.AddDataset(flow.Datasets[0], ["A"], [Row(("A", 1)), Row(("A", 2))]);
        var pair = Assert.Throws<SqlFlowException>(() => JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("X", 1))), many, flow.Nulls));
        Assert.Contains("found 2", pair.Message, StringComparison.Ordinal);
        Assert.Contains("$.header", pair.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_BindlessForEach_RepeatsEverywhereUnfiltered()
    {
        // A global reference list (no bind): every document carries the same block, regardless of its own row.
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: legaltags
                query: SELECT 1
            template:
              id: "{Id}"
              tags:
                $forEach: legaltags
                $item: "{Tag}"
            output: { path: ./out }
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["Tag"], [Row(("Tag", "public")), Row(("Tag", "no-pii"))]);

        var first = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("Id", "a"))), index, flow.Nulls)!.AsObject();
        var second = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("Id", "b"))), index, flow.Nulls)!.AsObject();

        Assert.Equal(["public", "no-pii"], first["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal(["public", "no-pii"], second["tags"]!.AsArray().Select(t => (string?)t));
    }

    [Fact]
    public void Render_RowOverPrimaryRows_WrapsTheSingleRowAtResultSetGrain()
    {
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            documents: { per: resultSet }
            template:
              summary: { $row: rows, $item: { total: "{Total}" } }
            output: { path: ./out }
            """);

        var index = new TranslateDatasetIndex();
        index.SetPrimaryRows([Row(("Total", 99))]);
        var document = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty, index, flow.Nulls)!.AsObject();
        Assert.Equal(99L, (long?)document["summary"]!["total"]);
    }

    [Fact]
    public void Render_HeaderInsideRepeater_ScopesChainThroughRowBlocks()
    {
        // $row nests inside $forEach: each line pulls its product master row (one-to-one by ProductId), and the
        // product block still sees both the line and the invoice.
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: lines
                query: SELECT 1
                bind: [InvoiceId]
              - name: product
                query: SELECT 1
                bind: [ProductId]
            template:
              invoice: "{InvoiceId}"
              lines:
                $forEach: lines
                $item:
                  lineNo: "{LineNo}"
                  product:
                    $row: product
                    $item:
                      sku: "{Sku}"
                      soldOn: "{InvoiceId}-{LineNo}"
            output: { path: ./out }
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["InvoiceId", "LineNo", "ProductId"],
        [
            Row(("InvoiceId", 7), ("LineNo", 1), ("ProductId", "P1")),
            Row(("InvoiceId", 7), ("LineNo", 2), ("ProductId", "P2")),
        ]);
        index.AddDataset(flow.Datasets[1], ["ProductId", "Sku"],
        [
            Row(("ProductId", "P1"), ("Sku", "SKU-1")),
            Row(("ProductId", "P2"), ("Sku", "SKU-2")),
        ]);

        var document = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("InvoiceId", 7))), index, flow.Nulls)!.AsObject();

        var lines = document["lines"]!.AsArray();
        Assert.Equal("SKU-1", (string?)lines[0]!["product"]!["sku"]);
        Assert.Equal("SKU-2", (string?)lines[1]!["product"]!["sku"]);
        // Three scope frames deep: product row, line row, invoice row.
        Assert.Equal("7-2", (string?)lines[1]!["product"]!["soldOn"]);
    }

    [Fact]
    public void Render_FullAdvancedDocument_CombinesEveryDialectFeature()
    {
        // The kitchen-sink transformation: header ($row over a bind-less dataset), repeaters with nested one-to-one
        // blocks, typed coercions, embedded GeoJSON, structured $value constants, templated strings, per-leaf null
        // policies filtering repeater elements, and column shadowing (the line's Amount shadows the header's).
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: meta
                query: SELECT 1
              - name: lines
                query: SELECT 1
                bind: [OrderId]
            template:
              header:
                $row: meta
                $item:
                  version: { $value: { schema: order-v2, revision: 3 } }
                  produced: { $column: ProducedUtc, $type: dateTime, $format: "yyyy-MM-dd'T'HH:mm:ss'Z'" }
              order:
                id: "urn:order:{OrderId}"
                total: { $column: Amount, $type: decimal }
                location: { $column: GeoJson, $type: json }
                lines:
                  $forEach: lines
                  $item:
                    sku: "{Sku}"
                    amount: { $column: Amount, $type: decimal }
                    note: { $column: Note, $whenNull: omit }
                    discount: { $column: Discount, $whenNull: default, $default: 0 }
            output: { path: ./out }
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["ProducedUtc"],
            [Row(("ProducedUtc", new DateTime(2026, 8, 17, 5, 30, 0, DateTimeKind.Utc)))]);
        index.AddDataset(flow.Datasets[1], ["OrderId", "Sku", "Amount", "Note", "Discount"],
        [
            Row(("OrderId", 1), ("Sku", "A"), ("Amount", 5m), ("Note", "gift"), ("Discount", 0.1m)),
            Row(("OrderId", 1), ("Sku", "B"), ("Amount", 7m), ("Note", null), ("Discount", null)),
        ]);

        var document = JsonTemplateRenderer.Render(
            flow.Template,
            TranslateScope.Empty.Push(Row(
                ("OrderId", 1), ("Amount", 12m), ("GeoJson", """{"type":"Point","coordinates":[5.7,58.9]}"""))),
            index, flow.Nulls)!.AsObject();

        Assert.Equal(3L, (long?)document["header"]!["version"]!["revision"]);
        Assert.Equal("2026-08-17T05:30:00Z", (string?)document["header"]!["produced"]);
        var order = document["order"]!.AsObject();
        Assert.Equal("urn:order:1", (string?)order["id"]);
        Assert.Equal(12m, (decimal?)order["total"]);
        Assert.Equal("Point", (string?)order["location"]!["type"]);

        var lines = order["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        // The line's Amount shadows the order's inside the repeater.
        Assert.Equal(5m, (decimal?)lines[0]!["amount"]);
        Assert.Equal("gift", (string?)lines[0]!["note"]);
        Assert.Equal(0.1m, (decimal?)lines[0]!["discount"]);
        Assert.False(lines[1]!.AsObject().ContainsKey("note"));
        Assert.Equal(0L, (long?)lines[1]!["discount"]);
    }
}
