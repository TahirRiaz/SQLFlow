using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public class ProgressiveRefinementWithHash
    {

        private static int _totalUniqueKeysSought = 1; // Maximum combinations to return
        private static int _maxKeyCombinationSize = 1; // Maximum combinations size to consider
        private static volatile int _hasRequiredCombinations = 0; // 0 means false, 1 means true
        private static CancellationTokenSource _cancellationTokenSource = new();
        private static StreamWriter _logWriter;
        public static ConcurrentBag<KeyDetectorResult> FindUniqueCombinations(StreamWriter logWriter,
            DataTable table,
            ConcurrentDictionary<string, double> sampledUniquenessResults,
            List<string> columns,
            int totalUniqueKeysSought,
            int maxKeyCombinationSize,
            double selectRatioFromTopUniquenessScore,
            AnalysisMode analysisMode,
            bool earlyExitOnFound
            )
        {
            _logWriter = logWriter;
            // Optimization: Adjust returnTotalCombinations dynamically
            _totalUniqueKeysSought = totalUniqueKeysSought;
            _maxKeyCombinationSize = maxKeyCombinationSize;
            _hasRequiredCombinations = 0; // Reset for new execution
            _cancellationTokenSource = new CancellationTokenSource(); // Reset CancellationTokenSource for new execution
            ConcurrentDictionary<List<string>, (double Uniqueness, int NumericCount)> allCombinations = new ConcurrentDictionary<List<string>, (double Uniqueness, int NumericCount)>(new ListStringComparer());

            // Calculate dynamic uniqueness threshold as 20% of the highest score
            double maxUniquenessScore = sampledUniquenessResults.Values.Max();
            double dynamicUniquenessThreshold = maxUniquenessScore * selectRatioFromTopUniquenessScore;

            // Use dynamic threshold for pre-filtering columns
            var likelyUniqueColumns = columns
                .Where(column => sampledUniquenessResults.TryGetValue(column, out double uniqueness) && uniqueness >= dynamicUniquenessThreshold)
                .ToList();

            logWriter.WriteLine($"#### maxUniquenessScore:{maxUniquenessScore}");
            logWriter.WriteLine($"#### dynamicUniquenessThreshold:{dynamicUniquenessThreshold}");
            logWriter.WriteLine("#### Likely Unique Columns");
            logWriter.WriteLine(JsonConvert.SerializeObject(likelyUniqueColumns, Formatting.Indented));
            logWriter.Flush();


            // Phase 1: Start with pre-filtered likely unique columns first
            foreach (var column in likelyUniqueColumns)
            {
                if (_hasRequiredCombinations == 1) break;
                List<string> singleColumnList = new List<string> { column };
                AttemptToAddCombination(table, allCombinations, singleColumnList);
            }

            if (analysisMode == AnalysisMode.Basic)
            {
                // Assuming this is the part causing the error, where you have a LINQ query resulting in an IEnumerable<KeyDetectorResult>
                IEnumerable<KeyDetectorResult> queryResult = allCombinations
                    .Where(kvp => kvp.Value.Uniqueness == 1) // Filter to include only fully unique combinations
                    .OrderByDescending(kvp => kvp.Value.NumericCount) // Order by descending NumericCount
                    .OrderByDescending(kvp => kvp.Value.Uniqueness) // Order by descending uniqueness
                    .ThenBy(kvp => kvp.Key.Count) // Then by ascending number of elements
                    .Take(_totalUniqueKeysSought) // Take up to 'ReturnTotalCombinations'
                    .Select(kvp => new KeyDetectorResult // Transform each KeyValuePair into a KeyDetectorResult
                    {
                        // Set properties according to your context
                        DetectedKey = string.Join(", ", kvp.Key.Select(col => "[" + col + "]")),
                        ColumnCountInKey = kvp.Key.Count,
                        NumericColumnCountInKey = kvp.Value.NumericCount,
                        ProofQueryExecuted = false
                    });

                // Convert the IEnumerable<KeyDetectorResult> to a ConcurrentBag<KeyDetectorResult>
                ConcurrentBag<KeyDetectorResult> results = new ConcurrentBag<KeyDetectorResult>(queryResult);

                if (earlyExitOnFound)
                {
                    if (results.Count >= 1)
                    {
                        return results;
                    }
                }

            }


            // Phase 2: Try combinations of likely unique columns
            if (_hasRequiredCombinations == 0)
            {
                var remainingColumns = columns.Except(likelyUniqueColumns).ToList();
                foreach (var column in remainingColumns)
                {
                    if (_hasRequiredCombinations == 1) break;
                    List<string> singleColumnList = new List<string> { column };
                    AttemptToAddCombination(table, allCombinations, singleColumnList);
                }
            }

            // Phase 3: Try combinations of increasing size if needed, starting with likely unique pairs
            if (_hasRequiredCombinations == 0)
            {
                for (int i = 2; i <= columns.Count && _hasRequiredCombinations == 0; i++)
                {
                    var combinations = GetCombinations(likelyUniqueColumns, i).ToList();
                    foreach (var combination in combinations)
                    {
                        if (_hasRequiredCombinations == 1) break;
                        AttemptToAddCombination(table, allCombinations, combination);
                    }
                }
            }

            if (analysisMode == AnalysisMode.Standard)
            {
                // Standard, Try combinations of likely unique columns
                IEnumerable<KeyDetectorResult> queryResult = allCombinations
                    .Where(kvp => kvp.Value.Uniqueness == 1) // Filter to include only fully unique combinations
                    .OrderByDescending(kvp => kvp.Value.NumericCount) // Order by descending NumericCount
                    .OrderByDescending(kvp => kvp.Value.Uniqueness) // Order by descending uniqueness
                    .ThenBy(kvp => kvp.Key.Count) // Then by ascending number of elements
                    .Take(_totalUniqueKeysSought) // Take up to 'ReturnTotalCombinations'
                    .Select(kvp => new KeyDetectorResult // Transform each KeyValuePair into a KeyDetectorResult
                    {
                        // Set properties according to your context
                        DetectedKey = string.Join(", ", kvp.Key.Select(col => "[" + col + "]")),
                        ColumnCountInKey = kvp.Key.Count,
                        NumericColumnCountInKey = kvp.Value.NumericCount,
                        ProofQueryExecuted = false
                    });

                // Convert the IEnumerable<KeyDetectorResult> to a ConcurrentBag<KeyDetectorResult>
                ConcurrentBag<KeyDetectorResult> results = new ConcurrentBag<KeyDetectorResult>(queryResult);

                if (earlyExitOnFound)
                {
                    if (results.Count >= 1)
                    {
                        return results;
                    }
                }
            }

            // Phase 4: Combine pre-filtered columns with remaining columns incrementally up to a maximum combination size of 4
            if (_hasRequiredCombinations == 0)
            {
                // Start with combinations of size 2 (since single columns and pairs have likely been tried already)
                for (int i = 2; i <= _maxKeyCombinationSize && _hasRequiredCombinations == 0; i++)
                {
                    // Generate all possible combinations of the given size.
                    foreach (var baseColumn in likelyUniqueColumns)
                    {
                        if (_hasRequiredCombinations == 1) break;

                        var otherColumns = columns.Except(new List<string> { baseColumn }).ToList();
                        var additionalCombinations = GetCombinations(otherColumns, i - 1);

                        foreach (var additionalCombination in additionalCombinations)
                        {
                            if (_hasRequiredCombinations == 1) break;

                            var newCombination = new List<string> { baseColumn };
                            newCombination.AddRange(additionalCombination);
                            AttemptToAddCombination(table, allCombinations, newCombination);
                        }
                    }
                }
            }

            // Final Phase: If no combinations found, return best attempts
            IEnumerable<KeyDetectorResult> finalResult = allCombinations
                .Where(kvp => kvp.Value.Uniqueness == 1) // Filter to include only fully unique combinations
                .OrderByDescending(kvp => kvp.Value.NumericCount) // Order by descending NumericCount
                .OrderByDescending(kvp => kvp.Value.Uniqueness) // Order by descending uniqueness
                .ThenBy(kvp => kvp.Key.Count) // Then by ascending number of elements
                .Take(_totalUniqueKeysSought) // Take up to 'ReturnTotalCombinations'
                .Select(kvp => new KeyDetectorResult // Transform each KeyValuePair into a KeyDetectorResult
                {
                    // Set properties according to your context
                    DetectedKey = string.Join(", ", kvp.Key.Select(col => "[" + col + "]")),
                    ColumnCountInKey = kvp.Key.Count,
                    NumericColumnCountInKey = kvp.Value.NumericCount,
                    ProofQueryExecuted = false
                });

            // Convert the IEnumerable<KeyDetectorResult> to a ConcurrentBag<KeyDetectorResult>
            ConcurrentBag<KeyDetectorResult> fResult = new ConcurrentBag<KeyDetectorResult>(finalResult);

            return fResult;
        }

        private static void AttemptToAddCombination(DataTable table, ConcurrentDictionary<List<string>, (double Uniqueness, int NumericCount)> allCombinations, List<string> combination)
        {
            var (uniqueness, numericCount) = CalculateUniquenessWithHash(table, combination);
            _logWriter.Write($"Column(s): {string.Join(", ", combination)} | Uniqueness: {uniqueness} | Numeric Columns: {numericCount}{Environment.NewLine}");
            _logWriter.Flush();
            bool added = allCombinations.TryAdd(combination, (uniqueness, numericCount));
            CheckAndUpdateRequiredCombinations(allCombinations, added, uniqueness);

        }

        private static IEnumerable<List<string>> GetCombinations(List<string> list, int length)
        {
            if (length == 1) return list.Select(t => new List<string> { t });

            return GetCombinations(list, length - 1)
                .SelectMany(t => list.Where(e => !t.Contains(e)),
                            (t1, t2) => new List<string>(t1) { t2 });
        }

        private static (double Uniqueness, int NumericCount) CalculateUniquenessWithHash(DataTable table, List<string> columns, int sampleSize = 20)
        {
            var uniqueHashes = new ConcurrentDictionary<string, bool>();
            var numericCounts = new int[columns.Count];
            int totalRows = 0;

            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = _cancellationTokenSource.Token
            };

            try
            {
                Parallel.ForEach(table.AsEnumerable(), parallelOptions, (row, state, index) =>
                {
                    if (parallelOptions.CancellationToken.IsCancellationRequested)
                    {
                        state.Break();
                        return;
                    }

                    Interlocked.Increment(ref totalRows);
                    var columnData = new List<string>();
                    for (int i = 0; i < columns.Count; i++)
                    {
                        var columnValue = row[columns[i]]?.ToString() ?? string.Empty;
                        if (index < sampleSize && double.TryParse(columnValue, out _))
                        {
                            Interlocked.Increment(ref numericCounts[i]);
                        }
                        columnData.Add(columnValue);
                    }
                    string combinedRowValues = string.Join("|", columnData);
                    string hash = GetHashString(combinedRowValues);
                    uniqueHashes.TryAdd(hash, true);
                });
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation
            }

            int totalNumericColumns = numericCounts.Count(n => n > sampleSize / 2);
            return (totalRows > 0 ? (double)uniqueHashes.Count / totalRows : 0.0, totalNumericColumns);
        }

        private static string GetHashString(string input)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] data = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
                StringBuilder sBuilder = new StringBuilder();
                foreach (byte b in data)
                {
                    sBuilder.Append(b.ToString("x2"));
                }
                return sBuilder.ToString();
            }
        }

        private static bool CheckAndUpdateRequiredCombinations(
            ConcurrentDictionary<List<string>, (double Uniqueness, int NumericCount)> allCombinations,
            bool added,
            double uniqueness)
        {
            if (added && uniqueness == 1)
            {
                if (allCombinations.Count(kvp => kvp.Value.Uniqueness == 1) >= _totalUniqueKeysSought)
                {
                    if (Interlocked.CompareExchange(ref _hasRequiredCombinations, 1, 0) == 0)
                    {
                        _cancellationTokenSource.Cancel(); // Signal all threads to stop
                        return true; // Indicate that required combinations are met
                    }
                }
            }
            return false; // Indicate that required combinations are not yet met
        }

        // Custom comparer for list of strings to use in ConcurrentDictionary
        private class ListStringComparer : IEqualityComparer<List<string>>
        {
            public bool Equals(List<string> x, List<string> y)
            {
                // Check if both lists are null or reference the same object
                if (ReferenceEquals(x, y)) return true;

                // Check if either list is null
                if (x == null || y == null) return false;

                // Check if lists have the same count of elements
                if (x.Count != y.Count) return false;

                // Use SortedSet to ignore the order of elements
                var setX = new SortedSet<string>(x);
                var setY = new SortedSet<string>(y);

                // Check if both sets contain the same elements
                return setX.SetEquals(setY);
            }

            public int GetHashCode(List<string> obj)
            {
                if (obj == null) return 0;

                unchecked // Overflow is fine, just wrap
                {
                    int hash = 19;
                    // Use SortedSet to ignore the order of elements
                    var sortedSet = new SortedSet<string>(obj);

                    foreach (var str in sortedSet)
                    {
                        hash = hash * 31 + str.GetHashCode();
                    }
                    return hash;
                }
            }
        }
    }



}
