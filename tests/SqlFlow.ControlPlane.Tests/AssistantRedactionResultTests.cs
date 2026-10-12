using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// <see cref="AssistantScope.RedactResultAsync"/> over every kind of result an endpoint on the assistant surface
/// returns, against a real catalog holding one table whose <c>Ssn</c> is outside the semantic layer and whose
/// <c>Name</c> is in it: a value, a text (a flow's YAML, a run's trace), an error carrying a value, and a problem.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantRedactionResultTests
{
    [SkippableFact]
    public async Task AText_IsRedactedLineByLine_InTheContextOfTheWholeText()
    {
        await WithCatalogAsync(async (http, table) =>
        {
            var yaml = $"flowType: ing\ntarget:\n  object: dbo.{table}\r\ncolumns:\n  - Name\n  - Ssn\n";
            var result = await AssistantScope.RedactResultAsync(http, TypedResults.Text(yaml, "text/yaml", statusCode: 200), default);

            var content = Assert.IsType<ContentHttpResult>(result);
            Assert.Equal("text/yaml", content.ContentType);
            Assert.Equal(200, content.StatusCode);
            var lines = content.ResponseContent!.Split('\n');
            Assert.Equal(7, lines.Length);
            Assert.Equal($"  object: dbo.{table}\r", lines[2]);
            Assert.Equal("  - Name", lines[4]);
            Assert.Equal(AssistantScope.WithheldText, lines[5]);
        });
    }

    [SkippableFact]
    public async Task AText_ThatNamesNothingOutsideTheLayer_IsReturnedAsItWas()
    {
        await WithCatalogAsync(async (http, table) =>
        {
            var original = TypedResults.Text($"SELECT Name FROM dbo.{table}");
            Assert.Same(original, await AssistantScope.RedactResultAsync(http, original, default));

            var empty = TypedResults.Text(string.Empty);
            Assert.Same(empty, await AssistantScope.RedactResultAsync(http, empty, default));
        });
    }

    [SkippableFact]
    public async Task AnErrorCarryingAValue_IsRedacted_AndKeepsItsStatus()
    {
        await WithCatalogAsync(async (http, table) =>
        {
            var result = await AssistantScope.RedactResultAsync(
                http, TypedResults.BadRequest($"Column dbo.{table}.Ssn is not allowed"), default);

            var json = Assert.IsType<JsonHttpResult<JsonNode>>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, json.StatusCode);
            Assert.Equal(AssistantScope.WithheldText, json.Value!.GetValue<string>());
        });
    }

    [SkippableFact]
    public async Task AProblem_HasItsTitleDetailAndExtensionsRedacted_ReadTogether()
    {
        await WithCatalogAsync(async (http, table) =>
        {
            var problem = TypedResults.Problem(
                detail: $"The query read {table}.",
                title: "Ssn is refused",
                statusCode: 400,
                extensions: new Dictionary<string, object?> { ["column"] = "Ssn", ["kept"] = "Name", ["count"] = 2, ["Ssn"] = 1 });

            var result = await AssistantScope.RedactResultAsync(http, problem, default);

            Assert.Same(problem, result);
            Assert.Equal(AssistantScope.WithheldText, problem.ProblemDetails.Title);
            Assert.Equal($"The query read {table}.", problem.ProblemDetails.Detail);
            Assert.Equal(AssistantScope.WithheldText, problem.ProblemDetails.Extensions["column"]?.ToString());

            // What redaction left alone keeps its own value, and an extension NAMED for the column goes, name and all.
            Assert.Equal("Name", problem.ProblemDetails.Extensions["kept"]);
            Assert.Equal(2, problem.ProblemDetails.Extensions["count"]);
            Assert.False(problem.ProblemDetails.Extensions.ContainsKey("Ssn"));
            Assert.True(problem.ProblemDetails.Extensions.ContainsKey(AssistantScope.WithheldText));
        });
    }

    [SkippableFact]
    public async Task AValue_IsRedacted_AndAResultWhoseHeadersCarryMeaning_IsNot()
    {
        await WithCatalogAsync(async (http, table) =>
        {
            var ok = await AssistantScope.RedactResultAsync(http, TypedResults.Ok(new { sql = $"SELECT Ssn FROM dbo.{table}" }), default);
            var json = Assert.IsType<JsonHttpResult<JsonNode>>(ok);
            Assert.Equal(AssistantScope.WithheldText, json.Value!["sql"]!.GetValue<string>());

            var created = TypedResults.Created("/x", new { sql = $"SELECT Ssn FROM dbo.{table}" });
            Assert.Same(created, await AssistantScope.RedactResultAsync(http, created, default));
        });
    }

    /// <summary>Seeds the table under a name of its own, runs <paramref name="check"/> with an HTTP context whose services
    /// reach the catalog, and removes what it seeded.</summary>
    private static async Task WithCatalogAsync(Func<HttpContext, string, Task> check)
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = "Redacted_" + suffix;
        var key = $"${{env:SQLFLOW_REDACT_{suffix}}}|dw|dbo|{table}".ToLowerInvariant();
        var now = DateTime.UtcNow;
        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Objects.Add(new CatalogObject
                {
                    Key = key, ServerRef = key.Split('|')[0], Database = "dw", Schema = "dbo", Name = table, Kind = "Table",
                    FirstSeenUtc = now, LastSeenUtc = now,
                });
                seed.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = key, Ordinal = 1, Name = "Name", DataType = "nvarchar(50)", Nullable = false, Tier = "Observed",
                });
                seed.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = key, Ordinal = 2, Name = "Ssn", DataType = "varchar(11)", Nullable = false, Tier = "Observed",
                });
                seed.ColumnPolicies.Add(new CatalogColumnPolicy { ObjectKey = key, ColumnName = "Name", IsAllowed = true, UpdatedUtc = now });
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => CatalogDatabase.Create(cs));
            services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            await check(http, table);
        }
        finally
        {
            await using var cleanup = CatalogDatabase.Create(cs);
            await cleanup.ColumnPolicies.Where(p => p.ObjectKey == key).ExecuteDeleteAsync();
            await cleanup.ObjectColumns.Where(c => c.ObjectKey == key).ExecuteDeleteAsync();
            await cleanup.Objects.Where(o => o.Key == key).ExecuteDeleteAsync();
        }
    }
}
