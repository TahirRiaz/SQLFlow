using System.Text.Json.Serialization;

namespace SqlFlow.Core.Runs;

/// <summary>The input control a client renders for one applicable run parameter. Serialized by name so the GUI
/// switches on a stable string, not an ordinal, regardless of the host's global serializer settings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunParameterInput
{
    /// <summary>A single on/off toggle (<c>fullLoad</c>, <c>assertionsOnly</c>).</summary>
    Toggle,

    /// <summary>A from/to date-time pair bounding the run (<c>backfillFrom</c> / <c>backfillTo</c>).</summary>
    DateRange,

    /// <summary>A file-name glob narrowing selection (<c>filePattern</c>).</summary>
    Glob,

    /// <summary>A raw SQL predicate fragment appended to a relational source read (<c>sourceFilter</c>). Rendered
    /// as free text because the operator writes it in the SOURCE's dialect, which the client cannot model.</summary>
    SqlPredicate,
}

/// <summary>
/// One run parameter that applies to a flow, carrying the presentation metadata a client needs to render a
/// labelled input with help text. <see cref="Key"/> is the stable identifier the GUI maps back to the trigger
/// request: <c>fullLoad</c>, <c>backfillWindow</c> (the from/to pair), <c>filePattern</c>, <c>assertionsOnly</c>.
/// </summary>
public sealed record RunParameterDescriptor(string Key, RunParameterInput Input, string Label, string Help);

/// <summary>
/// The single source of truth for WHICH run parameters apply to a flow of a given kind, mirroring the engine's own
/// per-kind interpretation (the copy engine's modified-window resolution, and the file/relational/export executors):
/// a copy or file flow takes a date backfill window, a relational ingestion takes an incremental-column window only
/// when it declares a date column, file flows also take a glob, ingestion also takes assertions-only, export takes a
/// window, and the kinds with no selection surface (stored procedure, health check, inventory) take none. A
/// relational ingestion additionally always takes a raw source filter, which needs no declared column. A client
/// renders exactly this set, so a user is never offered a control the run would ignore or reject.
/// </summary>
public static class RunParameterApplicability
{
    private static readonly RunParameterDescriptor FullLoad = new(
        "fullLoad", RunParameterInput.Toggle, "Full load",
        "Ignore the incremental watermark and process everything the flow selects (a forced full reload).");

    private static readonly RunParameterDescriptor AssertionsOnly = new(
        "assertionsOnly", RunParameterInput.Toggle, "Assertions only",
        "Evaluate the flow's data-quality assertions against the current target and load nothing else.");

    private static readonly RunParameterDescriptor FilePattern = new(
        "filePattern", RunParameterInput.Glob, "File pattern",
        "Restrict this run to files matching a glob, for example orders_2026-03*.json.");

    private static RunParameterDescriptor BackfillWindow(string help)
        => new("backfillWindow", RunParameterInput.DateRange, "Backfill window", help);

    private static readonly RunParameterDescriptor SourceFilter = new(
        "sourceFilter", RunParameterInput.SqlPredicate, "Source filter",
        "Extra predicate ANDed onto the source read for this run, in the source's own SQL dialect and starting "
        + "with AND, for example \"AND pk > 92992\". It replaces the incremental watermark for the run, so it can "
        + "reach rows already below the high-water mark, and needs no declared date column.");

    /// <summary>The applicable parameters for a flow, given its kind and (for relational ingestion) whether it
    /// declares an incremental date column a window can bound. An unknown kind returns an empty list.</summary>
    public static IReadOnlyList<RunParameterDescriptor> For(string? flowKind, bool hasIncrementalDateColumn)
    {
        switch ((flowKind ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "cpy":
                return
                [
                    FullLoad with { Help = "Copy every file the flow selects, ignoring its modifiedWithinDays window." },
                    BackfillWindow("Copy only files last-modified within this range, overriding the flow's modifiedWithinDays default."),
                ];
            case "file":
                return
                [
                    FullLoad,
                    BackfillWindow("Bound the run to files whose date falls within this range (the init file-date window, inclusive)."),
                    FilePattern,
                ];
            case "ing":
                var ing = new List<RunParameterDescriptor> { FullLoad };
                if (hasIncrementalDateColumn)
                {
                    ing.Add(BackfillWindow("Reload the incremental date column bounded to this range (>= from, < to)."));
                }

                // Unlike the window, this is offered whatever the flow declares: it bounds the read on any column
                // the SOURCE exposes, so a flow with no incremental date column (or one whose watermark sits past
                // the rows being recovered) is still backfillable without a YAML edit.
                ing.Add(SourceFilter);
                ing.Add(AssertionsOnly);
                return ing;
            case "exp":
                return [BackfillWindow("Export rows whose date falls within this range instead of the flow's default window.")];
            case "api":
                return
                [
                    FullLoad with { Help = "Ignore the stored watermark and re-fetch from the flow's declared bounds or seed." },
                    BackfillWindow("Re-window every date-window iteration to this range, re-fetching that historical slice."),
                ];
            case "sftp":
                return
                [
                    FullLoad with { Help = "Transfer every matched file, ignoring the modifiedWithinDays window." },
                    BackfillWindow("Transfer files whose modified date falls within this range, overriding modifiedWithinDays."),
                ];
            default:
                return [];
        }
    }
}
