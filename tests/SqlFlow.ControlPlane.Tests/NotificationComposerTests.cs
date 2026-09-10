using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Notifications;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The pure heart of the notification pipeline: composition (one message per window, grouped per flow with
/// repeat counts, capped, escaped per channel), the subscription filter (kinds and flow-name globs), the
/// drift-free digest clock, and the retry ladder. All deterministic, no database.
/// </summary>
public sealed class NotificationComposerTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private static CatalogNotificationEvent Event(
        long id, string kind = NotificationEventKinds.RunFailed, string flow = "orders-load",
        string? error = "Timeout expired.", int minutesAgo = 5) => new()
    {
        Id = id,
        Kind = kind,
        RunId = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        FlowName = flow,
        FlowKind = "ing",
        OccurredUtc = Now.AddMinutes(-minutesAgo),
        DetectedUtc = Now,
        Error = error,
    };

    private static NotificationComposition Composition(
        string channel, string mode, params CatalogNotificationEvent[] events)
        => new(channel, mode, events, MorePending: false, "https://sqlflow.example.com", Now);

    [Fact]
    public void SingleFailure_NamesTheFlowInTheSubject_AndLinksTheRun()
    {
        var evt = Event(1);
        var message = NotificationComposer.Compose(Composition(NotificationChannels.Email, NotificationModes.Immediate, evt));

        Assert.Equal("SQLFlow: flow 'orders-load' failed", message.Subject);
        Assert.Contains("Timeout expired.", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://sqlflow.example.com/runs/{evt.RunId}", message.TextBody, StringComparison.Ordinal);
        Assert.NotNull(message.HtmlBody);
        Assert.Null(message.SlackBlocksJson);
    }

    [Fact]
    public void RepeatedFailures_CoalesceIntoOneGroupWithACount()
    {
        var message = NotificationComposer.Compose(Composition(
            NotificationChannels.Email, NotificationModes.Immediate,
            Event(1), Event(2, minutesAgo: 3), Event(3, minutesAgo: 1)));

        Assert.Equal("SQLFlow: 3 failed runs across 1 flow", message.Subject);
        Assert.Contains("failed x3", message.TextBody, StringComparison.Ordinal);
        // One flow line, not three: the anti-spam contract in the body itself.
        Assert.Equal(1, CountOccurrences(message.TextBody, "- orders-load (ing)"));
    }

    [Fact]
    public void MixedKinds_SummarizeInSeverityOrder()
    {
        var message = NotificationComposer.Compose(Composition(
            NotificationChannels.Email, NotificationModes.Digest,
            Event(1, NotificationEventKinds.RunSkipped, "downstream-a", error: null),
            Event(2, NotificationEventKinds.AssertionFailed, "quality-checks", "row-count: bad object"),
            Event(3, flow: "orders-load")));

        Assert.StartsWith("SQLFlow digest: ", message.Subject, StringComparison.Ordinal);
        Assert.Contains("1 failed run", message.Subject, StringComparison.Ordinal);
        Assert.Contains("1 assertion failure", message.Subject, StringComparison.Ordinal);
        Assert.Contains("1 skipped run", message.Subject, StringComparison.Ordinal);
        // Failures list before the skipped echoes.
        var failedAt = message.TextBody.IndexOf("- orders-load", StringComparison.Ordinal);
        var skippedAt = message.TextBody.IndexOf("- downstream-a", StringComparison.Ordinal);
        Assert.True(failedAt >= 0 && skippedAt >= 0 && failedAt < skippedAt);
    }

    [Fact]
    public void ManyFlows_FoldBeyondTheSectionCap()
    {
        var events = Enumerable.Range(1, NotificationComposer.MaxFlowSections + 5)
            .Select(i => Event(i, flow: $"flow-{i:00}"))
            .ToArray();
        var message = NotificationComposer.Compose(Composition(NotificationChannels.Email, NotificationModes.Digest, events));

        Assert.Contains("... and 5 more flows.", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MorePending_IsAnnounced()
    {
        var composition = new NotificationComposition(
            NotificationChannels.Email, NotificationModes.Immediate, [Event(1)], MorePending: true, null, Now);
        var message = NotificationComposer.Compose(composition);
        Assert.Contains("the remainder follows in the next one", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_EncodesHostileContent()
    {
        var evt = Event(1, flow: "orders<script>alert(1)</script>", error: "boom & <b>bust</b>");
        var message = NotificationComposer.Compose(Composition(NotificationChannels.Email, NotificationModes.Immediate, evt));

        Assert.NotNull(message.HtmlBody);
        Assert.DoesNotContain("<script>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bust</b>", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SlackBlocks_AreValidJson_WithHeaderAndEscapedContent()
    {
        var evt = Event(1, flow: "orders <&> load");
        var message = NotificationComposer.Compose(Composition(NotificationChannels.Slack, NotificationModes.Immediate, evt));

        Assert.Null(message.HtmlBody);
        Assert.NotNull(message.SlackBlocksJson);
        using var blocks = JsonDocument.Parse(message.SlackBlocksJson);
        Assert.Equal(JsonValueKind.Array, blocks.RootElement.ValueKind);
        Assert.Equal("header", blocks.RootElement[0].GetProperty("type").GetString());
        // The flow section is block 2 (header, window context, then one section per flow); its mrkdwn text
        // carries the three Slack escapes, decoded here through the JSON reader.
        var section = blocks.RootElement[2].GetProperty("text").GetProperty("text").GetString();
        Assert.NotNull(section);
        Assert.Contains("orders &lt;&amp;&gt; load", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorExcerpts_CollapseWhitespace_AndAreBounded()
    {
        var noisy = "line one\r\n   line two\t\tline three " + new string('x', 1000);
        var message = NotificationComposer.Compose(Composition(
            NotificationChannels.Email, NotificationModes.Immediate, Event(1, error: noisy)));

        Assert.Contains("line one line two line three", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 400), message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TestMessage_CarriesTheChannelShape()
    {
        var email = NotificationComposer.ComposeTest(NotificationChannels.Email, "https://gui.example.com", Now);
        Assert.NotNull(email.HtmlBody);
        Assert.Null(email.SlackBlocksJson);
        Assert.Contains("test notification", email.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://gui.example.com/settings/notifications", email.TextBody, StringComparison.Ordinal);

        var slack = NotificationComposer.ComposeTest(NotificationChannels.Slack, null, Now);
        Assert.Null(slack.HtmlBody);
        Assert.NotNull(slack.SlackBlocksJson);
        _ = JsonDocument.Parse(slack.SlackBlocksJson);
    }

    [Fact]
    public void Filter_SelectsByKind_AndByGlob()
    {
        var subscription = new CatalogNotificationSubscription
        {
            Kinds = "run_failed,assertion_failed",
            FlowPattern = "sales_*, finance_??_load",
        };
        var filter = NotificationSubscriptionFilter.Build(subscription);

        Assert.True(filter(Event(1, flow: "sales_orders")));
        Assert.True(filter(Event(2, NotificationEventKinds.AssertionFailed, "SALES_ORDERS")));
        Assert.True(filter(Event(3, flow: "finance_no_load")));
        Assert.False(filter(Event(4, flow: "finance_nope_load")));
        Assert.False(filter(Event(5, NotificationEventKinds.RunCancelled, "sales_orders")));
        Assert.False(filter(Event(6, flow: "hr_people")));
    }

    [Fact]
    public void Filter_LiteralRegexCharactersStayLiteral()
    {
        var subscription = new CatalogNotificationSubscription { Kinds = "run_failed", FlowPattern = "a.b(c)*" };
        var filter = NotificationSubscriptionFilter.Build(subscription);
        Assert.True(filter(Event(1, flow: "a.b(c)-suffix")));
        Assert.False(filter(Event(2, flow: "aXb(c)")));
    }

    [Fact]
    public void Filter_RejectsAnEmptyGlobList()
    {
        Assert.Throws<ArgumentException>(() => NotificationSubscriptionFilter.BuildFlowPattern(",,,"));
        Assert.Null(NotificationSubscriptionFilter.BuildFlowPattern("   "));
    }

    [Fact]
    public void DigestClock_AdvancesDriftFree_AndSkipsMissedWindows()
    {
        var due = Now.AddMinutes(-2);
        // Two minutes late: the next window still ends on the original rhythm.
        Assert.Equal(due.AddMinutes(360), NotificationService.NextDigestDue(due, 360, Now));

        // Down for a day and a half: missed windows are skipped, not backfilled one by one.
        var longAgo = Now.AddHours(-36);
        var next = NotificationService.NextDigestDue(longAgo, 360, Now);
        Assert.True(next > Now);
        Assert.True(next <= Now.AddMinutes(360));
        Assert.Equal(0, (int)(next - longAgo).TotalMinutes % 360);

        // No prior rhythm: a fresh window starts now.
        Assert.Equal(Now.AddMinutes(360), NotificationService.NextDigestDue(null, 360, Now));
    }

    [Fact]
    public void RetryLadder_BacksOffAndCaps()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationService.RetryBackoff(1));
        Assert.Equal(TimeSpan.FromMinutes(5), NotificationService.RetryBackoff(2));
        Assert.Equal(TimeSpan.FromMinutes(15), NotificationService.RetryBackoff(3));
        Assert.Equal(TimeSpan.FromMinutes(60), NotificationService.RetryBackoff(4));
        Assert.Equal(TimeSpan.FromMinutes(60), NotificationService.RetryBackoff(99));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
