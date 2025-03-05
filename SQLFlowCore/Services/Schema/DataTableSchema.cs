using System;
using System.Data;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents a schema for a DataTable.
    /// </summary>
    internal class DataTableSchema
    {
        /// <summary>
        /// Checks if a column exists in a DataTable, ignoring case.
        /// </summary>
        /// <param name="table">The DataTable to check.</param>
        /// <param name="columnName">The name of the column to find.</param>
        /// <returns>True if the column exists, false otherwise.</returns>
        internal static bool DoesColumnExistCaseInsensitive(DataTable table, string columnName)
        {
            foreach (DataColumn column in table.Columns)
            {
                if (string.Equals(column.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

    }
}
