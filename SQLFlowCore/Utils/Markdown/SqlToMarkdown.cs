using System.Data;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.RegularExpressions;
using SQLFlowCore.Common;

namespace SQLFlowCore.Engine.Utils.Markdown
{
    /// <summary>
    /// Provides methods to convert SQL data to Markdown format.
    /// </summary>
    internal static class SqlToMarkdown
    {
        /// <summary>
        /// Converts a SQL table to a Markdown string.
        /// </summary>
        /// <param name="sqlFlow">The SQL connection to use.</param>
        /// <param name="cmdSQL">The SQL command to execute.</param>
        /// <returns>A string in Markdown format representing the SQL table.</returns>
        public static string ConvertTableToMarkdown(SqlConnection sqlFlow, string cmdSQL)
        {
            // Fetch the data from the database
            DataTable dataTable = CommonDB.GetData(sqlFlow, cmdSQL, 360);

            // Convert the DataTable to Markdown
            return ConvertDataTableToMarkdown(dataTable);
        }

        /// <summary>
        /// Converts a DataTable to a Markdown string.
        /// </summary>
        /// <param name="dataTable">The DataTable to convert.</param>
        /// <returns>A string in Markdown format representing the DataTable.</returns>
        private static string ConvertDataTableToMarkdown(DataTable dataTable)
        {
            var markdownBuilder = new StringBuilder();

            // Create Header
            markdownBuilder.Append("| ");
            foreach (DataColumn column in dataTable.Columns)
            {
                markdownBuilder.Append(column.ColumnName + " | ");
            }
            markdownBuilder.AppendLine();

            // Create Separator
            markdownBuilder.Append("| ");
            foreach (DataColumn column in dataTable.Columns)
            {
                markdownBuilder.Append("--- | ");
            }
            markdownBuilder.AppendLine();

            // Add Rows
            foreach (DataRow row in dataTable.Rows)
            {
                markdownBuilder.Append("| ");
                foreach (var item in row.ItemArray)
                {
                    string colValue = item.ToString();
                    // Remove all backticks
                    colValue = colValue.Replace("`", "");
                    // Encapsulate value in backticks if it contains markdown special characters
                    if (Regex.IsMatch(colValue, @"[\|\\]") || colValue.Contains("\n")) // Check for pipe or backslashes or newlines
                    {
                        colValue = $"`{colValue}`";
                    }

                    // - Replace line returns with nothing
                    // - Replace tabs and multiple spaces with a single space
                    colValue = Regex.Replace(colValue, "\r|\n", ""); // Remove backticks, carriage returns and newlines
                    colValue = Regex.Replace(colValue, @"\t| {2,}", " "); // Replace tabs and multiple spaces with a single space

                    markdownBuilder.Append(colValue + " | ");
                }
                markdownBuilder.AppendLine();
            }

            // Optionally use Markdig to further process the Markdown (if needed)
            // string markdown = Markdown.ToHtml(markdownBuilder.ToString());

            return markdownBuilder.ToString();
        }
    }
}
