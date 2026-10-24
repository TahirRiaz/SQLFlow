using System.Text.RegularExpressions;

namespace SqlFlow.ControlPlane.Maintenance;

/// <summary>
/// What maintenance may do with the data of a <see cref="StorageArea"/>, in the words the Maintenance page uses. The verdict
/// is about the data, not about how much of it there is: a large area can be one to keep, a small one safe to remove.
/// </summary>
public enum StorageVerdict
{
    /// <summary>Needed: it explains a run, a record or an action, holds state something reads, or holds statistics. Nothing
    /// an admin sets deletes it; it leaves only by its own lifecycle (<see cref="StorageArea.Lifecycle"/>).</summary>
    Keep,

    /// <summary>Removing it loses nothing anyone needs: a superseded copy, a duplicate, or work in progress that is finished.
    /// It either clears itself (<see cref="StorageArea.Lifecycle"/>) or an admin sets how long it stays.</summary>
    Safe,

    /// <summary>It may be removed, at a cost the area states (<see cref="StorageArea.Reason"/>): history, detail or timings
    /// nothing else holds. Nothing removes it until an admin sets how long it stays.</summary>
    Optional,
}

/// <summary>
/// A table of the catalog's database an area holds: one table by its schema and name, or every table of a schema whose name
/// starts with a prefix (for tables a module creates at run time, one per object it publishes). Names compare without
/// regard to case, as SQL Server's default collation does.
/// </summary>
public sealed record StorageTable
{
    private StorageTable(string schema, string name, bool isPrefix)
    {
        Schema = schema;
        Name = name;
        IsPrefix = isPrefix;
    }

    /// <summary>The table's schema.</summary>
    public string Schema { get; }

    /// <summary>The table's name, or the prefix every name of the area's tables starts with.</summary>
    public string Name { get; }

    /// <summary>Whether <see cref="Name"/> is a prefix rather than one table's name.</summary>
    public bool IsPrefix { get; }

    /// <summary>One table, by its schema and name.</summary>
    public static StorageTable Named(string schema, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new StorageTable(schema, name, isPrefix: false);
    }

    /// <summary>Every table of <paramref name="schema"/> whose name starts with <paramref name="prefix"/>.</summary>
    public static StorageTable StartingWith(string schema, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return new StorageTable(schema, prefix, isPrefix: true);
    }

    /// <summary>Whether the table <paramref name="schema"/>.<paramref name="table"/> is one this claim holds.</summary>
    public bool Matches(string schema, string table)
        => string.Equals(Schema, schema, StringComparison.OrdinalIgnoreCase)
            && (IsPrefix
                ? table.StartsWith(Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Name, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>The claim as the page writes it: <c>schema.table</c>, or <c>schema.prefix*</c>.</summary>
    public override string ToString() => IsPrefix ? $"{Schema}.{Name}*" : $"{Schema}.{Name}";
}

/// <summary>
/// One column of a table another area holds, whose values an area clears while the rows stay: the free-text log of an
/// activity, say, whose row (who, what, when, outcome) is kept. The area's size is what the column's values take, and the
/// area that holds the table is measured without them.
/// </summary>
public sealed record StorageColumn(string Schema, string Table, string Column)
{
    /// <summary>The column as the page writes it: <c>schema.table.column</c>.</summary>
    public override string ToString() => $"{Schema}.{Table}.{Column}";
}

/// <summary>
/// What an admin may set for an area: how many days its data stays, between <see cref="MinDays"/> and
/// <see cref="MaxDays"/>, or no limit at all, which is where every area starts. <see cref="Keeps"/> says what a cleanup
/// never removes whatever the days (each flow's latest run, each record's latest attempt), so a setting of the minimum is
/// still safe to choose. <see cref="Unit"/> names what a count and a cleanup count, in the plural ("statements", "runs"),
/// since an area's cleanup may count something other than rows of one table.
/// </summary>
public sealed record StorageRetention(string Keeps, string Unit, int MinDays = 1, int MaxDays = StorageRetention.MaxRetentionDays)
{
    /// <summary>The longest retention any area takes: enough for any real policy, and low enough that a mistyped value
    /// cannot overflow a date or quietly mean "never".</summary>
    public const int MaxRetentionDays = 36_500;
}

/// <summary>
/// A part of the catalog's database the Maintenance page describes: tables (or one column of a table) that hold one kind of
/// data, in words a person who runs the system recognises, with the verdict on what maintenance may do with it and why.
/// Every table of a contributor's model belongs to exactly one area, so the page accounts for the whole database.
/// </summary>
public sealed record StorageArea
{
    /// <summary>
    /// The area's stable id, which its retention setting and its cleanup history are stored under: lowercase letters, digits
    /// and hyphens, in dot-separated parts led by the contributor's own part (<c>sqlflow.run-statements</c>), at most 128
    /// characters. Never change an id that shipped: a setting stored under the old one would stop applying.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The heading the page lists the area under ("Run history"), shared by the areas of one subject.</summary>
    public required string Group { get; init; }

    /// <summary>The area's name ("SQL statements").</summary>
    public required string Title { get; init; }

    /// <summary>What maintenance may do with the area's data.</summary>
    public required StorageVerdict Verdict { get; init; }

    /// <summary>What the area holds, in one or two sentences: what one row is and what writes it.</summary>
    public required string Holds { get; init; }

    /// <summary>Why the verdict is what it is: what reads the data and what would be lost without it, or, for an area safe
    /// to remove, why nothing is.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// How the data leaves the database today, apart from any retention an admin sets: "Deleted with its repository",
    /// "Removed automatically 30 days after it was raised", "Only the flow that declares it removes it". Null when nothing
    /// removes it but the retention.
    /// </summary>
    public string? Lifecycle { get; init; }

    /// <summary>
    /// <see cref="Lifecycle"/> in a few words for the overview's grid ("Deleted with its repository", "After 30 days"), at
    /// most <see cref="StorageAreaRules.MaxBriefLength"/> characters. Every area without a retention has one.
    /// </summary>
    public string? Brief { get; init; }

    /// <summary>Whether the area grows with use and nothing removes it, so the page can flag growth nobody manages.</summary>
    public bool Grows { get; init; }

    /// <summary>The tables the area holds. Empty for an area that clears a <see cref="Column"/>.</summary>
    public IReadOnlyList<StorageTable> Tables { get; init; } = [];

    /// <summary>The column the area clears in a table another area holds; null for an area of whole tables.</summary>
    public StorageColumn? Column { get; init; }

    /// <summary>The GUI route where the data is managed or read in full (an absolute path), when there is one.</summary>
    public string? ManagedAt { get; init; }

    /// <summary>The retention an admin may set; null when nothing an admin sets removes the area's data.</summary>
    public StorageRetention? Retention { get; init; }
}

/// <summary>
/// A contributor of storage areas: SQLFlow describes its own catalog through one, and a host module describes its own schema
/// through another, so the Maintenance page and the automatic cleanup cover a module's data exactly as they cover the
/// platform's. Contributors are registered in the host's composition root and resolved per request or per pass, so one may
/// depend on scoped services (a <c>DbContext</c>).
/// </summary>
public interface IStorageContributor
{
    /// <summary>Who the data belongs to, as the page names it ("SQLFlow").</summary>
    string Owner { get; }

    /// <summary>The contributor's areas, in the order the page lists them.</summary>
    IReadOnlyList<StorageArea> Areas { get; }

    /// <summary>
    /// How many rows (or, for a <see cref="StorageArea.Column"/>, values) a cleanup of <paramref name="area"/> would remove
    /// at the cut-off <paramref name="olderThanUtc"/>. Called only for an area that has a <see cref="StorageArea.Retention"/>,
    /// and it changes nothing.
    /// </summary>
    Task<long> CountAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct);

    /// <summary>
    /// Removes what <see cref="CountAsync"/> counts at <paramref name="olderThanUtc"/>, always keeping what the area's
    /// <see cref="StorageRetention.Keeps"/> says, and answers how many rows (or values) it removed. It deletes in bounded
    /// statements, each its own short transaction, so it never holds a long lock on a table the platform writes to; it is
    /// safe to interrupt and to repeat, and two passes at once remove each row once.
    /// </summary>
    Task<long> CleanAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct);
}

/// <summary>The rules every <see cref="IStorageContributor"/> is held to, checked once as the control plane starts.</summary>
public static partial class StorageAreaRules
{
    /// <summary>The longest area id.</summary>
    public const int MaxIdLength = 128;

    /// <summary>The longest brief, which fits the overview's grid without being cut.</summary>
    public const int MaxBriefLength = 40;

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// Every contributor's areas, checked together: ids well formed and unique, every text present, a retention only on an
    /// area safe or optional to remove (and always on an optional one), each area either whole tables or one column, no table
    /// held by two areas, and every cleared column in a table another area holds. Answers the problems, one line each,
    /// naming the contributor and the area; empty when there are none.
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<IStorageContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        var problems = new List<string>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var claims = new List<(StorageTable Table, string Area)>();
        var columns = new List<(StorageColumn Column, string Area)>();

        foreach (var contributor in contributors)
        {
            var owner = string.IsNullOrWhiteSpace(contributor.Owner) ? contributor.GetType().Name : contributor.Owner;
            if (string.IsNullOrWhiteSpace(contributor.Owner))
            {
                problems.Add($"Storage contributor {contributor.GetType().Name} names no owner.");
            }

            foreach (var area in contributor.Areas)
            {
                var name = $"{owner} area '{area.Id}'";
                if (string.IsNullOrWhiteSpace(area.Id) || area.Id.Length > MaxIdLength || !IdPattern().IsMatch(area.Id))
                {
                    problems.Add($"{name}: the id must be dot-separated parts of lowercase letters, digits and hyphens (at most {MaxIdLength} characters).");
                }
                else if (!ids.TryAdd(area.Id, owner))
                {
                    problems.Add($"{name}: the id is already used by {ids[area.Id]}.");
                }

                foreach (var (text, label) in new[] { (area.Group, "group"), (area.Title, "title"), (area.Holds, "description"), (area.Reason, "reason") })
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        problems.Add($"{name}: the {label} is empty.");
                    }
                }

                if (area.Retention is null && string.IsNullOrWhiteSpace(area.Brief))
                {
                    problems.Add($"{name}: an area without a retention needs a brief saying how its data leaves.");
                }

                if (area.Brief is { Length: > MaxBriefLength })
                {
                    problems.Add($"{name}: the brief is longer than {MaxBriefLength} characters.");
                }

                if (area.Retention is { } retention)
                {
                    if (area.Verdict == StorageVerdict.Keep)
                    {
                        problems.Add($"{name}: an area to keep takes no retention.");
                    }

                    if (string.IsNullOrWhiteSpace(retention.Keeps))
                    {
                        problems.Add($"{name}: the retention does not say what it always keeps.");
                    }

                    if (string.IsNullOrWhiteSpace(retention.Unit))
                    {
                        problems.Add($"{name}: the retention does not say what its cleanup counts.");
                    }

                    if (retention.MinDays < 0 || retention.MaxDays > StorageRetention.MaxRetentionDays || retention.MinDays > retention.MaxDays)
                    {
                        problems.Add($"{name}: the retention's days must lie between 0 and {StorageRetention.MaxRetentionDays}, the least first.");
                    }
                }
                else if (area.Verdict == StorageVerdict.Optional)
                {
                    problems.Add($"{name}: an area optional to remove needs a retention, since nothing else removes it.");
                }

                if (area.Column is { } column)
                {
                    if (area.Tables.Count > 0)
                    {
                        problems.Add($"{name}: an area is either tables or one column, not both.");
                    }

                    columns.Add((column, area.Id));
                }
                else if (area.Tables.Count == 0)
                {
                    problems.Add($"{name}: the area holds no table and no column.");
                }

                claims.AddRange(area.Tables.Select(table => (table, area.Id)));
            }
        }

        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                if (Overlap(claims[i].Table, claims[j].Table))
                {
                    problems.Add($"Areas '{claims[i].Area}' and '{claims[j].Area}' both hold {claims[i].Table}{(claims[i].Table == claims[j].Table ? string.Empty : $" and {claims[j].Table}")}; a table belongs to one area.");
                }
            }
        }

        foreach (var (column, area) in columns)
        {
            if (!claims.Any(claim => claim.Table.Matches(column.Schema, column.Table) && claim.Area != area))
            {
                problems.Add($"Area '{area}' clears {column}, but no other area holds {column.Schema}.{column.Table}.");
            }
        }

        return problems;
    }

    /// <summary>Whether two claims can hold the same table: equal names, a name a prefix holds, or prefixes one of which
    /// starts the other.</summary>
    private static bool Overlap(StorageTable a, StorageTable b)
    {
        if (!string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (a.IsPrefix, b.IsPrefix) switch
        {
            (false, false) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (true, false) => b.Name.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase),
            (false, true) => a.Name.StartsWith(b.Name, StringComparison.OrdinalIgnoreCase),
            (true, true) => a.Name.StartsWith(b.Name, StringComparison.OrdinalIgnoreCase) || b.Name.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase),
        };
    }
}
