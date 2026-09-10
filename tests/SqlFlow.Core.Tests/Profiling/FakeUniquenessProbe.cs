using System.Globalization;
using SqlFlow.Core.Profiling;

namespace SqlFlow.Tests.Profiling;

/// <summary>
/// An <see cref="IUniquenessProbe"/> over in-memory rows, shared by the detector test suites. The sample is the
/// first N rows: a deterministic stand-in for the SQL probe's random draw, which keeps the sampling-contract tests
/// reproducible. Declared keys are injectable so the metadata fast path is testable without a database.
/// </summary>
internal sealed class FakeUniquenessProbe : IUniquenessProbe
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _full;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _working;

    public FakeUniquenessProbe(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int sampleSize)
    {
        _full = rows;
        if (sampleSize > 0 && rows.Count > sampleSize)
        {
            _working = rows.Take(sampleSize).ToList();
            Sampled = true;
        }
        else
        {
            _working = rows;
            Sampled = false;
        }

        TotalRows = _full.Count;
        WorkingRows = _working.Count;
    }

    public long TotalRows { get; }

    public long WorkingRows { get; }

    public bool Sampled { get; }

    public IReadOnlyList<IReadOnlyList<string>> DeclaredUniqueKeys { get; init; } = [];

    /// <summary>How many measurement passes (batched queries) the detector issued.</summary>
    public int MeasurePasses { get; private set; }

    public Task<IReadOnlyList<SetMeasure>> MeasureManyAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct = default)
    {
        MeasurePasses++;
        return Task.FromResult<IReadOnlyList<SetMeasure>>(sets.Select(s => Measure(_working, s, exact: !Sampled)).ToList());
    }

    public Task<SetVerdict> VerifyAsync(IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        var m = Measure(_full, columns, exact: true);
        return Task.FromResult(new SetVerdict
        {
            Columns = columns.ToList(),
            IsUnique = m.IsUnique,
            HasNulls = m.Nulls > 0,
            Rows = m.Scanned,
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Builds rows from a column list and value arrays, keyed case-insensitively like SQL Server.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Table(IReadOnlyList<string> columns, params object?[][] rows)
        => rows.Select(r =>
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                d[columns[i]] = r[i];
            }

            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

    private static SetMeasure Measure(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns, bool exact)
    {
        long nulls = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (columns.Any(c => row[c] is null))
            {
                nulls++;
                continue;
            }

            // A length-prefixed join so ("a","bc") and ("ab","c") never alias to the same tuple key.
            distinct.Add(string.Concat(columns.Select(c =>
            {
                var text = Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? string.Empty;
                return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text + "|";
            })));
        }

        return new SetMeasure
        {
            Columns = columns.ToList(),
            Scanned = rows.Count,
            Distinct = distinct.Count,
            Nulls = nulls,
            Exact = exact,
        };
    }
}
