using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public class UniquenessEstimator
    {
        // Estimates the uniqueness of combinations of columns in a DataTable.
        public static ConcurrentDictionary<string, double> EstimateUniqueness(DataTable table, List<string> nonRedundantColumns)
        {
            int totalRows = table.Rows.Count;

            // Systematically consider all rows from the table instead of a sample
            var allRows = table;

            // ConcurrentDictionary to hold the uniqueness estimates for each combination
            var uniquenessEstimates = new ConcurrentDictionary<string, double>();

            Parallel.ForEach(nonRedundantColumns, combo =>
            {
                try
                {
                    // 'combo' is a single column name, so use it directly without iterating over it
                    string comboKey = combo; // Since combo is just a single column name

                    // Check how many unique rows there are for this column
                    var uniqueRows = new HashSet<string>(
                        allRows.AsEnumerable()
                            .Select(row =>
                            {
                                // Safeguard against missing columns or null values
                                if (!allRows.Columns.Contains(combo) || row[combo] == DBNull.Value)
                                    return "NULL"; // Or some other placeholder for missing data
                                else
                                    return row[combo].ToString();
                            })
                    );

                    // Calculate the uniqueness of this column in the whole set
                    double uniqueness = totalRows > 0 ? (double)uniqueRows.Count / totalRows : 0;

                    // If all values are the same, uniqueness is 0, otherwise calculate normally
                    uniqueness = uniqueRows.Count == 1 ? 0 : uniqueness;
                    uniquenessEstimates[comboKey] = uniqueness;
                }
                catch (Exception)
                {
                    throw; // Rethrow the exception to maintain the original error behavior
                }
            });

            // Return the ConcurrentDictionary directly as it holds the uniqueness estimates
            return uniquenessEstimates;
        }



    }

}
