using System.Collections.Concurrent;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// A simulated fleet driving the dispatcher the way production does: hundreds of nodes polling concurrently over a
/// backlog of thousands of runs spread across pools and groups, with random lease renewals, random abandonment
/// (a node that stops polling mid-run), and operator cancels thrown in. After the fleet drains the backlog, every
/// invariant that matters is checked over the whole history: no run was ever executed by two nodes at once, no two
/// runs of one pipeline overlapped, no group member started before a lower wave finished, no group ever exceeded
/// its cap, and every run reached a terminal state in the journal. The run also reports throughput, so a regression
/// that makes the lock or the scan expensive is visible.
/// </summary>
public sealed class FleetSimulationTests
{
    private readonly ITestOutputHelper _output;

    public FleetSimulationTests(ITestOutputHelper output) => _output = output;

    private sealed record Interval(Guid RunId, Guid PipelineId, Guid? GroupId, int Wave, string Node, long Start, long End);

    [Theory]
    [InlineData(300, 3000, 4)]
    [InlineData(50, 500, 1)]
    public async Task HundredsOfNodes_DrainTheBacklog_WithEveryInvariantIntact(int nodeCount, int runCount, int slotsPerNode)
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var options = Make.Options(leaseSeconds: 3600, longPollSeconds: 1);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, options);
        var rng = new Random(12345);
        var pools = new[] { null, null, "etl", "ml" };
        var pipelines = Enumerable.Range(0, runCount / 3).Select(_ => Guid.NewGuid()).ToList();
        var t0 = clock.GetUtcNow().UtcDateTime;

        // The backlog: standalone runs across pools and pipelines, plus groups with waves and caps.
        var runs = new List<DispatchRun>();
        var caps = new Dictionary<Guid, int>();
        for (var i = 0; i < runCount; i++)
        {
            if (rng.Next(5) == 0)
            {
                var group = Guid.NewGuid();
                var cap = rng.Next(1, 4);
                caps[group] = cap;
                var members = rng.Next(2, 7);
                var pool = pools[rng.Next(pools.Length)];
                for (var m = 0; m < members && i < runCount; m++, i++)
                {
                    runs.Add(new DispatchRun(
                        Guid.CreateVersion7(), pipelines[rng.Next(pipelines.Count)], pool, group, m / 2, cap,
                        t0.AddMilliseconds(i), 0, false));
                }

                i--;
                continue;
            }

            runs.Add(new DispatchRun(
                Guid.CreateVersion7(), pipelines[rng.Next(pipelines.Count)], pools[rng.Next(pools.Length)], null, 0, null,
                t0.AddMilliseconds(i), 0, false));
        }

        foreach (var run in runs)
        {
            ledger.AddQueued(run);
        }

        dispatcher.NotifyRunsEnqueued(runs);

        var history = new ConcurrentBag<Interval>();
        var executing = new ConcurrentDictionary<Guid, (string Node, int Attempt)>();
        var ticks = 0L;
        var completed = 0;
        var abandoned = 0;
        var cancelled = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        async Task NodeLoopAsync(int index)
        {
            var name = $"node-{index:000}";
            var served = index % 3 == 0 ? new[] { "etl" } : index % 3 == 1 ? new[] { "ml" } : [];
            var local = new Random(index);
            var free = slotsPerNode;
            var holding = new List<HeldRun>();
            var idle = 0;
            while (idle < 5)
            {
                var request = new NodePollRequest(name, "sim", served, slotsPerNode, free, 0, 0, holding.ToList(), [], t0, 0);
                var response = await dispatcher.PollAsync(request, CancellationToken.None);
                foreach (var revoked in response.RevokedRuns)
                {
                    holding.RemoveAll(h => h.RunId == revoked);
                    free++;
                }

                foreach (var handout in response.Runs)
                {
                    free--;
                    var start = Interlocked.Increment(ref ticks);
                    Assert.True(executing.TryAdd(handout.RunId, (name, handout.Attempt)), $"run {handout.RunId} handed to {name} while executing elsewhere");
                    holding.Add(new HeldRun(handout.RunId, handout.Attempt));
                    // Execute "for a while": a few simulated ticks, then report, or abandon (lease lapses), or get cancelled.
                    var fate = local.Next(100);
                    var end = Interlocked.Increment(ref ticks);
                    var run = runs.First(r => r.RunId == handout.RunId);
                    if (fate < 3 && handout.Attempt == 1)
                    {
                        // Abandon: stop renewing this one; the lease lapses and the dispatcher requeues it.
                        executing.TryRemove(handout.RunId, out _);
                        holding.RemoveAll(h => h.RunId == handout.RunId);
                        history.Add(new Interval(run.RunId, run.PipelineId, run.GroupId, run.GroupWave, name, start, end));
                        Interlocked.Increment(ref abandoned);
                        free++;
                        continue;
                    }

                    RunOutcomeKind kind;
                    if (fate < 8)
                    {
                        ledger.Runs[handout.RunId].CancelRequested = true;
                        dispatcher.NotifyRunCancelled(handout.RunId);
                        kind = RunOutcomeKind.Cancelled;
                        Interlocked.Increment(ref cancelled);
                    }
                    else
                    {
                        kind = RunOutcomeKind.Completed;
                    }

                    var status = await dispatcher.RecordRunOutcomeAsync(
                        handout.RunId,
                        new RunOutcomeRequest(name, handout.Attempt, kind, null, fate < 12 ? Make.FailedArtifact : Make.Artifact),
                        CancellationToken.None);
                    Assert.Equal(RunOutcomeStatus.Recorded, status);
                    executing.TryRemove(handout.RunId, out _);
                    holding.RemoveAll(h => h.RunId == handout.RunId);
                    history.Add(new Interval(run.RunId, run.PipelineId, run.GroupId, run.GroupWave, name, start, end));
                    Interlocked.Increment(ref completed);
                    free++;
                }

                if (response.Runs.Count == 0)
                {
                    idle++;
                    await Task.Yield();
                }
                else
                {
                    idle = 0;
                }
            }
        }

        // Drive the fleet until every node is idle, then let the abandoned leases lapse (the clock only moves
        // while no node is executing, so an active node's lease can never expire under it) and drive the fleet
        // again over the requeued runs. Abandonment happens only on a first attempt, so two sweeps converge.
        await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(NodeLoopAsync));
        for (var sweep = 0; sweep < 3; sweep++)
        {
            clock.Advance(TimeSpan.FromMinutes(61));
            await dispatcher.TickAsync(CancellationToken.None);
            await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(NodeLoopAsync));
        }

        stopwatch.Stop();
        _output.WriteLine($"{nodeCount} nodes, {runCount} runs: {completed} completed, {abandoned} abandoned, {cancelled} cancelled, {ledger.WriteCount} journal writes in {stopwatch.ElapsedMilliseconds} ms");

        // Every run reached a terminal state in the journal, and nothing is left in memory.
        Assert.All(ledger.Runs.Values, row => Assert.Contains(row.Status, new[] { "succeeded", "failed", "cancelled", "skipped" }));
        var view = dispatcher.Snapshot();
        Assert.Empty(view.QueuedRuns);
        Assert.Empty(view.LeasedRuns);

        // Invariants over the whole history of executions (including the abandoned ones, which held their
        // pipeline until the lease lapsed and were then re-executed).
        var intervals = history.ToList();
        foreach (var byPipeline in intervals.GroupBy(i => i.PipelineId))
        {
            var ordered = byPipeline.OrderBy(i => i.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.True(ordered[i].Start > ordered[i - 1].End, $"pipeline {byPipeline.Key} had two overlapping executions");
            }
        }

        foreach (var byGroup in intervals.Where(i => i.GroupId is not null).GroupBy(i => i.GroupId!.Value))
        {
            var members = byGroup.ToList();
            foreach (var wave in members.Select(m => m.Wave).Distinct())
            {
                var lowerEnd = members.Where(m => m.Wave < wave).Select(m => m.End).DefaultIfEmpty(0).Max();
                var waveStart = members.Where(m => m.Wave == wave).Min(m => m.Start);
                Assert.True(waveStart > lowerEnd, $"group {byGroup.Key} wave {wave} started before a lower wave finished");
            }

            var cap = caps[byGroup.Key];
            var points = members.SelectMany(m => new[] { (m.Start, +1), (m.End, -1) }).OrderBy(p => p.Item1).ThenBy(p => p.Item2);
            var concurrent = 0;
            foreach (var (_, delta) in points)
            {
                concurrent += delta;
                Assert.True(concurrent <= cap, $"group {byGroup.Key} exceeded its cap of {cap}");
            }
        }

        Assert.True(intervals.Select(i => i.RunId).Distinct().Count() <= runs.Count);
        Assert.Equal(runs.Count(r => r.GroupId is null) + runs.Count(r => r.GroupId is not null),
            ledger.Runs.Count);
    }

    [Fact]
    public async Task HundredsOfParkedPolls_AreWokenSelectively_WhenWorkArrivesForTheirPool()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 30));

        var etl = Enumerable.Range(0, 150).Select(i => dispatcher.PollAsync(Make.Poll($"etl-{i}", freeRuns: 1, pools: ["etl"], waitSeconds: 30), CancellationToken.None)).ToList();
        var ml = Enumerable.Range(0, 150).Select(i => dispatcher.PollAsync(Make.Poll($"ml-{i}", freeRuns: 1, pools: ["ml"], waitSeconds: 30), CancellationToken.None)).ToList();
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 300);

        var run = Make.Run(clock.GetUtcNow().UtcDateTime, pool: "ml");
        ledger.AddQueued(run);
        dispatcher.NotifyRunsEnqueued([run]);

        var winner = await Task.WhenAny(ml).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(run.RunId, Assert.Single((await winner).Runs).RunId);
        // Exactly one node took it; every other poll is still parked.
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 299);
        await Task.Delay(100);
        Assert.Equal(299, dispatcher.Snapshot().Waiters);
        Assert.All(etl, p => Assert.False(p.IsCompleted));

        dispatcher.Deactivate();
        foreach (var poll in etl.Concat(ml).Where(p => p != winner))
        {
            await Assert.ThrowsAsync<DispatchInactiveException>(() => poll.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
