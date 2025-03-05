using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Threading.Tasks;

namespace SQLFlowCore.Common
{
    internal class FlattenDataset
    {
        private readonly DataSet _dataSet;
        private readonly List<DataTable> _selectedTables;

        internal FlattenDataset(DataSet dataSet, string hierarchyIdentifier)
        {
            _dataSet = dataSet ?? throw new ArgumentNullException(nameof(dataSet));

            // Special case for single table with no relations
            if (dataSet.Tables.Count == 1 && dataSet.Relations.Count == 0)
            {
                _selectedTables = new List<DataTable> { dataSet.Tables[0] };
                return;
            }

            var tableGroups = GenerateTableGroups(dataSet);
            int groupIndex = FindGroupIndex(hierarchyIdentifier, tableGroups);
            _selectedTables = groupIndex != -1 ? tableGroups[groupIndex] : null;
        }

        internal DataTable FlattenDataSet()
        {
            if (_dataSet == null || _dataSet.Tables.Count == 0)
            {
                throw new ArgumentException("DataSet is empty.");
            }

            if (_selectedTables == null || _selectedTables.Count == 0)
            {
                throw new ArgumentException("No tables were selected for flattening.");
            }

            DataTable baseTable = _selectedTables[0];

            // If we only have one table, just return it regardless of relations
            if (_selectedTables.Count == 1)
            {
                return baseTable.Copy();
            }

            // Only check for relations if we have multiple tables
            if (_dataSet.Relations.Count == 0 && _selectedTables.Count > 1)
            {
                throw new ArgumentException("DataSet has multiple tables but no relations between them.");
            }

            return PerformTableJoins();
        }

        private DataTable PerformTableJoins()
        {
            // Create result table with unique column names
            DataTable resultTable = new DataTable("JoinedTable");
            // Changed to store (DataTable, ColumnName) -> UniqueColumnName
            Dictionary<(DataTable, string), string> columnMappings = PrepareResultTableColumns(resultTable);

            // If only one table selected, just copy its data
            if (_selectedTables.Count == 1)
            {
                CopyDataToResultTable(resultTable, _selectedTables[0], columnMappings);
                return resultTable;
            }

            // Optimize the join order based on relationship types and table sizes
            List<DataTable> optimizedJoinOrder = OptimizeJoinOrder();

            // Build join path
            var joinPaths = BuildJoinPaths(optimizedJoinOrder);

            // Execute optimized joins
            ExecuteJoins(resultTable, joinPaths, columnMappings);

            return resultTable;
        }

        /// <summary>
        /// Prepare the result table columns, generating unique column names
        /// and storing those mappings in a dictionary that can be referenced later.
        /// </summary>
        /// <summary>
        /// Prepare the result table columns, generating unique column names
        /// using a table_column format for all columns to prevent collisions.
        /// </summary>
        private Dictionary<(DataTable, string), string> PrepareResultTableColumns(DataTable resultTable)
        {
            var columnMappings = new Dictionary<(DataTable, string), string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataTable table in _selectedTables)
            {
                // Always use table_column format regardless of number of tables
                string tablePrefix = table.TableName + "_";

                foreach (DataColumn column in table.Columns)
                {
                    string baseName = column.ColumnName;
                    string proposedName = tablePrefix + baseName;

                    // Ensure uniqueness by adding a numeric suffix as needed
                    string uniqueName = proposedName;
                    int suffix = 1;
                    while (usedNames.Contains(uniqueName))
                    {
                        uniqueName = proposedName + "_" + suffix;
                        suffix++;
                    }

                    usedNames.Add(uniqueName);
                    columnMappings[(table, column.ColumnName)] = uniqueName;
                    resultTable.Columns.Add(uniqueName, column.DataType);
                }
            }

            return columnMappings;
        }

        /// <summary>
        /// Copy data from a single source table directly into the result table,
        /// using the provided column name mappings.
        /// </summary>
        private void CopyDataToResultTable(
            DataTable resultTable,
            DataTable sourceTable,
            Dictionary<(DataTable, string), string> columnMappings)
        {
            foreach (DataRow sourceRow in sourceTable.Rows)
            {
                DataRow newRow = resultTable.NewRow();

                foreach (DataColumn column in sourceTable.Columns)
                {
                    string mappedName = columnMappings[(sourceTable, column.ColumnName)];
                    newRow[mappedName] = sourceRow[column];
                }

                resultTable.Rows.Add(newRow);
            }
        }

        /// <summary>
        /// Optimize the order in which tables are joined,
        /// e.g., starting from the smallest table if possible.
        /// </summary>
        private List<DataTable> OptimizeJoinOrder()
        {
            // Simple optimization: Start with the smallest table and continue
            var orderedTables = new List<DataTable>();
            var remainingTables = new HashSet<DataTable>(_selectedTables);

            DataTable currentTable = _selectedTables
                .OrderBy(t => t.Rows.Count)
                .First();

            orderedTables.Add(currentTable);
            remainingTables.Remove(currentTable);

            while (remainingTables.Count > 0)
            {
                bool foundRelated = false;
                DataTable relatedTable = null;

                foreach (var table in remainingTables)
                {
                    if (HasDirectRelation(_dataSet, currentTable, table))
                    {
                        relatedTable = table;
                        foundRelated = true;
                        break;
                    }
                }

                if (foundRelated)
                {
                    orderedTables.Add(relatedTable);
                    remainingTables.Remove(relatedTable);
                    currentTable = relatedTable;
                }
                else
                {
                    // If no direct relation, pick the next available
                    relatedTable = remainingTables.First();
                    orderedTables.Add(relatedTable);
                    remainingTables.Remove(relatedTable);
                    currentTable = relatedTable;
                }
            }

            return orderedTables;
        }

        /// <summary>
        /// Build a list of join paths (parent/child) based on the optimized join order.
        /// </summary>
        private List<(DataTable parentTable, DataColumn parentColumn,
                      DataTable childTable, DataColumn childColumn)>
            BuildJoinPaths(List<DataTable> joinOrder)
        {
            var joinPaths = new List<(DataTable, DataColumn, DataTable, DataColumn)>();

            for (int i = 0; i < joinOrder.Count - 1; i++)
            {
                DataTable currentTable = joinOrder[i];
                DataTable nextTable = joinOrder[i + 1];

                DataRelation relation = FindRelation(_dataSet, currentTable, nextTable);
                if (relation == null)
                {
                    // Try indirect relations
                    foreach (var table in _selectedTables)
                    {
                        if (table != currentTable && table != nextTable)
                        {
                            var relation1 = FindRelation(_dataSet, currentTable, table);
                            var relation2 = FindRelation(_dataSet, table, nextTable);

                            if (relation1 != null && relation2 != null)
                            {
                                // Found an indirect path
                                joinPaths.Add((relation1.ParentTable,
                                               relation1.ParentColumns[0],
                                               relation1.ChildTable,
                                               relation1.ChildColumns[0]));

                                joinPaths.Add((relation2.ParentTable,
                                               relation2.ParentColumns[0],
                                               relation2.ChildTable,
                                               relation2.ChildColumns[0]));
                                goto nextIteration;
                            }
                        }
                    }

                    throw new InvalidOperationException(
                        $"No relation found between {currentTable.TableName} and {nextTable.TableName}."
                    );
                }

                joinPaths.Add((relation.ParentTable,
                               relation.ParentColumns[0],
                               relation.ChildTable,
                               relation.ChildColumns[0]));

            nextIteration:;
            }

            return joinPaths;
        }

        /// <summary>
        /// Execute the actual joins and populate the rows in the result table.
        /// Now consistently uses the columnMappings dictionary for both reading and writing.
        /// </summary>
        private void ExecuteJoins(
            DataTable resultTable,
            List<(DataTable parentTable, DataColumn parentColumn,
                  DataTable childTable, DataColumn childColumn)> joinPaths,
            Dictionary<(DataTable, string), string> columnMappings)
        {
            // Create a fast lookup of columnName -> index in resultTable
            var columnIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < resultTable.Columns.Count; i++)
            {
                columnIndexMap[resultTable.Columns[i].ColumnName] = i;
            }

            // We will keep track of rows in resultTable that correspond to a join key
            var indexedData = new Dictionary<object, List<DataRow>>();

            // Start by loading data from the first table
            DataTable firstTable = _selectedTables[0];
            foreach (DataRow row in firstTable.Rows)
            {
                DataRow newRow = resultTable.NewRow();

                // Fill data from the first table
                foreach (DataColumn col in firstTable.Columns)
                {
                    string mappedName = columnMappings[(firstTable, col.ColumnName)];
                    if (columnIndexMap.TryGetValue(mappedName, out int colIndex))
                    {
                        newRow[colIndex] = row[col];
                    }
                }

                resultTable.Rows.Add(newRow);

                // Index this new row by join key(s) if firstTable is a parent in any path
                foreach (var path in joinPaths.Where(p => p.parentTable == firstTable))
                {
                    object key = row[path.parentColumn];
                    if (key != null && key != DBNull.Value)
                    {
                        if (!indexedData.TryGetValue(key, out var rowList))
                        {
                            rowList = new List<DataRow>();
                            indexedData[key] = rowList;
                        }
                        rowList.Add(newRow);
                    }
                }
            }

            // Join remaining tables
            for (int i = 1; i < _selectedTables.Count; i++)
            {
                DataTable tableToJoin = _selectedTables[i];
                var joinPathsForTable = joinPaths
                    .Where(p => p.childTable == tableToJoin)
                    .ToList();

                if (joinPathsForTable.Any())
                {
                    // For each relevant join path, populate the matching columns
                    foreach (var path in joinPathsForTable)
                    {
                        foreach (DataRow childRow in tableToJoin.Rows)
                        {
                            object key = childRow[path.childColumn];
                            if (key != null && key != DBNull.Value && indexedData.TryGetValue(key, out var matchedRows))
                            {
                                foreach (DataRow resultRow in matchedRows)
                                {
                                    // Copy child table columns into the matching rows
                                    foreach (DataColumn col in tableToJoin.Columns)
                                    {
                                        string mappedName = columnMappings[(tableToJoin, col.ColumnName)];
                                        if (columnIndexMap.TryGetValue(mappedName, out int colIndex))
                                        {
                                            resultRow[colIndex] = childRow[col];
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private bool HasDirectRelation(DataSet ds, DataTable table1, DataTable table2)
        {
            return FindRelation(ds, table1, table2) != null;
        }

        private static DataRelation FindRelation(DataSet ds, DataTable table1, DataTable table2)
        {
            return ds.Relations
                     .Cast<DataRelation>()
                     .FirstOrDefault(r =>
                        (r.ParentTable == table1 && r.ChildTable == table2) ||
                        (r.ParentTable == table2 && r.ChildTable == table1));
        }

        private static List<List<DataTable>> GenerateTableGroups(DataSet dataSet)
        {
            var tableGroups = new List<List<DataTable>>();
            var visitedTables = new HashSet<DataTable>();

            foreach (DataTable table in dataSet.Tables)
            {
                if (!visitedTables.Contains(table))
                {
                    var group = new List<DataTable>();
                    AddTableAndDependencies(dataSet, table, group, visitedTables);
                    tableGroups.Add(group);
                }
            }

            return tableGroups;
        }

        private static void AddTableAndDependencies(
            DataSet dataSet,
            DataTable table,
            List<DataTable> currentGroup,
            HashSet<DataTable> visitedTables)
        {
            if (!visitedTables.Contains(table))
            {
                currentGroup.Add(table);
                visitedTables.Add(table);

                // Add child tables recursively
                foreach (DataRelation relation in dataSet.Relations)
                {
                    if (relation.ParentTable == table)
                    {
                        AddTableAndDependencies(dataSet, relation.ChildTable, currentGroup, visitedTables);
                    }
                }
            }
        }

        private static int FindGroupIndex(string tableName, List<List<DataTable>> tableGroups)
        {
            for (int i = 0; i < tableGroups.Count; i++)
            {
                foreach (var table in tableGroups[i])
                {
                    if (table.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }
    }
}
