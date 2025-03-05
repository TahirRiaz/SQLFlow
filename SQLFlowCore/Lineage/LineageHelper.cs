using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;


namespace SQLFlowCore.Lineage
{
    public static class LineageHelper
    {

        internal static string CombineBatchValues(DataTable lineageTable)
        {
            HashSet<string> uniqueBatches = new HashSet<string>();

            foreach (DataRow row in lineageTable.Rows)
            {
                if (row["Batch"] != DBNull.Value)
                {
                    uniqueBatches.Add(row["Batch"]?.ToString() ?? string.Empty);
                }
            }

            return string.Join(",", uniqueBatches);
        }

        internal static string ShortenObjectName(string name)
        {
            // CheckForError for regex special characters
            if (name.IndexOfAny(new char[] { '^', '$', '*', '+', '?', '|', '(', ')', '[', ']', '{', '}', '\\' }) > -1)
            {
                return name;
            }

            // Split on the period to separate database, schema, and table
            string[] parts = name.Split('.');

            // CheckForError if it's in format database.schema.table
            if (parts.Length == 3)
            {
                return parts[1] + "." + parts[2];  // Return schema.table
            }

            // CheckForError if it's in format schema.table
            else if (parts.Length == 2)
            {
                return name;  // Since it's already in the format schema.table
            }

            // CheckForError if it looks like a path (contains slashes)
            else if (name.Contains("/") || name.Contains("\\"))
            {
                string fileName = Path.GetFileName(name);

                // Return the name if FileName doesn't extract correctly.
                return string.IsNullOrEmpty(fileName) ? name : fileName;
            }

            // If it's just a file name or doesn't match other patterns
            else
            {
                return name;
            }
        }

        internal static DataTable GetRowsByFromObjectMKList(DataTable table, List<int> fromObjectMKList)
        {
            // Create a new DataTable to hold the filtered rows
            DataTable filteredTable = table.Clone(); // Clone structure of original table, no data

            foreach (DataRow row in table.Rows)
            {
                int fromObjectMK = Convert.ToInt32(row["FromObjectMK"]);

                if (fromObjectMKList.Contains(fromObjectMK))
                {
                    DataRow newRow = filteredTable.NewRow();
                    newRow.ItemArray = row.ItemArray; // Copy data
                    filteredTable.Rows.Add(newRow);
                }
            }

            // Sort the filteredTable by the "Step" column
            DataView view = filteredTable.DefaultView;
            view.Sort = "Step ASC";  // ASC for ascending, DESC for descending
            DataTable sortedTable = view.ToTable();

            return filteredTable;
        }

        internal static int GetLowestLevelForFlowId(DataTable lineageTable, int flowId)
        {
            int lowestLevel = -1;
            bool found = false;
            foreach (DataRow row in lineageTable.Rows)
            {
                if ((int)row["FlowID"] == flowId)
                {
                    found = true;
                    int currentLevel = (int)row["Level"];
                    if (lowestLevel == -1 || currentLevel < lowestLevel)
                    {
                        lowestLevel = currentLevel;
                    }
                }
            }
            return found ? lowestLevel : -1;  // Return -1 if not found
        }

        internal static int GetHighestLevelForFlowId(DataTable lineageTable, int flowId)
        {
            int highestLevel = -1;
            bool found = false;
            foreach (DataRow row in lineageTable.Rows)
            {
                if ((int)row["FlowID"] == flowId)
                {
                    found = true;
                    int currentLevel = (int)row["Level"];
                    if (highestLevel == -1 || currentLevel > highestLevel)
                    {
                        highestLevel = currentLevel;
                    }
                }
            }
            return found ? highestLevel : -1;  // Return -1 if not found
        }
        internal static string GetFlowExecutionOrder(DataTable lineageTable)
        {
            var sortedFlows = from row in lineageTable.AsEnumerable()
                              group row by row.Field<int>("FlowID") into flowGroup
                              select new { FlowID = flowGroup.Key, MaxLevel = flowGroup.Max(r => r.Field<int>("Level")) }
                              into flow
                              orderby flow.MaxLevel
                              select flow.FlowID;

            return string.Join(" -> ", sortedFlows);
        }

        internal static DataTable FindFlowExecutionOrder(DataTable lineageTable)
        {
            DataTable executionSteps = new DataTable();
            executionSteps.Columns.Add("FlowID", typeof(int));
            executionSteps.Columns.Add("Step", typeof(int));

            Dictionary<int, List<int>> dependencies = new Dictionary<int, List<int>>();
            HashSet<int> flows = new HashSet<int>();
            Dictionary<int, int> levels = new Dictionary<int, int>();

            // ... Populate dependencies and flows as before ...

            foreach (DataRow row in lineageTable.Rows)
            {
                int flowID = (int)row["FlowID"];
                flows.Add(flowID);
                // Other code for building dependencies
            }

            HashSet<int> visited = new HashSet<int>();
            Stack<int> stack = new Stack<int>();
            int level = 0;

            foreach (int flowID in flows)
            {
                if (!visited.Contains(flowID))
                {
                    TopologicalSort(flowID, visited, stack, dependencies, levels, level);
                }
            }

            foreach (int flowID in stack)
            {
                executionSteps.Rows.Add(flowID, levels[flowID]);
            }

            return executionSteps;
        }

        internal static void TopologicalSort(int flowID, HashSet<int> visited, Stack<int> stack, Dictionary<int, List<int>> dependencies, Dictionary<int, int> levels, int level)
        {
            visited.Add(flowID);
            levels[flowID] = level;

            if (dependencies.ContainsKey(flowID))
            {
                foreach (int depFlowID in dependencies[flowID])
                {
                    if (!visited.Contains(depFlowID))
                    {
                        TopologicalSort(depFlowID, visited, stack, dependencies, levels, level + 1);
                    }
                    else
                    {
                        levels[flowID] = Math.Max(levels[flowID], levels[depFlowID] + 1);
                    }
                }
            }

            stack.Push(flowID);
        }


        /// <summary>
        /// Gets the maximum step per FlowID from the provided DataTable.
        /// </summary>
        /// <param name="baseTable">The DataTable containing the base data.</param>
        /// <returns>A DataTable with the maximum step per FlowID.</returns>
        /// <remarks>
        /// This method groups the data by FlowID and selects the maximum step for each group. It also includes other columns such as FlowType, SourceIsAzCont, and DeactivateFromBatch from the first row of each group.
        /// </remarks>
        public static DataTable GetMaxStepPerFlowID(DataTable baseTable)
        {
            var resultTable = new DataTable();
            resultTable.Columns.Add("FlowID", typeof(int));
            resultTable.Columns.Add("FlowType", typeof(string));
            resultTable.Columns.Add("SourceIsAzCont", typeof(bool)); // Assuming this is a boolean column (wasn't provided in the initial table structure)
            resultTable.Columns.Add("Step", typeof(int));
            resultTable.Columns.Add("DeactivateFromBatch", typeof(bool));

            var groupedData = from row in baseTable.AsEnumerable()
                              group row by row.Field<int>("FlowID") into grp
                              select new
                              {
                                  FlowID = grp.Key,
                                  FlowType = grp.First().Field<string>("FlowType"),
                                  SourceIsAzCont = grp.First().Field<bool>("SourceIsAzCont"), // Assuming the Circular column is equivalent to the SourceIsAzCont
                                  MaxStep = grp.Max(r => r.Field<int>("Step")),
                                  DeactivateFromBatch = grp.First().Field<bool>("DeactivateFromBatch"),
                              };

            foreach (var item in groupedData)
            {
                resultTable.Rows.Add(item.FlowID, item.FlowType, item.SourceIsAzCont, item.MaxStep, item.DeactivateFromBatch);
            }

            return resultTable;
        }
    }
}
