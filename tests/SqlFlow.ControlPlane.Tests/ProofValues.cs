using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core.Query;
using SqlFlow.SqlServer.Query;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The stored PowerAI proof values, read once and shared by the suites that use them:
/// <see cref="PowerAiProofValueTests"/> compares the whole set against the warehouse, and
/// <see cref="PowerAiQuestionReuseTests"/> borrows individual questions as a corpus of answers that are known
/// to be right, so a reused query can be checked against the value it is supposed to produce.
///
/// The values are DATA captured from the AdventureWorksDW sample by tools/powerai-proof/capture.py, in the
/// executor's own cell rendering. Nothing here ever adjusts one to make a test pass.
/// </summary>
internal static class ProofValues
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<ProofFixture> Loaded = new(Load);

    /// <summary>Every stored question, in fixture order.</summary>
    public static IReadOnlyList<ProofQuestion> All => Loaded.Value.Questions;

    /// <summary>The database the proof values were captured against.</summary>
    public static string Database => Loaded.Value.Source.Database;

    /// <summary>The number of questions the fixture claims to hold, as recorded when it was built.</summary>
    public static int DeclaredCount => Loaded.Value.QuestionCount;

    /// <summary>One stored question by its fixture id, failing loudly when it is absent: a test naming a
    /// question that no longer exists is a broken test, not a skipped one.</summary>
    public static ProofQuestion ById(string id)
    {
        var question = All.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.Ordinal));
        Assert.True(question is not null, $"No proof value '{id}' in the fixture.");
        return question!;
    }

    /// <summary>Runs a statement through the same runner the node uses and returns its rows, so a test can
    /// compare what a reused query produces to what the fixture recorded.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<string?>>> RunAsync(string connectionString, string sql)
    {
        var result = await SqlServerQueryRunner.RunAsync(
            connectionString,
            new QueryRunRequest { Sql = sql, Database = Database, MaxRows = QueryRunRequest.DefaultMaxRows },
            CancellationToken.None);
        return result.Rows;
    }

    private static ProofFixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "powerai-proof-values.json");
        Assert.True(File.Exists(path), $"Expected the proof-value fixture at {Path.GetFullPath(path)}.");

        var fixture = JsonSerializer.Deserialize<ProofFixture>(File.ReadAllText(path), JsonOptions);
        Assert.NotNull(fixture);
        return fixture;
    }

    internal sealed record ProofFixture
    {
        [JsonPropertyName("source")]
        public required ProofSource Source { get; init; }

        [JsonPropertyName("questionCount")]
        public required int QuestionCount { get; init; }

        [JsonPropertyName("questions")]
        public required IReadOnlyList<ProofQuestion> Questions { get; init; }
    }

    internal sealed record ProofSource
    {
        [JsonPropertyName("database")]
        public required string Database { get; init; }

        [JsonPropertyName("datasourceRef")]
        public required string DatasourceRef { get; init; }
    }

    internal sealed record ProofQuestion
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("shape")]
        public required string Shape { get; init; }

        [JsonPropertyName("question")]
        public required string Question { get; init; }

        [JsonPropertyName("sql")]
        public required string Sql { get; init; }

        [JsonPropertyName("columns")]
        public required IReadOnlyList<string> Columns { get; init; }

        [JsonPropertyName("rowCount")]
        public required int RowCount { get; init; }

        [JsonPropertyName("rows")]
        public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }
    }
}
