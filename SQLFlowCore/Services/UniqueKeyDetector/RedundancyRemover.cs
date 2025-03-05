using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public static class RedundancyRemover
    {
        public static List<string> EliminateRedundantColumns(DataTable table, Dictionary<string, double> columnMetrics, double redundantColSimilarityThreshold = 0.95)
        {
            var columnRedundancy = new ConcurrentDictionary<string, string>();

            // Parallel iteration to determine redundant columns.
            Parallel.ForEach(columnMetrics, metric =>
            {
                var columns = metric.Key.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length == 2)
                {
                    var columnName1 = columns[0].Trim();
                    var columnName2 = columns[1].Trim();

                    // Check if similarity exceeds the threshold.
                    if (metric.Value >= redundantColSimilarityThreshold)
                    {
                        // Choose the redundant column (using alphabetical order here).
                        var redundantColumn = string.Compare(columnName1, columnName2, StringComparison.InvariantCultureIgnoreCase) > 0
                            ? columnName1
                            : columnName2;

                        // Mark as redundant if not already marked.
                        columnRedundancy.TryAdd(redundantColumn, redundantColumn == columnName1 ? columnName2 : columnName1);
                    }
                }
            });

            // Build the final list excluding redundant columns.
            return table.Columns.Cast<DataColumn>()
                .Select(c => c.ColumnName)
                .Where(c => !columnRedundancy.ContainsKey(c))
                .ToList();
        }
    }

}
