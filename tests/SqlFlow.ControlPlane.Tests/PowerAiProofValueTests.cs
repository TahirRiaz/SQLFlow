using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Query;
using SqlFlow.SqlServer.Query;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The answers PowerAI gives, checked against stored proof values rather than against the contract that carries
/// them.
///
/// Every other test around the question surface asserts the SHAPE of an exchange: that a token is minted, that a
/// refusal is a refusal, that the queued payload holds the statement someone was shown. None of them notice if the
/// number that comes back is wrong, because none of them ever run the query against real data. This suite closes
/// that gap. Each of the 300 questions in the fixture carries the SQL that answers it and the result that SQL
/// produced against the AdventureWorksDW sample, captured cell for cell in the executor's own rendering. A run
/// compares what the engine returns now to what it returned when the proof was taken, so a regression in the read
/// path, the guard, the cell rendering, or the sample itself shows up as a failing value rather than a passing
/// contract.
///
/// The proof values are DATA, not expectations someone typed: they were taken from the database by
/// tools/powerai-proof/capture.py and are regenerated the same way. A test here never adjusts a value to make
/// itself pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PowerAiProofValueTests
{
    /// <summary>The row cap every proof value was captured under, matching the auto-run default the control plane
    /// applies (<see cref="QueryRunRequest.DefaultMaxRows"/>). A fixture row count at this cap would be a page
    /// rather than an answer, which the fixture-integrity test rejects outright.</summary>
    private const int MaxRows = QueryRunRequest.DefaultMaxRows;


    // ---- the proof values themselves -----------------------------------------------------------------------

    /// <summary>
    /// Every question in the fixture, run against the warehouse, must still produce the stored proof value:
    /// the same columns in the same order, the same number of rows, and every cell identical.
    ///
    /// This runs as ONE test over all 300 rather than one test per question on purpose. The questions share a
    /// connection and the suite is a single statement about the estate ("the answers are still right"), so a
    /// regression that moves many values reads as one failure listing them rather than 300 separate reds. Each
    /// mismatch is reported with its question, so the output still names exactly what changed.
    /// </summary>
    [SkippableFact]
    public async Task EveryStoredQuestion_StillProducesItsProofValue()
    {
        var connectionString = AdventureWorksDb.Require();
        var failures = new List<string>();

        foreach (var question in ProofValues.All)
        {
            QueryResult result;
            try
            {
                result = await SqlServerQueryRunner.RunAsync(
                    connectionString,
                    new QueryRunRequest
                    {
                        Sql = question.Sql,
                        Database = ProofValues.Database,
                        MaxRows = MaxRows,
                    },
                    CancellationToken.None);
            }
            catch (SqlException ex)
            {
                failures.Add($"{question.Id} ({question.Question}): the query failed: {ex.Message}");
                continue;
            }
            catch (SqlFlowException ex)
            {
                failures.Add($"{question.Id} ({question.Question}): refused by the guard: {ex.Message}");
                continue;
            }

            var difference = Compare(question, result);
            if (difference is not null)
            {
                failures.Add($"{question.Id} ({question.Question}): {difference}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {ProofValues.All.Count} questions no longer produce their proof value. "
            + "Either the read path changed or the sample database did; regenerate with "
            + "tools/powerai-proof/capture.py only once the change is understood and intended."
            + Environment.NewLine
            + string.Join(Environment.NewLine, failures.Take(25))
            + (failures.Count > 25 ? $"{Environment.NewLine}... and {failures.Count - 25} more." : string.Empty));
    }

    /// <summary>
    /// A single value answer is the one a person reads as a sentence ("we sold 60,855 lines"), so the shape it
    /// arrives in matters as much as the number: exactly one row of exactly one column. A query that quietly
    /// started returning two rows would still "have the right first value" while meaning something else.
    /// </summary>
    [SkippableFact]
    public async Task ASingleValueQuestion_AnswersWithExactlyOneCell()
    {
        var connectionString = AdventureWorksDb.Require();
        var scalars = ProofValues.All.Where(q => q.Shape == "scalar").ToArray();
        Assert.NotEmpty(scalars);

        foreach (var question in scalars)
        {
            var result = await SqlServerQueryRunner.RunAsync(
                connectionString,
                new QueryRunRequest { Sql = question.Sql, Database = ProofValues.Database, MaxRows = MaxRows },
                CancellationToken.None);

            Assert.True(
                result.Columns.Count == 1,
                $"{question.Id} ({question.Question}) is a single-value question but returned "
                + $"{result.Columns.Count} columns.");
            Assert.True(
                result.RowCount <= 1,
                $"{question.Id} ({question.Question}) is a single-value question but returned "
                + $"{result.RowCount} rows.");
        }
    }

    /// <summary>
    /// A multi-value answer is one row read across its columns (sales, cost, and profit together). Its columns
    /// carry meaning by NAME, so both the naming and the single-row shape are pinned: a renamed column silently
    /// re-labels a number in whatever reads it.
    /// </summary>
    [SkippableFact]
    public async Task AMultiValueQuestion_AnswersWithOneRowOfNamedColumns()
    {
        var connectionString = AdventureWorksDb.Require();
        var multi = ProofValues.All.Where(q => q.Shape == "multivalue").ToArray();
        Assert.NotEmpty(multi);

        foreach (var question in multi)
        {
            var result = await SqlServerQueryRunner.RunAsync(
                connectionString,
                new QueryRunRequest { Sql = question.Sql, Database = ProofValues.Database, MaxRows = MaxRows },
                CancellationToken.None);

            Assert.True(
                result.RowCount == 1,
                $"{question.Id} ({question.Question}) is a multi-value question but returned "
                + $"{result.RowCount} rows.");
            Assert.True(
                result.Columns.Count > 1,
                $"{question.Id} ({question.Question}) is a multi-value question but returned one column.");
            Assert.Equal(question.Columns, result.Columns.Select(c => c.Name).ToArray());
        }
    }

    /// <summary>
    /// A dataset answer is a table someone reads down, so ORDER is part of the answer: the same rows in a
    /// different order is a different chart. Every dataset question orders explicitly, and this pins that the
    /// order still holds, along with the row count and the fact that the answer is whole rather than a page.
    /// </summary>
    [SkippableFact]
    public async Task ADatasetQuestion_AnswersWithItsRowsInOrder_AndIsNotATruncatedPage()
    {
        var connectionString = AdventureWorksDb.Require();
        var datasets = ProofValues.All.Where(q => q.Shape == "dataset").ToArray();
        Assert.NotEmpty(datasets);

        foreach (var question in datasets)
        {
            var result = await SqlServerQueryRunner.RunAsync(
                connectionString,
                new QueryRunRequest { Sql = question.Sql, Database = ProofValues.Database, MaxRows = MaxRows },
                CancellationToken.None);

            Assert.False(
                result.Truncated,
                $"{question.Id} ({question.Question}) came back truncated at {MaxRows} rows, so its proof value "
                + "is a page rather than an answer.");
            Assert.Equal(question.RowCount, result.RowCount);

            // Compared as whole rows, in sequence, so a reordering fails here rather than passing on a set match.
            for (var i = 0; i < question.Rows.Count; i++)
            {
                Assert.Equal(question.Rows[i], result.Rows[i]);
            }
        }
    }

    /// <summary>
    /// A question whose answer is legitimately nothing must come back as an empty result rather than as an error
    /// or an invented row. This is the case an assistant most easily reports wrongly, because "no rows" and
    /// "the query did not run" are indistinguishable to anything that only checks for failure.
    /// </summary>
    [SkippableFact]
    public async Task AQuestionWithNoAnswer_ComesBackEmptyRatherThanFailing()
    {
        var connectionString = AdventureWorksDb.Require();
        var empties = ProofValues.All.Where(q => q.RowCount == 0).ToArray();
        Assert.NotEmpty(empties);

        foreach (var question in empties)
        {
            var result = await SqlServerQueryRunner.RunAsync(
                connectionString,
                new QueryRunRequest { Sql = question.Sql, Database = ProofValues.Database, MaxRows = MaxRows },
                CancellationToken.None);

            Assert.Equal(0, result.RowCount);
            Assert.Empty(result.Rows);
            Assert.False(result.Truncated);
        }
    }

    /// <summary>
    /// The proof values must remain runnable through the same gate a person's question goes through. Running the
    /// guard over all 300 statements is what stops a future proof value being captured from SQL the product would
    /// never actually execute, which would make the whole suite prove something the estate cannot do.
    /// </summary>
    [Fact]
    public void EveryStoredQuestion_IsAStatementTheReadOnlyGuardAllows()
    {
        foreach (var question in ProofValues.All)
        {
            var validated = ReadOnlyQueryGuard.Validate(question.Sql);
            Assert.False(string.IsNullOrWhiteSpace(validated));
        }
    }

    // ---- the fixture itself ---------------------------------------------------------------------------------

    /// <summary>
    /// The fixture is the whole point of this suite, so its own integrity is asserted before anything trusts it:
    /// a duplicate question would be scored twice, a proof value at the row cap would be a page presented as an
    /// answer, and a row whose width disagrees with its header cannot be compared to anything. This test needs no
    /// database, so a broken fixture is caught even where the sample is not available.
    /// </summary>
    [Fact]
    public void TheFixture_IsWellFormed_AndItsProofValuesAreWholeAnswers()
    {
        Assert.Equal(ProofValues.DeclaredCount, ProofValues.All.Count);
        Assert.Equal(300, ProofValues.All.Count);

        var ids = ProofValues.All.Select(q => q.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var questions = ProofValues.All.Select(q => q.Question).ToArray();
        Assert.Equal(questions.Length, questions.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var question in ProofValues.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(question.Question), $"{question.Id} has no question text.");
            Assert.False(string.IsNullOrWhiteSpace(question.Sql), $"{question.Id} has no SQL.");
            Assert.Contains(question.Shape, new[] { "scalar", "multivalue", "dataset" }, StringComparer.Ordinal);
            Assert.Equal(question.RowCount, question.Rows.Count);

            // A proof value sitting exactly at the cap is indistinguishable from a truncated page, so it is not
            // admissible as a proof value at all.
            Assert.True(
                question.RowCount < MaxRows,
                $"{question.Id} has {question.RowCount} rows, at or above the {MaxRows}-row cap.");

            foreach (var row in question.Rows)
            {
                Assert.Equal(question.Columns.Count, row.Count);
            }

            switch (question.Shape)
            {
                case "scalar":
                    Assert.True(question.Columns.Count == 1, $"{question.Id} is scalar with {question.Columns.Count} columns.");
                    Assert.True(question.RowCount <= 1, $"{question.Id} is scalar with {question.RowCount} rows.");
                    break;
                case "multivalue":
                    Assert.True(question.Columns.Count > 1, $"{question.Id} is multivalue with one column.");
                    Assert.True(question.RowCount == 1, $"{question.Id} is multivalue with {question.RowCount} rows.");
                    break;
                default:
                    Assert.True(question.Columns.Count >= 1, $"{question.Id} is a dataset with no columns.");
                    break;
            }
        }

        // All three answer shapes must actually be represented, or the suite would silently stop covering one.
        foreach (var shape in new[] { "scalar", "multivalue", "dataset" })
        {
            Assert.Contains(ProofValues.All, q => q.Shape == shape);
        }
    }

    // ---- loading ---------------------------------------------------------------------------------------------

    /// <summary>Describes the first difference between a stored proof value and what the engine just returned,
    /// or null when they match. Returning the difference rather than asserting lets one run report every
    /// question that moved instead of stopping at the first.</summary>
    private static string? Compare(ProofValues.ProofQuestion question, QueryResult result)
    {
        var actualColumns = result.Columns.Select(c => c.Name).ToArray();
        if (!question.Columns.SequenceEqual(actualColumns, StringComparer.Ordinal))
        {
            return $"columns are now [{string.Join(", ", actualColumns)}], "
                + $"the proof value has [{string.Join(", ", question.Columns)}].";
        }

        if (question.RowCount != result.RowCount)
        {
            return $"returned {result.RowCount} rows, the proof value has {question.RowCount}.";
        }

        if (result.Truncated)
        {
            return $"came back truncated at {MaxRows} rows, so it can no longer be compared to a whole answer.";
        }

        for (var r = 0; r < question.Rows.Count; r++)
        {
            for (var c = 0; c < question.Columns.Count; c++)
            {
                var expected = question.Rows[r][c];
                var actual = result.Rows[r][c];
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"row {r + 1}, column '{question.Columns[c]}' is now '{actual ?? "(null)"}', "
                        + $"the proof value is '{expected ?? "(null)"}'.");
                }
            }
        }

        return null;
    }
}
