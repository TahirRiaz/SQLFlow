using SqlFlow.Core.SourceControl;

namespace SqlFlow.SourceControl;

/// <summary>
/// Progress from a scripting pass, reported as it happens so a long run says what it is doing instead of going
/// silent for minutes. <see cref="Scripted"/> counts objects finished in <see cref="Category"/> and
/// <see cref="Total"/> how many that category holds, so a caller can render "412 of 1,204" without keeping its
/// own tally. This restores the per-object progress the legacy runner published through OnObjectScripted.
///
/// The two counts also carry the stage a category is at, so one report type covers the whole walk: a
/// <see cref="Total"/> of zero means the category is still being enumerated and its size is not yet known, a
/// <see cref="Scripted"/> of zero with a known total means enumeration finished and scripting is starting, and
/// anything else is a running tally.
/// </summary>
public sealed record ScriptProgress
{
    /// <summary>The object category being walked (Table, View, StoredProcedure, Data, and so on).</summary>
    public required string Category { get; init; }

    public int Scripted { get; init; }

    public int Total { get; init; }

    /// <summary>Set when the report carries something other than a count: the warning text of an object that
    /// could not be scripted.</summary>
    public string? Warning { get; init; }
}

/// <summary>Scripts a database's objects to an in-memory snapshot. Abstracted so the orchestration service can
/// be exercised without a live SQL Server, while <see cref="SmoDatabaseScripter"/> is the production engine.</summary>
public interface IDatabaseScripter
{
    /// <param name="connectionString">The resolved connection to the server holding the database.</param>
    /// <param name="database">The database to script, or null for the connection's default catalog.</param>
    /// <param name="scripting">Which object categories and data tables to capture.</param>
    /// <param name="progress">Called as each category is walked, so the run can report what it is doing while it
    /// does it. Null reports nothing, which is what the offline tests want.</param>
    /// <param name="ct">Cancels between objects, so a long walk stops promptly.</param>
    ScriptedDatabase Script(
        string connectionString, string? database, SourceControlScripting scripting,
        Action<ScriptProgress>? progress = null, CancellationToken ct = default);
}
