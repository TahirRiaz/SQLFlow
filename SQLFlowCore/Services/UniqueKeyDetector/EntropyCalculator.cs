using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks; // Add this for Parallel.ForEach

namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public class EntropyCalculator
    {
        public static Dictionary<string, double> CalculateEntropyForColumns(DataTable table)
        {
            // Object to lock updates to the dictionary is mentioned but not used since we use ConcurrentDictionary
            // Initialize a dictionary to hold entropy values for each column
            var entropyResults = new ConcurrentDictionary<string, double>();

            try
            {
                // Use Parallel.ForEach for parallel processing of columns
                Parallel.ForEach(table.Columns.Cast<DataColumn>(), column =>
                {
                    var counts = new Dictionary<string, int>();
                    foreach (DataRow row in table.Rows)
                    {
                        string value = row[column].ToString();
                        // No need for locking here since each thread has its own 'counts' dictionary
                        if (counts.ContainsKey(value))
                        {
                            counts[value]++;
                        }
                        else
                        {
                            counts[value] = 1;
                        }
                    }

                    double entropy = 0.0;
                    int total = table.Rows.Count;
                    foreach (var pair in counts)
                    {
                        double probability = (double)pair.Value / total;
                        entropy -= probability * Math.Log(probability, 2); // Using base 2 for Shannon entropy
                    }

                    // Directly update the ConcurrentDictionary, no lock needed here
                    entropyResults[column.ColumnName] = entropy;
                });
            }
            catch (Exception ex)
            {
                throw new Exception("Error in EntropyCalculator.CalculateEntropyForColumns: " + ex.Message);
            }


            return new Dictionary<string, double>(entropyResults);
        }


    }

}
