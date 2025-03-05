using Newtonsoft.Json.Linq;
using System;
using System.Data;
using System.Linq;

namespace SQLFlowCore.Services.Json
{
    /// <summary>
    /// Provides methods for parsing JSON data into a DataTable.
    /// </summary>
    internal static class JsonParser
    {
        /// <summary>
        /// Parses a JSON string into a DataTable.
        /// </summary>
        /// <param name="json">The JSON string to parse.</param>
        /// <returns>A DataTable representing the JSON data.</returns>
        internal static DataTable ParseJsonFileToDataTable(string json)
        {
            // Read the JSON file and parse it into a JArray
            var jArray = JArray.Parse(json);

            // Create the DataTable
            var dataTable = new DataTable();

            if (jArray.Count == 0)
                return dataTable;

            // Process each item in the JArray
            foreach (var item in jArray)
            {
                // Create a new row for this item
                var row = dataTable.NewRow();

                // Process the item
                ProcessToken(item, row, dataTable, null);

                // Add the row to the DataTable
                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        /// <summary>
        /// Processes a JToken and adds its data to a DataRow in a DataTable.
        /// </summary>
        /// <param name="token">The JToken to process.</param>
        /// <param name="row">The DataRow to add data to.</param>
        /// <param name="dataTable">The DataTable that the DataRow belongs to.</param>
        /// <param name="parentColumnName">The name of the parent column, if any.</param>
        private static void ProcessToken(JToken token, DataRow row, DataTable dataTable, string parentColumnName)
        {
            // Handle nested objects/arrays
            if (token.HasValues)
            {
                foreach (var child in token.Children())
                {
                    var childName = GetChildName(child);

                    // Concatenate the parent and child names (if they both exist)
                    var columnName = string.IsNullOrWhiteSpace(parentColumnName) ? childName : $"{parentColumnName}_{childName}";

                    ProcessToken(child, row, dataTable, columnName);
                }
            }
            // Handle values
            else
            {
                var columnName = parentColumnName ?? "Value";
                var value = ((JValue)token).Value;

                // Add the column if it doesn't exist
                if (!dataTable.Columns.Contains(columnName))
                    dataTable.Columns.Add(columnName);

                // Set the value in the DataRow
                row[columnName] = value ?? DBNull.Value;
            }
        }

        /// <summary>
        /// Gets the name of a child JToken.
        /// </summary>
        /// <param name="child">The child JToken to get the name of.</param>
        /// <returns>The name of the child JToken.</returns>
        private static string GetChildName(JToken child)
        {
            if (child is JProperty propChild)
                return propChild.Name;

            return child.Path.Split('.').Last();
        }
    }
}
