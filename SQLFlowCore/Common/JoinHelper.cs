using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SQLFlowCore.Common
{
    internal class JoinCondition
    {
        internal int FirstTableIndex { get; set; }
        internal List<string> FirstTableKeys { get; set; }
        internal int SecondTableIndex { get; set; }
        internal List<string> SecondTableKeys { get; set; }
    }

    internal class JoinHelper
    {
        internal static List<JoinCondition> CreateJoinConditionsFromDataSet(DataSet ds)
        {
            var joinConditions = new List<JoinCondition>();

            foreach (DataRelation relation in ds.Relations)
            {
                var joinCondition = new JoinCondition
                {
                    FirstTableIndex = ds.Tables.IndexOf(relation.ParentTable),
                    FirstTableKeys = relation.ParentColumns.Select(c => c.ColumnName).ToList(),
                    SecondTableIndex = ds.Tables.IndexOf(relation.ChildTable),
                    SecondTableKeys = relation.ChildColumns.Select(c => c.ColumnName).ToList()
                };

                joinConditions.Add(joinCondition);
            }

            // Additional logic to detect and handle many-to-many relationships
            foreach (DataTable table in ds.Tables)
            {
                if (IsJunctionTable(table, ds))
                {
                    // Logic to handle many-to-many relationships
                    // This is a simplified placeholder. Actual implementation would require specific logic based on your schema.
                    HandleManyToMany(table, ds, joinConditions);
                }
            }

            return joinConditions;
        }

        private static bool IsJunctionTable(DataTable table, DataSet ds)
        {
            // A junction table is unlikely to have many columns
            if (table.Columns.Count < 2 || table.Columns.Count > 5)
            {
                return false;
            }

            int matchingTablesCount = 0;

            // CheckForError for common column names with other tables
            foreach (var otherTable in ds.Tables.Cast<DataTable>())
            {
                if (otherTable == table)
                {
                    continue; // Skip the same table
                }

                bool hasCommonColumn = table.Columns.Cast<DataColumn>()
                    .Any(jtColumn => otherTable.Columns.Contains(jtColumn.ColumnName));

                if (hasCommonColumn)
                {
                    matchingTablesCount++;
                }
            }

            // Assuming a junction table will have common columns with at least two other tables
            return matchingTablesCount >= 2;
        }

        private static void HandleManyToMany(DataTable junctionTable, DataSet ds, List<JoinCondition> joinConditions)
        {
            foreach (DataTable table in ds.Tables)
            {
                if (table == junctionTable)
                {
                    continue; // Skip the junction table itself
                }

                var commonColumns = junctionTable.Columns
                    .Cast<DataColumn>()
                    .Where(jtColumn => table.Columns.Contains(jtColumn.ColumnName))
                    .Select(col => col.ColumnName)
                    .ToList();

                if (commonColumns.Any())
                {
                    // Add join condition for the junction table with this related table
                    joinConditions.Add(new JoinCondition
                    {
                        FirstTableIndex = ds.Tables.IndexOf(junctionTable),
                        FirstTableKeys = commonColumns,
                        SecondTableIndex = ds.Tables.IndexOf(table),
                        SecondTableKeys = commonColumns
                    });
                }
            }
        }
    }
}
