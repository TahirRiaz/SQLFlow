using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The semantic layer end to end, as approved AI contexts make it: a table is on the assistant's schema surface only
/// once an admin approves a context naming it, with every column but those its file denies, and every annotation the
/// file gives (a key, a join, a measure) is refused when it names a denied column and withheld when served once a column
/// it names is denied. The denied columns' names must never appear anywhere in what an assistant is served. Every seeded
/// row is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticLayerApiTests
{
    [SkippableFact]
    public async Task TheLayerServesOnlyWhatAnApprovedContextOpens_AndRefusesAnnotationsOverDeniedColumns()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_SEMLAYER_" + suffix + "}";
        var ordersName = "Orders_" + suffix;
        var storeName = "Store_" + suffix;
        var ordersKey = $"{serverRef}|dw|dbo|{ordersName.ToLowerInvariant()}";
        var storeKey = $"{serverRef}|dw|dbo|{storeName.ToLowerInvariant()}";
        var repoId = Guid.NewGuid();
        var context = "orders-" + suffix;
        var measureName = "net_amount_" + suffix;
        var now = DateTime.UtcNow;

        // The two denied columns carry the suffix so their names cannot occur by accident in a served payload.
        var customerSsn = "CustomerSsn_" + suffix;
        var storeSecret = "Secret_" + suffix;
        var blockedExampleSql = $"SELECT {customerSsn} FROM dbo.{ordersName}";
        var servableExampleSql = $"SELECT SUM(Amount) AS total FROM dbo.{ordersName}";

        // The context file, with an extra Orders measure, key or join when a step needs one, and with Amount denied
        // (and so no longer described) when a step closes it.
        string ContextFile(string? extraMeasure = null, string key = "OrderId", string joinColumns = "StoreId", bool denyAmount = false)
        {
            var lines = new List<string>
            {
                $"aiContext: {context}",
                "instructions: Amounts are in the reporting currency.",
                "tables:",
                $"  - table: dw.dbo.{ordersName}",
                "    name: Orders",
                "    description: One row per order line.",
                "    synonyms: [sales]",
                $"    key: [{key}]",
            };
            if (!denyAmount)
            {
                lines.AddRange([
                    "    columns:",
                    "      - column: Amount",
                    "        description: Line amount excluding tax.",
                    $"        synonyms: [turnover{suffix}, revenue]",
                ]);
            }

            lines.Add(denyAmount ? $"    deny: [{customerSsn}, Amount]" : $"    deny: [{customerSsn}]");
            lines.AddRange([
                "    measures:",
                $"      - name: {measureName}",
                "        expression: SUM(Amount)",
                "        description: Total amount",
            ]);
            if (extraMeasure is not null)
            {
                lines.AddRange([$"      - name: bad_{suffix}", $"        expression: \"{extraMeasure}\""]);
            }

            lines.AddRange([
                "    joins:",
                $"      - to: dw.dbo.{storeName}",
                $"        columns: [{joinColumns}]",
                $"        toColumns: [{(joinColumns == "StoreId" ? "StoreId" : storeSecret)}]",
                "        type: left",
                "        description: Orders to the store that took them",
                $"  - table: dw.dbo.{storeName}",
                $"    deny: [{storeSecret}]",
            ]);
            return string.Join('\n', lines) + "\n";
        }

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs, repoId);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = ordersKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = ordersName,
                    Kind = "Table", KeyColumns = customerSsn, KeyOrigin = "Declared", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.Objects.Add(new CatalogObject
                {
                    Key = storeKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = storeName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                AddColumn(db, ordersKey, 1, "OrderId", "bigint");
                AddColumn(db, ordersKey, 2, "StoreId", "int");
                AddColumn(db, ordersKey, 3, "Amount", "decimal(18,2)");
                AddColumn(db, ordersKey, 4, customerSsn, "varchar(11)");
                AddColumn(db, storeKey, 1, "StoreId", "int");
                AddColumn(db, storeKey, 2, "StoreName", "nvarchar(100)");
                AddColumn(db, storeKey, 3, storeSecret, "nvarchar(100)");

                // Two joins the codebase exhibits: one on open columns, one on the two columns that stay denied.
                db.ObjectRelationships.Add(new CatalogObjectRelationship
                {
                    RepoId = repoId, FromObjectKey = ordersKey, FromColumns = "StoreId", ToObjectKey = storeKey,
                    ToColumns = "StoreId", Operators = string.Empty, JoinTypes = "Inner", Origin = "Join",
                    Tier = "Observed", Occurrences = 4,
                });
                db.ObjectRelationships.Add(new CatalogObjectRelationship
                {
                    RepoId = repoId, FromObjectKey = ordersKey, FromColumns = customerSsn, ToObjectKey = storeKey,
                    ToColumns = storeSecret, Operators = string.Empty, JoinTypes = "Left", Origin = "Join",
                    Tier = "Observed", Occurrences = 1,
                });

                db.SemanticExamples.Add(Example($"who ordered {suffix}", blockedExampleSql, ordersKey, now));
                db.SemanticExamples.Add(Example($"total amount {suffix}", servableExampleSql, ordersKey, now.AddMinutes(-1)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
            var describe = $"/api/v1/semantic-layer/tables/describe?key={Uri.EscapeDataString(ordersKey)}";

            // 1. Nothing is approved: neither table exists on the assistant's surface, even once a file proposes it.
            contexts.Write("orders.ai-context.yaml", ContextFile());
            await contexts.SyncAsync();
            using (var notInLayer = await AiContextTestKit.SendAsync(client, adminToken, HttpMethod.Get, describe))
            {
                Assert.Equal(HttpStatusCode.NotFound, notInLayer.StatusCode);
            }

            var emptySearch = await GetJsonAsync<SemanticSearchDto>(client, adminToken, $"/api/v1/semantic-layer/search?q={ordersName}");
            Assert.DoesNotContain(emptySearch.Tables.Items, h => h.Table.Key == ordersKey);

            // 2. An admin approves it: both tables, every column but the denied ones.
            await AiContextTestKit.ApproveAsync(client, adminToken, context);

            // 3. A business synonym finds the table through its column.
            var synonymSearch = await GetJsonAsync<SemanticSearchDto>(
                client, adminToken, $"/api/v1/semantic-layer/search?q={Uri.EscapeDataString("turnover" + suffix)}");
            var hit = Assert.Single(synonymSearch.Tables.Items, h => h.Table.Key == ordersKey);
            Assert.Contains(hit.MatchedColumns, c => c.Name == "Amount");
            Assert.Contains("columns", hit.MatchedOn);

            // 4. The served bundle carries only what the context opens: the open columns, its key, its join (the
            //    discovered twin is not repeated, the join on denied columns is withheld), the measure, the context's
            //    instructions, and only the example whose SQL passes the guards. No denied name appears anywhere.
            var tableJson = await GetStringAsync(client, adminToken, describe);
            Assert.DoesNotContain(customerSsn, tableJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storeSecret, tableJson, StringComparison.OrdinalIgnoreCase);

            var table = await GetJsonAsync<SemanticTableDto>(client, adminToken, describe);
            Assert.Equal(["OrderId", "StoreId", "Amount"], table.Columns.Select(c => c.Name));
            Assert.Equal("Orders", table.BusinessName);
            Assert.Equal(["OrderId"], table.KeyColumns);
            Assert.Equal(SemanticLayer.CuratedOrigin, table.KeyOrigin);
            Assert.Equal(context, table.Context);
            Assert.Equal("Amounts are in the reporting currency.", table.Instructions);
            var servedJoin = Assert.Single(table.Joins);
            Assert.Equal(SemanticLayer.CuratedOrigin, servedJoin.Source);
            Assert.Equal(["Left"], servedJoin.JoinTypes);
            Assert.Contains(table.Measures, m => m.Name == measureName);
            var example = Assert.Single(table.Examples);
            Assert.Equal(servableExampleSql, example.Sql);

            var overview = await GetJsonAsync<SemanticOverviewDto>(client, adminToken, "/api/v1/semantic-layer");
            var served = Assert.Single(overview.Contexts, c => c.Name == context);
            Assert.Equal(2, served.TableCount);
            Assert.Contains($"Context '{context}':", overview.Instructions, StringComparison.Ordinal);

            // The admin view shows what is withheld and why.
            var adminObject = await GetJsonAsync<SemanticObjectAdminDto>(
                client, adminToken, $"/api/v1/powerai/semantic-layer/objects/detail?key={Uri.EscapeDataString(ordersKey)}");
            Assert.Equal(context, adminObject.Context);
            Assert.Equal(4, adminObject.Columns.Count);
            Assert.False(adminObject.Columns.Single(c => c.ColumnName == customerSsn).IsAllowed);
            Assert.Equal(2, adminObject.DiscoveredJoins.Count);
            Assert.All(adminObject.DiscoveredJoins, j => Assert.NotNull(j.Problem));
            Assert.Contains(adminObject.Examples, e => e.Sql == blockedExampleSql && e.Problem is not null);

            // 5. A measure over a denied column, a FROM smuggled in, a subquery, a key or a join over denied columns keeps
            //    a version from being approved; the approved one stays served meanwhile.
            var refusedVersions = new (string Name, string File)[]
            {
                ("denied measure", ContextFile(extraMeasure: $"COUNT(DISTINCT {customerSsn})")),
                ("smuggled FROM", ContextFile(extraMeasure: $"SUM(Amount) FROM dbo.{storeName} --")),
                ("subquery", ContextFile(extraMeasure: $"(SELECT MAX(StoreId) FROM dbo.{storeName})")),
                ("denied key", ContextFile(key: customerSsn)),
                ("denied join", ContextFile(joinColumns: customerSsn)),
            };
            foreach (var (what, file) in refusedVersions)
            {
                contexts.Write("orders.ai-context.yaml", file);
                await contexts.SyncAsync();
                var invalid = await AiContextTestKit.GetAsync(client, adminToken, context);
                Assert.True(invalid.Summary.Status == AiContextStatus.Invalid, $"{what}: {invalid.Summary.Status}");
                Assert.NotEmpty(invalid.Problems);
                using (var refused = await AiContextTestKit.TryApproveAsync(client, adminToken, context, invalid.Summary.CurrentVersion))
                {
                    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                }

                var stillServed = await GetJsonAsync<SemanticTableDto>(client, adminToken, describe);
                Assert.Contains(stillServed.Measures, m => m.Name == measureName);
                Assert.DoesNotContain(stillServed.Measures, m => m.Name == "bad_" + suffix);
                Assert.Equal(["OrderId"], stillServed.KeyColumns);
                Assert.Single(stillServed.Joins);
            }

            // 6. The file denies Amount: the next sync closes it with no approval, and the measure reading it is withdrawn.
            contexts.Write("orders.ai-context.yaml", ContextFile(denyAmount: true));
            await contexts.SyncAsync();
            Assert.DoesNotContain("Amount", await contexts.AllowedColumnsAsync(ordersKey));
            var afterDeny = await GetJsonAsync<SemanticOverviewDto>(client, adminToken, "/api/v1/semantic-layer");
            Assert.DoesNotContain(afterDeny.Measures, m => m.Name == measureName);
            var withdrawn = await GetJsonAsync<SemanticObjectAdminDto>(
                client, adminToken, $"/api/v1/powerai/semantic-layer/objects/detail?key={Uri.EscapeDataString(ordersKey)}");
            Assert.NotNull(Assert.Single(withdrawn.Measures, m => m.Name == measureName).Problem);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SemanticExamples.Where(e => e.ObjectKeys == ordersKey).ExecuteDeleteAsync();
            await db.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == ordersKey || c.ObjectKey == storeKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == ordersKey || o.Key == storeKey).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A Power BI model built on a warehouse table is served in that table's bundle, graded against what the approved
    /// context opens the same way every other annotation is: a measure reading only open columns is served, one reading a
    /// denied column is withheld, and so is one that merely USES a withheld measure. A relationship is served only when
    /// both tables are in the layer and both columns are open. The denied column's name never reaches the assistant.
    /// </summary>
    [SkippableFact]
    public async Task APowerBiModel_IsServedOnTheTableItLoadsFrom_OnlyWhereEveryColumnItNamesIsOpen()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_PBIMODEL_" + suffix + "}";
        var salesName = "FactSales_" + suffix;
        var dateName = "DimDate_" + suffix;
        var salesKey = $"{serverRef}|dw|dbo|{salesName.ToLowerInvariant()}";
        var dateKey = $"{serverRef}|dw|dbo|{dateName.ToLowerInvariant()}";
        var subscriberKey = $"subscriber|||pbimodel_{suffix}";
        var customerSsn = "CustomerSsn_" + suffix;
        var repoId = Guid.NewGuid();
        const string ReportFile = "sales.pbix";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        await using var contexts = new AiContextTestKit(cs, repoId);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = salesKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = salesName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.Objects.Add(new CatalogObject
                {
                    Key = dateKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = dateName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                AddColumn(db, salesKey, 1, "Amount", "decimal(18,2)");
                AddColumn(db, salesKey, 2, "DateKey", "int");
                AddColumn(db, salesKey, 3, customerSsn, "varchar(11)");
                AddColumn(db, dateKey, 1, "DateKey", "int");

                db.Subscribers.Add(new CatalogSubscriber
                {
                    RepoId = repoId, Name = "Sales Report " + suffix, Type = "PowerBI", ObjectKey = subscriberKey,
                    File = "subscribers.yaml", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.SubscriberModelTables.Add(ModelTable(repoId, subscriberKey, ReportFile, "Sales", salesKey));
                db.SubscriberModelTables.Add(ModelTable(repoId, subscriberKey, ReportFile, "Date", dateKey));
                db.SubscriberModelFields.Add(ModelField(repoId, subscriberKey, ReportFile, "Sales", "Total Sales", "SUM(Sales[Amount])"));
                db.SubscriberModelFields.Add(ModelField(repoId, subscriberKey, ReportFile, "Sales", "Customers", $"DISTINCTCOUNT(Sales[{customerSsn}])"));
                db.SubscriberModelFields.Add(ModelField(repoId, subscriberKey, ReportFile, "Sales", "Average Sale", "DIVIDE([Total Sales], COUNTROWS(Sales))"));
                db.SubscriberModelFields.Add(ModelField(repoId, subscriberKey, ReportFile, "Sales", "Sales per Customer", "DIVIDE([Total Sales], [Customers])"));
                db.SubscriberModelRelationships.Add(ModelRelationship(repoId, subscriberKey, ReportFile, "Sales", "DateKey", "Date", "DateKey", isActive: false));
                db.SubscriberModelRelationships.Add(ModelRelationship(repoId, subscriberKey, ReportFile, "Sales", customerSsn, "Date", "DateKey", isActive: true));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
            contexts.Write("sales.ai-context.yaml", $"""
                aiContext: sales-{suffix}
                tables:
                  - table: dw.dbo.{salesName}
                    deny: [{customerSsn}]
                  - table: dw.dbo.{dateName}
                """);
            await contexts.SyncAsync();
            await AiContextTestKit.ApproveAsync(client, adminToken, $"sales-{suffix}");

            var describe = $"/api/v1/semantic-layer/tables/describe?key={Uri.EscapeDataString(salesKey)}";
            Assert.DoesNotContain(customerSsn, await GetStringAsync(client, adminToken, describe), StringComparison.OrdinalIgnoreCase);

            var table = await GetJsonAsync<SemanticTableDto>(client, adminToken, describe);
            var model = Assert.Single(table.ReportModels);
            Assert.Equal("Sales", model.ModelTable);
            Assert.Equal(["Total Sales", "Average Sale"], model.Measures.Select(m => m.Name));
            Assert.Equal("SUM(Sales[Amount])", model.Measures[0].Expression);
            var relationship = Assert.Single(model.Relationships);
            Assert.Equal("DateKey", relationship.OwnColumn);
            Assert.Equal(dateKey, relationship.OtherObjectKey);
            Assert.Equal("DateKey", relationship.OtherColumn);
            Assert.False(relationship.IsActive);

            // The read-only view shows everything the report defines, with why each withheld item is withheld.
            var admin = await GetJsonAsync<SemanticObjectAdminDto>(
                client, adminToken, $"/api/v1/powerai/semantic-layer/objects/detail?key={Uri.EscapeDataString(salesKey)}");
            var adminModel = Assert.Single(admin.ReportModels);
            Assert.Equal(4, adminModel.Fields.Count);
            Assert.NotNull(Assert.Single(adminModel.Fields, f => f.Name == "Customers").Problem);
            Assert.NotNull(Assert.Single(adminModel.Fields, f => f.Name == "Sales per Customer").Problem);
            Assert.Null(Assert.Single(adminModel.Fields, f => f.Name == "Average Sale").Problem);
            Assert.Equal(2, adminModel.Relationships.Count);
            Assert.Single(adminModel.Relationships, r => r.Problem is not null);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SubscriberModelFields.Where(f => f.SubscriberKey == subscriberKey).ExecuteDeleteAsync();
            await db.SubscriberModelRelationships.Where(r => r.SubscriberKey == subscriberKey).ExecuteDeleteAsync();
            await db.SubscriberModelTables.Where(t => t.SubscriberKey == subscriberKey).ExecuteDeleteAsync();
            await db.Subscribers.Where(s => s.ObjectKey == subscriberKey).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == salesKey || c.ObjectKey == dateKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == salesKey || o.Key == dateKey).ExecuteDeleteAsync();
        }
    }

    private static CatalogSubscriberModelTable ModelTable(
        Guid repoId, string subscriberKey, string reportFile, string name, string objectKey)
        => new()
        {
            RepoId = repoId, SubscriberKey = subscriberKey, ReportFile = reportFile, Name = name, ObjectKey = objectKey,
        };

    private static CatalogSubscriberModelField ModelField(
        Guid repoId, string subscriberKey, string reportFile, string table, string name, string dax)
        => new()
        {
            RepoId = repoId, SubscriberKey = subscriberKey, ReportFile = reportFile, TableName = table, Name = name,
            Kind = "measure", Expression = dax,
        };

    private static CatalogSubscriberModelRelationship ModelRelationship(
        Guid repoId, string subscriberKey, string reportFile, string fromTable, string fromColumn, string toTable,
        string toColumn, bool isActive)
        => new()
        {
            RepoId = repoId, SubscriberKey = subscriberKey, ReportFile = reportFile, FromTable = fromTable,
            FromColumn = fromColumn, ToTable = toTable, ToColumn = toColumn, Cardinality = "M:1", IsActive = isActive,
        };

    private static void AddColumn(CatalogDbContext db, string objectKey, int ordinal, string name, string dataType)
        => db.ObjectColumns.Add(new CatalogObjectColumn
        {
            ObjectKey = objectKey, Ordinal = ordinal, Name = name, DataType = dataType, Nullable = false, Tier = "Observed",
        });

    private static CatalogSemanticExample Example(string question, string sql, string objectKey, DateTime confirmedUtc)
        => new()
        {
            Question = question, Sql = sql, ObjectKeys = objectKey, Provenance = SemanticExampleProvenance.UserConfirmed,
            ConfirmedUtc = confirmedUtc, ContentHash = SemanticExampleHash.Compute(question, sql),
        };

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
}
