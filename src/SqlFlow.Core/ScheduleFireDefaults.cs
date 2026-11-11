using System.Globalization;

namespace SqlFlow.Core;

/// <summary>
/// The width a product gives a schedule's fire when the schedule leaves <c>maxConcurrency</c> out: how many members of
/// one fire execute at once. SQLFlow's is <see cref="ScheduleDefaults.MaxConcurrency"/> (<see cref="SqlFlow"/>), bounded
/// because the members of a wave usually read one upstream SQL Server. A host whose flows bound their own load on what
/// they share registers another one in its services; the document loader carries it (<c>YamlDocumentLoader.FireDefaults</c>)
/// to every path that reads a schedule (the repository sync, the proposal preflight, the CLI's checks and the API's create
/// path), so a schedule that says nothing means the same thing however it was declared. A schedule that sets the key keeps
/// what it set, and a host that registers none keeps SQLFlow's.
/// </summary>
public sealed class ScheduleFireDefaults
{
    /// <param name="maxConcurrency">How many members of one fire execute at once when a schedule does not say: a bound of
    /// at least 1, or null for unbounded (the wave runs as wide as the fleet takes it).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrency"/> is below 1.</exception>
    public ScheduleFireDefaults(int? maxConcurrency)
    {
        if (maxConcurrency is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency), maxConcurrency, "A product's default fire width is a bound of at least 1, or null for unbounded.");
        }

        MaxConcurrency = maxConcurrency;
    }

    /// <summary>SQLFlow's own default: four members at once.</summary>
    public static ScheduleFireDefaults SqlFlow { get; } = new(ScheduleDefaults.MaxConcurrency);

    /// <summary>Every member of a wave at once, as wide as the fleet takes it.</summary>
    public static ScheduleFireDefaults Unbounded { get; } = new(null);

    /// <summary>The bound an omitted <c>maxConcurrency</c> takes; null is unbounded.</summary>
    public int? MaxConcurrency { get; }

    /// <summary>
    /// Resolves a declared <c>maxConcurrency</c> to the effective bound, where null means UNBOUNDED:
    /// <list type="bullet">
    /// <item>omitted (<paramref name="declared"/> null) takes <see cref="MaxConcurrency"/>;</item>
    /// <item><c>0</c> is the explicit opt-out and returns null (unbounded);</item>
    /// <item>a positive value is taken verbatim;</item>
    /// <item>a negative value is meaningless, so <paramref name="invalid"/> is signalled and the default applies;
    /// it is never stored, because a bound below zero would leave every member of the fire unclaimable.</item>
    /// </list>
    /// </summary>
    public int? Resolve(int? declared, out bool invalid)
    {
        invalid = false;
        switch (declared)
        {
            case null:
                return MaxConcurrency;
            case 0:
                return null;
            case > 0:
                return declared;
            default:
                invalid = true;
                return MaxConcurrency;
        }
    }

    /// <summary>The default as a warning names it: the bound, or <c>unbounded</c>.</summary>
    public string Describe() => MaxConcurrency is { } bound ? bound.ToString(CultureInfo.InvariantCulture) : "unbounded";
}
