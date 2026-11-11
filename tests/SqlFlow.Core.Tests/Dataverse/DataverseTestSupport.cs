using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Dataverse;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Dataverse;

namespace SqlFlow.Tests.Dataverse;

/// <summary>
/// A clock whose timers fire at once and remember what they were asked to wait, so a retry honouring
/// <c>Retry-After: 7</c> is proven by the 7 seconds it asked for, without the test waiting them. Its time moves on by
/// every wait, so a token's expiry is reached the way it would be.
/// </summary>
internal sealed class InstantClock : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _waits = new();
    private long _elapsedTicks;

    public DateTimeOffset Start { get; } = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<TimeSpan> Waits => _waits.ToList();

    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _elapsedTicks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            _waits.Enqueue(dueTime);
            Advance(dueTime);
        }

        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        }

        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <c>token-1</c>, then <c>token-2</c>, <c>token-3</c>... on each forced refresh, counting the refreshes.</summary>
internal sealed class ScriptedTokens : IDataverseTokenProvider
{
    private int _generation = 1;

    public int Refreshes { get; private set; }

    public string Identity => "the test identity";

    public ValueTask<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (forceRefresh)
        {
            Refreshes++;
            _generation++;
        }

        return ValueTask.FromResult($"token-{_generation}");
    }
}

/// <summary>Builds readers, specs and rows against a <see cref="FakeDataverse"/>.</summary>
internal static class DataverseTesting
{
    public static DataverseSourceReader Reader(FakeDataverse service, InstantClock? clock = null, ScriptedTokens? tokens = null)
    {
        var scripted = tokens ?? new ScriptedTokens();
        return new DataverseSourceReader(
            new SecretResolver([new EnvSecretProvider()]),
            ambient: null,
            service,
            _ => scripted,
            clock ?? new InstantClock(),
            NullLogger.Instance);
    }

    public static SourceSpec Spec(params (string Key, string? Value)[] options)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [DataverseSourceOptions.TableOption] = "account" };
        foreach (var (key, value) in options)
        {
            map[key] = value;
        }

        return new SourceSpec { Type = "dataverse", Location = FakeDataverse.Environment, Options = map };
    }

    /// <summary>A spec carrying the engine's injected watermark bound and order, as the runner hands it to the reader.</summary>
    public static SourceSpec Bounded(SourceSpec spec, string column, string value, WatermarkKind kind)
    {
        var map = new Dictionary<string, string?>(spec.Options, StringComparer.OrdinalIgnoreCase)
        {
            [WatermarkPredicate.ColumnOption] = column,
            [WatermarkPredicate.ValueOption] = value,
            [WatermarkPredicate.KindOption] = kind.ToString(),
            [WatermarkPredicate.OrderColumnOption] = column,
        };
        return spec with { Options = map };
    }

    /// <summary>An account table with one column of each everyday kind.</summary>
    public static FakeDataverse Accounts(int rows)
    {
        var service = new FakeDataverse();
        service.Attributes.AddRange(
        [
            new FakeAttribute("accountid", "UniqueidentifierType"),
            new FakeAttribute("name", "StringType") { MaxLength = 160 },
            new FakeAttribute("description", "MemoType") { MaxLength = 2000 },
            new FakeAttribute("revenue", "MoneyType"),
            new FakeAttribute("numberofemployees", "IntegerType"),
            new FakeAttribute("statecode", "StateType"),
            new FakeAttribute("versionnumber", "BigIntType"),
            new FakeAttribute("modifiedon", "DateTimeType") { DateTimeBehavior = "UserLocal" },
            new FakeAttribute("parentaccountid", "LookupType") { Targets = ["account"] },
            new FakeAttribute("parentaccountidname", "StringType") { AttributeOf = "parentaccountid", MaxLength = 160 },
        ]);

        for (var i = 1; i <= rows; i++)
        {
            service.Rows.Add(Account(i));
        }

        return service;
    }

    public static Dictionary<string, object?> Account(int i) => new()
    {
        ["accountid"] = Id(i),
        ["name"] = $"Account {i}",
        ["description"] = i % 3 == 0 ? null : $"Line one of {i}\nline two",
        ["revenue"] = 1000.25m * i,
        ["numberofemployees"] = i * 10,
        ["statecode"] = 0,
        ["versionnumber"] = 638_183_964_640_916_096L + i,
        ["modifiedon"] = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        ["parentaccountid"] = i > 1 ? Id(1) : null,
    };

    public static Guid Id(int i) => new($"00000000-0000-0000-0000-{i:D12}");
}
