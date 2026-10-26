using System.Globalization;
using System.Text;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// Renders a trigger request as Markdown for a reader outside SQLFlow: the summary a GitHub job writes to its page (and any
/// other caller pastes where it reports). It says how the request ended in its heading, then the commits and the sync,
/// each schedule with every flow it ran (wave, status, how long, what it did), the errors in full, and the sync's own lines,
/// with links into SQLFlow wherever the GUI's address is known. One rendering serves every caller, so a GitHub job, a
/// pipeline elsewhere and the GUI's copy button show the same thing.
/// </summary>
public static class TriggerRequestSummary
{
    /// <summary>The most flows listed per schedule; the rest are counted.</summary>
    public const int MaxFlowsPerSchedule = 300;

    /// <summary>The most sync lines shown; the rest are counted.</summary>
    public const int MaxSyncLines = 200;

    /// <summary>The most errors shown in full; the rest are counted.</summary>
    public const int MaxErrors = 25;

    /// <summary>The summary of <paramref name="detail"/>, with the sync's lines (<paramref name="syncLines"/>, in order) and
    /// <paramref name="productName"/> naming where the links lead.</summary>
    public static string Markdown(TriggerRequestDetailDto detail, IReadOnlyList<string> syncLines, string productName)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(syncLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        var request = detail.Request;
        var md = new StringBuilder();

        md.Append("## ").Append(Mark(request.State)).Append(' ').Append(Escape(request.RepoName)).Append(": ")
            .AppendLine(Escape(Headline(request)));
        md.AppendLine();
        md.Append("<sub>Request `").Append(request.Id.ToString("D")).Append("` by ").Append(Escape(request.RequestedBy))
            .Append(", ").Append(Utc(request.CreatedUtc));
        if (request.Url is { } url)
        {
            md.Append(" · [Open in ").Append(Escape(productName)).Append("](").Append(url).Append(')');
        }

        md.AppendLine("</sub>");
        md.AppendLine();

        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        if (request.ExpectedCommit is { } expected)
        {
            md.Append("| Commit asked for | `").Append(Short(expected)).AppendLine("` |");
        }

        md.Append("| Sync | ").Append(SyncText(request)).AppendLine(" |");
        if (request.SyncedCommit is { } synced)
        {
            md.Append("| Synced at | `").Append(Short(synced)).AppendLine("` |");
        }

        if (request.Batch.Count > 0)
        {
            md.Append("| Batches | ").Append(Escape(string.Join(", ", request.Batch))).AppendLine(" |");
        }

        if (!request.Chain)
        {
            md.AppendLine("| Chained schedules | held back |");
        }

        if (request.Schedules.Count == 0)
        {
            md.AppendLine("| Schedules | none asked for |");
        }

        if (request.Origin is { } origin)
        {
            md.Append("| Asked from | ").Append(request.OriginUrl is { } originUrl ? $"[{Escape(origin)}]({originUrl})" : Escape(origin)).AppendLine(" |");
        }

        if (request.Detail is { } why && TriggerRequestStates.IsTerminal(request.State))
        {
            md.Append("| Outcome | ").Append(Escape(why)).AppendLine(" |");
        }

        md.AppendLine();

        var runsBySchedule = detail.Runs.GroupBy(r => r.ScheduleOrdinal).ToDictionary(g => g.Key, g => g.ToList());
        var errors = new List<TriggerRequestRunDto>();
        foreach (var schedule in request.Schedules)
        {
            md.Append("### ").Append(Mark(schedule.State)).Append(" Schedule `").Append(schedule.Name.Replace("`", "'", StringComparison.Ordinal))
                .Append("`: ").AppendLine(Escape(ScheduleText(schedule)));
            md.AppendLine();
            if (schedule.Url is { } scheduleUrl)
            {
                md.Append("<sub>").Append(schedule.GroupId is null ? "Run" : "Run group").Append(" · [open](").Append(scheduleUrl).Append(')')
                    .Append(schedule.CommitSha is { } at ? $" · at `{Short(at)}`" : string.Empty).AppendLine("</sub>");
                md.AppendLine();
            }

            var runs = runsBySchedule.GetValueOrDefault(schedule.Ordinal) ?? [];
            if (runs.Count > 0)
            {
                md.AppendLine("| Wave | Flow | Status | Took | Outcome |");
                md.AppendLine("|---:|---|---|---:|---|");
                foreach (var run in runs.Take(MaxFlowsPerSchedule))
                {
                    var flow = run.Url is { } runUrl ? $"[{Escape(run.FlowName)}]({runUrl})" : Escape(run.FlowName);
                    md.Append("| ").Append(run.Wave.ToString(CultureInfo.InvariantCulture))
                        .Append(" | ").Append(flow)
                        .Append(" | ").Append(Mark(run.Status)).Append(' ').Append(run.Status)
                        .Append(" | ").Append(run.DurationSeconds is { } seconds ? TriggerRequestProcessor.Duration(seconds) : string.Empty)
                        .Append(" | ").Append(Escape(run.Outcome ?? (run.Status == RunStatuses.Failed ? "see the error below" : string.Empty)))
                        .AppendLine(" |");
                }

                if (runs.Count > MaxFlowsPerSchedule)
                {
                    md.Append("| | ... and ").Append((runs.Count - MaxFlowsPerSchedule).ToString(CultureInfo.InvariantCulture))
                        .AppendLine(" more | | | |");
                }

                md.AppendLine();
            }

            errors.AddRange(runs.Where(r => r.Error is not null));
        }

        if (errors.Count > 0)
        {
            md.Append("<details open><summary>Errors (").Append(errors.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(")</summary>");
            md.AppendLine();
            foreach (var run in errors.Take(MaxErrors))
            {
                md.Append("**").Append(Escape(run.FlowName)).Append("** (run `").Append(run.RunId.ToString("D")).AppendLine("`)");
                md.AppendLine();
                AppendCode(md, run.Error!);
            }

            if (errors.Count > MaxErrors)
            {
                md.Append("... and ").Append((errors.Count - MaxErrors).ToString(CultureInfo.InvariantCulture)).AppendLine(" more errors in the runs themselves.");
                md.AppendLine();
            }

            md.AppendLine("</details>");
            md.AppendLine();
        }

        if (syncLines.Count > 0)
        {
            md.Append("<details><summary>Sync trace (").Append(syncLines.Count.ToString(CultureInfo.InvariantCulture))
                .Append(syncLines.Count == 1 ? " line" : " lines").AppendLine(")</summary>");
            md.AppendLine();
            var shown = syncLines.Take(MaxSyncLines).ToList();
            AppendCode(md, string.Join('\n', shown) + (syncLines.Count > MaxSyncLines
                ? $"\n... and {syncLines.Count - MaxSyncLines} more lines"
                : string.Empty));
            md.AppendLine("</details>");
        }

        return md.ToString();
    }

    /// <summary>The heading's sentence: how the request stands or ended.</summary>
    public static string Headline(TriggerRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.State switch
        {
            TriggerRequestStates.Syncing => request.SyncRequestedUtc is null ? "waiting to sync" : "syncing",
            TriggerRequestStates.Firing => "firing schedules",
            TriggerRequestStates.Running => "schedules running",
            TriggerRequestStates.Succeeded => request.Schedules.Count == 0
                ? "synced"
                : request.Schedules.Count == 1 ? "synced, and its schedule succeeded" : $"synced, and its {request.Schedules.Count} schedules succeeded",
            TriggerRequestStates.Cancelled => "cancelled",
            _ => request.SyncOutcome is TriggerSyncOutcomes.Failed or TriggerSyncOutcomes.TimedOut
                ? "the sync did not succeed"
                : request.Schedules.Any(s => s.State == TriggerScheduleStates.NotFired) && !request.Schedules.Any(s => s.State == TriggerScheduleStates.Failed)
                    ? "a schedule did not fire"
                    : request.SyncOutcome is null ? "failed" : "a schedule did not succeed",
        };
    }

    private static string SyncText(TriggerRequestDto request)
        => request.SyncOutcome switch
        {
            TriggerSyncOutcomes.Current => "not needed: already synced at the commit asked for",
            TriggerSyncOutcomes.Succeeded => "succeeded",
            TriggerSyncOutcomes.Failed => "failed" + (request.SyncError is { } error ? ": " + Escape(FirstLine(error)) : string.Empty),
            TriggerSyncOutcomes.TimedOut => "timed out",
            TriggerSyncOutcomes.Skipped => "not asked for",
            _ => request.SyncRequestedUtc is null ? "waiting" : "running",
        };

    private static string ScheduleText(TriggerRequestScheduleDto schedule)
    {
        if (schedule.State is TriggerScheduleStates.NotFired or TriggerScheduleStates.Cancelled || schedule.Counts.Total == 0)
        {
            return schedule.Detail ?? schedule.State;
        }

        var counts = schedule.Counts;
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
            {
                parts.Add($"{n} {what}");
            }
        }

        Add(counts.Succeeded, "succeeded");
        Add(counts.Failed, "failed");
        Add(counts.Skipped, "skipped");
        Add(counts.Cancelled, "cancelled");
        Add(counts.Running, "running");
        Add(counts.Queued, "queued");
        return $"{counts.Total} {(counts.Total == 1 ? "flow" : "flows")}: {string.Join(", ", parts)}";
    }

    /// <summary>A status as a mark a reader takes in at a glance, the same for a request, a schedule and a run.</summary>
    public static string Mark(string state)
        => state switch
        {
            TriggerRequestStates.Succeeded => "✅",
            TriggerRequestStates.Failed or TriggerScheduleStates.NotFired => "❌",
            TriggerRequestStates.Cancelled => "⏹️",
            RunStatuses.Skipped => "⏭️",
            RunStatuses.Queued or TriggerScheduleStates.Pending => "🕓",
            _ => "⏳",
        };

    private static void AppendCode(StringBuilder md, string text)
    {
        // A fence longer than any run of backticks inside the text, so no line of it can close the block early.
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        md.Append(fence).AppendLine("text");
        md.AppendLine(text.TrimEnd());
        md.AppendLine(fence);
        md.AppendLine();
    }

    /// <summary>Text safe in a table cell or a heading: no line break, no cell separator, no HTML.</summary>
    private static string Escape(string text)
        => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Trim();

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static string Utc(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
