using SqlFlow.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>The named schedules one library file declares, plus any warnings raised parsing it.</summary>
public sealed record ScheduleLibrary(IReadOnlyList<NamedSchedule> Schedules, IReadOnlyList<string> Warnings);

/// <summary>One shared schedule a library file (or a named inline block) publishes for reuse by name.</summary>
public sealed record NamedSchedule(string Name, ScheduleSpec Spec);

/// <summary>
/// Loads a shared-schedule library file: a <c>schedules.yaml</c> (or <c>*.schedules.yaml</c>) whose top-level
/// <c>schedules:</c> key maps a name to a cadence, so many flows can reference one definition by name instead of
/// repeating the block. The cadence fields are exactly a flow's inline <c>schedule:</c> block (cron or
/// intervalSeconds, timezone, enabled, catchup, maxConcurrency); the map key is the reference name. Cron/interval syntax is not
/// validated here (the catalog owns the cron library, as for inline schedules); an entry that declares neither a
/// cron, an interval, nor an <c>after</c> is dropped with a warning so an empty placeholder never becomes a broken
/// schedule.
/// <para>
/// An entry may instead declare <c>after: &lt;other-schedule&gt;</c>, which makes it a CHAINED (shadow) schedule: it
/// carries no cadence and never becomes due on the clock, firing once each time the named parent's fire completes.
/// That is how a strict serial chain is expressed (<c>a</c> on a cron, <c>b: {after: a}</c>, <c>c: {after: b}</c>),
/// which a stagger of independent crons can only approximate. Declaring both a cron and an <c>after</c> is
/// contradictory, so the cron is dropped with a warning; chaining a schedule to itself is dropped entirely. Longer
/// cycles cannot be seen from one file and are broken at fire time by the scheduler.
/// </para>
/// </summary>
public sealed class YamlScheduleLibraryLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class LibraryYaml
    {
        public Dictionary<string, ScheduleEntryYaml?>? Schedules { get; set; }
    }

    private sealed class ScheduleEntryYaml
    {
        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        /// <summary>Deliberately typed as <see cref="object"/>: <c>after:</c> is written either as one name
        /// (<c>after: nightly</c>) or as a list of them (<c>after: [a, b, c]</c>), and YamlDotNet cannot bind both
        /// shapes to one typed property. <see cref="NormalizeAfter"/> collapses whichever arrived.</summary>
        public object? After { get; set; }

        public int? ParentFreshnessHours { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>Parses a library file's YAML. <paramref name="source"/> only labels warnings.</summary>
    public ScheduleLibrary Parse(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var warnings = new List<string>();

        LibraryYaml? parsed;
        try
        {
            parsed = _deserializer.Deserialize<LibraryYaml>(yaml);
        }
        catch (YamlException ex)
        {
            warnings.Add($"{source}: invalid schedule library - {ex.Message}");
            return new ScheduleLibrary([], warnings);
        }

        if (parsed?.Schedules is not { Count: > 0 } entries)
        {
            warnings.Add($"{source}: no 'schedules:' entries; nothing to publish.");
            return new ScheduleLibrary([], warnings);
        }

        var schedules = new List<NamedSchedule>(entries.Count);
        foreach (var (rawName, entry) in entries)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add($"{source}: a schedule with a blank name is ignored.");
                continue;
            }

            var after = NormalizeAfter(entry?.After);
            var chained = after.Count > 0;
            var hasClock = entry is not null && (!string.IsNullOrWhiteSpace(entry.Cron) || entry.IntervalSeconds is not null);

            if (entry is null || (!hasClock && !chained))
            {
                warnings.Add(
                    $"{source}: schedule '{name}' declares neither a cron, an intervalSeconds, nor an after; ignored.");
                continue;
            }

            // A schedule is driven by the clock or by its parents, never both: honouring a cron on a chained schedule
            // would fire it twice per cycle, once on the clock and once behind its parents.
            if (hasClock && chained)
            {
                warnings.Add(
                    $"{source}: schedule '{name}' sets 'after: {string.Join(", ", after)}' together with a "
                    + "cron/intervalSeconds; a chained schedule has no cadence of its own, so the cron is ignored.");
            }

            // Naming itself is the one cycle worth catching here: it is always a mistake, and it is the only one
            // visible without resolving the whole repo. Longer cycles are left to stall quietly at fire time, which
            // is what ScheduleStore.ListChainedReadyAsync documents.
            if (after.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{source}: schedule '{name}' chains after itself; ignored.");
                continue;
            }

            schedules.Add(new NamedSchedule(name, new ScheduleSpec
            {
                Name = name,
                Cron = chained || string.IsNullOrWhiteSpace(entry.Cron) ? null : entry.Cron.Trim(),
                IntervalSeconds = chained ? null : entry.IntervalSeconds,
                After = after,
                ParentFreshnessHours = NormalizeFreshness(entry.ParentFreshnessHours, name, source, warnings),
                Timezone = string.IsNullOrWhiteSpace(entry.Timezone) ? "UTC" : entry.Timezone.Trim(),
                Enabled = entry.Enabled ?? true,
                Catchup = entry.Catchup ?? false,
                MaxConcurrency = NormalizeMaxConcurrency(entry.MaxConcurrency, name, source, warnings),
            }));
        }

        return new ScheduleLibrary(schedules, warnings);
    }

    /// <summary>Resolves the declared member-concurrency bound through the shared
    /// <see cref="ScheduleDefaults.Resolve"/> (omitted takes the product default, <c>0</c> is the explicit unbounded
    /// opt-out, negative is meaningless), warning on a value that had to be corrected.</summary>
    internal static int? NormalizeMaxConcurrency(int? value, string name, string source, List<string> warnings)
    {
        var resolved = ScheduleDefaults.Resolve(value, out var invalid);
        if (invalid)
        {
            warnings.Add(
                $"{source}: schedule '{name}' sets maxConcurrency to {value}, which is not a usable bound; "
                + $"using the default of {ScheduleDefaults.MaxConcurrency} (use 0 for unbounded).");
        }

        return resolved;
    }

    /// <summary>
    /// Collapses the two shapes <c>after:</c> is written in, a single name or a sequence of them, into one ordered
    /// list. Blanks are dropped and repeats collapsed (first spelling wins), because the fan-in readiness rule counts
    /// parents: a name listed twice would be counted twice and could never be satisfied.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeAfter(object? raw)
    {
        List<string?> names = raw switch
        {
            null => [],
            string scalar => [scalar],
            IEnumerable<object?> sequence => sequence.Select(v => v as string ?? v?.ToString()).ToList(),
            _ => [raw.ToString()],
        };

        var result = new List<string>(names.Count);
        foreach (var candidate in names)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var trimmed = candidate.Trim();
            if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }

        return result;
    }

    /// <summary>Resolves the declared parent-freshness window: omitted takes the product default, <c>0</c> opts out
    /// of the check, and a negative value is meaningless so the default applies with a warning.</summary>
    internal static int NormalizeFreshness(int? value, string name, string source, List<string> warnings)
    {
        switch (value)
        {
            case null:
                return ScheduleDefaults.ParentFreshnessHours;
            case >= 0:
                return value.Value;
            default:
                warnings.Add(
                    $"{source}: schedule '{name}' sets parentFreshnessHours to {value}, which is not a usable window; "
                    + $"using the default of {ScheduleDefaults.ParentFreshnessHours} (use 0 to disable the check).");
                return ScheduleDefaults.ParentFreshnessHours;
        }
    }
}
