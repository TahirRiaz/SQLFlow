namespace SqlFlow.Core.Profiling;

/// <summary>
/// Finds the minimal column sets that uniquely identify a table's rows, without any prior knowledge of its keys, and
/// does so cheaply enough to be practical on a large table. The strategy mirrors the legacy detector: rank columns by
/// how identifying they are (distinct non-null values; a column that is ever null can never be in a key and is
/// dropped), take any single column that is already unique, else grow a composite greedily from the most-identifying
/// columns until it is unique and strip every column that is not needed to leave a minimal key.
///
/// The cost discipline is what makes it usable at scale: keys the store itself declares (an enforced unique index or
/// constraint) are reported straight from metadata without reading a row, the probe measures a whole level of trial
/// combinations in a single pass (not one scan per trial), the entire search runs against the probe's working set (a
/// sample when the table is large), a distinct-count product bound rules out "no key is possible" without a single
/// query, and only the handful of surviving candidates are confirmed against the whole table - with a
/// short-circuiting duplicate probe rather than a full distinct count. All measurement is delegated to an
/// <see cref="IUniquenessProbe"/>, so the algorithm is storage-agnostic and unit-testable against in-memory data.
/// </summary>
public static class UniqueKeyDetector
{
    public static async Task<UniqueKeyReport> DetectAsync(
        IUniquenessProbe probe, IReadOnlyList<string> columns, UniqueKeyOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);

        // 0. Declared keys. A set the store itself enforces as unique is already proven by the engine that maintains
        // it, so it is reported without scanning: the answer on any table, at any size, in metadata time.
        if (probe.DeclaredUniqueKeys.Count > 0)
        {
            var declared = new List<UniqueKeyCandidate>();
            foreach (var key in probe.DeclaredUniqueKeys.Where(k => k.Count > 0))
            {
                if (declared.Any(c => SameSet(c.Columns, key)))
                {
                    continue;
                }

                declared.Add(new UniqueKeyCandidate
                {
                    Columns = key.ToList(), IsUnique = true, Verified = true, Declared = true,
                    Distinct = probe.TotalRows, Nulls = 0, Rows = probe.TotalRows, Duplicates = 0,
                });
            }

            if (declared.Count > 0)
            {
                return new UniqueKeyReport
                {
                    TotalRows = probe.TotalRows,
                    ScannedRows = 0,
                    Sampled = false,
                    Columns = [],
                    Candidates = declared
                        .OrderBy(c => c.Columns.Count)
                        .ThenBy(c => string.Join(",", c.Columns), StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, options.MaxCandidates))
                        .ToList(),
                    Note = "The database metadata declares these column set(s) unique (a primary key or an enabled, "
                        + "unfiltered unique index/constraint), so the rows were not profiled. Use --no-metadata to "
                        + "profile the data anyway.",
                };
            }
        }

        var working = probe.WorkingRows;
        if (columns.Count == 0 || working == 0)
        {
            return new UniqueKeyReport
            {
                TotalRows = probe.TotalRows,
                ScannedRows = working,
                Sampled = probe.Sampled,
                Columns = [],
                Candidates = [],
                Note = working == 0 ? "The table is empty; no key can be inferred." : "The table has no columns to analyze.",
            };
        }

        // 1. Per-column cardinality in a single pass: every column measured as its own singleton set.
        var singleMeasures = await probe.MeasureManyAsync(columns.Select(c => (IReadOnlyList<string>)[c]).ToList(), ct).ConfigureAwait(false);
        var stats = new List<ColumnCardinality>(columns.Count);
        var sampleByColumn = new Dictionary<string, SetMeasure>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var m = singleMeasures[i];
            stats.Add(new ColumnCardinality { Column = columns[i], Distinct = m.Distinct, Nulls = m.Nulls, Scanned = m.Scanned });
            sampleByColumn[columns[i]] = m;
        }

        var found = new List<(IReadOnlyList<string> Set, SetMeasure Sample)>();

        // 2. Single-column keys: any column that is already unique over the working set.
        foreach (var s in stats.Where(s => s.Nulls == 0 && s.Distinct == working))
        {
            found.Add(([s.Column], sampleByColumn[s.Column]));
        }

        // 3. Composites, only when no single column is a key (a composite containing a unique single column is never
        // minimal). The search pool is the non-null columns with more than one value, ranked most-identifying first:
        // a column that is ever null cannot be in a key, and a constant cannot help form one.
        SetMeasure? nearMiss = null;
        if (found.Count == 0)
        {
            var pool = stats
                .Where(s => s.Nulls == 0 && s.Distinct > 1)
                .OrderByDescending(s => s.Distinct)
                .ThenBy(s => s.Column, StringComparer.Ordinal)
                .Take(Math.Max(1, options.MaxScanColumns))
                .Select(s => s.Column)
                .ToList();

            // A necessary condition: distinct(set) <= product of the columns' distinct counts, so if even the whole
            // pool's product cannot reach the row count, no subset can be unique. Rules out wide low-cardinality
            // tables without a single combination query.
            if (pool.Count > 0 && ProductReaches(pool, sampleByColumn, working))
            {
                foreach (var seed in pool.Take(Math.Min(3, pool.Count)))
                {
                    var (built, measure) = await GreedyBuildAsync(probe, seed, pool, options.MaxKeyColumns, ct).ConfigureAwait(false);
                    if (measure.IsUnique)
                    {
                        var (minimal, minimalMeasure) = await ReduceAsync(probe, built, measure, ct).ConfigureAwait(false);
                        if (!found.Any(f => SameSet(f.Set, minimal)))
                        {
                            found.Add((minimal, minimalMeasure));
                        }
                    }
                    else if (nearMiss is null || measure.Duplicates < nearMiss.Duplicates)
                    {
                        nearMiss = measure;
                    }
                }
            }
            else if (pool.Count > 0)
            {
                // No key is reachable; still surface the most-identifying combination so the operator sees the gap.
                var (_, measure) = await GreedyBuildAsync(probe, pool[0], pool, options.MaxKeyColumns, ct).ConfigureAwait(false);
                nearMiss = measure;
            }
        }

        // 4. Confirm candidates. On a full scan the working measurement is already exact; on a sample each survivor is
        // verified against the whole table (unless --no-verify), so a fast search never reports an unproven key.
        var candidates = new List<UniqueKeyCandidate>();
        foreach (var (set, sample) in found)
        {
            candidates.Add(await ConfirmAsync(probe, set, sample, options.Verify, ct).ConfigureAwait(false));
        }

        var rankedCandidates = candidates
            .OrderByDescending(c => c.IsUnique)
            .ThenByDescending(c => c.Verified)
            .ThenBy(c => c.Columns.Count)
            .ThenByDescending(c => c.Selectivity)
            .Take(Math.Max(1, options.MaxCandidates))
            .ToList();

        string? note = null;
        if (!rankedCandidates.Any(c => c.IsUnique))
        {
            // No key found. Surface the closest combination (fewest duplicates) so the operator sees how far off it is.
            if (nearMiss is not null && rankedCandidates.All(c => !SameSet(c.Columns, nearMiss.Columns)))
            {
                rankedCandidates.Insert(0, await ConfirmAsync(probe, nearMiss.Columns, nearMiss, verify: false, ct).ConfigureAwait(false));
            }

            note = "No unique key was found within the search limits (try raising --max-columns). The top candidate is the closest non-unique combination.";
        }
        else if (probe.Sampled)
        {
            note = options.Verify
                ? $"Profiled on a sample of {working} of {probe.TotalRows} rows; reported keys were verified against the whole table."
                : $"Profiled on a sample of {working} of {probe.TotalRows} rows; candidates are NOT verified against the whole table (--no-verify).";
        }

        return new UniqueKeyReport
        {
            TotalRows = probe.TotalRows,
            ScannedRows = working,
            Sampled = probe.Sampled,
            Columns = stats,
            Candidates = rankedCandidates,
            Note = note,
        };
    }

    /// <summary>
    /// Grows a column set from <paramref name="seed"/>, at each step measuring every one-column extension in a single
    /// pass and adding the one that most increases the distinct-tuple count. Stops as soon as an extension is unique,
    /// no extension improves the set, or the width cap is hit. Returns the built set and its final measurement.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Set, SetMeasure Measure)> GreedyBuildAsync(
        IUniquenessProbe probe, string seed, IReadOnlyList<string> pool, int maxColumns, CancellationToken ct)
    {
        IReadOnlyList<string> set = [seed];
        var current = (await probe.MeasureManyAsync([set], ct).ConfigureAwait(false))[0];

        while (!current.IsUnique && set.Count < maxColumns)
        {
            var extensions = pool.Where(c => !set.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (extensions.Count == 0)
            {
                break;
            }

            var trials = extensions.Select(c => (IReadOnlyList<string>)[.. set, c]).ToList();
            var measures = await probe.MeasureManyAsync(trials, ct).ConfigureAwait(false);

            var unique = measures.FindIndex(m => m.IsUnique);
            if (unique >= 0)
            {
                return (trials[unique], measures[unique]);
            }

            var best = measures.MaxIndexBy(m => m.Distinct);
            if (measures[best].Distinct <= current.Distinct)
            {
                break; // no extension improves the set: uniqueness is unreachable from here
            }

            set = trials[best];
            current = measures[best];
        }

        return (set, current);
    }

    /// <summary>Strips columns from a unique set as long as it stays unique, leaving a minimal key. Each pass measures
    /// every "drop one column" subset in a single pass and takes the first that stays unique.</summary>
    private static async Task<(IReadOnlyList<string> Set, SetMeasure Measure)> ReduceAsync(
        IUniquenessProbe probe, IReadOnlyList<string> set, SetMeasure measure, CancellationToken ct)
    {
        var reduced = set.ToList();
        var reducedMeasure = measure;
        var changed = true;
        while (changed && reduced.Count > 1)
        {
            changed = false;
            var subsets = reduced.Select((_, i) => (IReadOnlyList<string>)reduced.Where((_, j) => j != i).ToList()).ToList();
            var measures = await probe.MeasureManyAsync(subsets, ct).ConfigureAwait(false);
            var idx = measures.FindIndex(m => m.IsUnique);
            if (idx >= 0)
            {
                reduced = subsets[idx].ToList();
                reducedMeasure = measures[idx];
                changed = true;
            }
        }

        return (reduced, reducedMeasure);
    }

    /// <summary>Builds the reported candidate: an exact full-scan measurement is used as is; a sampled candidate is
    /// confirmed against the whole table (when <paramref name="verify"/>), else reported as an unverified estimate.</summary>
    private static async Task<UniqueKeyCandidate> ConfirmAsync(
        IUniquenessProbe probe, IReadOnlyList<string> set, SetMeasure sample, bool verify, CancellationToken ct)
    {
        if (!probe.Sampled)
        {
            return new UniqueKeyCandidate
            {
                Columns = set, IsUnique = sample.IsUnique, Verified = true,
                Distinct = sample.Distinct, Nulls = sample.Nulls, Rows = sample.Scanned, Duplicates = sample.Duplicates,
            };
        }

        if (!verify)
        {
            return new UniqueKeyCandidate
            {
                Columns = set, IsUnique = sample.IsUnique, Verified = false,
                Distinct = sample.Distinct, Nulls = sample.Nulls, Rows = sample.Scanned, Duplicates = sample.Duplicates,
                Estimated = true,
            };
        }

        var verdict = await probe.VerifyAsync(set, ct).ConfigureAwait(false);
        if (verdict.IsUnique)
        {
            return new UniqueKeyCandidate
            {
                Columns = set, IsUnique = true, Verified = true,
                Distinct = verdict.Rows, Nulls = 0, Rows = verdict.Rows, Duplicates = 0,
            };
        }

        // Verified not-unique. The exact duplicate count is intentionally not computed (that would defeat the
        // early-exit probe); the sample's counts stand as an estimate, flagged as such.
        return new UniqueKeyCandidate
        {
            Columns = set, IsUnique = false, Verified = true,
            Distinct = sample.Distinct, Nulls = sample.Nulls, Rows = verdict.Rows, Duplicates = sample.Duplicates,
            Estimated = true,
        };
    }

    /// <summary>True when the product of the pool columns' distinct counts reaches <paramref name="rows"/> - a
    /// necessary condition for any subset to be unique. Saturates at <paramref name="rows"/> to avoid overflow.</summary>
    private static bool ProductReaches(IReadOnlyList<string> pool, IReadOnlyDictionary<string, SetMeasure> byColumn, long rows)
    {
        long product = 1;
        foreach (var column in pool)
        {
            var distinct = byColumn[column].Distinct;
            if (distinct <= 0)
            {
                continue;
            }

            // Saturating multiply: once we reach the row count the condition is satisfied.
            if (product > rows / Math.Max(1, distinct))
            {
                return true;
            }

            product *= distinct;
            if (product >= rows)
            {
                return true;
            }
        }

        return product >= rows;
    }

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (predicate(list[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int MaxIndexBy<T>(this IReadOnlyList<T> list, Func<T, long> selector)
    {
        var best = 0;
        var bestValue = selector(list[0]);
        for (var i = 1; i < list.Count; i++)
        {
            var value = selector(list[i]);
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }

        return best;
    }
}
