using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using SQLFlowCore.Common;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Index = Microsoft.SqlServer.Management.Smo.Index;
using PropertyNotSetException = Microsoft.SqlServer.Management.Smo.PropertyNotSetException;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Provides a set of helper methods for SQL Server Management Objects (SMO) operations.
    /// </summary>
    /// <remarks>
    /// This class includes methods for table and view synchronization, column manipulation, 
    /// data type parsing, object scripting, and more. It is designed to work with the SQLFlowCore 
    /// engine schema.
    /// </remarks>
    internal class SmoHelper
    {
        internal static string CreateTableUrnFromComponents(string serverName, string databaseName, string schemaName, string tableName)
        {
            return string.Format(
                "Server[@Name='{0}']/Database[@Name='{1}']/Table[@Name='{2}' and @Schema='{3}']",
                Urn.EscapeString(serverName),
                Urn.EscapeString(databaseName),
                Urn.EscapeString(tableName),
                Urn.EscapeString(schemaName)
            );
        }

        internal static string CreateViewUrnFromComponents(string serverName, string databaseName, string schemaName, string tableName)
        {
            return string.Format(
                "Server[@Name='{0}']/Database[@Name='{1}']/View[@Name='{2}' and @Schema='{3}']",
                Urn.EscapeString(serverName),
                Urn.EscapeString(databaseName),
                Urn.EscapeString(tableName),
                Urn.EscapeString(schemaName)
            );
        }

        internal static string CreateStoredProcedureUrnFromComponents(string serverName, string databaseName, string schemaName, string procedureName)
        {
            return string.Format(
                "Server[@Name='{0}']/Database[@Name='{1}']/StoredProcedure[@Name='{2}' and @Schema='{3}']",
                Urn.EscapeString(serverName),
                Urn.EscapeString(databaseName),
                Urn.EscapeString(procedureName),
                Urn.EscapeString(schemaName)
            );
        }


        internal static SqlSmoObject GetSmoObjectFromUrn(Server srv, ServiceParam sp, string type)
        {
            SqlSmoObject smoObject = new Default();
            try
            {
                // This ensures we're using the correct server identification format that SMO expects
                string serverName = srv.Urn.Value.Split('/')[0].Replace("Server[@Name='", "").Replace("']", "");

                string objUrn = String.Empty;
                if (type == "sp")
                {
                    objUrn = SmoHelper.CreateStoredProcedureUrnFromComponents(serverName, sp.trgDatabase, sp.trgSchema,
                        sp.trgObject);
                }
                if (type == "Table")
                {
                    objUrn = SmoHelper.CreateTableUrnFromComponents(serverName, sp.trgDatabase, sp.trgSchema,
                        sp.trgObject);
                }
                if (type == "View")
                {
                    objUrn = SmoHelper.CreateViewUrnFromComponents(serverName, sp.trgDatabase, sp.trgSchema,
                        sp.trgObject);
                }
                smoObject = srv.GetSmoObject(objUrn);
            }
            catch { }

            return smoObject;
        }

        internal static Column[] ReorderColumns(ColumnCollection columns)
        {
            // Separate columns based on naming conventions
            var pkColumns = columns.Cast<Column>().Where(c => c.Name.StartsWith("PK", StringComparison.InvariantCultureIgnoreCase)).ToList();
            var pkColumns2 = columns.Cast<Column>().Where(c => c.Name.EndsWith("PK", StringComparison.InvariantCultureIgnoreCase)).ToList();
            var dwColumns = columns.Cast<Column>().Where(c => c.Name.EndsWith("_DW", StringComparison.InvariantCultureIgnoreCase)).ToList();
            var otherColumns = columns.Cast<Column>().Except(pkColumns).Except(dwColumns).ToList();

            // Combine columns in the desired order
            var reorderedColumns = new List<Column>();
            reorderedColumns.AddRange(pkColumns);
            reorderedColumns.AddRange(pkColumns2);
            reorderedColumns.AddRange(otherColumns);
            reorderedColumns.AddRange(dwColumns);

            return reorderedColumns.ToArray();
        }

        internal static Dictionary<string, (bool Nullable, bool Identity, long IdentityIncrement, long IdentitySeed)> CopyIdentitySettings(Table table)
        {
            var identitySettings = new Dictionary<string, (bool, bool, long, long)>();

            foreach (Column column in table.Columns)
            {
                if (GetPropertySafely(column, c => c.Identity))
                {
                    identitySettings.Add(column.Name,
                        (GetPropertySafely(column, c => c.Nullable),
                            GetPropertySafely(column, c => c.Identity)
                            , GetPropertySafely(column, c => c.IdentityIncrement), column.IdentitySeed));
                }
            }
            return identitySettings;
        }

        private static T GetPropertySafely<T>(Column column, Func<Column, T> propertyAccessor)
        {
            try
            {
                return propertyAccessor(column);
            }
            catch (PropertyNotSetException)
            {
                return default;
            }
        }

        internal static void ReapplyIdentitySettings(Table table, Dictionary<string, (bool Nullable, bool Identity, long IdentityIncrement, long IdentitySeed)> identitySettings)
        {
            foreach (var kvp in identitySettings)
            {
                var column = table.Columns[kvp.Key];
                column.Nullable = kvp.Value.Nullable;
                column.Identity = kvp.Value.Identity;
                column.IdentityIncrement = kvp.Value.IdentityIncrement;
                column.IdentitySeed = kvp.Value.IdentitySeed;
            }
        }


        /// <summary>
        /// Adds a column to the specified table based on the provided column and comparison options.
        /// </summary>
        /// <param name="trgTbl">The table to which the column will be added.</param>
        /// <param name="col">The column to be added.</param>
        /// <param name="cOpt">The comparison options used to determine how the column will be added.</param>
        /// <returns>The updated table with the added column.</returns>
        internal static Table AddColToTable(Table trgTbl, Column col, CompareOptions cOpt)
        {
            if (cOpt.SyncSrcColumns)
            {
                if (ColExists(col.Name, trgTbl))
                {
                    Column trgCol = trgTbl.Columns[col.Name];
                    if (cOpt.SyncDataType)
                    {
                        if (trgCol.DataType.Name.Equals(col.DataType.Name, StringComparison.InvariantCultureIgnoreCase) == false
                            || trgCol.DataType.MaximumLength != col.DataType.MaximumLength
                            || trgCol.DataType.NumericScale != col.DataType.NumericScale
                            || trgCol.DataType.NumericPrecision != col.DataType.NumericPrecision)
                        {
                            trgCol.DataType = col.DataType;
                            //TrgCol.DataType.Name.Equals(col.DataType.Name,StringComparison.InvariantCultureIgnoreCase)
                            trgCol.DataType.SqlDataType = col.DataType.SqlDataType;
                            //TrgCol.DataType.Name = col.DataType.Name;
                            trgCol.DataType.MaximumLength = col.DataType.MaximumLength;
                            trgCol.DataType.NumericScale = col.DataType.NumericScale;
                            trgCol.DataType.NumericPrecision = col.DataType.NumericPrecision;
                            trgCol.DataType.SqlDataType = col.DataType.SqlDataType;
                        }
                    }

                    if (cOpt.SyncCollation)
                    {
                        if (trgCol.Collation != col.Collation)
                        {
                            trgCol.Collation = col.Collation;
                        }
                        else
                        {
                            trgCol.Collation = "";
                        }
                    }

                    if (cOpt.SyncNullable)
                    {
                        if (trgCol.Nullable != col.Nullable)
                        {
                            trgCol.Nullable = col.Nullable;
                        }
                    }

                    if (cOpt.SyncIdentity)
                    {
                        if (trgCol.Identity != col.Identity)
                        {
                            trgCol.Identity = col.Identity;
                            trgCol.IdentityIncrement = col.IdentityIncrement;
                            trgCol.IdentitySeed = col.IdentitySeed;
                        }
                    }

                }
                else
                {
                    //Add Missing Column
                    Column newCol = new Column(trgTbl, col.Name, col.DataType);
                    if (cOpt.SyncNullable)
                    {
                        newCol.Nullable = col.Nullable;
                    }
                    else
                    {
                        newCol.Nullable = true;
                    }


                    if (cOpt.SyncCollation)
                    {
                        if (newCol.Collation != col.Collation)
                        {
                            newCol.Collation = col.Collation;
                        }
                        else
                        {
                            newCol.Collation = "";
                        }
                    }

                    if (cOpt.SyncIdentity)
                    {
                        newCol.Identity = col.Identity;
                        newCol.IdentityIncrement = col.IdentityIncrement;
                        newCol.IdentitySeed = col.IdentitySeed;
                    }

                    trgTbl.Columns.Add(newCol);
                }

            }

            return trgTbl;
        }

        /// <summary>
        /// Synchronizes columns from a source table to a target table with optimized performance.
        /// </summary>
        /// <param name="srcTbl">The source table containing columns to sync from</param>
        /// <param name="trgTbl">The target table to sync columns to</param>
        /// <param name="cOpt">Comparison options controlling how synchronization is performed</param>
        /// <returns>The updated target table with synchronized columns</returns>
        internal static Table SyncSrcTblTrgTbl(Table srcTbl, Table trgTbl, CompareOptions cOpt)
        {
            // Early exit if we're not adding missing columns
            if (!cOpt.AddMissingColToTarget) return trgTbl;

            // Create HashSet of ignored columns for O(1) lookup performance
            // Using case-insensitive comparison to match SQL Server behavior
            var ignoredColumnsSet = new HashSet<string>(
                cOpt.IgnoreColumns ?? new List<string>(),
                StringComparer.InvariantCultureIgnoreCase
            );

            // Create HashSet of existing column names for O(1) lookup performance
            var existingColumnNames = new HashSet<string>(
                trgTbl.Columns.Cast<Column>().Select(c => c.Name),
                StringComparer.InvariantCultureIgnoreCase
            );

            // Get all columns that need to be processed
            // Filter out ignored columns upfront to avoid processing them later
            var columnsToProcess = srcTbl.Columns.Cast<Column>()
                .Where(col => !ignoredColumnsSet.Contains(col.Name))
                .ToList();

            // Early exit if no columns to process
            if (columnsToProcess.Count == 0) return trgTbl;

            // Process each column that needs to be added or updated
            foreach (var col in columnsToProcess)
            {
                if (!existingColumnNames.Contains(col.Name))
                {
                    // Column doesn't exist - create new column with all properties
                    Column newCol = new Column(trgTbl, col.Name, col.DataType)
                    {
                        // Set nullable based on sync option or default to true
                        Nullable = cOpt.SyncNullable ? col.Nullable : true,
                        // Set collation based on sync option
                        Collation = cOpt.SyncCollation ? col.Collation : "",
                        // Set identity properties based on sync option
                        Identity = cOpt.SyncIdentity && col.Identity,
                        IdentityIncrement = col.IdentityIncrement,
                        IdentitySeed = col.IdentitySeed
                    };
                    trgTbl.Columns.Add(newCol);
                    existingColumnNames.Add(col.Name); // Update our tracking set
                }
                else if (cOpt.SyncSrcColumns)
                {
                    // Column exists - update properties based on sync options
                    Column trgCol = trgTbl.Columns[col.Name];

                    // Update data type if needed and option is enabled
                    if (cOpt.SyncDataType && !AreDataTypesEqual(trgCol.DataType, col.DataType))
                    {
                        UpdateDataType(trgCol, col.DataType);
                    }

                    // Update collation if different and option is enabled
                    if (cOpt.SyncCollation && trgCol.Collation != col.Collation)
                    {
                        trgCol.Collation = string.IsNullOrEmpty(col.Collation) ? "" : col.Collation;
                    }

                    // Update nullable property if different and option is enabled
                    if (cOpt.SyncNullable && trgCol.Nullable != col.Nullable)
                    {
                        trgCol.Nullable = col.Nullable;
                    }

                    // Update identity settings if different and option is enabled
                    if (cOpt.SyncIdentity && trgCol.Identity != col.Identity)
                    {
                        UpdateIdentitySettings(trgCol, col);
                    }
                }
            }

            return trgTbl;
        }


        /// <summary>
        /// Synchronizes columns from a source view to a target table with optimized performance.
        /// </summary>
        /// <param name="srcTbl">The source view containing columns to sync from</param>
        /// <param name="trgTbl">The target table to sync columns to</param>
        /// <param name="cOpt">Comparison options controlling how synchronization is performed</param>
        /// <returns>The updated target table with synchronized columns</returns>
        internal static Table SyncSrcVewTrgTbl(View srcTbl, Table trgTbl, CompareOptions cOpt)
        {
            // Early exit if we're not adding missing columns
            if (!cOpt.AddMissingColToTarget) return trgTbl;

            // Create HashSet of ignored columns for O(1) lookup performance
            // Using case-insensitive comparison to match SQL Server behavior
            var ignoredColumnsSet = new HashSet<string>(
                cOpt.IgnoreColumns,
                StringComparer.InvariantCultureIgnoreCase
            );

            // Create HashSet of existing column names for O(1) lookup performance
            // This avoids repeated linear searches through the columns collection
            var existingColumnNames = new HashSet<string>(
                trgTbl.Columns.Cast<Column>().Select(c => c.Name),
                StringComparer.InvariantCultureIgnoreCase
            );

            // Get all columns that need to be processed
            // Filter out ignored columns upfront to avoid processing them later
            var columnsToAdd = srcTbl.Columns.Cast<Column>()
                .Where(col => !ignoredColumnsSet.Contains(col.Name))
                .ToList();

            // Early exit if no columns to process
            if (columnsToAdd.Count == 0) return trgTbl;

            // Process each column that needs to be added or updated
            foreach (var col in columnsToAdd)
            {
                if (!existingColumnNames.Contains(col.Name))
                {
                    // Column doesn't exist - create new column with all properties
                    Column newCol = new Column(trgTbl, col.Name, col.DataType)
                    {
                        // Set nullable based on sync option or default to true
                        Nullable = cOpt.SyncNullable ? col.Nullable : true,
                        // Set collation based on sync option
                        Collation = cOpt.SyncCollation ? col.Collation : "",
                        // Set identity properties based on sync option
                        Identity = cOpt.SyncIdentity && col.Identity,
                        IdentityIncrement = col.IdentityIncrement,
                        IdentitySeed = col.IdentitySeed
                    };
                    trgTbl.Columns.Add(newCol);
                }
                else if (cOpt.SyncSrcColumns)
                {
                    // Column exists - update properties based on sync options
                    Column trgCol = trgTbl.Columns[col.Name];

                    // Update data type if needed and option is enabled
                    if (cOpt.SyncDataType && !AreDataTypesEqual(trgCol.DataType, col.DataType))
                    {
                        UpdateDataType(trgCol, col.DataType);
                    }

                    // Update collation if different and option is enabled
                    if (cOpt.SyncCollation && trgCol.Collation != col.Collation)
                    {
                        trgCol.Collation = col.Collation;
                    }

                    // Update nullable property if different and option is enabled
                    if (cOpt.SyncNullable && trgCol.Nullable != col.Nullable)
                    {
                        trgCol.Nullable = col.Nullable;
                    }

                    // Update identity settings if different and option is enabled
                    if (cOpt.SyncIdentity && trgCol.Identity != col.Identity)
                    {
                        UpdateIdentitySettings(trgCol, col);
                    }
                }
            }

            return trgTbl;
        }

        /// <summary>
        /// Compares two SQL data types for equality considering all relevant properties.
        /// </summary>
        /// <param name="dt1">First data type to compare</param>
        /// <param name="dt2">Second data type to compare</param>
        /// <returns>True if the data types are equivalent, false otherwise</returns>
        private static bool AreDataTypesEqual(DataType dt1, DataType dt2)
        {
            return dt1.Name.Equals(dt2.Name, StringComparison.InvariantCultureIgnoreCase)
                && dt1.MaximumLength == dt2.MaximumLength
                && dt1.NumericScale == dt2.NumericScale
                && dt1.NumericPrecision == dt2.NumericPrecision;
        }

        /// <summary>
        /// Updates all properties of a target column's data type to match a source data type.
        /// </summary>
        /// <param name="trgCol">Target column to update</param>
        /// <param name="srcDataType">Source data type to copy from</param>
        private static void UpdateDataType(Column trgCol, DataType srcDataType)
        {
            // Update all data type properties in a single batch
            trgCol.DataType = srcDataType;
            trgCol.DataType.SqlDataType = srcDataType.SqlDataType;
            trgCol.DataType.MaximumLength = srcDataType.MaximumLength;
            trgCol.DataType.NumericScale = srcDataType.NumericScale;
            trgCol.DataType.NumericPrecision = srcDataType.NumericPrecision;
        }

        /// <summary>
        /// Updates all identity-related properties of a target column to match a source column.
        /// </summary>
        /// <param name="trgCol">Target column to update</param>
        /// <param name="srcCol">Source column to copy from</param>
        private static void UpdateIdentitySettings(Column trgCol, Column srcCol)
        {
            // Update all identity properties in a single batch
            trgCol.Identity = srcCol.Identity;
            trgCol.IdentityIncrement = srcCol.IdentityIncrement;
            trgCol.IdentitySeed = srcCol.IdentitySeed;
        }

        /// <summary>
        /// Converts a StringCollection to a single string.
        /// </summary>
        /// <param name="scritLines">The StringCollection to be converted.</param>
        /// <returns>A string that represents the combined elements of the StringCollection, separated by a newline character.</returns>
        internal static string CollectionToString(StringCollection scritLines)
        {
            string[] newTlbScriptColAry = scritLines.Cast<string>().ToArray();
            string rValue = string.Join(Environment.NewLine, newTlbScriptColAry);
            return rValue;
        }


        internal static Table MakeAllColsNullable(Table srcTbl, Table trgTbl, List<string> nonNullableColumnNames, SqlConnection srcConnection)
        {
            if (srcConnection == null)
                throw new ArgumentNullException(nameof(srcConnection), "SQL connection cannot be null");

            if (srcConnection.State != ConnectionState.Open)
                throw new InvalidOperationException("SQL connection must be open");

            // Initialize set with provided non-nullable column names
            HashSet<string> nonNullableColumns = new HashSet<string>(nonNullableColumnNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            if (srcTbl != null)
            {
                // Query to find primary key and clustered index columns
                string sql = @"
            SELECT DISTINCT c.name AS ColumnName
            FROM sys.indexes i with (nolock)
            INNER JOIN sys.index_columns ic with (nolock) ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c with (nolock) ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.object_id = OBJECT_ID(@tableName)
            AND (
                i.is_primary_key = 1  -- Primary key columns
                OR i.type = 1         -- Clustered index columns
            )";

                using (SqlCommand command = new SqlCommand(sql, srcConnection))
                {
                    // Use fully qualified table name
                    string fullTableName = $"{srcTbl.Schema}.{srcTbl.Name}";
                    command.Parameters.AddWithValue("@tableName", fullTableName);

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string columnName = reader.GetString(0);
                            nonNullableColumns.Add(columnName);
                        }
                    }
                }
            }

            // Set columns to nullable in the target table except those that should remain non-nullable
            foreach (Column col in trgTbl.Columns)
            {
                if (!nonNullableColumns.Contains(col.Name))
                {
                    col.Nullable = true;
                }
            }

            return trgTbl;
        }



        /// <summary>
        /// Converts the Unicode data types of the columns in the given table to non-Unicode data types.
        /// </summary>
        /// <param name="trgTbl">The table whose columns' data types are to be converted.</param>
        /// <returns>The table with its columns' data types converted from Unicode to non-Unicode.</returns>
        /// <remarks>
        /// This method iterates over each column in the given table. If the data type of a column is Unicode, 
        /// it removes the Unicode specifier from the data type name. The method is used to ensure compatibility 
        /// with systems that do not support Unicode.
        /// </remarks>
        internal static Table ConvertUnicodeDt(Table trgTbl)
        {
            if (trgTbl == null)
            {
                throw new ArgumentNullException(nameof(trgTbl), "The provided table cannot be null.");
            }

            foreach (Column col in trgTbl.Columns)
            {
                string dataTypeName = col.DataType.Name.ToLower();
                switch (dataTypeName)
                {
                    case "nvarchar":
                        // Handle conversion for regular and max length nvarchar
                        if (col.DataType.MaximumLength == -1)  // Check if it's 'max'
                        {
                            col.DataType = new DataType(SqlDataType.VarCharMax);
                        }
                        else
                        {
                            col.DataType = new DataType(SqlDataType.VarChar)
                            {
                                MaximumLength = col.DataType.MaximumLength
                            };
                        }
                        break;
                    case "nchar":
                        col.DataType = new DataType(SqlDataType.Char)
                        {
                            MaximumLength = col.DataType.MaximumLength
                        };
                        break;
                    case "ntext":  // Note: ntext to text conversion is generally straightforward, but text type is deprecated
                        col.DataType = new DataType(SqlDataType.Text);
                        break;
                    default:
                        // Optionally handle or log the data types that are not converted
                        break;
                }
            }

            return trgTbl;
        }

        /// <summary>
        /// Cleans up the column names in the provided table.
        /// </summary>
        /// <param name="trgTbl">The table whose column names need to be cleaned up.</param>
        /// <param name="validCharsRegex">The regular expression pattern defining valid characters for column names.</param>
        /// <returns>The table with cleaned column names.</returns>
        /// <remarks>
        /// This method iterates over each column in the provided table, and replaces any characters in the column name that do not match the provided regular expression pattern with an empty string. If the cleaned name is empty, it is replaced with "EmptyColumnName". If there are duplicate cleaned names, a counter is appended to make the name unique.
        /// </remarks>
        internal static Table CleanupColumnName(Table trgTbl, string validCharsRegex, string replaceStr = null)
        {
            if (trgTbl == null)
                throw new ArgumentNullException(nameof(trgTbl), "Target table cannot be null.");
            if (string.IsNullOrEmpty(validCharsRegex))
                throw new ArgumentException("Valid characters regex cannot be null or empty.", nameof(validCharsRegex));
            Regex regex = new Regex($"{validCharsRegex}");
            Dictionary<string, string> newNames = new Dictionary<string, string>();
            Dictionary<string, int> nameUsage = new Dictionary<string, int>();
            // First, determine the new names without modifying the original Columns collection
            foreach (Column col in trgTbl.Columns)
            {
                string originalName = col.Name;
                string cleanedName;
                if (!string.IsNullOrEmpty(replaceStr))
                {
                    cleanedName = regex.Replace(originalName, replaceStr);
                }
                else
                {
                    cleanedName = regex.Replace(originalName, string.Empty);
                }
                if (string.IsNullOrEmpty(cleanedName))
                {
                    cleanedName = "EmptyColumnName";
                }
                // Handle duplicate names by ensuring uniqueness
                if (nameUsage.ContainsKey(cleanedName))
                {
                    int counter = nameUsage[cleanedName]++;
                    cleanedName = $"{cleanedName}{counter}";
                }
                else
                {
                    nameUsage[cleanedName] = 1;  // Start tracking usage of this name
                }
                newNames[originalName] = cleanedName;
            }
            // Then apply the new names to avoid modifying the collection during iteration
            foreach (var pair in newNames)
            {
                Column col = trgTbl.Columns[pair.Key];
                if (col.Name != pair.Value)
                {
                    col.Name = pair.Value;
                }
            }
            //trgTbl.Alter(); // Apply all changes to the database
            return trgTbl;
        }


        /// <summary>
        /// Determines whether the given data type is a Unicode data type.
        /// </summary>
        /// <param name="DataTypeName">The name of the data type to check.</param>
        /// <returns>
        /// <c>true</c> if the data type is a Unicode data type; otherwise, <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This method checks if the provided data type name matches any of the Unicode data types in SQL Server.
        /// </remarks>
        internal static bool DataTypeIsUniCode(string DataTypeName)
        {
            bool rValue = false;
            List<SqlDataType> UniCodeDataTypes = GetUniCodeDataType();

            foreach (SqlDataType type in UniCodeDataTypes)
            {
                if (SqlDataType.SysName.ToString() == DataTypeName)
                    rValue = true;
            }

            return rValue;
        }

        /// <summary>
        /// Determines whether the source and target tables are in sync based on the provided comparison options.
        /// </summary>
        /// <param name="srcTbl">The source table to compare.</param>
        /// <param name="trgTbl">The target table to compare.</param>
        /// <param name="cOpt">The comparison options to use when comparing the tables.</param>
        /// <returns>Returns true if the source and target tables are in sync according to the comparison options; otherwise, false.</returns>
        /// <remarks>
        /// This method checks for column existence and, if the CompareDataType option is set, it also checks for data type equality. 
        /// If a column does not exist in the target table or the data types do not match (when CompareDataType is true), the method returns false.
        /// </remarks>
        internal static bool IsSrcTrgInSync(Table srcTbl, Table trgTbl, CompareOptions cOpt)
        {
            bool rValue = false;
            foreach (Column col in srcTbl.Columns)
            {
                if (ColExists(col.Name, trgTbl) == true)
                {
                    if (cOpt.CompareDataType)
                    {
                        if (col.DataType == trgTbl.Columns[col.Name].DataType)
                        {
                            rValue = true;
                        }
                    }
                    else
                    {
                        rValue = true;
                    }
                }
                else
                {
                    rValue = false;
                    break;
                }
            }
            return rValue;
        }

        /// <summary>
        /// Determines whether the source view and target table are in sync based on the specified compare options.
        /// </summary>
        /// <param name="srcTbl">The source view to be compared.</param>
        /// <param name="trgTbl">The target table to be compared.</param>
        /// <param name="cOpt">The compare options used to determine synchronization.</param>
        /// <returns>Returns true if the source view and target table are in sync according to the compare options; otherwise, returns false.</returns>
        /// <remarks>
        /// This method checks each column in the source view against the corresponding column in the target table. 
        /// If the 'CompareDataType' option is set, it also checks if the data types of the columns are the same.
        /// </remarks>
        internal static bool IsSrcTrgInSync(View srcTbl, Table trgTbl, CompareOptions cOpt)
        {
            bool rValue = false;
            foreach (Column col in srcTbl.Columns)
            {
                if (ColExists(col.Name, trgTbl) == true)
                {
                    if (cOpt.CompareDataType)
                    {
                        if (col.DataType == trgTbl.Columns[col.Name].DataType)
                        {
                            rValue = true;
                        }
                    }
                    else
                    {
                        rValue = true;
                    }
                }
                else
                {
                    rValue = false;
                    break;
                }
            }
            return rValue;
        }

        /// <summary>
        /// Checks if a column exists in the specified table.
        /// </summary>
        /// <param name="colName">The name of the column to check.</param>
        /// <param name="tbl">The table to check for the column.</param>
        /// <returns>Returns true if the column exists in the table, otherwise false.</returns>
        internal static bool ColExists(string colName, Table tbl)
        {
            bool rValue = false;

            foreach (Column col in tbl.Columns)
            {
                if (col.Name.Equals(colName, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = true;
                }
            }

            return rValue;
        }

        /// <summary>
        /// Synchronizes the columns from the source view to the target table based on the provided comparison options.
        /// </summary>
        /// <param name="srcView">The source view from which columns will be synchronized.</param>
        /// <param name="trgTbl">The target table to which columns will be added.</param>
        /// <param name="cOpt">The comparison options used to determine how the columns will be synchronized.</param>
        /// <returns>The updated target table with the synchronized columns.</returns>
        internal static Table SyncSrcTrg(View srcView, Table trgTbl, CompareOptions cOpt)
        {
            if (cOpt.SyncSrcColumns)
            {
                foreach (Column col in srcView.Columns)
                {
                    trgTbl = AddColToTable(trgTbl, col, cOpt);
                }
            }

            return trgTbl;
        }

        /// <summary>
        /// Synchronizes the surrogate key table with the base table.
        /// </summary>
        /// <param name="baseTable">The base table to synchronize with.</param>
        /// <param name="smoDbSKey">The SMO database object for the surrogate key.</param>
        /// <param name="keyColumnsList">The list of key columns in the base table.</param>
        /// <param name="sKeyDatabase">The name of the surrogate key database.</param>
        /// <param name="sKeySchema">The schema of the surrogate key database.</param>
        /// <param name="sKeyObject">The surrogate key object.</param>
        /// <param name="surrogateColumn">The surrogate column in the base table.</param>
        /// <param name="generalTimeoutInSek">The general timeout in seconds.</param>
        /// <remarks>
        /// This method creates a new surrogate key table if it does not exist, otherwise it synchronizes the existing surrogate key table with the base table.
        /// It also ensures that the surrogate key column exists in the base table and adds an index to it.
        /// </remarks>
        internal static void SyncSkeyTable(SqlConnection trgConnection, Server smoSrvSKey, Table baseTable, Database smoDbSKey, List<string> keyColumnsList, string sKeyDatabase, string sKeySchema, string sKeyObject, string surrogateColumn, int generalTimeoutInSek)
        {
            if (baseTable != null)
            {
                CompareOptions cmpOpt = new CompareOptions();
                ScriptingOptions alterOpt = SmoScriptingOptionsAlter();

                CommonDB.BuildFullObjectName(sKeyDatabase, sKeySchema, sKeyObject);

                Table newSkeyTbl = BuildSurrogateKeyTable(baseTable, keyColumnsList, smoDbSKey, sKeyObject, sKeySchema, surrogateColumn);

                string serverName = smoSrvSKey.Urn.Value.Split('/')[0].Replace("Server[@Name='", "").Replace("']", "");

                var objUrn = SmoHelper.CreateTableUrnFromComponents(serverName, sKeyDatabase, sKeySchema,sKeyObject);
                Table sKeyTable = null;
                try
                {
                    var smoObject = smoSrvSKey.GetSmoObject(objUrn);
                    sKeyTable = smoObject as Table;
                    
                } catch {}

                if (sKeyTable != null)
                {
                    SyncSrcTblTrgTbl(newSkeyTbl, sKeyTable, cmpOpt);

                    var script = sKeyTable.Script(alterOpt);
                    string[] allScripts = script.Cast<string>().ToArray();
                    string alterScript = string.Join(Environment.NewLine, allScripts);
                    if (alterScript.Length > 10)
                    {
                        sKeyTable.Alter();
                    }
                }
                else
                {
                    StringBuilder script = new StringBuilder();

                    ScriptingOptions scriptingOptions = new ScriptingOptions
                    {
                        ScriptDrops = false,
                        WithDependencies = false,
                        Indexes = true,
                        DriPrimaryKey = true,
                        DriIndexes = true,
                        DriUniqueKeys = true,
                        DriAll = false,
                        SchemaQualify = true,
                        IncludeHeaders = true,
                        ToFileOnly = false,
                    };

                    StringCollection scriptCollection = newSkeyTbl.Script(scriptingOptions);

                    foreach (string line in scriptCollection)
                    {
                        script.AppendLine(line);
                    }

                    string createScript = @$"IF OBJECT_ID('[{newSkeyTbl.Schema}].[{newSkeyTbl.Name}]', 'U') IS NULL
                    BEGIN
                        {script.ToString()}
                    END
                    ";
                    if (createScript.Length > 0)
                    {
                        CommonDB.ExecDDLScript(trgConnection, createScript, 512, false);
                    }
                }

                //Ensure that Skeycolumn exsists in source table. Used for pushback values
                Column column = Schema.AddColToTable.SysColumnInt(baseTable, surrogateColumn);
                baseTable = AddColToTable(baseTable, column, cmpOpt);

                // Add Index To SurrogateColumn
                List<string> iCols = new List<string>();
                iCols.Add(surrogateColumn);
                baseTable = SyncTableIndex(baseTable, "", iCols, IndexType.NonClusteredIndex, IndexKeyType.None);

                var scrSkeyCmd = baseTable.Script(alterOpt);
                string[] skeyScripts = scrSkeyCmd.Cast<string>().ToArray();
                string sKeyScript = string.Join(Environment.NewLine, skeyScripts);
                if (sKeyScript.Length > 10)
                {
                    baseTable.Alter();
                }

            }
        }

        /// <summary>
        /// Builds a surrogate key table based on the provided source table and key columns.
        /// </summary>
        /// <param name="srcTbl">The source table to use as a basis for the surrogate key table.</param>
        /// <param name="srcKeyColumns">A list of key columns from the source table to include in the surrogate key table.</param>
        /// <param name="db">The database where the surrogate key table will be created.</param>
        /// <param name="sKeyObject">The name of the surrogate key object.</param>
        /// <param name="sKeySchema">The schema of the surrogate key object.</param>
        /// <param name="surrogateColumn">The name of the surrogate column in the new table.</param>
        /// <returns>A new table object representing the surrogate key table.</returns>
        internal static Table BuildSurrogateKeyTable(Table srcTbl, List<string> srcKeyColumns, Database db, string sKeyObject, string sKeySchema, string surrogateColumn)
        {
            Table newSKeyTable = new Table(db, sKeyObject, sKeySchema);

            Column col2 = Schema.AddColToTable.SysColumnInt(newSKeyTable, surrogateColumn);
            col2.Nullable = false;
            col2.Identity = true;
            col2.IdentityIncrement = 1;
            col2.IdentitySeed = 1;

            newSKeyTable.Columns.Add(col2);

            List<string> columnList = new List<string>();
            columnList.Add(surrogateColumn);

            newSKeyTable = SyncTableIndex(newSKeyTable, "", columnList, IndexType.ClusteredIndex, IndexKeyType.DriPrimaryKey);

            foreach (Column col in srcTbl.Columns)
            {
                if (srcKeyColumns.Contains(col.Name, StringComparer.InvariantCultureIgnoreCase))
                {
                    Column xCol = new Column
                    {
                        Name = col.Name,
                        Parent = newSKeyTable,
                        DataType = col.DataType,
                        Nullable = col.Nullable
                    };
                    newSKeyTable.Columns.Add(xCol);
                }
            }

            Column insertedDateDw = Schema.AddColToTable.SysColumnDt(newSKeyTable, "InsertedDate_DW");
            newSKeyTable.Columns.Add(insertedDateDw);

            Column srcDbSchTbl = Schema.AddColToTable.SysColumnStr(newSKeyTable, "SrcDBSchTbl", 250);
            newSKeyTable.Columns.Add(srcDbSchTbl);

            return newSKeyTable;
        }

        /// <summary>
        /// Adds an index to the specified table.
        /// </summary>
        /// <param name="trgTbl">The target table to which the index will be added.</param>
        /// <param name="indexName">The name of the index. If not provided, a name will be automatically generated based on the index type and key type.</param>
        /// <param name="columnList">A list of column names that will be included in the index.</param>
        /// <param name="indexType">The type of the index to be created.</param>
        /// <param name="indexKeyType">The type of the key for the index.</param>
        /// <returns>The updated table with the new index.</returns>
        internal static Table SyncTableIndex(Table trgTbl, string indexName, List<string> columnList, IndexType indexType, IndexKeyType indexKeyType)
        {
            string autoName = columnList[0];
            if (indexName.Length > 0)
            {
                autoName = indexName;
            }
            else
            {
                if (indexType == IndexType.ClusteredIndex)
                {
                    autoName = $"CI_{autoName}";
                }
                else if (indexType == IndexType.ClusteredIndex && indexKeyType == IndexKeyType.DriPrimaryKey)
                {
                    autoName = $"PK_{trgTbl.Name}";
                }
                else if (indexType == IndexType.ClusteredColumnStoreIndex)
                {
                    autoName = $"CCSI_{autoName}";
                }
                else if (indexType == IndexType.HeapIndex)
                {
                    autoName = $"HI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredColumnStoreIndex)
                {
                    autoName = $"NCCSI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredHashIndex)
                {
                    autoName = $"NCHI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredIndex)
                {
                    autoName = $"NCI_{autoName}";
                }
                else if (indexType == IndexType.SpatialIndex)
                {
                    autoName = $"SI_{autoName}";
                }
            }

            Index existingIndex = trgTbl.Indexes[autoName];
            if (existingIndex != null)
            {
                // Check if the existing index has the correct definition
                bool hasChanges = false;
                if (existingIndex.IndexKeyType != indexKeyType || existingIndex.IndexType != indexType)
                {
                    existingIndex.IndexKeyType = indexKeyType;
                    existingIndex.IndexType = indexType;
                    hasChanges = true;
                }

                // Check if the indexed columns match
                if (existingIndex.IndexedColumns.Count != columnList.Count)
                {
                    hasChanges = true;
                }
                else
                {
                    for (int i = 0; i < columnList.Count; i++)
                    {
                        if (existingIndex.IndexedColumns[i].Name != columnList[i])
                        {
                            hasChanges = true;
                            break;
                        }
                    }
                }

                if (hasChanges)
                {
                    // Drop the existing index
                    existingIndex.Drop();

                    // Create a new index with the correct definition
                    Index newIndex = new Index
                    {
                        Parent = trgTbl,
                        Name = autoName,
                        IndexKeyType = indexKeyType,
                        IndexType = indexType,
                        IsClustered = indexType == IndexType.ClusteredIndex,
                        IsUnique = indexKeyType == IndexKeyType.DriUniqueKey
                    };
                    foreach (string col in columnList)
                    {
                        IndexedColumn icol = new IndexedColumn(newIndex, col, false);
                        newIndex.IndexedColumns.Add(icol);
                    }
                    trgTbl.Indexes.Add(newIndex);
                }
            }
            else
            {
                // Create a new index
                Index newIndex = new Index
                {
                    Parent = trgTbl,
                    Name = autoName,
                    IndexKeyType = indexKeyType,
                    IndexType = indexType,
                    IsClustered = indexType == IndexType.ClusteredIndex,
                    IsUnique = indexKeyType == IndexKeyType.DriUniqueKey
                };
                foreach (string col in columnList)
                {
                    IndexedColumn icol = new IndexedColumn(newIndex, col, false);
                    newIndex.IndexedColumns.Add(icol);
                }
                trgTbl.Indexes.Add(newIndex);
            }

            return trgTbl;
        }

        /// <summary>
        /// Generates a T-SQL delta script to synchronize a given index definition with the target table.
        /// This method replicates the same logic as <c>SyncTableIndex</c>, but instead of
        /// modifying <see cref="Table"/> and <see cref="Index"/> objects, it returns the script
        /// required to make those changes.
        /// </summary>
        /// <param name="trgTbl">The target table against which to synchronize the index.</param>
        /// <param name="indexName">
        /// The name of the index to be created or updated.
        /// If empty, the name is auto-generated using the same rules as <c>SyncTableIndex</c>.
        /// </param>
        /// <param name="columnList">A list of column names on which the index will be based.</param>
        /// <param name="indexType">The SMO <see cref="IndexType"/> (e.g. Clustered, NonClustered, etc.).</param>
        /// <param name="indexKeyType">The SMO <see cref="IndexKeyType"/> (None, DriPrimaryKey, DriUniqueKey, etc.).</param>
        /// <returns>
        /// A string containing the T-SQL statements needed to create or update the index so that
        /// it matches the requested definition. If no changes are necessary, returns an empty string.
        /// </returns>
        internal static string SyncTableIndexScript(
            Table trgTbl,
            string indexName,
            List<string> columnList,
            IndexType indexType,
            IndexKeyType indexKeyType)
        {
            if (trgTbl == null)
                throw new ArgumentNullException(nameof(trgTbl), "Target table cannot be null.");

            if (columnList == null || columnList.Count == 0)
                throw new ArgumentException("You must specify at least one column for the index.", nameof(columnList));

            // ----------------------------------------------------------------------------------
            // 1) Replicate the "autoName" logic from the original SyncTableIndex
            // ----------------------------------------------------------------------------------
            string autoName = columnList[0];
            // If a specific index name was provided, use it directly.
            if (!string.IsNullOrEmpty(indexName))
            {
                autoName = indexName;
            }
            else
            {
                // The original code has separate if-blocks (not else-if) but effectively the
                // first condition that matches gets used. We replicate exactly that behavior.
                if (indexType == IndexType.ClusteredIndex)
                {
                    autoName = $"CI_{autoName}";
                }
                else if (indexType == IndexType.ClusteredIndex && indexKeyType == IndexKeyType.DriPrimaryKey)
                {
                    // Note: In practice this block never executes because the above condition
                    // grabs ClusteredIndex first. Kept here for fidelity with original code.
                    autoName = $"PK_{trgTbl.Name}";
                }
                else if (indexType == IndexType.ClusteredColumnStoreIndex)
                {
                    autoName = $"CCSI_{autoName}";
                }
                else if (indexType == IndexType.HeapIndex)
                {
                    autoName = $"HI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredColumnStoreIndex)
                {
                    autoName = $"NCCSI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredHashIndex)
                {
                    autoName = $"NCHI_{autoName}";
                }
                else if (indexType == IndexType.NonClusteredIndex)
                {
                    autoName = $"NCI_{autoName}";
                }
                else if (indexType == IndexType.SpatialIndex)
                {
                    autoName = $"SI_{autoName}";
                }
            }

            // ----------------------------------------------------------------------------------
            // 2) Check if index already exists and compare definitions
            // ----------------------------------------------------------------------------------
            Index existingIndex = trgTbl.Indexes[autoName];
            bool indexExists = (existingIndex != null);
            bool hasDifferences = false;

            if (indexExists)
            {
                // Compare the IndexKeyType and IndexType
                if (existingIndex.IndexKeyType != indexKeyType || existingIndex.IndexType != indexType)
                {
                    hasDifferences = true;
                }

                // Compare the column counts and the actual column names
                if (existingIndex.IndexedColumns.Count != columnList.Count)
                {
                    hasDifferences = true;
                }
                else
                {
                    for (int i = 0; i < columnList.Count; i++)
                    {
                        if (!existingIndex.IndexedColumns[i].Name.Equals(columnList[i], StringComparison.OrdinalIgnoreCase))
                        {
                            hasDifferences = true;
                            break;
                        }
                    }
                }
            }

            // ----------------------------------------------------------------------------------
            // 3) Generate the T-SQL script
            // ----------------------------------------------------------------------------------
            StringBuilder sb = new StringBuilder();
            string schema = trgTbl.Schema;
            string tableName = trgTbl.Name;

            // If the index exists but differs, first drop it
            if (indexExists && hasDifferences)
            {
                sb.AppendLine(GenerateDropIndexScript(schema, tableName, existingIndex));
            }

            // If the index does not exist at all, or it differs (and we dropped it above), then create it
            if (!indexExists || hasDifferences)
            {
                sb.AppendLine(GenerateCreateIndexScript(schema, tableName, autoName, columnList, indexType, indexKeyType));
            }

            // If the index already exists and no differences, do nothing => empty script
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Generates the T-SQL statement to drop an existing index or constraint (if it's a primary key or unique constraint).
        /// </summary>
        private static string GenerateDropIndexScript(string schema, string tableName, Index existingIndex)
        {
            // If it's PK or Unique constraint => drop constraint, else => drop index
            bool isConstraint = existingIndex.IndexKeyType == IndexKeyType.DriPrimaryKey ||
                                existingIndex.IndexKeyType == IndexKeyType.DriUniqueKey;

            if (isConstraint)
            {
                // For primary key or unique constraints:
                return $"ALTER TABLE [{schema}].[{tableName}] DROP CONSTRAINT [{existingIndex.Name}];";
            }
            else
            {
                // For normal indexes:
                return $"DROP INDEX [{existingIndex.Name}] ON [{schema}].[{tableName}];";
            }
        }

        /// <summary>
        /// Generates the T-SQL statement to create the desired index definition,
        /// including PK or unique constraints if applicable.
        /// </summary>
        private static string GenerateCreateIndexScript(
            string schema,
            string tableName,
            string indexName,
            List<string> columns,
            IndexType indexType,
            IndexKeyType indexKeyType)
        {
            // Format column list, e.g. [ColA],[ColB],[ColC]
            string columnListSql = string.Join(", ", columns.Select(c => $"[{c}]"));
            // Convert the SMO IndexType into the T-SQL keywords (CLUSTERED, NONCLUSTERED, etc.)
            string indexOptions = GetIndexTypeOptions(indexType);

            // If it's a primary key or unique constraint, we do ALTER TABLE ... ADD CONSTRAINT ...
            // otherwise, we do CREATE {CLUSTERED|NONCLUSTERED...} INDEX ...
            switch (indexKeyType)
            {
                case IndexKeyType.DriPrimaryKey:
                    return
                        $"ALTER TABLE [{schema}].[{tableName}] " +
                        $"ADD CONSTRAINT [{indexName}] PRIMARY KEY {indexOptions} ({columnListSql});";

                case IndexKeyType.DriUniqueKey:
                    return
                        $"ALTER TABLE [{schema}].[{tableName}] " +
                        $"ADD CONSTRAINT [{indexName}] UNIQUE {indexOptions} ({columnListSql});";

                default:
                    // Normal index
                    // (Note: The original SMO code sets IsUnique if the IndexKeyType == DriUniqueKey, but for
                    // consistency with the original method, we only do constraints for PK/UniqueKey types.)
                    string createStmt = $"CREATE {indexOptions} INDEX [{indexName}] ON [{schema}].[{tableName}] ({columnListSql});";
                    return createStmt;
            }
        }

        /// <summary>
        /// Maps SMO IndexType to T-SQL keywords (e.g. CLUSTERED, NONCLUSTERED, etc.).
        /// If your workflow never uses certain index types, you can simplify or remove them.
        /// </summary>
        private static string GetIndexTypeOptions(IndexType indexType)
        {
            // This matches the naming used in the original SyncTableIndex logic.
            // You can refine or extend as needed for your environment (e.g. HASH indexes often require WITH(BUCKET_COUNT=...)).
            switch (indexType)
            {
                case IndexType.ClusteredIndex:
                    return "CLUSTERED";
                case IndexType.NonClusteredIndex:
                    return "NONCLUSTERED";
                case IndexType.ClusteredColumnStoreIndex:
                    return "CLUSTERED COLUMNSTORE";
                case IndexType.NonClusteredColumnStoreIndex:
                    return "NONCLUSTERED COLUMNSTORE";
                case IndexType.NonClusteredHashIndex:
                    return "NONCLUSTERED HASH";
                case IndexType.SpatialIndex:
                    // T-SQL syntax for spatial indexes is typically "CREATE SPATIAL INDEX [name] ON ..."
                    // Here, we just return SPATIAL for simplicity. 
                    // If you actually need a spatial index, you may need more specialized syntax.
                    return "SPATIAL";
                case IndexType.HeapIndex:
                    // "Heap" is not created with a T-SQL index statement. It's the absence of a clustered index.
                    // Typically, you don't 'create' a heap, you drop any existing clustered index. 
                    // For fidelity, we'll just return an empty string:
                    return "";
                default:
                    return "";
            }
        }


        /// <summary>
        /// Retrieves a list of Unicode SQL data types.
        /// </summary>
        /// <returns>
        /// A list of <see cref="SqlDataType"/> that represents Unicode SQL data types.
        /// </returns>
        /// <remarks>
        /// The returned list includes the following SQL data types: NVarChar, NChar, NText, and NVarCharMax.
        /// </remarks>
        internal static List<SqlDataType> GetUniCodeDataType()
        {
            List<SqlDataType> rValue = new List<SqlDataType>
            {
                SqlDataType.NVarChar,
                SqlDataType.NChar,
                SqlDataType.NText,
                SqlDataType.NVarCharMax
            };
            return rValue;
        }

        /// <summary>
        /// Parses a SQL data type from a string command.
        /// </summary>
        /// <param name="dataTypeCmd">The string command representing the SQL data type.</param>
        /// <returns>A DataType object representing the parsed SQL data type.</returns>
        internal static DataType ParseDataTypeFromString(string dataTypeCmd)
        {
            // Remove white spaces
            dataTypeCmd = dataTypeCmd.Replace(" ", string.Empty).ToLowerInvariant();

            DataType rValue = new DataType();

            // remove any null
            dataTypeCmd = dataTypeCmd.Trim().Replace("[", "").Replace("]", "");
            dataTypeCmd = dataTypeCmd.Replace("NOT NULL", "");
            dataTypeCmd = dataTypeCmd.Replace("NULL", "");

            //getparts
            int firstParantesis = dataTypeCmd.IndexOf("(");

            string datatype = "";
            string tail = "";
            string val1 = "";
            string val2 = "";

            if (firstParantesis > -1)
            {
                datatype = dataTypeCmd.Substring(0, firstParantesis).Trim();
                tail = dataTypeCmd.Substring(firstParantesis + 1).Trim();

                tail = tail.Replace("(", "").Replace(")", "");
                string[] tailValues = tail.Split(',');

                if (tailValues.Length == 1)
                {
                    val1 = tailValues[0].Trim();
                }
                if (tailValues.Length == 2)
                {
                    val1 = tailValues[0].Trim();
                    val2 = tailValues[1].Trim();
                }
            }
            else
            {
                datatype = dataTypeCmd;
            }

            SqlDataType stringToDataType = DataType.SqlToEnum(datatype);
            switch (stringToDataType)
            {
                case SqlDataType.DateTime2:
                    if (val1.Length > 0)
                    {
                        int numericPrecision = 0;
                        int.TryParse(val1, out numericPrecision);
                        if (numericPrecision > 0)
                        {
                            rValue.MaximumLength = numericPrecision;
                        }
                    }
                    break;
                case SqlDataType.DateTimeOffset:

                case SqlDataType.Float:
                    if (val1.Length > 0)
                    {
                        int numericPrecision = 0;
                        int.TryParse(val1, out numericPrecision);
                        if (numericPrecision > 0)
                        {
                            rValue.NumericPrecision = numericPrecision;
                        }
                    }
                    break;
                case SqlDataType.VarChar:
                    if (val1.ToLowerInvariant() == "max")
                    {
                        rValue.SqlDataType = SqlDataType.VarCharMax;
                        rValue.MaximumLength = -1;
                    }
                    else
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.NVarChar:
                    if (val1.ToLowerInvariant() == "max")
                    {
                        rValue.SqlDataType = SqlDataType.NVarCharMax;
                        rValue.MaximumLength = -1;
                    }
                    else
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.Binary:
                    if (val1.Length > 0)
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.VarBinary:
                    if (val1.ToLowerInvariant() == "max")
                    {
                        rValue.SqlDataType = SqlDataType.VarBinaryMax;
                        rValue.MaximumLength = -1;
                    }
                    else
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.Char:
                    if (val1.Length > 0)
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.NChar:
                    if (val1.Length > 0)
                    {
                        int maximumLength = 0;
                        int.TryParse(val1, out maximumLength);
                        if (maximumLength > 0)
                        {
                            rValue.MaximumLength = maximumLength;
                        }
                    }
                    break;
                case SqlDataType.Decimal:
                    if (val1.Length > 0)
                    {
                        int numericScale = 0;
                        int numericPrecision = 0;
                        int.TryParse(val1, out numericScale);
                        int.TryParse(val2, out numericPrecision);

                        if (numericScale > 0)
                        {
                            rValue.NumericScale = numericScale;
                            rValue.NumericPrecision = numericPrecision;
                        }
                        else
                        {
                            rValue.NumericScale = 18;
                            rValue.NumericPrecision = 2;
                        }

                    }
                    break;
                case SqlDataType.Numeric:
                    if (val1.Length > 0)
                    {
                        int numericScale = 0;
                        int numericPrecision = 0;
                        int.TryParse(val1, out numericScale);
                        int.TryParse(val2, out numericPrecision);

                        if (numericScale > 0)
                        {
                            rValue.NumericScale = numericScale;
                            rValue.NumericPrecision = numericPrecision;
                        }
                        else
                        {
                            rValue.NumericScale = 18;
                            rValue.NumericPrecision = 2;
                        }

                    }
                    break;

            }

            bool DataTypeIsSet = false;
            switch (stringToDataType)
            {
                case SqlDataType.VarChar:
                    if (val1.Length > 0 && val1.Equals("MAX", StringComparison.InvariantCultureIgnoreCase))
                    {
                        rValue.SqlDataType = SqlDataType.VarCharMax;
                        rValue.MaximumLength = -1;
                        DataTypeIsSet = true;
                    }
                    break;

                case SqlDataType.NVarChar:
                    if (val1.Length > 0 && val1.Equals("MAX", StringComparison.InvariantCultureIgnoreCase))
                    {
                        rValue.SqlDataType = SqlDataType.NVarCharMax;
                        rValue.MaximumLength = -1;
                        DataTypeIsSet = true;
                    }
                    break;
                case SqlDataType.VarBinary:
                    if (val1.Length > 0 && val1.Equals("MAX", StringComparison.InvariantCultureIgnoreCase))
                    {
                        rValue.SqlDataType = SqlDataType.VarBinaryMax;
                        rValue.MaximumLength = -1;
                        DataTypeIsSet = true;
                    }
                    break;
            }

            if (DataTypeIsSet == false)
            {
                rValue.SqlDataType = stringToDataType;
            }

            return rValue;
        }



        

        /// <summary>
        /// Creates and configures a new instance of the ScriptingOptions class.
        /// </summary>
        /// <returns>
        /// A ScriptingOptions object with the desired settings for scripting SQL Server Management Objects (SMO).
        /// </returns>
        internal static ScriptingOptions SmoScriptingOptions()
        {
            ScriptingOptions sOpt = new ScriptingOptions();
            {
                sOpt.AllowSystemObjects = false;
                sOpt.AnsiPadding = false; // ANSI Padding
                sOpt.AppendToFile = false; // Append To File
                sOpt.IncludeIfNotExists = false; // CheckForError for object exis  tence
                sOpt.ContinueScriptingOnError = false; // Continue scripting on Error
                sOpt.ConvertUserDefinedDataTypesToBaseType = false; // Convert UDDTs to Base Types
                sOpt.WithDependencies = false; // Generate Scripts for Dependant Objects
                sOpt.IncludeHeaders = false; // Include Descriptive Headers
                sOpt.DriIncludeSystemNames = false; // Include system constraint names
                sOpt.Bindings = false; // Script Bindings
                sOpt.NoCollation = false; // Script Collation (Reverse of SSMS)
                sOpt.Default = true; // Script Defaults
                sOpt.ScriptDrops = false; // Script DROP or Create (set to false to only script creates)
                sOpt.ExtendedProperties = true; // Script Extended Properties
                sOpt.LoginSid = false; // Script Logins
                sOpt.Permissions = false; // Script Object-Level Permissions
                sOpt.ScriptOwner = false; // Script Owner
                sOpt.Statistics = false; // Script Statistics
                sOpt.ScriptData = false; // Types of data to script (set to false for Schema Only)
                sOpt.ChangeTracking = false; // Script Change Tracking
                sOpt.ScriptDataCompression = false; // Script Data Compression Options
                sOpt.DriAll = true; // to include referential constraints in the script
                                    //scrp.Options.DriAllConstraints = true; // to include referential constraints in the script
                                    //scrp.Options.DriAllKeys = true;
                                    // scrp.Options.DriForeignKeys = true; // Script Foreign Keys
                                    // scrp.Options.DriChecks = true; Script CheckForError Constraints
                sOpt.FullTextIndexes = true; // Script Full-Text Indexes
                sOpt.Indexes = true;   // Script Indexes
                sOpt.Triggers = true; // Script Triggers
                                      //scrp.Options.ScriptBatchTerminator = true; // ???
                                      //scrp.Options.BatchSize = 3; 
            }
            return sOpt;
        }

        /// <summary>
        /// Creates a new instance of the ScriptingOptions class with the ScriptForAlter property set to true.
        /// </summary>
        /// <returns>
        /// A ScriptingOptions object with the ScriptForAlter property set to true. This option indicates that the generated script is for altering the existing database objects.
        /// </returns>
        internal static ScriptingOptions SmoScriptingOptionsAlter()
        {
            ScriptingOptions sOpt = new ScriptingOptions();
            {
                sOpt.ScriptForAlter = true;
            }
            return sOpt;
        }

        /// <summary>
        /// Generates a comma-separated string of column names from the provided SQL table.
        /// </summary>
        /// <param name="tbl">The SQL table from which to extract column names.</param>
        /// <returns>A string containing a comma-separated list of column names from the provided table.</returns>
        internal static string ColumnsFromTable(Table tbl)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            // Use LINQ to select the formatted column names
            var columnNames = tbl.Columns.Cast<Column>().Select(col => $"[{col.Name}]");

            // Join all column names with a comma separator
            return string.Join(", ", columnNames);
        }

        /// <summary>
        /// Generates a comma-separated list of column names from the provided table, excluding the columns specified in the ignoreColumns list.
        /// </summary>
        /// <param name="tbl">The Table object from which to extract column names.</param>
        /// <param name="ignoreColumns">A list of column names to be excluded from the output.</param>
        /// <returns>A string containing a comma-separated list of column names from the provided table, excluding those specified in the ignoreColumns list.</returns>
        internal static string ColumnsFromTable(Table tbl, List<string> ignoreColumns)
        {
            // Check for null values
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            if (tbl.Columns == null)
            {
                throw new ArgumentException("Table columns cannot be null.", nameof(tbl));
            }

            if (ignoreColumns == null)
            {
                throw new ArgumentNullException(nameof(ignoreColumns), "Ignore columns list cannot be null.");
            }

            string rValue = "";
            foreach (Column col in tbl.Columns)
            {
                if (col == null)
                {
                    throw new ArgumentException("Table column cannot be null.", nameof(tbl.Columns));
                }

                if (!Functions.IsInList(ignoreColumns, col.Name))
                {
                    rValue = rValue + $",[{col.Name}]";
                }
            }

            // Ensure that rValue is not empty before attempting to remove the first character
            if (rValue.Length > 0)
            {
                rValue = rValue.Substring(1);
            }
            else
            {
                throw new InvalidOperationException("No valid columns found in the table.");
            }

            return rValue;
        }

        internal static bool IsCharacterType(DataType dataType)
        {
            return dataType != null && (dataType.SqlDataType == SqlDataType.Char ||
                                        dataType.SqlDataType == SqlDataType.VarChar ||
                                        dataType.SqlDataType == SqlDataType.Text ||
                                        dataType.SqlDataType == SqlDataType.NChar ||
                                        dataType.SqlDataType == SqlDataType.NVarChar ||
                                        dataType.SqlDataType == SqlDataType.NText ||
                                        dataType.SqlDataType == SqlDataType.SysName
                                        );
        }

        /// <summary>
        /// Generates a select expression for each column in the provided SQL table.
        /// </summary>
        /// <param name="mainTable">The main SQL table for which select expressions are to be generated.</param>
        /// <param name="selectExpressions">A DataTable containing select expressions for each column.</param>
        /// <returns>A string containing select expressions for each column in the main table. If the main table is null, an empty string is returned.</returns>
        internal static string SelectExpFromTable(Table mainTable, DataTable selectExpressions, List<string> ignoreColumns, string srcDatabaseCollation, string trgDatabaseCollation)
        {
            // Check if the main table is null
            if (mainTable == null)
            {
                return string.Empty;
            }

            // Check if ignoreColumns is null and initialize if necessary
            if (ignoreColumns == null)
            {
                ignoreColumns = new List<string>();
            }

            // Create a dictionary to store the select expressions for each column
            var selectExpressionDict = selectExpressions.AsEnumerable()
                .ToDictionary(row => row.Field<string>("ColumnName"), row => row.Field<string>("SelectExp"));

            // Modify the select expressions by replacing [ColName] with src.[ColName]
            foreach (var col in mainTable.Columns.Cast<Column>())
            {
                if (selectExpressionDict.ContainsKey(col.Name))
                {
                    selectExpressionDict[col.Name] = selectExpressionDict[col.Name].Replace($"[{col.Name}]", $"src.[{col.Name}]");
                }
            }

            // Build the select expressions for each column in the main table, excluding ignored columns
            string rValue = string.Join(", ", mainTable.Columns.Cast<Column>()
                .Where(col => !ignoreColumns.Contains(col.Name))
                .Select(col => selectExpressionDict.ContainsKey(col.Name)
                    ? $"{selectExpressionDict[col.Name]}" + (IsCharacterType(col.DataType) && (col.Collation != srcDatabaseCollation || srcDatabaseCollation != trgDatabaseCollation) ? $" COLLATE {trgDatabaseCollation}" : "") + $" AS [{col.Name}]"
                    : $"src.[{col.Name}]" + (IsCharacterType(col.DataType) && (col.Collation != srcDatabaseCollation || srcDatabaseCollation != trgDatabaseCollation) ? $" COLLATE {trgDatabaseCollation}" : "") + $" AS  [{col.Name}]"));

            return rValue;
        }


        internal static string SelectExpFromTable(Table mainTable, DataTable selectExpressions)
        {
            // Check if the main table is null
            if (mainTable == null)
            {
                return string.Empty;
            }

            // Create a dictionary to store the select expressions for each column
            var selectExpressionDict = selectExpressions.AsEnumerable()
                .ToDictionary(row => row.Field<string>("ColumnName"), row => row.Field<string>("SelectExp"));

            // Modify the select expressions by replacing [ColName] with src.[ColName]
            foreach (var col in mainTable.Columns.Cast<Column>())
            {
                if (selectExpressionDict.ContainsKey(col.Name))
                {
                    selectExpressionDict[col.Name] = selectExpressionDict[col.Name].Replace($"[{col.Name}]", $"src.[{col.Name}]");
                }
            }

            // Build the select expressions for each column in the main table, excluding ignored columns
            string rValue = string.Join(", ", mainTable.Columns.Cast<Column>()
                .Select(col => selectExpressionDict.ContainsKey(col.Name)
                    ? $"{selectExpressionDict[col.Name]} as [{col.Name}]"
                    : $"src.[{col.Name}]"));

            return rValue;
        }

        /// <summary>
        /// Generates a comma-separated string of column names from the given table, excluding those specified in the ignore lists.
        /// </summary>
        /// <param name="tbl">The table from which to extract column names.</param>
        /// <param name="ignoreColumns">A list of column names to ignore.</param>
        /// <param name="ignoreDataTypes">A list of data types to ignore.</param>
        /// <returns>A string of column names, each enclosed in square brackets and separated by commas.</returns>
        internal static string ColumnsFromTable(Table tbl, List<string> ignoreColumns, List<string> ignoreDataTypes)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            if (tbl.Columns == null)
            {
                throw new ArgumentException("Table columns cannot be null.", nameof(tbl));
            }

            if (ignoreColumns == null)
            {
                throw new ArgumentNullException(nameof(ignoreColumns), "Ignore columns list cannot be null.");
            }

            if (ignoreDataTypes == null)
            {
                throw new ArgumentNullException(nameof(ignoreDataTypes), "Ignore data types list cannot be null.");
            }

            List<string> validColumns = new List<string>();

            foreach (Column col in tbl.Columns)
            {
                if (!Functions.IsInList(ignoreColumns, col.Name) && !Functions.IsInList(ignoreDataTypes, col.DataType.Name))
                {
                    validColumns.Add($"[{col.Name}]");
                }
            }

            return string.Join(",", validColumns);
        }



        /// <summary>
        /// Generates a comma-separated string of column names from the provided table, each prefixed with 'src.'.
        /// </summary>
        /// <param name="tbl">The table from which to extract column names.</param>
        /// <returns>A string representing a comma-separated list of column names, each prefixed with 'src.'.</returns>
        internal static string ColumnsWithSrcFromTable(Table tbl)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            // Select and transform each column name into the desired format with LINQ
            var columnNames = tbl.Columns.Cast<Column>().Select(col => $"src.[{col.Name}]");

            // Join all column names with a comma separator
            return string.Join(", ", columnNames);
        }

        internal static string ColumnsWithSrcFromTable(Table tbl, List<string> ignoreColumns)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            if (ignoreColumns == null)
            {
                ignoreColumns = new List<string>();
            }

            // Use LINQ to select and format each column name, excluding the specified columns
            var columnNames = tbl.Columns.Cast<Column>()
                .Where(col => !ignoreColumns.Contains(col.Name))
                .Select(col => $"src.[{col.Name}]");

            // Join all formatted column names with a comma separator
            return string.Join(", ", columnNames);
        }

        /// <summary>
        /// Generates a comma-separated list of column names from the specified table, each prefixed with 'trg.'.
        /// </summary>
        /// <param name="tbl">The table from which the column names are to be retrieved.</param>
        /// <returns>A string containing a comma-separated list of column names, each prefixed with 'trg.'.</returns>
        internal static string ColumnsWithTrgFromTable(Table tbl)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            // Use LINQ to select and format each column name
            var columnNames = tbl.Columns.Cast<Column>().Select(col => $"trg.[{col.Name}]");

            // Join all formatted column names with a comma separator
            return string.Join(", ", columnNames);
        }

        internal static string ColumnsWithTrgFromTable(Table tbl, List<string> ignoreColumns)
        {
            if (tbl == null)
            {
                throw new ArgumentNullException(nameof(tbl), "Table cannot be null.");
            }

            if (ignoreColumns == null)
            {
                ignoreColumns = new List<string>();
            }

            // Use LINQ to select and format each column name, excluding the specified columns
            var columnNames = tbl.Columns.Cast<Column>()
                .Where(col => !ignoreColumns.Contains(col.Name))
                .Select(col => $"trg.[{col.Name}]");

            // Join all formatted column names with a comma separator
            return string.Join(", ", columnNames);
        }


        /// <summary>
        /// Generates a mapping between the column names of a SQL table.
        /// </summary>
        /// <param name="tbl">The SQL table for which the mapping is to be generated.</param>
        /// <returns>A dictionary where the keys and values are the column names of the input table.</returns>
        internal static Dictionary<string, string> SqlBulkCopyMapping(Table tbl)
        {
            Dictionary<string, string> rValue = new Dictionary<string, string>();
            foreach (Column col in tbl.Columns)
            {
                rValue.Add(col.Name, col.Name);
            }
            return rValue;
        }

        /// <summary>
        /// Generates basic scripting options for SQL Server Management Objects (SMO).
        /// </summary>
        /// <remarks>
        /// This method configures a set of basic scripting options, such as disabling system objects, ANSI padding, and object-level permissions, among others. 
        /// It is designed to provide a simplified and standardized way of generating scripts for SQL Server objects.
        /// </remarks>
        /// <returns>
        /// Returns a <see cref="ScriptingOptions"/> object with the basic scripting options set.
        /// </returns>
        internal static ScriptingOptions SmoScriptingOptionsBasic()
        {
            ScriptingOptions sOpt = new ScriptingOptions();
            {
                sOpt.AllowSystemObjects = false;
                sOpt.AnsiPadding = false; // ANSI Padding
                sOpt.AppendToFile = false; // Append To File
                sOpt.IncludeIfNotExists = false; // CheckForError for object existence
                sOpt.ContinueScriptingOnError = false; // Continue scripting on Error
                sOpt.ConvertUserDefinedDataTypesToBaseType = false; // Convert UDDTs to Base Types
                sOpt.WithDependencies = false; // Generate Scripts for Dependant Objects
                sOpt.IncludeHeaders = false; // Include Descriptive Headers
                sOpt.DriIncludeSystemNames = false; // Include system constraint names
                sOpt.Bindings = false; // Script Bindings
                sOpt.NoCollation = true; // Script Collation (Reverse of SSMS)
                sOpt.Default = false; // Script Defaults
                sOpt.ScriptDrops = false; // Script DROP or Create (set to false to only script creates)
                sOpt.ExtendedProperties = false; // Script Extended Properties
                sOpt.LoginSid = false; // Script Logins
                sOpt.Permissions = false; // Script Object-Level Permissions
                sOpt.ScriptOwner = false; // Script Owner
                sOpt.Statistics = false; // Script Statistics
                sOpt.ScriptData = false; // Types of data to script (set to false for Schema Only)
                sOpt.ChangeTracking = false; // Script Change Tracking
                sOpt.ScriptDataCompression = false; // Script Data Compression Options
                sOpt.DriAll = false; // to include referential constraints in the script
                                    //scrp.Options.DriAllConstraints = true; // to include referential constraints in the script
                                    //scrp.Options.DriAllKeys = true;
                                    // scrp.Options.DriForeignKeys = true; // Script Foreign Keys
                                    // scrp.Options.DriChecks = true; Script CheckForError Constraints
                sOpt.FullTextIndexes = false; // Script Full-Text Indexes
                sOpt.Indexes = false;   // Script Indexes
                sOpt.Triggers = false; // Script Triggers
                                       //scrp.Options.ScriptBatchTerminator = true; // ???
                                       //scrp.Options.BatchSize = 3; 
            }
            return sOpt;
        }

        /// <summary>
        /// Generates a list of script folder names.
        /// </summary>
        /// <returns>
        /// A list of strings representing the names of script folders.
        /// </returns>
        internal static List<string> ScriptFolders()
        {
            List<string> scriptFolders = new List<string>();
            scriptFolders.Add("Table");
            scriptFolders.Add("View");
            scriptFolders.Add("StoredProcedure");
            scriptFolders.Add("UserDefinedFunction");
            scriptFolders.Add("UserDefinedDataType");
            scriptFolders.Add("User");
            scriptFolders.Add("Default");
            scriptFolders.Add("Rule");
            scriptFolders.Add("DatabaseRole");
            scriptFolders.Add("ApplicationRole");
            scriptFolders.Add("DatabaseDdlTrigger");
            scriptFolders.Add("Synonym");
            scriptFolders.Add("XmlSchemaCollection");
            scriptFolders.Add("Schema");
            scriptFolders.Add("PlanGuide");
            scriptFolders.Add("UserDefinedType");
            scriptFolders.Add("UserDefinedAggregate");
            scriptFolders.Add("FullTextCatalog");
            scriptFolders.Add("UserDefinedTableType");
            scriptFolders.Add("SecurityPolicy");
            scriptFolders.Add("Sequence");
            scriptFolders.Add("Data");

            return scriptFolders;
        }

        /// <summary>
        /// Returns a list of valid lineage object types.
        /// </summary>
        /// <returns>A list of strings representing the valid lineage object types.</returns>
        /// <remarks>
        /// The valid lineage object types include "Table", "View", and "StoredProcedure".
        /// </remarks>
        internal static List<string> ValidLineageObjects()
        {
            List<string> objectTypes = new List<string>();
            objectTypes.Add("Table");
            objectTypes.Add("View");
            objectTypes.Add("StoredProcedure");
            return objectTypes;
        }


        internal static void LookupObject(Server smoSrc, Database srcDatabaseObj, string SrcSchema, string SrcObject,
            out Table lkpTable, out View lkpView, out StoredProcedure lkpSp)
        {
            lkpTable = null;
            lkpView = null;
            lkpSp = null;

            string serverName = smoSrc.Urn.Value.Split('/')[0].Replace("Server[@Name='", "").Replace("']", "");

            try
            {
               
                var objUrn = SmoHelper.CreateTableUrnFromComponents(serverName, srcDatabaseObj.Name, SrcSchema,
                    SrcObject);
                var smoObject = smoSrc.GetSmoObject(objUrn);
                if (smoObject is Table)
                {
                    lkpTable = smoObject as Table;
                    return; // Exit early if Table is found
                }
            }
            catch { }

            try
            {
                var objUrn = SmoHelper.CreateViewUrnFromComponents(serverName, srcDatabaseObj.Name, SrcSchema,
                    SrcObject);
                var smoObject = smoSrc.GetSmoObject(objUrn);
                if (smoObject is View)
                {
                    lkpView = smoObject as View;
                    return; // Exit early if View is found
                }
            }
            catch { }

            try
            {
                var objUrn = SmoHelper.CreateStoredProcedureUrnFromComponents(serverName, srcDatabaseObj.Name, SrcSchema, SrcObject);
                var smoObject = smoSrc.GetSmoObject(objUrn);
                if (smoObject is StoredProcedure)
                {
                    lkpSp = smoObject as StoredProcedure;
                    // No need for return here as it's the last check
                }
            }
            catch { }
        }

        /// <summary>
        /// Validates database objects in bulk and returns their information including URNs
        /// </summary>
        /// <param name="sqlConnection">An existing SqlConnection to the database</param>
        /// <param name="databaseName">The name of the database</param>
        /// <param name="objectsTable">DataTable containing objects to validate with Schema and Object columns</param>
        /// <returns>DataTable with validated objects including Schema, Object, ObjectType, and URN</returns>
        public static DataTable ValidateObjectsBulk(SqlConnection sqlConnection, DataTable objectsTable)
        {
            // Create result table with appropriate schema
            DataTable result = new DataTable("ValidatedObjects");
            result.Columns.Add("Schema", typeof(string));
            result.Columns.Add("Object", typeof(string));
            result.Columns.Add("ObjectType", typeof(string));
            result.Columns.Add("ObjectTypeDescription", typeof(string));
            result.Columns.Add("URN", typeof(string));
            result.Columns.Add("IsValid", typeof(bool));

            if (objectsTable == null || objectsTable.Rows.Count == 0)
                return result;

            // Extract schema and object names from the DataTable
            List<Tuple<string, string>> objectsToLookup = new List<Tuple<string, string>>();
            foreach (DataRow row in objectsTable.Rows)
            {
                string schema = row["Schema"]?.ToString() ?? string.Empty;
                string objectName = row["Object"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(schema) && !string.IsNullOrEmpty(objectName))
                {
                    objectsToLookup.Add(new Tuple<string, string>(schema, objectName));
                }
            }

            if (objectsToLookup.Count == 0)
                return result;

            // Handle batching for large number of objects
            const int batchSize = 500; // Adjust based on your database performance
            bool connectionWasOpen = (sqlConnection.State == ConnectionState.Open);

            try
            {
                if (!connectionWasOpen)
                    sqlConnection.Open();

                // Process in batches
                for (int batchStart = 0; batchStart < objectsToLookup.Count; batchStart += batchSize)
                {
                    // Define the current batch
                    int currentBatchSize = Math.Min(batchSize, objectsToLookup.Count - batchStart);
                    var currentBatch = objectsToLookup.Skip(batchStart).Take(currentBatchSize).ToList();

                    // Build query using parameters for security
                    StringBuilder queryBuilder = new StringBuilder();
                    queryBuilder.AppendLine(@"
                    SELECT 
                        SCHEMA_NAME(o.schema_id) AS [Schema], 
                        o.name AS [Object], 
                        o.type AS ObjectType,
                        CASE o.type 
                            WHEN 'U' THEN 'Table' 
                            WHEN 'V' THEN 'View' 
                            WHEN 'P' THEN 'Stored Procedure' 
                            ELSE o.type 
                        END AS ObjectTypeDescription,
                        CONCAT('Server[@Name=''', @@SERVERNAME, ''']',
                               '/Database[@Name=''', DB_NAME(), ''']',
                               '/', CASE o.type 
                                      WHEN 'U' THEN 'Table' 
                                      WHEN 'V' THEN 'View' 
                                      WHEN 'P' THEN 'StoredProcedure' 
                                      ELSE 'UnknownObjectType' 
                                    END,
                               '[@Name=''', o.name, ''' and @Schema=''', SCHEMA_NAME(o.schema_id), ''']') AS URN
                    FROM sys.objects o
                    WHERE (o.type = 'U' OR o.type = 'V' OR o.type = 'P')
                    AND (");

                    List<string> conditions = new List<string>();
                    for (int i = 0; i < currentBatch.Count; i++)
                    {
                        conditions.Add($"(SCHEMA_NAME(o.schema_id) = @Schema{i} AND o.name = @Object{i})");
                    }

                    queryBuilder.AppendLine(string.Join(" OR ", conditions));
                    queryBuilder.AppendLine(")");

                    using (SqlCommand command = new SqlCommand(queryBuilder.ToString(), sqlConnection))
                    {
                        // Add parameters
                        for (int i = 0; i < currentBatch.Count; i++)
                        {
                            command.Parameters.AddWithValue($"@Schema{i}", currentBatch[i].Item1);
                            command.Parameters.AddWithValue($"@Object{i}", currentBatch[i].Item2);
                        }

                        command.CommandTimeout = 300;
                        using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                        {
                            DataTable batchResults = new DataTable();
                            adapter.Fill(batchResults);

                            // Merge results
                            foreach (DataRow row in batchResults.Rows)
                            {
                                DataRow newRow = result.NewRow();
                                newRow["Schema"] = row["Schema"];
                                newRow["Object"] = row["Object"];
                                newRow["ObjectType"] = row["ObjectType"];
                                newRow["ObjectTypeDescription"] = row["ObjectTypeDescription"];
                                newRow["URN"] = row["URN"];
                                newRow["IsValid"] = true;
                                result.Rows.Add(newRow);
                            }
                        }
                    }
                }

                // Add entries for objects that were not found
                foreach (var objectToLookup in objectsToLookup)
                {
                    string schema = objectToLookup.Item1;
                    string objectName = objectToLookup.Item2;

                    // Check if this object was found
                    bool found = false;
                    foreach (DataRow row in result.Rows)
                    {
                        if (string.Equals(row["Schema"].ToString(), schema, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(row["Object"].ToString(), objectName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }

                    // If not found, add a row with IsValid = false
                    if (!found)
                    {
                        DataRow newRow = result.NewRow();
                        newRow["Schema"] = schema;
                        newRow["Object"] = objectName;
                        newRow["ObjectType"] = DBNull.Value;
                        newRow["ObjectTypeDescription"] = "Not Found";
                        newRow["URN"] = DBNull.Value;
                        newRow["IsValid"] = false;
                        result.Rows.Add(newRow);
                    }
                }
            }
            catch (Exception ex)
            {
                // Add error handling as needed
                // Consider adding a column for error messages or throw a more specific exception
                throw new Exception($"Error during bulk object validation: {ex.Message}", ex);
            }
            finally
            {
                if (!connectionWasOpen && sqlConnection.State == ConnectionState.Open)
                    sqlConnection.Close();
            }

            return result;
        }
    }
}


