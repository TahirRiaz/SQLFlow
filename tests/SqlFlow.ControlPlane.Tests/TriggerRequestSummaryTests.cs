using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Markdown a caller posts where it reports (a GitHub job's summary): its heading says how the request ended, its
/// table the commits and the sync, each schedule lists its flows, the errors appear in full and the sync's lines at the
/// end, and nothing a run or a sync wrote can break the layout.
/// </summary>
public sealed class TriggerRequestSummaryTests
{
    [Fact]
    public void A_request_that_succeeded_says_so_with_its_commits_schedules_and_links()
    {
        var detail = Detail(
            Request(TriggerRequestStates.Succeeded, TriggerSyncOutcomes.Succeeded, Schedule(1, "nightly", TriggerScheduleStates.Succeeded, 2, 2, 0)),
            Run(1, "land_wells", RunStatuses.Succeeded, 0, outcome: "1,234 rows loaded"),
            Run(1, "deliver_wells", RunStatuses.Succeeded, 1, outcome: "1,200 records delivered"));

        var md = TriggerRequestSummary.Markdown(detail, ["[clone] Checked out abc", "[result] Pipelines: 2 added"], "Acme Flows");

        Assert.StartsWith("## ✅ estate: synced, and its schedule succeeded", md, StringComparison.Ordinal);
        Assert.Contains("[Open in Acme Flows](https://gui.example.com/external-triggers?request=", md, StringComparison.Ordinal);
        Assert.Contains("| Commit asked for | `0123456789ab` |", md, StringComparison.Ordinal);
        Assert.Contains("| Synced at | `fedcba987654` |", md, StringComparison.Ordinal);
        Assert.Contains("### ✅ Schedule `nightly`: 2 flows: 2 succeeded", md, StringComparison.Ordinal);
        Assert.Contains("| 0 | [land_wells](https://gui.example.com/runs/", md, StringComparison.Ordinal);
        Assert.Contains("| 1,234 rows loaded |", md, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Sync trace (2 lines)</summary>", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Errors (", md, StringComparison.Ordinal);
        Assert.Contains("[GitHub acme/estate, run 7](https://github.com/acme/estate/actions/runs/7)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_shows_its_error_in_full_and_a_schedule_that_did_not_fire_says_why()
    {
        var detail = Detail(
            Request(TriggerRequestStates.Failed, TriggerSyncOutcomes.Succeeded,
                Schedule(1, "nightly", TriggerScheduleStates.Failed, 2, 1, 1),
                Schedule(2, "hourly", TriggerScheduleStates.NotFired, 0, 0, 0, "Schedule 'hourly' is paused by an operator.")),
            Run(1, "land_wells", RunStatuses.Succeeded, 0),
            Run(1, "deliver_wells", RunStatuses.Failed, 1, error: "The service answered 403\n  at Deliver()\n```injected fence```"));

        var md = TriggerRequestSummary.Markdown(detail, [], "SQLFlow");

        Assert.StartsWith("## ❌ estate: a schedule did not succeed", md, StringComparison.Ordinal);
        Assert.Contains("### ❌ Schedule `nightly`: 2 flows: 1 succeeded, 1 failed", md, StringComparison.Ordinal);
        Assert.Contains("### ❌ Schedule `hourly`: Schedule 'hourly' is paused by an operator.", md, StringComparison.Ordinal);
        Assert.Contains("<details open><summary>Errors (1)</summary>", md, StringComparison.Ordinal);
        // The error carries a fence of its own; the block around it is longer, so the error cannot close it early.
        Assert.Contains("````text\nThe service answered 403\n  at Deliver()\n```injected fence```\n````", md.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("Sync trace", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_sync_is_the_headline_and_its_error_is_in_the_table()
    {
        var request = Request(TriggerRequestStates.Failed, TriggerSyncOutcomes.Failed, Schedule(1, "nightly", TriggerScheduleStates.NotFired, 0, 0, 0, "Not fired: the sync failed")) with
        {
            SyncError = "could not materialize: authentication required\nsecond line",
            SyncedCommit = null,
        };

        var md = TriggerRequestSummary.Markdown(new TriggerRequestDetailDto(request, []), [], "SQLFlow");

        Assert.StartsWith("## ❌ estate: the sync did not succeed", md, StringComparison.Ordinal);
        Assert.Contains("| Sync | failed: could not materialize: authentication required |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_would_break_a_table_or_inject_markup_is_escaped()
    {
        var detail = Detail(
            Request(TriggerRequestStates.Succeeded, TriggerSyncOutcomes.Succeeded, Schedule(1, "night`ly", TriggerScheduleStates.Succeeded, 1, 1, 0)) with
            {
                RepoName = "est|ate<script>",
            },
            Run(1, "flow|one", RunStatuses.Succeeded, 0, outcome: "a\nb | c"));

        var md = TriggerRequestSummary.Markdown(detail, [], "SQLFlow");

        Assert.Contains("## ✅ est\\|ate&lt;script&gt;:", md, StringComparison.Ordinal);
        Assert.Contains("### ✅ Schedule `night'ly`", md, StringComparison.Ordinal);
        Assert.Contains("[flow\\|one]", md, StringComparison.Ordinal);
        Assert.Contains("| a b \\| c |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schedule_with_more_flows_than_the_summary_lists_counts_the_rest()
    {
        var runs = Enumerable.Range(0, TriggerRequestSummary.MaxFlowsPerSchedule + 7)
            .Select(i => Run(1, $"flow_{i:000}", RunStatuses.Succeeded, 0)).ToArray();
        var detail = Detail(Request(TriggerRequestStates.Running, TriggerSyncOutcomes.Succeeded, Schedule(1, "nightly", TriggerScheduleStates.Running, runs.Length, 0, 0)), runs);

        var md = TriggerRequestSummary.Markdown(detail, [], "SQLFlow");

        Assert.Contains("| | ... and 7 more | | | |", md, StringComparison.Ordinal);
        Assert.StartsWith("## ⏳ estate: schedules running", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_sync_trace_shows_its_first_lines_and_counts_the_rest()
    {
        var lines = Enumerable.Range(1, TriggerRequestSummary.MaxSyncLines + 3).Select(i => $"[sync] line {i}").ToList();

        var md = TriggerRequestSummary.Markdown(Detail(Request(TriggerRequestStates.Succeeded, TriggerSyncOutcomes.Succeeded)), lines, "SQLFlow");

        Assert.Contains($"Sync trace ({lines.Count} lines)", md, StringComparison.Ordinal);
        Assert.Contains("... and 3 more lines", md, StringComparison.Ordinal);
        Assert.DoesNotContain($"line {lines.Count}\n", md.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_GUI_address_the_summary_has_no_links()
    {
        var detail = Detail(
            Request(TriggerRequestStates.Succeeded, TriggerSyncOutcomes.Current, Schedule(1, "nightly", TriggerScheduleStates.Succeeded, 1, 1, 0, url: null)) with
            {
                Url = null,
                OriginUrl = null,
            },
            Run(1, "land", RunStatuses.Succeeded, 0, url: null));

        var md = TriggerRequestSummary.Markdown(detail, [], "SQLFlow");

        Assert.DoesNotContain("](", md, StringComparison.Ordinal);
        Assert.Contains("| Sync | not needed: already synced at the commit asked for |", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TriggerRequestStates.Syncing, false, "waiting to sync")]
    [InlineData(TriggerRequestStates.Syncing, true, "syncing")]
    [InlineData(TriggerRequestStates.Firing, true, "firing schedules")]
    [InlineData(TriggerRequestStates.Cancelled, true, "cancelled")]
    public void The_headline_says_where_a_request_stands(string state, bool syncAsked, string headline)
    {
        var request = Request(state, null) with { SyncRequestedUtc = syncAsked ? DateTime.UtcNow : null };

        Assert.Equal(headline, TriggerRequestSummary.Headline(request));
    }

    [Fact]
    public void A_request_with_no_schedule_says_it_synced()
        => Assert.Equal("synced", TriggerRequestSummary.Headline(Request(TriggerRequestStates.Succeeded, TriggerSyncOutcomes.Succeeded)));

    [Theory]
    [InlineData("abc1234", "ABC1234DEF", true)]
    [InlineData("abc123", "abc1234def", false)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789abcdef", "0123456789abcdee", false)]
    public void Two_spellings_name_the_same_commit_when_equal_or_one_is_a_short_prefix(string a, string b, bool same)
    {
        Assert.Equal(same, TriggerRequestProcessor.SameCommit(a, b));
        Assert.Equal(same, TriggerRequestProcessor.SameCommit(b, a));
    }

    [Theory]
    [InlineData(0.04, "0 s")]
    [InlineData(12.34, "12.3 s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(93780, "26h 03m")]
    public void A_duration_reads_as_a_person_says_it(double seconds, string expected)
        => Assert.Equal(expected, TriggerRequestProcessor.Duration(seconds));

    [Fact]
    public void A_run_is_described_by_the_first_describer_with_something_to_say_and_otherwise_by_its_rows()
    {
        var run = new CatalogRun { RunId = Guid.NewGuid(), FlowName = "f", FlowKind = "ing", RowsLoaded = 1234, RowsInserted = 1000, RowsUpdated = 234 };

        Assert.Equal("1,234 rows loaded (1,000 inserted, 234 updated)", RunOutcomeDescriptions.Describe(run, [], NullLogger.Instance));
        Assert.Equal("delivered 3", RunOutcomeDescriptions.Describe(run, [new Silent(), new Says("delivered 3")], NullLogger.Instance));
        Assert.Equal("1,234 rows loaded (1,000 inserted, 234 updated)", RunOutcomeDescriptions.Describe(run, [new Throws()], NullLogger.Instance));
        Assert.Null(RunOutcomeDescriptions.Describe(new CatalogRun { FlowName = "sp", FlowKind = "sp" }, [], NullLogger.Instance));
    }

    private sealed class Silent : IRunOutcomeDescriber
    {
        public string? Describe(CatalogRun run) => null;
    }

    private sealed class Says(string text) : IRunOutcomeDescriber
    {
        public string? Describe(CatalogRun run) => text;
    }

    private sealed class Throws : IRunOutcomeDescriber
    {
        public string? Describe(CatalogRun run) => throw new InvalidOperationException("broken describer");
    }

    private static TriggerRequestDetailDto Detail(TriggerRequestDto request, params TriggerRequestRunDto[] runs) => new(request, runs);

    private static TriggerRequestDto Request(string state, string? syncOutcome, params TriggerRequestScheduleDto[] schedules)
        => new(
            Guid.NewGuid(), "estate", Guid.NewGuid(), "deliver", "trigger:deliver", "key-1", "GitHub acme/estate, run 7",
            "https://github.com/acme/estate/actions/runs/7", "0123456789abcdef0123456789abcdef01234567", true, [], true, state,
            TriggerRequestEndpoints.Conclusion(state), TriggerRequestStates.IsTerminal(state) ? "done" : null, syncOutcome,
            DateTime.UtcNow.AddMinutes(-5), "fedcba9876543210fedcba9876543210fedcba98", null, DateTime.UtcNow.AddMinutes(-6), DateTime.UtcNow,
            TriggerRequestStates.IsTerminal(state) ? DateTime.UtcNow : null, null, null, schedules,
            "https://gui.example.com/external-triggers?request=1");

    private static TriggerRequestScheduleDto Schedule(
        int ordinal, string name, string state, int total, int succeeded, int failed, string? detail = null,
        string? url = "https://gui.example.com/runs/groups/1")
        => new(
            ordinal, name, Guid.NewGuid(), state, detail, Guid.NewGuid(), Guid.NewGuid(), total, "fedcba9876543210", DateTime.UtcNow, null,
            new TriggerRunCountsDto(total, 0, total - succeeded - failed, succeeded, failed, 0, 0), url);

    private static TriggerRequestRunDto Run(
        int ordinal, string flow, string status, int wave, string? outcome = null, string? error = null, string? url = "https://gui.example.com/runs/x")
    {
        var id = Guid.NewGuid();
        return new TriggerRequestRunDto(id, ordinal, flow, "ing", wave, status, DateTime.UtcNow, DateTime.UtcNow, 12.5, outcome, error,
            url is null ? null : $"https://gui.example.com/runs/{id:D}");
    }
}
