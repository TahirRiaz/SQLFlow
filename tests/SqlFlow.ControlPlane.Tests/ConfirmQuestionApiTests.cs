using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The write half of PowerAI retrieval at the HTTP boundary: the confirm endpoint that grows the
/// confirmed-example store (POWERAI.md Section 6). What matters here is the contract a caller acts on rather
/// than the ranking (covered by <see cref="QuestionSearchTests"/>): a rejection stores NOTHING and says so, an
/// acceptance stores exactly one example, a query that would write is refused before it can become precedent
/// later answers are adapted from, and confirming the same pair twice refreshes one row instead of stacking
/// duplicates that would each score identically in every later search.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConfirmQuestionApiTests
{
    [SkippableFact]
    public async Task WhenRetrievalIsDisabled_SaysNotConfigured_RatherThanSilentlyStoring()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // Retrieval is off in the shipped defaults. A confirmation accepted here would accumulate knowledge
        // nobody could ever retrieve, which is worse than refusing it with the key that turns it on.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);

        using var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
            "What is our revenue by region?", "SELECT Region FROM Sales", QuestionConfirmationOutcome.Accepted));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(
            "PowerAI:Retrieval:Enabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ARejection_StoresNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var question = $"Which depot missed its SLA on {Guid.NewGuid():N}?";
        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);

        // Deliberately a statement that would NOT survive the read-only guard: a rejection is answered before
        // the SQL is parsed, since nothing is stored either way, so this proves that path does not quietly
        // demand a valid SELECT before it will accept a rejection.
        using var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
            question, "DELETE FROM Depots", QuestionConfirmationOutcome.Rejected));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
        Assert.NotNull(body);
        Assert.False(body.Stored);
        Assert.Null(body.ExampleId);
        Assert.Null(body.Provenance);

        await using var db = CatalogDatabase.Create(cs);
        Assert.False(await db.SemanticExamples.AnyAsync(e => e.Question == question));
    }

    [SkippableFact]
    public async Task AnAcceptance_StoresOneExample_AndConfirmingItAgainRefreshesTheSameRow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var question = $"How many parcels shipped on {Guid.NewGuid():N}?";
        const string Sql = "SELECT COUNT(*) FROM Parcels";
        var hash = SemanticExampleHash.Compute(question, Sql);

        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            using (var first = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
                question, Sql, QuestionConfirmationOutcome.Accepted, ["[Dw].[arc].[Parcels]"], Confidence: 4)))
            {
                first.EnsureSuccessStatusCode();
                var body = await first.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
                Assert.NotNull(body);
                Assert.True(body.Stored);
                Assert.Equal(SemanticExampleProvenance.UserConfirmed, body.Provenance);
            }

            var stored = await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.ContentHash == hash);
            Assert.Equal(Sql, stored.Sql);
            Assert.Equal("[Dw].[arc].[Parcels]", stored.ObjectKeys);
            Assert.Equal(4, stored.Confidence);
            Assert.Equal(SemanticExampleProvenance.UserConfirmed, stored.Provenance);

            // The same pair again, differently spaced and capitalized: the hash normalizes both away, so this
            // is the same fact being reaffirmed rather than a second one to store.
            using (var second = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
                question.ToUpperInvariant(), "SELECT  COUNT(*)  FROM Parcels",
                QuestionConfirmationOutcome.Corrected)))
            {
                second.EnsureSuccessStatusCode();
                var body = await second.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
                Assert.NotNull(body);
                Assert.True(body.Stored);
                Assert.Equal(stored.Id, body.ExampleId);
            }

            Assert.Equal(1, await db.SemanticExamples.CountAsync(e => e.ContentHash == hash));
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// An example confirmed without a datasource is stored with the one its data lives on, so a later auto-run
    /// needs nobody to pick a connection: from the object keys when the caller supplies them (the lineage
    /// identity, whose first segment is the connection reference), otherwise from the tables the SQL reads.
    /// </summary>
    [SkippableFact]
    public async Task AnExampleConfirmedWithoutADatasource_IsStoredWithTheOneItsDataLivesOn()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var reference = "${env:SQLFLOW_CONFIRM_INFER_" + suffix + "}";
        var table = "Parcels_" + suffix;
        var objectKey = $"{reference}|dw|edw|{table}".ToLowerInvariant();
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var fromSqlQuestion = $"How many parcels are there ({suffix})?";
        var fromSql = $"SELECT COUNT(*) AS N FROM edw.{table}";
        var fromKeysQuestion = $"How many parcels did the report count ({suffix})?";
        const string FromKeysSql = "SELECT COUNT(*) AS N FROM ReportModelParcels";
        var hashes = new[]
        {
            SemanticExampleHash.Compute(fromSqlQuestion, fromSql),
            SemanticExampleHash.Compute(fromKeysQuestion, FromKeysSql),
        };

        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = "confirm_infer_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = Guid.NewGuid(), RepoId = repoId, Name = "confirm_infer_flow_" + suffix, Kind = "ing",
                RelativePath = "infer/flow.yaml", Active = true, SourceServer = reference, TargetServer = reference,
                DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
            });
            db.Objects.Add(new CatalogObject
            {
                Key = objectKey, ServerRef = reference, Database = "dw", Schema = "edw", Name = table, Kind = "Table",
                FirstSeenUtc = now, LastSeenUtc = now,
            });
            await db.SaveChangesAsync();

            using (var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
                fromSqlQuestion, fromSql, QuestionConfirmationOutcome.Accepted)))
            {
                response.EnsureSuccessStatusCode();
            }

            // The SQL names a model entity the catalog does not know; the object keys still carry the reference.
            using (var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
                fromKeysQuestion, FromKeysSql, QuestionConfirmationOutcome.Accepted, [objectKey])))
            {
                response.EnsureSuccessStatusCode();
            }

            var stored = await db.SemanticExamples.AsNoTracking()
                .Where(e => hashes.Contains(e.ContentHash))
                .ToDictionaryAsync(e => e.Question, e => e.SourceRef);
            Assert.Equal(reference, stored[fromSqlQuestion]);
            Assert.Equal(reference, stored[fromKeysQuestion]);
        }
        finally
        {
            await db.SemanticExamples.Where(e => hashes.Contains(e.ContentHash)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task AQueryThatWouldWrite_IsRefused_RatherThanStoredAsPrecedent()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var question = $"How do we clear the staging table on {Guid.NewGuid():N}?";
        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);

        using var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
            question, "TRUNCATE TABLE Staging", QuestionConfirmationOutcome.Accepted));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = CatalogDatabase.Create(cs);
        Assert.False(await db.SemanticExamples.AnyAsync(e => e.Question == question));
    }

    [SkippableFact]
    public async Task AnUnknownOutcome_IsRejected_NamingTheValidValues()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);

        using var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
            "Anything at all?", "SELECT 1", "maybe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(QuestionConfirmationOutcome.Accepted, body, StringComparison.Ordinal);
        Assert.Contains(QuestionConfirmationOutcome.Rejected, body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AQuestionConfirmedWithTheCwdCommand_IsStoredWithoutIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // The GUI row sends the message as typed ("!cwd ..."); an assistant strips the command. Both must land the
        // same stored question, or one question saved from two surfaces becomes two examples.
        var question = $"How many depots are open ({Guid.NewGuid():N})?";
        const string Sql = "SELECT 1 AS Depots";
        await using var factory = Enabled(cs);
        using var client = factory.CreateClient();
        var token = await IssueOperateTokenAsync(client);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            using (var response = await ConfirmAsync(client, token, new ConfirmQuestionRequest(
                $"!cwd   {question}", Sql, QuestionConfirmationOutcome.Accepted)))
            {
                response.EnsureSuccessStatusCode();
            }

            var stored = await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.Question.Contains(question));
            Assert.Equal(question, stored.Question);
            Assert.Equal(SemanticExampleHash.Compute(question, Sql), stored.ContentHash);
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.Question.Contains(question)).ExecuteDeleteAsync();
        }
    }

    /// <summary>A host with retrieval turned on, which is what makes the example store writable at all. Synonym
    /// expansion is switched off: this file tests the write path and its storage semantics, not the LLM
    /// expansion (covered by <see cref="QuestionSearchTests"/>), and turning it on would require an Anthropic
    /// key that need not exist in a test environment for this file's assertions to hold.</summary>
    private static ControlPlaneAppFactory Enabled(string catalogConnection)
        => new ControlPlaneAppFactory()
            .WithCatalog(catalogConnection)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");

    private static async Task<string> IssueOperateTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(
        HttpClient client, string token, ConfirmQuestionRequest body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/powerai/questions/confirm", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
