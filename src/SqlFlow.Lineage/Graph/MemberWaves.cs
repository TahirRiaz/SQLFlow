namespace SqlFlow.Lineage.Graph;

/// <summary>
/// The waves a set of flows runs in when only the dependencies among the flows of the set count: lineage's modified Kahn
/// order with max-level assignment, scoped to the set. A member's wave is one past the latest wave of a member it
/// depends on, so a member waits for its longest chain of members and no longer. An edge with an end outside the set
/// orders nothing, and neither does a path through a flow outside it: a flow that does not run changes nothing its
/// downstream reads. The one exception is a flow the caller names as passed over (a member it leaves out by its mode,
/// say): a path through such flows still orders the members at its two ends, so leaving a flow out of a chain never moves
/// what follows it ahead of what precedes it. Members on a dependency cycle, and every member behind one, go to one
/// fallback wave after all the others and are reported as unordered, never refused. Waves are dense, numbered from 0.
/// <para>
/// The one implementation behind a batch flow's waves (<c>BatchOrchestrator</c>) and a run group's (a schedule's fire, a
/// flow with its descendants: <c>RunScopeExpander</c>), so a set of flows is ordered the same way however it is run.
/// </para>
/// </summary>
public static class MemberWaves
{
    /// <summary>
    /// Places each of <paramref name="members"/> in its wave.
    /// </summary>
    /// <param name="members">The set's flows, by key. A key listed twice is one member, placed at its first position.</param>
    /// <param name="dependencies">Flow-to-flow edges, <c>From</c> finishing before <c>To</c>; a repeated edge counts once,
    /// and an edge with an end outside <paramref name="members"/> or from a member to itself is ignored.</param>
    /// <param name="comparer">How keys compare; the default comparer when null.</param>
    /// <param name="passedOver">Flows the set leaves out whose paths still order it: a path from one member to another
    /// through these flows alone orders the two as an edge between them would. A key that is also a member counts as a
    /// member. None when null.</param>
    /// <returns>For each member, by position: its wave and whether it is in the fallback wave of a cycle.</returns>
    public static IReadOnlyList<MemberWave> Compute<TKey>(
        IReadOnlyList<TKey> members,
        IEnumerable<(TKey From, TKey To)> dependencies,
        IEqualityComparer<TKey>? comparer = null,
        IEnumerable<TKey>? passedOver = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(dependencies);

        var count = members.Count;
        var index = new Dictionary<TKey, int>(count, comparer);
        for (var i = 0; i < count; i++)
        {
            index.TryAdd(members[i], i);
        }

        var passed = passedOver is null ? null : new HashSet<TKey>(passedOver.Where(k => !index.ContainsKey(k)), comparer);
        if (passed is { Count: > 0 })
        {
            var edges = dependencies.ToList();
            dependencies = [.. edges, .. Through(edges, index, passed, comparer)];
        }

        // waitsFor[i]: the members i must wait for; feeds[j]: the members waiting for j.
        var waitsFor = new HashSet<int>[count];
        var feeds = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            waitsFor[i] = [];
            feeds[i] = [];
        }

        foreach (var (from, to) in dependencies)
        {
            if (index.TryGetValue(from, out var before) && index.TryGetValue(to, out var after) && before != after
                && waitsFor[after].Add(before))
            {
                feeds[before].Add(after);
            }
        }

        var level = new int[count];
        var remaining = waitsFor.Select(w => w.Count).ToArray();
        var ready = new Queue<int>(Enumerable.Range(0, count).Where(i => remaining[i] == 0));
        var placed = new bool[count];
        var highest = -1;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            placed[current] = true;
            highest = Math.Max(highest, level[current]);
            foreach (var next in feeds[current])
            {
                level[next] = Math.Max(level[next], level[current] + 1);
                if (--remaining[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        // A repeated key sits where its first listing does.
        var fallback = highest + 1;
        var waves = new MemberWave[count];
        for (var i = 0; i < count; i++)
        {
            var first = index[members[i]];
            waves[i] = placed[first] ? new MemberWave(level[first], false) : new MemberWave(fallback, true);
        }

        return waves;
    }

    /// <summary>
    /// The member-to-member edges the paths through <paramref name="passed"/> flows imply: from each member, every member
    /// reached over one or more passed-over flows and nothing else.
    /// </summary>
    private static List<(TKey From, TKey To)> Through<TKey>(
        List<(TKey From, TKey To)> edges, Dictionary<TKey, int> index, HashSet<TKey> passed, IEqualityComparer<TKey>? comparer)
        where TKey : notnull
    {
        var next = new Dictionary<TKey, List<TKey>>(comparer);
        foreach (var (from, to) in edges)
        {
            if (!next.TryGetValue(from, out var targets))
            {
                targets = [];
                next[from] = targets;
            }

            targets.Add(to);
        }

        var implied = new List<(TKey From, TKey To)>();
        foreach (var member in index.Keys)
        {
            if (!next.TryGetValue(member, out var fed))
            {
                continue;
            }

            var seen = new HashSet<TKey>(comparer);
            var stack = new Stack<TKey>(fed.Where(passed.Contains));
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current) || !next.TryGetValue(current, out var targets))
                {
                    continue;
                }

                foreach (var target in targets)
                {
                    if (index.ContainsKey(target))
                    {
                        implied.Add((member, target));
                    }
                    else if (passed.Contains(target))
                    {
                        stack.Push(target);
                    }
                }
            }
        }

        return implied;
    }
}

/// <summary>Where <see cref="MemberWaves.Compute"/> placed one member.</summary>
/// <param name="Wave">Its wave, numbered densely from 0.</param>
/// <param name="Unordered">True when it is in the fallback wave: on a dependency cycle, or behind one.</param>
public readonly record struct MemberWave(int Wave, bool Unordered);
