namespace SqlFlow.Core.Runs;

/// <summary>
/// Who a run records as the one that asked for it when no person asked through the API: the schedule that fired it,
/// or the account a direct CLI run executes under. A person asking through the API is recorded under their own name
/// (the token's subject), so every run names what started it and an executor can attribute what the run does, instead
/// of an audit trail reading "unknown". Every label fits <see cref="MaxLength"/>, the catalog's width for the column.
/// </summary>
public static class RunActors
{
    /// <summary>The widest label the catalog records (the run's <c>RequestedBy</c> column).</summary>
    public const int MaxLength = 256;

    /// <summary>The prefix of a label naming a schedule.</summary>
    public const string SchedulePrefix = "schedule:";

    /// <summary>The prefix of a label naming the account a direct CLI run executed under.</summary>
    public const string CliPrefix = "cli:";

    /// <summary>
    /// The schedule that fired a run, <c>schedule:&lt;name&gt;</c>. A name that is blank, or too long for the column
    /// once prefixed, is named by the schedule's id instead, which always fits and still says which schedule it was.
    /// </summary>
    public static string Schedule(string? name, Guid id)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || SchedulePrefix.Length + trimmed.Length > MaxLength
            ? SchedulePrefix + id.ToString("D")
            : SchedulePrefix + trimmed;
    }

    /// <summary>The account this process runs under on this machine, as a direct CLI run records it.</summary>
    public static string LocalAccount() => Cli(Environment.UserName, Environment.MachineName);

    /// <summary>
    /// A direct CLI run's account, <c>cli:&lt;user&gt;@&lt;machine&gt;</c>: <c>cli:&lt;user&gt;</c> when the machine
    /// has no name, <c>cli:@&lt;machine&gt;</c> when the process has no user name (a container without one), and
    /// <c>cli:local</c> when neither is known. The user part is shortened to fit <see cref="MaxLength"/>; the machine
    /// part is kept whole, since host names are short.
    /// </summary>
    public static string Cli(string? user, string? machine)
    {
        var who = user?.Trim() ?? string.Empty;
        var where = machine?.Trim() ?? string.Empty;
        if (who.Length == 0 && where.Length == 0)
        {
            return CliPrefix + "local";
        }

        var host = where.Length == 0 ? string.Empty : "@" + where;
        var room = MaxLength - CliPrefix.Length - host.Length;
        if (room < 0)
        {
            // A machine name longer than the column is not a host name; keep what fits of it.
            return (CliPrefix + "@" + where)[..MaxLength];
        }

        return CliPrefix + (who.Length > room ? who[..room] : who) + host;
    }
}
