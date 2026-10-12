using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// AI contexts end to end, the way they reach an assistant: a file in a repository proposes a version, a sync records it
/// with its problems, and only an admin approving that exact version serves it. A change waits for the next approval,
/// while what a file takes away (a table, a column it now denies, a measure, an example) stops being served at the next
/// sync. Every catalog row a test seeds carries a suffix and is removed when it ends, and the kit removes the contexts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiContextApiTests
{
    [SkippableFact]
    public async Task AContext_IsServedOnlyOnceAnAdminApprovesTheVersionTheyReviewed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "Amount", "CustomerEmail");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        var measure = "total_amount_" + estate.Suffix;
        var question = $"What is the total order amount ({estate.Suffix})?";
        contexts.Write("sales/sales.ai-context.yaml", $"""
            aiContext: {name}
            description: Orders and what they are worth.
            instructions: Amounts are in the reporting currency.
            tables:
              - table: dw.dbo.{estate.Name("Orders")}
                name: Orders
                key: [OrderId]
                deny: [CustomerEmail]
                measures:
                  - name: {measure}
                    expression: SUM(Amount)
            examples:
              - question: "! {question}"
                sql: SELECT SUM(Amount) AS total FROM dbo.{estate.Name("Orders")}
            """);
        Assert.Empty(await contexts.SyncAsync());

        // 1. Synced, the file is a proposal: recorded with what approving it would add, and nothing is served.
        var proposed = await AiContextTestKit.GetAsync(client, admin, name);
        Assert.Equal(AiContextStatus.AwaitingApproval, proposed.Summary.Status);
        Assert.Equal("sales/sales.ai-context.yaml", proposed.Summary.Path);
        Assert.NotNull(proposed.Summary.CurrentVersion);
        Assert.Null(proposed.Summary.ApprovedVersion);
        Assert.Equal(1, proposed.Summary.TableCount);
        Assert.Equal(0, proposed.Summary.ServedTableCount);
        Assert.Empty(proposed.Problems);
        Assert.Contains(proposed.Changes, c => c.Kind == "added" && c.Subject.StartsWith("table ", StringComparison.Ordinal));
        var table = Assert.Single(proposed.Current!.Tables);
        Assert.Equal(["OrderId", "Amount"], table.Columns.Select(c => c.Name));
        Assert.Equal(["CustomerEmail"], table.Denied);
        Assert.Equal(question, Assert.Single(proposed.Current.Examples).Question);
        Assert.Empty(await contexts.AllowedColumnsAsync(orders));
        var overview = await GetJsonAsync<SemanticOverviewDto>(client, admin, "/api/v1/semantic-layer");
        Assert.DoesNotContain(overview.Contexts, c => c.Name == name);

        // 2. Only the version the admin reviewed is approved: none, an empty one or another is refused.
        foreach (var version in new string?[] { null, string.Empty, new('0', 64) })
        {
            using var refused = await AiContextTestKit.TryApproveAsync(client, admin, name, version);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        using (var missing = await AiContextTestKit.TryApproveAsync(client, admin, "missing-" + estate.Suffix, proposed.Summary.CurrentVersion))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.Empty(await contexts.AllowedColumnsAsync(orders));

        // 3. Approved, it is served: every column but the denied one, the measure, the instructions and the example.
        var approved = await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(AiContextStatus.Approved, approved.Summary.Status);
        Assert.Equal(proposed.Summary.CurrentVersion, approved.Summary.ApprovedVersion);
        Assert.False(string.IsNullOrEmpty(approved.Summary.ApprovedBy));
        Assert.Equal(1, approved.Summary.ServedTableCount);
        Assert.Empty(approved.Changes);
        Assert.Equal(["Amount", "OrderId"], await contexts.AllowedColumnsAsync(orders));

        overview = await GetJsonAsync<SemanticOverviewDto>(client, admin, "/api/v1/semantic-layer");
        var served = Assert.Single(overview.Contexts, c => c.Name == name);
        Assert.Equal(1, served.TableCount);
        Assert.Equal("Orders and what they are worth.", served.Description);
        Assert.Contains("Amounts are in the reporting currency.", overview.Instructions, StringComparison.Ordinal);
        Assert.Contains(overview.Measures, m => m.Name == measure);

        await using (var db = CatalogDatabase.Create(cs))
        {
            var example = await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.AiContext == name);
            Assert.Equal(question, example.Question);
            Assert.Equal(SemanticExampleProvenance.AiContext, example.Provenance);
            Assert.Equal(contexts.RepoId, example.RepoId);
            Assert.Contains(orders, example.ObjectKeys, StringComparison.Ordinal);
        }

        // 4. A sync that finds the file as it was writes nothing: no row is rewritten, and the saved answer keeps its id.
        var before = await SnapshotAsync(cs, name);
        Assert.Empty(await contexts.SyncAsync());
        Assert.Equal(before, await SnapshotAsync(cs, name));
        Assert.Equal(AiContextStatus.Approved, (await AiContextTestKit.GetAsync(client, admin, name)).Summary.Status);

        // 5. Revoked, nothing is served, and the version waits for approval again.
        var revoked = await AiContextTestKit.RevokeAsync(client, admin, name);
        Assert.Equal(AiContextStatus.AwaitingApproval, revoked.Summary.Status);
        Assert.Null(revoked.Summary.ApprovedBy);
        Assert.Empty(await contexts.AllowedColumnsAsync(orders));
        Assert.Equal(string.Empty, await SnapshotAsync(cs, name));
        overview = await GetJsonAsync<SemanticOverviewDto>(client, admin, "/api/v1/semantic-layer");
        Assert.DoesNotContain(overview.Contexts, c => c.Name == name);
        Assert.DoesNotContain(overview.Measures, m => m.Name == measure);
    }

    [SkippableFact]
    public async Task AChange_WaitsForApproval_WhileWhatAFileTakesAwayStopsBeingServedAtOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "Amount", "CustomerEmail");
        var products = await estate.TableAsync("Products", "ProductId", "ProductName", "Cost");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        var firstQuestion = $"How many orders are there ({estate.Suffix})?";
        var secondQuestion = $"What is the latest order ({estate.Suffix})?";
        string File(string ordersDeny, string description, bool withProducts, bool withFirstQuestion)
            => string.Join('\n', new[]
            {
                $"aiContext: {name}",
                "tables:",
                $"  - table: dw.dbo.{estate.Name("Orders")}",
                $"    description: {description}",
                $"    deny: [{ordersDeny}]",
            }
            .Concat(withProducts ? [$"  - table: dw.dbo.{estate.Name("Products")}", "    deny: [Cost]"] : [])
            .Concat(["examples:"])
            .Concat(withFirstQuestion ? [$"  - question: {firstQuestion}", $"    sql: SELECT COUNT(OrderId) AS orders FROM dbo.{estate.Name("Orders")}"] : [])
            .Concat([$"  - question: {secondQuestion}", $"    sql: SELECT MAX(OrderId) AS latest FROM dbo.{estate.Name("Orders")}"])) + "\n";

        contexts.Write("sales.ai-context.yaml", File("CustomerEmail", "One row per order.", withProducts: false, withFirstQuestion: true));
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(["Amount", "OrderId"], await contexts.AllowedColumnsAsync(orders));
        Assert.Equal(2, await ExampleCountAsync(cs, name));

        // 1. The file opens a column, adds a table, rewords a description and drops an example. What it adds waits;
        //    the dropped example stops being served at once.
        contexts.Write("sales.ai-context.yaml", File("Amount", "One row per order line.", withProducts: true, withFirstQuestion: false));
        await contexts.SyncAsync();
        var changed = await AiContextTestKit.GetAsync(client, admin, name);
        Assert.Equal(AiContextStatus.Changed, changed.Summary.Status);
        Assert.Contains(changed.Changes, c => c.Kind == "added" && c.Subject.StartsWith("column CustomerEmail of", StringComparison.Ordinal));
        Assert.Contains(changed.Changes, c => c.Kind == "removed" && c.Subject.StartsWith("column Amount of", StringComparison.Ordinal));
        Assert.Contains(changed.Changes, c => c.Kind == "added" && c.Subject.Contains(estate.Name("Products"), StringComparison.Ordinal));
        Assert.Contains(changed.Changes, c => c.Kind == "changed" && c.Subject.StartsWith("description of", StringComparison.Ordinal));
        Assert.Contains(changed.Changes, c => c.Kind == "removed" && c.Subject.Contains(firstQuestion, StringComparison.Ordinal));

        Assert.Equal(["OrderId"], await contexts.AllowedColumnsAsync(orders));
        Assert.Empty(await contexts.AllowedColumnsAsync(products));
        Assert.Equal("One row per order.", await DescriptionAsync(cs, orders));
        Assert.Equal(1, await ExampleCountAsync(cs, name));

        // 2. Approved, the additions are served too.
        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(["CustomerEmail", "OrderId"], await contexts.AllowedColumnsAsync(orders));
        Assert.Equal(["ProductId", "ProductName"], await contexts.AllowedColumnsAsync(products));
        Assert.Equal("One row per order line.", await DescriptionAsync(cs, orders));

        // 3. The file drops the table again and denies another column: both stop at the next sync, with no approval.
        contexts.Write("sales.ai-context.yaml", File("Amount, CustomerEmail", "One row per order line.", withProducts: false, withFirstQuestion: false));
        await contexts.SyncAsync();
        Assert.Equal(["OrderId"], await contexts.AllowedColumnsAsync(orders));
        Assert.Empty(await contexts.AllowedColumnsAsync(products));
        Assert.Equal(AiContextStatus.Changed, (await AiContextTestKit.GetAsync(client, admin, name)).Summary.Status);
    }

    [SkippableFact]
    public async Task TheFileItself_IsKeptAsTheSyncReadIt_AndAsItWasApproved_WithCredentialsRedacted()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        var first = $"# Orders for the assistant.\naiContext: {name}\ntables:\n  - table: dbo.{estate.Name("Orders")}\n";
        contexts.Write("sales.ai-context.yaml", first);
        await contexts.SyncAsync();

        var proposed = await YamlAsync(client, admin, name);
        Assert.Equal("sales.ai-context.yaml", proposed.Path);
        Assert.Equal(first, proposed.Current);
        Assert.Null(proposed.Approved);

        // Approving keeps the file as it was approved, comment and all, so a later change can be compared with it.
        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(first, (await YamlAsync(client, admin, name)).Approved);

        // A changed file is read again; a credential typed into it never reaches the catalog.
        var second = first.Replace(
            "tables:", "description: Reads the warehouse with password=Hunter2Secret for now.\ntables:", StringComparison.Ordinal);
        contexts.Write("sales.ai-context.yaml", second);
        await contexts.SyncAsync();
        var changed = await YamlAsync(client, admin, name);
        Assert.Equal(first, changed.Approved);
        Assert.NotNull(changed.Current);
        Assert.Contains("description: Reads the warehouse with", changed.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2Secret", changed.Current, StringComparison.Ordinal);

        // Revoking drops the approved file with the approval.
        await AiContextTestKit.RevokeAsync(client, admin, name);
        Assert.Null((await YamlAsync(client, admin, name)).Approved);

        using var missing = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Get, $"/api/v1/powerai/ai-contexts/missing-{estate.Suffix}/yaml");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        static async Task<AiContextYamlDto> YamlAsync(HttpClient client, string token, string context)
            => await GetJsonAsync<AiContextYamlDto>(client, token, $"/api/v1/powerai/ai-contexts/{context}/yaml");
    }

    [SkippableFact]
    public async Task AColumnTheWarehouseAdds_OpensOnlyOnceApproved_AndOneItDrops_ClosesAtOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var customers = await estate.TableAsync("Customers", "CustomerId", "Region");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "customers-" + estate.Suffix;
        contexts.Write("customers.ai-context.yaml", $"aiContext: {name}\ntables:\n  - table: dbo.{estate.Name("Customers")}\n");
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(["CustomerId", "Region"], await contexts.AllowedColumnsAsync(customers));

        await estate.AddColumnAsync("Customers", "TaxNumber");
        await contexts.SyncAsync();
        var changed = await AiContextTestKit.GetAsync(client, admin, name);
        Assert.Equal(AiContextStatus.Changed, changed.Summary.Status);
        Assert.Contains(changed.Changes, c => c.Kind == "added" && c.Subject.StartsWith("column TaxNumber of", StringComparison.Ordinal));
        Assert.Equal(["CustomerId", "Region"], await contexts.AllowedColumnsAsync(customers));

        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal(["CustomerId", "Region", "TaxNumber"], await contexts.AllowedColumnsAsync(customers));

        await estate.DropColumnAsync("Customers", "Region");
        await contexts.SyncAsync();
        Assert.Equal(["CustomerId", "TaxNumber"], await contexts.AllowedColumnsAsync(customers));
    }

    [SkippableFact]
    public async Task AVersionWithProblems_CannotBeApproved_AndSaysWhatAndWhere()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "StoreId", "Amount", "CustomerEmail");
        await estate.TableAsync("Store", "StoreId", "StoreName");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        var ordersTable = $"dw.dbo.{estate.Name("Orders")}";
        var storeTable = $"dw.dbo.{estate.Name("Store")}";
        var cases = new (string Expected, string Body)[]
        {
            ("is not a table or view the catalog holds", $"  - table: dw.dbo.Nowhere_{estate.Suffix}\n"),
            ("denied column 'Nowhere' is not a column", $"  - table: {ordersTable}\n    deny: [Nowhere]\n"),
            ("described column 'Nowhere' is not a column", $"  - table: {ordersTable}\n    columns:\n      - column: Nowhere\n        description: Not here.\n"),
            ("denies every column", $"  - table: {ordersTable}\n    deny: [OrderId, StoreId, Amount, CustomerEmail]\n"),
            ("measure 'by_email'", $"  - table: {ordersTable}\n    deny: [CustomerEmail]\n    measures:\n      - name: by_email\n        expression: COUNT(DISTINCT CustomerEmail)\n"),
            ("both described and denied", $"  - table: {ordersTable}\n    columns:\n      - column: CustomerEmail\n        description: Email.\n    deny: [CustomerEmail]\n"),
            ("unknown key 'colour'", $"  - table: {ordersTable}\n    colour: blue\n"),
            ("declared twice", $"  - table: {ordersTable}\n    joins:\n      - to: {storeTable}\n        columns: [StoreId]\n  - table: {storeTable}\n    joins:\n      - to: {ordersTable}\n        columns: [StoreId]\n"),
        };

        foreach (var (expected, body) in cases)
        {
            contexts.Write("sales.ai-context.yaml", $"aiContext: {name}\ntables:\n{body}");
            await contexts.SyncAsync();
            var detail = await AiContextTestKit.GetAsync(client, admin, name);
            Assert.True(detail.Summary.Status == AiContextStatus.Invalid, $"{expected}: {detail.Summary.Status}");
            Assert.True(
                detail.Problems.Any(p => p.Contains(expected, StringComparison.Ordinal) && p.StartsWith("sales.ai-context.yaml", StringComparison.Ordinal)),
                $"expected a problem containing '{expected}', got: {string.Join(" | ", detail.Problems)}");
            Assert.Equal(detail.Problems.Count, detail.Summary.ProblemCount);
            using (var refused = await AiContextTestKit.TryApproveAsync(client, admin, name, detail.Summary.CurrentVersion))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            Assert.Empty(await contexts.AllowedColumnsAsync(orders));
        }

        // An example reading a denied column is a problem too, named by its question.
        var question = $"Who ordered most ({estate.Suffix})?";
        contexts.Write("sales.ai-context.yaml", $"""
            aiContext: {name}
            tables:
              - table: {ordersTable}
                deny: [CustomerEmail]
            examples:
              - question: {question}
                sql: SELECT TOP 1 CustomerEmail FROM dbo.{estate.Name("Orders")} ORDER BY Amount DESC
            """);
        await contexts.SyncAsync();
        var withExample = await AiContextTestKit.GetAsync(client, admin, name);
        Assert.Equal(AiContextStatus.Invalid, withExample.Summary.Status);
        Assert.Contains(withExample.Problems, p => p.Contains(question, StringComparison.Ordinal));

        // A name that is not a valid context name makes no context at all, and the sync says why.
        contexts.Delete("sales.ai-context.yaml");
        contexts.Write("bad.ai-context.yaml", $"aiContext: Not A Name\ntables:\n  - table: {ordersTable}\n");
        var warnings = await contexts.SyncAsync();
        Assert.Contains(warnings, w => w.Contains("is not a valid context name", StringComparison.Ordinal));
        Assert.Null(await contexts.RowAsync(name));
    }

    [SkippableFact]
    public async Task ATable_AMeasureName_AndAJoin_EachBelongToOneContext()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "StoreId", "Amount");
        var store = await estate.TableAsync("Store", "StoreId", "StoreName");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var first = "orders-" + estate.Suffix;
        var second = "stores-" + estate.Suffix;
        var measure = "order_total_" + estate.Suffix;
        contexts.Write("orders.ai-context.yaml", $"""
            aiContext: {first}
            tables:
              - table: dbo.{estate.Name("Orders")}
                measures:
                  - name: {measure}
                    expression: SUM(Amount)
                joins:
                  - to: dbo.{estate.Name("Store")}
                    columns: [StoreId]
            """);
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, first);

        async Task AssertRefusedAsync(string body, string expected)
        {
            contexts.Write("stores.ai-context.yaml", $"aiContext: {second}\ntables:\n{body}");
            await contexts.SyncAsync();
            var detail = await AiContextTestKit.GetAsync(client, admin, second);
            Assert.Equal(AiContextStatus.Invalid, detail.Summary.Status);
            Assert.Contains(detail.Problems, p => p.Contains(expected, StringComparison.Ordinal) && p.Contains(first, StringComparison.Ordinal));
            using var refused = await AiContextTestKit.TryApproveAsync(client, admin, second, detail.Summary.CurrentVersion);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        await AssertRefusedAsync($"  - table: dbo.{estate.Name("Orders")}\n", "a table belongs to one context");
        await AssertRefusedAsync(
            $"  - table: dbo.{estate.Name("Store")}\n    measures:\n      - name: {measure}\n        expression: COUNT(StoreId)\n",
            "a measure name is unique");
        await AssertRefusedAsync(
            $"  - table: dbo.{estate.Name("Store")}\n    joins:\n      - to: dbo.{estate.Name("Orders")}\n        columns: [StoreId]\n",
            "declare it in one of the two files");

        // The first context keeps everything it served, and the second, once it claims only what is free, is approved.
        Assert.Equal(["Amount", "OrderId", "StoreId"], await contexts.AllowedColumnsAsync(orders));
        contexts.Write("stores.ai-context.yaml", $"aiContext: {second}\ntables:\n  - table: dbo.{estate.Name("Store")}\n");
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, second);
        Assert.Equal(["StoreId", "StoreName"], await contexts.AllowedColumnsAsync(store));
    }

    [SkippableFact]
    public async Task AProblemAnotherContextCaused_ClearsAtTheNextSync_OnceThatContextLetsGo()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var first = "orders-" + estate.Suffix;
        var second = "sales-" + estate.Suffix;
        contexts.Write("orders.ai-context.yaml", $"aiContext: {first}\ntables:\n  - table: dbo.{estate.Name("Orders")}\n");
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, first);

        contexts.Write("sales.ai-context.yaml", $"aiContext: {second}\ntables:\n  - table: dbo.{estate.Name("Orders")}\n");
        await contexts.SyncAsync();
        Assert.Equal(AiContextStatus.Invalid, (await AiContextTestKit.GetAsync(client, admin, second)).Summary.Status);

        // The file did not change, but what made it invalid did: the next sync checks it again.
        await AiContextTestKit.RevokeAsync(client, admin, first);
        await contexts.SyncAsync();
        var cleared = await AiContextTestKit.GetAsync(client, admin, second);
        Assert.Equal(AiContextStatus.AwaitingApproval, cleared.Summary.Status);
        Assert.Empty(cleared.Problems);
        await AiContextTestKit.ApproveAsync(client, admin, second);
        Assert.Equal(["Amount", "OrderId"], await contexts.AllowedColumnsAsync(orders));

        // A version that resolves as before and had no problems is not checked again at a sync, so the first context
        // still reads as approvable; approving it checks the catalog as it stands and refuses it, naming the cause.
        await contexts.SyncAsync();
        var stale = await AiContextTestKit.GetAsync(client, admin, first);
        using var refused = await AiContextTestKit.TryApproveAsync(client, admin, first, stale.Summary.CurrentVersion);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(second, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(AiContextStatus.Invalid, (await AiContextTestKit.GetAsync(client, admin, first)).Summary.Status);
    }

    [SkippableFact]
    public async Task AContextName_IsDefinedByOneFile_InOneRepository()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var mine = new AiContextTestKit(cs);
        await using var theirs = new AiContextTestKit(cs);

        var name = "sales-" + estate.Suffix;
        var body = $"aiContext: {name}\ntables:\n  - table: dbo.{estate.Name("Orders")}\n";

        // Two files of one repository: the first in path order defines it, and the other is reported and ignored.
        mine.Write("a/sales.ai-context.yaml", body);
        mine.Write("b/sales.ai-context.yaml", body);
        var warnings = await mine.SyncAsync();
        Assert.Contains(warnings, w => w.StartsWith("b/sales.ai-context.yaml", StringComparison.Ordinal)
            && w.Contains("already defined by a/sales.ai-context.yaml", StringComparison.Ordinal));
        Assert.Equal("a/sales.ai-context.yaml", (await mine.RowAsync(name))!.Path);

        // Another repository naming it is ignored while the first defines it...
        theirs.Write("ai-context.yaml", body);
        warnings = await theirs.SyncAsync();
        Assert.Contains(warnings, w => w.Contains($"AI context '{name}' is defined by repository", StringComparison.Ordinal));
        Assert.Equal(mine.RepoId, (await mine.RowAsync(name))!.RepoId);

        // ...and takes it once the first repository's files are gone.
        mine.Delete("a/sales.ai-context.yaml");
        mine.Delete("b/sales.ai-context.yaml");
        await mine.SyncAsync();
        Assert.Null(await mine.RowAsync(name));
        Assert.Empty(await theirs.SyncAsync());
        var row = await theirs.RowAsync(name);
        Assert.NotNull(row);
        Assert.Equal(theirs.RepoId, row.RepoId);
        Assert.Equal("ai-context.yaml", row.Path);
        Assert.Null(row.ApprovedHash);
    }

    [SkippableFact]
    public async Task ARemovedFile_RemovesItsContext_AndEverythingItServed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        contexts.Write("sales.ai-context.yaml", $"""
            aiContext: {name}
            tables:
              - table: dbo.{estate.Name("Orders")}
                measures:
                  - name: amount_{estate.Suffix}
                    expression: SUM(Amount)
            examples:
              - question: How much was ordered ({estate.Suffix})?
                sql: SELECT SUM(Amount) AS amount FROM dbo.{estate.Name("Orders")}
            """);
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.NotEqual(string.Empty, await SnapshotAsync(cs, name));

        contexts.Delete("sales.ai-context.yaml");
        await contexts.SyncAsync();
        Assert.Null(await contexts.RowAsync(name));
        Assert.Empty(await contexts.AllowedColumnsAsync(orders));
        Assert.Equal(string.Empty, await SnapshotAsync(cs, name));
        using var gone = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Get, $"/api/v1/powerai/ai-contexts/{name}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SkippableFact]
    public async Task RemovingTheRepository_RemovesItsContexts_AndEverythingTheyServed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var orders = await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo { Id = contexts.RepoId, Name = "aictx_" + estate.Suffix, FirstSeenUtc = DateTime.UtcNow, LastSyncUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var name = "sales-" + estate.Suffix;
        contexts.Write("sales.ai-context.yaml", $"aiContext: {name}\ntables:\n  - table: dbo.{estate.Name("Orders")}\n");
        await contexts.SyncAsync();
        var approved = await AiContextTestKit.ApproveAsync(client, admin, name);
        Assert.Equal("aictx_" + estate.Suffix, approved.Summary.RepoName);

        await using (var db = CatalogDatabase.Create(cs))
        {
            Assert.NotNull(await RepoStore.DeleteAsync(db, contexts.RepoId));
        }

        Assert.Null(await contexts.RowAsync(name));
        Assert.Empty(await contexts.AllowedColumnsAsync(orders));
    }

    [SkippableFact]
    public async Task AnAnswerAlreadySaved_StaysAsItIs_AndOnlyTheContextsOwnAnswersComeAndGoWithIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        await estate.TableAsync("Orders", "OrderId", "Amount");
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        var name = "sales-" + estate.Suffix;
        var savedQuestion = $"How many orders are there ({estate.Suffix})?";
        var savedSql = $"SELECT COUNT(OrderId) AS orders FROM dbo.{estate.Name("Orders")}";
        var ownQuestion = $"What is the average order ({estate.Suffix})?";
        long savedId;
        await using (var db = CatalogDatabase.Create(cs))
        {
            var saved = new CatalogSemanticExample
            {
                Question = savedQuestion, Sql = savedSql, ObjectKeys = string.Empty,
                Provenance = SemanticExampleProvenance.UserConfirmed, ConfirmedUtc = DateTime.UtcNow,
                ContentHash = SemanticExampleHash.Compute(savedQuestion, savedSql),
            };
            db.SemanticExamples.Add(saved);
            await db.SaveChangesAsync();
            savedId = saved.Id;
        }

        contexts.Write("sales.ai-context.yaml", $"""
            aiContext: {name}
            tables:
              - table: dbo.{estate.Name("Orders")}
            examples:
              - question: {savedQuestion}
                sql: {savedSql}
              - question: {ownQuestion}
                sql: SELECT AVG(Amount) AS average FROM dbo.{estate.Name("Orders")}
            """);
        await contexts.SyncAsync();
        await AiContextTestKit.ApproveAsync(client, admin, name);

        long ownId;
        await using (var db = CatalogDatabase.Create(cs))
        {
            var saved = await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.Id == savedId);
            Assert.Null(saved.AiContext);
            Assert.Equal(SemanticExampleProvenance.UserConfirmed, saved.Provenance);
            ownId = (await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.AiContext == name)).Id;
        }

        // The context's own answer is removed from its file, not by hand.
        using (var refused = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/powerai/semantic-layer/examples/{ownId}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        await AiContextTestKit.RevokeAsync(client, admin, name);
        await using (var db = CatalogDatabase.Create(cs))
        {
            Assert.False(await db.SemanticExamples.AnyAsync(e => e.Id == ownId));
            Assert.True(await db.SemanticExamples.AnyAsync(e => e.Id == savedId));
        }
    }

    [SkippableFact]
    public async Task WhatTheGuiHeldBeforeContextsWereFiles_IsExportedForReview_AndGivesWayToTheContextThatTakesItsTable()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var estate = new Estate(cs);
        var legacy = await estate.TableAsync("Ledger", "EntryId", "Amount", "Note");
        var measure = "ledger_total_" + estate.Suffix;
        var now = DateTime.UtcNow;
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.ColumnPolicies.Add(new CatalogColumnPolicy { ObjectKey = legacy, ColumnName = "EntryId", UpdatedUtc = now });
                db.ColumnPolicies.Add(new CatalogColumnPolicy
                {
                    ObjectKey = legacy, ColumnName = "Amount", Description = "Posted amount", Synonyms = "value", UpdatedUtc = now,
                });
                db.SemanticObjects.Add(new CatalogSemanticObject
                {
                    ObjectKey = legacy, BusinessName = "Ledger", Description = "One row per entry.", KeyColumns = "EntryId", UpdatedUtc = now,
                });
                db.SemanticMeasures.Add(new CatalogSemanticMeasure
                {
                    Name = measure, ObjectKey = legacy, Expression = "SUM(Amount)", UpdatedUtc = now,
                });
                await db.SaveChangesAsync();
            }

            var list = await GetJsonAsync<AiContextListDto>(client, admin, "/api/v1/powerai/ai-contexts");
            Assert.True(list.HasEarlierContent);

            // The export is one context file that reads back cleanly: the table opens what it opened, denies the rest,
            // and keeps its description, key, column notes and measure. None of it is served until approved.
            var yaml = await GetStringAsync(client, admin, "/api/v1/powerai/ai-contexts/earlier-content");
            var parse = YamlAiContextLoader.Parse(yaml, "earlier.ai-context.yaml");
            Assert.True(parse.Problems.Count == 0, string.Join(" | ", parse.Problems));
            Assert.Equal(AiContextEndpoints.EarlierContentName, parse.Name);
            var table = Assert.Single(parse.Document!.Tables, t => t.Table.Name == estate.Name("Ledger"));
            Assert.Equal(["Note"], table.Deny);
            Assert.Equal(["EntryId"], table.Key);
            Assert.Equal("Ledger", table.BusinessName);
            var described = Assert.Single(table.Columns);
            Assert.Equal("Amount", described.Name);
            Assert.Equal("Posted amount", described.Description);
            Assert.Equal(["value"], described.Synonyms);
            Assert.Equal(measure, Assert.Single(table.Measures).Name);
            Assert.Empty(await contexts.AllowedColumnsAsync(legacy));

            // A context that takes the table replaces what the GUI held for it.
            var name = "ledger-" + estate.Suffix;
            contexts.Write("ledger.ai-context.yaml", $"aiContext: {name}\ntables:\n  - table: dbo.{estate.Name("Ledger")}\n    deny: [Note]\n");
            await contexts.SyncAsync();
            await AiContextTestKit.ApproveAsync(client, admin, name);
            Assert.Equal(["Amount", "EntryId"], await contexts.AllowedColumnsAsync(legacy));
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.ColumnPolicies.AnyAsync(p => p.ObjectKey == legacy && p.AiContext == null));
                Assert.False(await db.SemanticObjects.AnyAsync(o => o.ObjectKey == legacy && o.AiContext == null));
                Assert.False(await db.SemanticMeasures.AnyAsync(m => m.Name == measure));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ColumnPolicies.Where(p => p.ObjectKey == legacy).ExecuteDeleteAsync();
            await db.SemanticObjects.Where(o => o.ObjectKey == legacy).ExecuteDeleteAsync();
            await db.SemanticMeasures.Where(m => m.Name == measure).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task OnlyAnAdmin_SeesOrDecidesAContext()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var operate = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate"]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Get, "/api/v1/powerai/ai-contexts"),
            (HttpMethod.Get, "/api/v1/powerai/ai-contexts/earlier-content"),
            (HttpMethod.Get, "/api/v1/powerai/ai-contexts/sales"),
            (HttpMethod.Get, "/api/v1/powerai/ai-contexts/sales/yaml"),
            (HttpMethod.Post, "/api/v1/powerai/ai-contexts/sales/approve"),
            (HttpMethod.Post, "/api/v1/powerai/ai-contexts/sales/revoke"),
        })
        {
            using var response = await AiContextTestKit.SendAsync(
                client, operate, method, uri, method == HttpMethod.Post ? new ApproveAiContextRequest("x") : null);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {uri}: {(int)response.StatusCode}");
        }
    }

    [SkippableFact]
    public async Task AFileTooLargeToBeAContext_IsSkippedWithAWarning()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var contexts = new AiContextTestKit(cs);
        var name = "huge-" + Guid.NewGuid().ToString("N")[..8];
        contexts.Write("huge.ai-context.yaml", $"aiContext: {name}\ndescription: {new string('x', AiContextSync.MaxFileBytes)}\n");

        var warnings = await contexts.SyncAsync();

        Assert.Contains(warnings, w => w.StartsWith("huge.ai-context.yaml: skipped", StringComparison.Ordinal));
        Assert.Null(await contexts.RowAsync(name));
    }

    /// <summary>Everything a context serves, with each row's id and when it was written, so two snapshots differ when
    /// any row was rewritten.</summary>
    private static async Task<string> SnapshotAsync(string cs, string context)
    {
        await using var db = CatalogDatabase.Create(cs);
        var rows = new List<string>();
        rows.AddRange((await db.ColumnPolicies.AsNoTracking().Where(p => p.AiContext == context).OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.UpdatedUtc }).ToListAsync()).Select(p => $"column {p.Id} {p.UpdatedUtc:O}"));
        rows.AddRange((await db.SemanticObjects.AsNoTracking().Where(o => o.AiContext == context).OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.UpdatedUtc }).ToListAsync()).Select(o => $"table {o.Id} {o.UpdatedUtc:O}"));
        rows.AddRange((await db.SemanticMeasures.AsNoTracking().Where(m => m.AiContext == context).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.UpdatedUtc }).ToListAsync()).Select(m => $"measure {m.Id} {m.UpdatedUtc:O}"));
        rows.AddRange((await db.SemanticRelationships.AsNoTracking().Where(r => r.AiContext == context).OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.UpdatedUtc }).ToListAsync()).Select(r => $"join {r.Id} {r.UpdatedUtc:O}"));
        rows.AddRange((await db.SemanticExamples.AsNoTracking().Where(e => e.AiContext == context).OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.ConfirmedUtc }).ToListAsync()).Select(e => $"example {e.Id} {e.ConfirmedUtc:O}"));
        return string.Join('\n', rows);
    }

    private static async Task<int> ExampleCountAsync(string cs, string context)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.SemanticExamples.CountAsync(e => e.AiContext == context);
    }

    private static async Task<string?> DescriptionAsync(string cs, string objectKey)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.SemanticObjects.AsNoTracking().Where(o => o.ObjectKey == objectKey).Select(o => o.Description).SingleOrDefaultAsync();
    }

    private static async Task<string> GetStringAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await AiContextTestKit.SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await AiContextTestKit.SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    /// <summary>Warehouse tables in the catalog, as a scan or a sync leaves them, each named with the test's suffix so a
    /// context file names only them. Disposing it removes them, their columns, and the example questions the test made.</summary>
    private sealed class Estate(string cs) : IAsyncDisposable
    {
        private readonly List<string> _keys = [];

        public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

        private string ServerRef => "${env:SQLFLOW_AICTX_" + Suffix + "}";

        public string Name(string table) => $"{table}_{Suffix}";

        private string Key(string table) => $"{ServerRef}|dw|dbo|{Name(table).ToLowerInvariant()}";

        public async Task<string> TableAsync(string table, params string[] columns)
        {
            var key = Key(table);
            var now = DateTime.UtcNow;
            await using var db = CatalogDatabase.Create(cs);
            db.Objects.Add(new CatalogObject
            {
                Key = key, ServerRef = ServerRef, Database = "dw", Schema = "dbo", Name = Name(table), Kind = "Table",
                FirstSeenUtc = now, LastSeenUtc = now,
            });
            for (var i = 0; i < columns.Length; i++)
            {
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = key, Ordinal = i + 1, Name = columns[i], DataType = "int", Nullable = false, Tier = "Observed",
                });
            }

            await db.SaveChangesAsync();
            _keys.Add(key);
            return key;
        }

        public async Task AddColumnAsync(string table, string column)
        {
            var key = Key(table);
            await using var db = CatalogDatabase.Create(cs);
            var ordinal = await db.ObjectColumns.Where(c => c.ObjectKey == key).MaxAsync(c => c.Ordinal);
            db.ObjectColumns.Add(new CatalogObjectColumn
            {
                ObjectKey = key, Ordinal = ordinal + 1, Name = column, DataType = "int", Nullable = false, Tier = "Observed",
            });
            await db.SaveChangesAsync();
        }

        public async Task DropColumnAsync(string table, string column)
        {
            var key = Key(table);
            await using var db = CatalogDatabase.Create(cs);
            await db.ObjectColumns.Where(c => c.ObjectKey == key && c.Name == column).ExecuteDeleteAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SemanticExamples.Where(e => e.Question.Contains(Suffix)).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => _keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => _keys.Contains(o.Key)).ExecuteDeleteAsync();
        }
    }
}
