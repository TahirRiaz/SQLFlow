using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using MySql.Data.MySqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tensorflow;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;

namespace SQLFlowCore.Common
{
    internal static class CommonDB
    {
        internal static void DropTable(string schema, string tableName, SqlConnection connection)
        {
            // Use IF EXISTS pattern for safe drops and properly escaped identifiers
            string dropSql = $@"
        IF OBJECT_ID('[{schema}].[{tableName}]', 'U') IS NOT NULL 
            DROP TABLE [{schema}].[{tableName}]";

            using (var cmd = new SqlCommand(dropSql, connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        internal static void TruncateAndBulkInsert(SqlConnection connection, string targetTableName, bool truncateTarget, DataTable dataTable, Dictionary<string, string> columnMapping)
        {
            if (truncateTarget)
            {
                // Truncate the target table
                TruncateTable(connection, targetTableName);
            }

            // Bulk insert data
            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
            {
                bulkCopy.DestinationTableName = targetTableName;

                // Handle column mappings if provided
                foreach (var kvp in columnMapping)
                {
                    bulkCopy.ColumnMappings.Add(kvp.Key, kvp.Value);
                }

                // Set options for handling identity columns
                // bulkCopy.SqlBulkCopyOptions = bulkCopy.SqlBulkCopyOptions | SqlBulkCopyOptions.KeepIdentity;

                bulkCopy.WriteToServer(dataTable);
            }
        }

        internal static Dictionary<string, string> BuildColumnMapping(DataTable dataTable)
        {
            var columnMapping = new Dictionary<string, string>();
            foreach (DataColumn column in dataTable.Columns)
            {
                columnMapping.Add(column.ColumnName, column.ColumnName); // Use the same column names by default
            }

            return columnMapping;
        }


        static void TruncateTable(SqlConnection connection, string tableName)
        {
            using (SqlCommand command = new SqlCommand($"TRUNCATE TABLE {tableName}", connection))
            {
                command.ExecuteNonQuery();
            }
        }


        internal static string GetRowCountCMD(string Database, string Schema, string TableName, string WithHint)
        {
            //dm_pdw_nodes_db_partition_stats used by synapse
            string cmdRowCount = $@"IF (OBJECT_ID('sys.dm_pdw_nodes_db_partition_stats') IS NOT NULL) 
                                                            BEGIN
                                                                SELECT  COUNT_BIG(1) as [RowCount]  from [{Database}].[{Schema}].[{TableName}]
                                                            END;
                                                            ELSE IF (OBJECT_ID('[sys].[dm_db_partition_stats]') IS NOT NULL AND Exists(SELECT * FROM fn_my_permissions (NULL, 'DATABASE') WHERE  permission_name = 'VIEW DATABASE STATE'))
                                                            BEGIN
                                                                SELECT SUM(ps.[row_count]) AS [RowCount]
                                                                  FROM [{Database}].[sys].[dm_db_partition_stats] as ps WITH({WithHint}) 
                                                                 WHERE [index_id]   < 2
                                                                   AND ps.object_id = OBJECT_ID('[{Database}].[{Schema}].[{TableName}]')
                                                                 GROUP BY ps.object_id;

                                                            END;
                                                            ELSE 
                                                            BEGIN
                                                                SELECT SUM(sPTN.Rows) AS[RowCount]
                                                                FROM [{Database}].sys.objects AS sOBJ WITH({WithHint}) 
                                                                INNER JOIN [{Database}].sys.partitions AS sPTN WITH({WithHint}) 
                                                                ON sOBJ.object_id = sPTN.object_id
                                                                WHERE sOBJ.type = 'U'
                                                                AND sOBJ.is_ms_shipped = 0x0
                                                                AND index_id< 2-- 0:Heap, 1:Clustered
                                                                AND      sOBJ.Object_id = OBJECT_ID('[{Database}].[{Schema}].[{TableName}]')
                                                                GROUP BY sOBJ.schema_id,sOBJ.name
                                                            END; ";

            return cmdRowCount;
        }

        


        

        internal static SQLObject SQLObjectFromDBSchobj(string srcObject)
        {
            var sObject = srcObject.Split('.');
            var sQLObject = new SQLObject
            {
                ObjDatabase = "",
                ObjSchema = "",
                ObjName = ""
            };
            switch (sObject.Length)
            {
                case 3:
                    sQLObject.ObjDatabase = CleanObjectName(sObject[0]);
                    sQLObject.ObjSchema = CleanObjectName(sObject[1]);
                    sQLObject.ObjName = CleanObjectName(sObject[2]);
                    sQLObject.ObjFullName = $"[{sQLObject.ObjDatabase}].[{sQLObject.ObjSchema}].[{sQLObject.ObjName}]";
                    break;
                case 2:
                    sQLObject.ObjSchema = CleanObjectName(sObject[0]);
                    sQLObject.ObjName = CleanObjectName(sObject[1]);
                    sQLObject.ObjFullName = $"[{sQLObject.ObjSchema}].[{sQLObject.ObjName}]";
                    break;
                case 1:
                    sQLObject.ObjName = CleanObjectName(sObject[0]);
                    sQLObject.ObjFullName = $"[dbo].[{sQLObject.ObjName}]";
                    break;
            }
            return sQLObject;
        }
        private static string CleanObjectName(string objectName)
        {
            return objectName.Replace("[", "").Replace("]", "");
        }

        internal static DataTable GetDataSetRows(DataSet ds, string tableName)
        {
            DataTable rValue = new DataTable();

            if (ds != null)
            {
                if (ds.Tables.Count == 1)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  select new { dt1r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 2)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()

                                  select new { dt1r, dt2r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 3)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()

                                  select new { dt1r, dt2r, dt3r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 4)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 5)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }

                else if (ds.Tables.Count == 6)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()

                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 7)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()

                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 8)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 9)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()

                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 10)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()

                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 11)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()
                                  from dt11r in ds.Tables[10].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r, dt11r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 12)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()
                                  from dt11r in ds.Tables[10].AsEnumerable()
                                  from dt12r in ds.Tables[11].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r, dt11r, dt12r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 13)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()
                                  from dt11r in ds.Tables[10].AsEnumerable()
                                  from dt12r in ds.Tables[11].AsEnumerable()
                                  from dt13r in ds.Tables[12].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r, dt11r, dt12r, dt13r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 14)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()
                                  from dt11r in ds.Tables[10].AsEnumerable()
                                  from dt12r in ds.Tables[11].AsEnumerable()
                                  from dt13r in ds.Tables[12].AsEnumerable()
                                  from dt14r in ds.Tables[13].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r, dt11r, dt12r, dt13r, dt14r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }
                else if (ds.Tables.Count == 15)
                {
                    var result2 = from dt1r in ds.Tables[0].AsEnumerable()
                                  from dt2r in ds.Tables[1].AsEnumerable()
                                  from dt3r in ds.Tables[2].AsEnumerable()
                                  from dt4r in ds.Tables[3].AsEnumerable()
                                  from dt5r in ds.Tables[4].AsEnumerable()
                                  from dt6r in ds.Tables[5].AsEnumerable()
                                  from dt7r in ds.Tables[6].AsEnumerable()
                                  from dt8r in ds.Tables[7].AsEnumerable()
                                  from dt9r in ds.Tables[8].AsEnumerable()
                                  from dt10r in ds.Tables[9].AsEnumerable()
                                  from dt11r in ds.Tables[10].AsEnumerable()
                                  from dt12r in ds.Tables[11].AsEnumerable()
                                  from dt13r in ds.Tables[12].AsEnumerable()
                                  from dt14r in ds.Tables[13].AsEnumerable()
                                  from dt15r in ds.Tables[15].AsEnumerable()
                                  select new { dt1r, dt2r, dt3r, dt4r, dt5r, dt6r, dt7r, dt8r, dt9r, dt10r, dt11r, dt12r, dt13r, dt14r, dt15r };
                    var rowList = result2.ToList();
                    rValue = ToDataTable(rowList, tableName);
                }

            }

            return rValue;
        }

        internal static List<dynamic> FlattenTable(DataTable table, string prefix = "")
        {
            List<dynamic> flatList = new List<dynamic>();
            foreach (DataRow row in table.Rows)
            {
                var rowDict = row.Table.Columns.Cast<DataColumn>()
                                   .Where(col => col.DataType != typeof(DataTable))  // Exclude columns of DataTable type
                                   .ToDictionary(col => prefix + col.ColumnName, col => row[col]);
                bool hasNestedTable = false;
                foreach (DataColumn col in table.Columns)
                {
                    // If this column is a nested table, recursively flatten it
                    if (col.DataType == typeof(DataTable))
                    {
                        hasNestedTable = true;
                        DataTable nestedTable = (DataTable)row[col];
                        string nestedPrefix = prefix + col.ColumnName + "_";

                        var nestedRows = FlattenTable(nestedTable, nestedPrefix);

                        foreach (var nestedRow in nestedRows)
                        {
                            // Merge the parent row with the flattened nested row
                            var mergedRow = new Dictionary<string, object>(rowDict);
                            foreach (var pair in nestedRow)
                            {
                                mergedRow[pair.Key] = pair.Value;
                            }
                            flatList.Add(mergedRow);
                        }
                    }
                }
                if (!hasNestedTable)
                {
                    flatList.Add(rowDict);
                }
            }
            return flatList;
        }

        internal static DataTable GetDataSetRowsX(DataSet ds, string tableName)
        {
            DataTable rValue = new DataTable();

            if (ds != null && ds.Tables.Count > 0)
            {
                var result = FlattenTable(ds.Tables[0]);

                // Join the remaining tables
                for (int i = 1; i < ds.Tables.Count; i++)
                {
                    var otherResult = FlattenTable(ds.Tables[i]);
                    result = result.Concat(otherResult).ToList();
                }

                rValue = ToDataTableFromList(result, tableName);
            }

            return rValue;
        }


        internal static DataTable ToDataTableFromList(List<dynamic> list, string tableName)
        {
            DataTable table = new DataTable(tableName);

            if (list.Count == 0)
                return table;

            // Find all unique keys
            var allKeys = new HashSet<string>();
            foreach (Dictionary<string, object> dict in list)
            {
                foreach (var key in dict.Keys)
                {
                    allKeys.Add(key);
                }
            }

            // Add columns for all unique keys
            foreach (var key in allKeys)
            {
                // Use typeof(object) as a default type. This could be changed based on your needs.
                if (typeof(DataTable) != typeof(object))
                {
                    table.Columns.Add(key, typeof(object));
                }
            }

            // Add rows
            foreach (Dictionary<string, object> dict in list)
            {
                var row = table.NewRow();
                foreach (var pair in dict)
                {
                    row[pair.Key] = pair.Value;
                }
                table.Rows.Add(row);
            }

            return table;
        }




        internal static DataTable ToDataTable<T>(List<T> items, string TableName)
        {
            DataTable dataTable = new DataTable(TableName);
            //Get all the properties

            PropertyInfo[] Props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            if (items.Count > 0)
            {
                T baseItem = items[0];
                if (baseItem != null)
                {
                    for (int i = 0; i < Props.Length; i++)
                    {
                        //inserting property values to datatable rows
                        DataRow Val = (DataRow)Props[i].GetValue(baseItem, null);

                        if (Val != null)
                            foreach (DataColumn dc in Val.Table.Columns)
                            {
                                string colName = dc.ColumnName;
                                if (dataTable.Columns.Contains(dc.ColumnName))
                                {
                                    colName = dc.ColumnName + i;
                                }

                                DataColumn newCol = new DataColumn();
                                newCol.ColumnName = colName;
                                newCol.DataType = dc.DataType;
                                dataTable.Columns.Add(newCol);
                            }
                    }
                }

                foreach (T item in items)
                {
                    int ValueCounter = 0;
                    var values = new object[dataTable.Columns.Count];
                    for (int i = 0; i < Props.Length; i++)
                    {
                        DataRow Val = (DataRow)Props[i].GetValue(item);
                        if (Val != null)
                            for (int n = 0; n < Val.ItemArray.Length; n++)
                            {
                                values[ValueCounter] = Val[n];
                                ValueCounter++;
                            }
                    }
                    dataTable.Rows.Add(values);
                }
            }


            //put a breakpoint here and check datatable
            return dataTable;
        }

        internal static string BuildCreateTableScript(DataTable Table, string DataTypeExp)
        {
            StringBuilder result = new StringBuilder();
            result.AppendFormat("CREATE TABLE [{1}] ({0}   ", Environment.NewLine, Table.TableName);

            bool FirstTime = true;
            foreach (DataColumn column in Table.Columns.OfType<DataColumn>())
            {
                string dType = DataTypeExp;

                if (dType.Length == 0)
                {
                    dType = GetSQLTypeAsString(column.DataType);
                }

                if (FirstTime)
                {
                    FirstTime = false;
                }
                else
                {
                    result.Append("   ,");
                    result.AppendFormat($"[{column.ColumnName}] {dType} {(column.AllowDBNull ? "NULL" : "NOT NULL")} {Environment.NewLine}");
                }
            }

            result.Append($") ON [PRIMARY]{Environment.NewLine}");

            return result.ToString();
        }

        private static string GetSQLTypeAsString(Type DataType)
        {
            switch (DataType.Name)
            {
                case "Boolean": return "[bit]";
                case "Char": return "[char]";
                case "SByte": return "[tinyint]";
                case "Int16": return "[smallint]";
                case "Int32": return "[int]";
                case "Int64": return "[bigint]";
                case "Byte": return "[tinyint] UNSIGNED";
                case "UInt16": return "[smallint] UNSIGNED";
                case "UInt32": return "[int] UNSIGNED";
                case "UInt64": return "[bigint] UNSIGNED";
                case "Single": return "[float]";
                case "Double": return "[double]";
                case "Decimal": return "[decimal]";
                case "DateTime": return "[datetime]";
                case "Guid": return "[uniqueidentifier]";
                case "Object": return "[variant]";
                case "String": return "[nvarchar](250)";
                default: return "[nvarchar](MAX)";
            }
        }

        internal static List<string> FixedLengthDataTypes()
        {
            List<string> FixedDataTypes = new List<string>();
            FixedDataTypes.Add("sql_variant");
            FixedDataTypes.Add("text");
            FixedDataTypes.Add("ntext");
            FixedDataTypes.Add("int");
            FixedDataTypes.Add("float");
            FixedDataTypes.Add("smallint");
            FixedDataTypes.Add("bigint");
            FixedDataTypes.Add("real");
            FixedDataTypes.Add("datetime");
            FixedDataTypes.Add("smalldatetime");
            FixedDataTypes.Add("tinyint");
            FixedDataTypes.Add("bit");
            FixedDataTypes.Add("datetime2");
            FixedDataTypes.Add("date");
            FixedDataTypes.Add("xml");
            FixedDataTypes.Add("hierarchyid");
            FixedDataTypes.Add("geography");

            return FixedDataTypes;
        }

        internal static string BuildJoinExp(List<string> colList)
        {
            string rValue = "";

            rValue = string.Join("AND ", colList.Select(s => $"src.[{s}] = trg.[{s}]"));

            //rValue = rValue.Substring(4);

            return rValue;
        }

        internal static string BuildJoinExp(List<string> srcColList, List<string> trgColList)
        {
            string rValue = "";
            int ind = 0;

            foreach (string item in srcColList)
            {
                rValue = rValue + $"AND src.[{item}] = trg.[{trgColList[ind]}]";
                ind++;
            }

            rValue = rValue.Substring(4);

            return rValue;
        }

        internal static string BuildColListWithSrc(List<string> colList)
        {
            string rValue = "";

            rValue = string.Join(", ", colList.Select(s => $"src.[{s}]"));

            //rValue = rValue.Substring(4);

            return rValue;
        }

        internal static string BuildColList(List<string> colList)
        {
            string rValue = "";

            rValue = string.Join(", ", colList.Select(s => $"[{s}]"));

            //rValue = rValue.Substring(4);

            return rValue;
        }


        internal static string BuildWhereTrgColIsNull(string IsNullColName)
        {
            string rValue = "";

            rValue = $"WHERE trg.[{IsNullColName}] IS NULL";

            return rValue;
        }

        internal static string BuildFullObjectName(string Database, string Schema, string ObjectName)
        {
            string rValue = "";

            rValue = $"[{Database}].[{Schema}].[{ObjectName}]";

            return rValue;
        }

        internal static string BuildSKeyGenPushCmd(string srcDbSchTbl, string sKeyDbSchTbl, List<string> KeyColumns, List<string> sKeyColumns, string SKeyColumn)
        {
            string rValue = "";

            string colList = BuildColList(KeyColumns);
            string colListWithSrc = BuildColListWithSrc(KeyColumns);
            string joinA = BuildJoinExp(KeyColumns);
            string whereSkey = BuildWhereTrgColIsNull(KeyColumns[0]);
            string wherePushBack = BuildWhereTrgColIsNull(SKeyColumn);
            string joinB = BuildJoinExp(KeyColumns);

            if (sKeyColumns.Count > 0)
            {
                colList = BuildColList(sKeyColumns);
                colListWithSrc = BuildColListWithSrc(KeyColumns);
                joinA = BuildJoinExp(KeyColumns, sKeyColumns);
                joinB = BuildJoinExp(sKeyColumns, KeyColumns);
                whereSkey = BuildWhereTrgColIsNull(sKeyColumns[0]);
                wherePushBack = BuildWhereTrgColIsNull(SKeyColumn);
            }

            rValue = @$"INSERT INTO {sKeyDbSchTbl} ({colList}, InsertedDate_DW, [SrcDBSchTbl])
                SELECT DISTINCT {colListWithSrc},
                       GETDATE() AS InsertedDate_DW,
                       '{srcDbSchTbl}' AS[SrcDBSchTbl]
                FROM {srcDbSchTbl} AS src
                    LEFT OUTER JOIN
                    {sKeyDbSchTbl} AS trg
                        ON {joinA}
                {whereSkey}

                UPDATE trg
                    SET trg.[{SKeyColumn}] = src.[{SKeyColumn}]
                FROM {srcDbSchTbl} AS trg
                    INNER join
                    {sKeyDbSchTbl} AS src
                        ON {joinB}
                {wherePushBack}";

            return rValue;
        }

        internal static string BuildSKeyPushCmd(string srcDbSchTbl, string sKeyDbSchTbl, List<string> KeyColumns, List<string> sKeyColumns, string SKeyColumn)
        {
            string rValue = "";

            BuildColList(KeyColumns);
            BuildColListWithSrc(KeyColumns);
            string join = BuildJoinExp(KeyColumns);
            BuildWhereTrgColIsNull(KeyColumns[0]);
            string wherePushBack = BuildWhereTrgColIsNull(SKeyColumn);

            if (sKeyColumns.Count > 0)
            {
                BuildColList(sKeyColumns);
                BuildColListWithSrc(KeyColumns);
                join = BuildJoinExp(KeyColumns, sKeyColumns);
                BuildWhereTrgColIsNull(sKeyColumns[0]);
                wherePushBack = BuildWhereTrgColIsNull(SKeyColumn);
            }

            rValue = @$"UPDATE trg
                SET trg.[{SKeyColumn}] = src.[{SKeyColumn}]
            FROM {srcDbSchTbl} AS trg
                INNER join
                {sKeyDbSchTbl} AS src
                    ON {join}
            {wherePushBack}
";

            return rValue;
        }


        internal static List<string> VariableNumericDataTypes()
        {
            List<string> NumericDataTypes = new List<string>();
            NumericDataTypes.Add("decimal");
            NumericDataTypes.Add("numeric");

            return NumericDataTypes;
        }

        internal static DbType GetSqlDbTypeFromObject(object obj)
        {
            return new SqlParameter("Dummy", obj).DbType;
        }

        internal static DbType GetSqlDbTypeFromType(Type type)
        {
            return new SqlParameter("Dummy", type.IsValueType ? Activator.CreateInstance(type) : null).DbType;
        }

        internal static List<string> VariableStringDataTypes()
        {
            List<string> StringDataTypes = new List<string>();
            StringDataTypes.Add("varchar");
            StringDataTypes.Add("nvarchar");
            StringDataTypes.Add("char");
            StringDataTypes.Add("nchar");

            return StringDataTypes;
        }


        internal static List<string> GetColListFromTable(Table tbl)
        {
            List<string> rValue = new List<string>();

            foreach (Column Col in tbl.Columns)
            {
                rValue.Add(Col.Name);
            }

            return rValue;
        }

        internal static string GetListQuoted(List<string> list)
        {
            string rValue = "";

            rValue = string.Join(",", list.Select(s => $"'{s}'"));

            return rValue;
        }

        internal static string InsertCmdUknownDimRow(string trgConString, string trgDatabase, string trgSchema, string trgTable, List<string> KeyColumnsList)
        {
            string rValue = "";

            using (SqlConnection smoSqlCon = new SqlConnection(trgConString))
            {
                ServerConnection smoSrvCon = new ServerConnection(smoSqlCon);
                Server smoSrv = new Server(smoSrvCon);
                Database smoDb = smoSrv.Databases[trgDatabase];

                if (smoDb != null)
                {
                    Table baseTable = smoDb.Tables[trgTable, trgSchema];

                    //List<string> KeyColumnsList = baseTable.Columns.;
                    List<string> collist = GetColListFromTable(baseTable);
                    string ColListWithSrc = BuildColListWithSrc(collist);
                    string ColList = BuildColList(collist);
                    string KeyColQuoted = GetListQuoted(KeyColumnsList);
                    string Join = BuildJoinExp(KeyColumnsList);
                    string WhereKey = BuildWhereTrgColIsNull(KeyColumnsList[0]);

                    rValue = $@";WITH BASE
                AS(SELECT Column_Name,
                           CASE
                               WHEN Column_Name in ({KeyColQuoted})  THEN
                                   '-1'
                               WHEN Data_Type IN('bigint', 'numeric', 'bit', 'smallint', 'decimal', 'smallmoney', 'int', 'tinyint',
                                                   'money', 'float', 'real'
                                                 ) THEN
                                   '0'
                               WHEN Data_Type IN('date', 'datetimeoffset', 'datetime2', 'smalldatetime', 'datetime', 'time') THEN
                                   '1900-01-01'
                               WHEN Data_Type IN('char', 'varchar', 'text', 'nchar', 'nvarchar', 'ntext') THEN
                                   'N/A'
                               ELSE
                                   NULL
                           END[Value]
                    FROM [{trgDatabase}].information_schema.columns col
                    WHERE Table_Name = '{trgTable}'
                          AND Table_Schema = '{trgSchema}'),
                     RowValues
                AS(SELECT *
                    FROM BASE
                        PIVOT
                        (
                            MAX([Value])
                            FOR Column_Name IN({ColList})
                        ) AS PivotTable)
                INSERT INTO [{trgDatabase}].[{trgSchema}].[{trgTable}]({ColList})
                SELECT {ColListWithSrc}
                FROM RowValues src
                    LEFT OUTER JOIN [{trgDatabase}].[{trgSchema}].[{trgTable}] trg
                        ON {Join}
                {WhereKey}";


                }

                smoSrv.ConnectionContext.Disconnect();
                smoSrvCon.Disconnect();
                smoSqlCon.Close();
                smoSqlCon.Dispose();
            }


            //string inputStr = "[nvarchar] (max)";
            //SMOHelper.ParseDataTypeFromString(inputStr);
            //SqlDataType web = Microsoft.SqlServer.Management.Smo.DataType.SqlToEnum(inputStr);
            return rValue;
        }


        internal static string BuildAddAlterColCmd(string From, string Type, SrcTrgColumns col)
        {
            //Type ALTER COLUMN or ADD
            FixedLengthDataTypes();
            List<string> variableNumericDataTypes = VariableNumericDataTypes();
            List<string> variableStringDataTypes = VariableStringDataTypes();

            string AddCMD = "";
            if (From == "src")
            {
                AddCMD = @$"ALTER TABLE [{col.trgObjDatabase}].[{col.trgObjSchema}].[{col.trgObjName}] {Type} [{col.srcColName}] {col.srcDataType}";

                if (variableNumericDataTypes.Contains(col.srcDataType.ToLower(), StringComparer.InvariantCultureIgnoreCase))
                {
                    AddCMD += @$"({col.srcPrecision},{col.srcScale})";
                }

                if (variableStringDataTypes.Contains(col.srcDataType.ToLower(), StringComparer.InvariantCultureIgnoreCase))
                {
                    string size = col.srcMaxLength == -1 ? "max" : col.srcPrecision.ToString();
                    AddCMD += @$"({size})";
                }

                AddCMD += @$" NULL ";

                if (col.srcColumnDefault.Length > 0)
                {
                    AddCMD += @$" DEFAULT {col.srcColumnDefault} ";
                }
            }

            if (From == "trg")
            {
                AddCMD = @$"ALTER TABLE [{col.trgObjDatabase}].[{col.trgObjSchema}].[{col.trgObjName}] {Type} [{col.trgColName}] {col.trgDataType}";

                if (variableNumericDataTypes.Contains(col.trgDataType.ToLower(), StringComparer.InvariantCultureIgnoreCase))
                {
                    AddCMD += @$"({col.trgPrecision},{col.srcScale})";
                }

                if (variableStringDataTypes.Contains(col.trgDataType.ToLower(), StringComparer.InvariantCultureIgnoreCase))
                {
                    string size = col.trgMaxLength == -1 ? "max" : col.trgPrecision.ToString();
                    AddCMD += @$"({size})";
                }

                AddCMD += @$" NULL ";

                if (col.trgColumnDefault.Length > 0)
                {
                    AddCMD += @$" DEFAULT {col.trgColumnDefault} ";
                }
            }

            return AddCMD;
        }

        internal static SrcTrgDataTypeStatus CompareColDataTypeSrcTrg(SqlConnection srcSqlCon, SqlConnection trgSqlCon, int commandTimeOutInSek, string srcDatabase, string srcSchema, string srcObject,
            string srcWithHint, string trgDatabase, string trgSchema, string trgObject, string trgWithHint,
            List<ObjectName> hashKeyColumns, List<ObjectName> keyColumns, ObjectName dateColumns, List<ObjectName> ignoreColumns, List<ObjectName> incrementalColumns, ObjectName identityColumns)
        {
            string srcCmd = CompareSchemaCmd(srcDatabase, srcSchema, srcObject, srcWithHint);
            string trgCmd = CompareSchemaCmd(trgDatabase, trgSchema, trgObject, trgWithHint);
            //var hashKeyCol = HashKeyColumns.Split(',');
            //var keyColumns = KeyColumns.Split(',');
            //var dateColumn = DateColumn.Split(',');
            //var ignoreColumns = IgnoreColumns.Split(',');
            //var incrementalColumns = IncrementalColumns.Split(',');
            //var identityColumn = IdentityColumn.Split(',');

            DataTable srcTbl = FetchData(srcSqlCon, srcCmd, commandTimeOutInSek);
            DataTable trgTbl = FetchData(trgSqlCon, trgCmd, commandTimeOutInSek);

            var comColumns = from srcTable in srcTbl.AsEnumerable()
                             join trgTable in trgTbl.AsEnumerable() on
                                 srcTable["ColName"].ToString().ToLower() equals trgTable["ColName"].ToString().ToLower()
                             orderby srcTable["OrdinalPosition"]
                             select new
                             {
                                 srcObjDatabase = (string)srcTable["ObjDatabase"],
                                 srcObjSchema = (string)srcTable["ObjSchema"],
                                 srcObjName = (string)srcTable["ObjName"],
                                 srcColName = (string)srcTable["ColName"],
                                 srcOrdinalPosition = (int)srcTable["OrdinalPosition"],
                                 srcDataType = (string)srcTable["DataType"],
                                 srcLength = (int)srcTable["Length"],
                                 srcPrecision = (int)srcTable["Precision"],
                                 srcScale = (int)srcTable["Scale"],
                                 srcMaxLength = (int)srcTable["MaxLength"],
                                 srcColumnDefault = (string)srcTable["ColumnDefault"],
                                 srcCollationName = (string)srcTable["CollationName"],
                                 trgObjDatabase = (string)trgTable["ObjDatabase"],
                                 trgObjSchema = (string)trgTable["ObjSchema"],
                                 trgObjName = (string)trgTable["ObjName"],
                                 trgColName = (string)trgTable["ColName"],
                                 trgOrdinalPosition = (int)trgTable["OrdinalPosition"],
                                 trgDataType = (string)trgTable["DataType"],
                                 trgLength = (int)trgTable["Length"],
                                 trgPrecision = (int)trgTable["Precision"],
                                 trgScale = (int)trgTable["Scale"],
                                 trgMaxLength = (int)trgTable["MaxLength"],
                                 trgColumnDefault = (string)trgTable["ColumnDefault"],
                                 trgCollationName = (string)trgTable["CollationName"]
                             };

            var srcColumns = from srcTable in srcTbl.AsEnumerable()
                             join trgTable in trgTbl.AsEnumerable() on
                                 srcTable["ColName"].ToString().ToLower() equals trgTable["ColName"].ToString().ToLower()
                                 into outer
                             from dtouter in outer.DefaultIfEmpty()
                             where dtouter == null
                             orderby srcTable["OrdinalPosition"]
                             select new
                             {
                                 srcObjDatabase = (string)srcTable["ObjDatabase"],
                                 srcObjSchema = (string)srcTable["ObjSchema"],
                                 srcObjName = (string)srcTable["ObjName"],
                                 srcColName = (string)srcTable["ColName"],
                                 srcOrdinalPosition = (int)srcTable["OrdinalPosition"],
                                 srcDataType = (string)srcTable["DataType"],
                                 srcLength = (int)srcTable["Length"],
                                 srcPrecision = (int)srcTable["Precision"],
                                 srcScale = (int)srcTable["Scale"],
                                 srcMaxLength = (int)srcTable["MaxLength"],
                                 srcColumnDefault = (string)srcTable["ColumnDefault"],
                                 srcCollationName = (string)srcTable["CollationName"]
                             };



            foreach (var srcColumn in srcColumns)
            {
                string trgObjDatabase = string.Empty;
                string trgObjSchema = string.Empty;
                string trgObjName = string.Empty;
                string trgColName = string.Empty;
                int trgOrdinalPosition = 0;
                string trgDataType = string.Empty;
                int trgLength = 0;
                int trgPrecision = 0;
                int trgScale = 0;
                int trgMaxLength = 0;
                string trgColumnDefault = string.Empty;
                string trgCollationName = string.Empty;

                comColumns.Append(new
                {
                    srcColumn.srcObjDatabase,
                    srcColumn.srcObjSchema,
                    srcColumn.srcObjName,
                    srcColumn.srcColName,
                    srcColumn.srcOrdinalPosition,
                    srcColumn.srcDataType,
                    srcColumn.srcLength,
                    srcColumn.srcPrecision,
                    srcColumn.srcScale,
                    srcColumn.srcMaxLength,
                    srcColumn.srcColumnDefault,
                    srcColumn.srcCollationName,
                    trgObjDatabase,
                    trgObjSchema,
                    trgObjName,
                    trgColName,
                    trgOrdinalPosition,
                    trgDataType,
                    trgLength,
                    trgPrecision,
                    trgScale,
                    trgMaxLength,
                    trgColumnDefault,
                    trgCollationName
                }
                );
            }

            DataTable result = new DataTable();
            result.Columns.Add("srcObjDatabase", typeof(string));
            result.Columns.Add("srcObjSchema", typeof(string));
            result.Columns.Add("srcObjName", typeof(string));
            result.Columns.Add("srcColName", typeof(string));
            result.Columns.Add("srcOrdinalPosition", typeof(int));
            result.Columns.Add("srcDataType", typeof(string));
            result.Columns.Add("srcLength", typeof(int));
            result.Columns.Add("srcPrecision", typeof(int));
            result.Columns.Add("srcScale", typeof(int));
            result.Columns.Add("srcMaxLength", typeof(int));
            result.Columns.Add("srcColumnDefault", typeof(string));
            result.Columns.Add("srcCollationName", typeof(string));
            result.Columns.Add("trgObjDatabase", typeof(string));
            result.Columns.Add("trgObjSchema", typeof(string));
            result.Columns.Add("trgObjName", typeof(string));
            result.Columns.Add("trgColName", typeof(string));
            result.Columns.Add("trgOrdinalPosition", typeof(int));
            result.Columns.Add("trgDataType", typeof(string));
            result.Columns.Add("trgLength", typeof(int));
            result.Columns.Add("trgPrecision", typeof(int));
            result.Columns.Add("trgScale", typeof(int));
            result.Columns.Add("trgMaxLength", typeof(int));
            result.Columns.Add("trgColumnDefault", typeof(string));
            result.Columns.Add("trgCollationName", typeof(string));
            result.Columns.Add("ColMissingInTrg", typeof(bool));
            result.Columns.Add("AddCmd", typeof(string));
            result.Columns.Add("AddCmd2", typeof(string));
            result.Columns.Add("AlterCmd", typeof(string));
            result.Columns.Add("MisMatchDataType", typeof(bool));
            result.Columns.Add("MisMatchLength", typeof(bool));
            result.Columns.Add("MisMatchPrecision", typeof(bool));
            result.Columns.Add("MisMatchCollation", typeof(bool));
            result.Columns.Add("IsHashKeyCol", typeof(bool));
            result.Columns.Add("CriticalMismatch", typeof(bool));
            result.Columns.Add("IsKeyCol", typeof(bool));
            result.Columns.Add("IsDateCol", typeof(bool));
            result.Columns.Add("IsIgnoreCol", typeof(bool));
            result.Columns.Add("IsIncrementalCol", typeof(bool));
            result.Columns.Add("IsSurrogateKeyCol", typeof(bool));

            bool CriticalMismatch = false;
            bool MisMatchDataType = false;
            SrcTrgDataTypeStatus scc = new SrcTrgDataTypeStatus();

            foreach (var col in comColumns)
            {
                SrcTrgColumns sc = new SrcTrgColumns
                {
                    srcObjDatabase = col.srcObjDatabase,
                    srcObjSchema = col.srcObjSchema,
                    srcObjName = col.srcObjName,
                    srcColName = col.srcColName,
                    srcOrdinalPosition = col.srcOrdinalPosition,
                    srcDataType = col.srcDataType,
                    srcLength = col.srcLength,
                    srcPrecision = col.srcPrecision,
                    srcScale = col.srcScale,
                    srcMaxLength = col.srcMaxLength,
                    srcColumnDefault = col.srcColumnDefault,
                    srcCollationName = col.srcCollationName,
                    trgObjDatabase = col.trgObjDatabase,
                    trgObjSchema = col.trgObjSchema,
                    trgObjName = col.trgObjName,
                    trgColName = col.trgColName,
                    trgOrdinalPosition = col.trgOrdinalPosition,
                    trgDataType = col.trgDataType,
                    trgLength = col.trgLength,
                    trgPrecision = col.trgPrecision,
                    trgScale = col.trgScale,
                    trgMaxLength = col.trgMaxLength,
                    trgColumnDefault = col.trgColumnDefault,
                    trgCollationName = col.trgCollationName
                };

                string AddCmd = BuildAddAlterColCmd("src", "ADD", sc);
                string AlterCmd = BuildAddAlterColCmd("src", "ALTER COLUMN", sc);

                string AddCmd2 = BuildAddAlterColCmd("trg", "ADD", sc);


                DataRow dr = result.NewRow();
                dr["srcObjDatabase"] = col.srcObjDatabase;
                dr["srcObjSchema"] = col.srcObjSchema;
                dr["srcObjName"] = col.srcObjName;
                dr["srcColName"] = col.srcColName;
                dr["srcOrdinalPosition"] = col.srcOrdinalPosition;
                dr["srcDataType"] = col.srcDataType;
                dr["srcLength"] = col.srcLength;
                dr["srcPrecision"] = col.srcPrecision;
                dr["srcScale"] = col.srcScale;
                dr["srcMaxLength"] = col.srcMaxLength;
                dr["srcColumnDefault"] = col.srcColumnDefault;
                dr["srcCollationName"] = col.srcCollationName;
                dr["trgObjDatabase"] = col.trgObjDatabase;
                dr["trgObjSchema"] = col.trgObjSchema;
                dr["trgObjName"] = col.trgObjName;
                dr["trgColName"] = col.trgColName;
                dr["trgOrdinalPosition"] = col.trgOrdinalPosition;
                dr["trgDataType"] = col.trgDataType;
                dr["trgLength"] = col.trgLength;
                dr["trgPrecision"] = col.trgPrecision;
                dr["trgScale"] = col.trgScale;
                dr["trgMaxLength"] = col.trgMaxLength;
                dr["trgColumnDefault"] = col.trgColumnDefault;
                dr["trgCollationName"] = col.trgCollationName;
                dr["ColMissingInTrg"] = col.trgColName == string.Empty ? true : false;
                dr["AddCmd"] = AddCmd;
                dr["AddCmd2"] = AddCmd2;
                dr["AlterCmd"] = AlterCmd;
                dr["MisMatchDataType"] = col.srcDataType != col.trgDataType;
                dr["MisMatchLength"] = col.srcLength != col.trgLength;
                dr["MisMatchPrecision"] = col.srcPrecision != col.trgPrecision;
                dr["MisMatchCollation"] = col.srcCollationName != col.srcCollationName;


                dr["IsHashKeyCol"] = hashKeyColumns.Any(hk => hk.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) ? true : false;
                dr["CriticalMismatch"] = hashKeyColumns.Any(hk => hk.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) && (col.srcDataType != col.trgDataType || col.srcLength != col.trgLength || col.srcPrecision != col.trgPrecision || col.srcCollationName != col.srcCollationName) ? true : false;
                dr["IsKeyCol"] = keyColumns.Any(k => k.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) ? true : false;
                dr["IsDateCol"] = dateColumns.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase) ? true : false;
                dr["IsIgnoreCol"] = ignoreColumns.Any(i => i.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) ? true : false;
                dr["IsIncrementalCol"] = incrementalColumns.Any(inc => inc.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) ? true : false;
                dr["IsSurrogateKeyCol"] = identityColumns.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase) ? true : false;


                CriticalMismatch = hashKeyColumns.Any(hk => hk.UnquotedName.Equals(col.srcColName, StringComparison.InvariantCultureIgnoreCase)) && (col.srcDataType != col.trgDataType || col.srcLength != col.trgLength || col.srcPrecision != col.trgPrecision || col.srcCollationName != col.srcCollationName) ? true : false;
                MisMatchDataType = col.srcDataType != col.trgDataType || col.srcLength != col.trgLength || col.srcPrecision != col.trgPrecision || col.srcCollationName != col.srcCollationName ? true : false;

                if (CriticalMismatch)
                {
                    scc.CriticalMismatch = true;
                }

                if (MisMatchDataType)
                {
                    scc.MisMatchDataType = true;
                }

                result.Rows.Add(dr);
            }

            var misMatchDataTypes = result
                        .AsEnumerable()
                        .Where(myRow => myRow.Field<bool>("MisMatchDataType") == true);

            XElement misMatchDataTypesXML = new XElement("DataTypeWarningXML");

            foreach (DataRow dr in misMatchDataTypes)
            {
                XElement Column = new XElement("Column");
                XElement ColDDL = new XElement("ColDDL");
                XElement srcColumn = new XElement("srcColumn");
                XElement trgColumn = new XElement("trgColumn");
                XElement details = new XElement("Details");
                XAttribute ColAttrib = new XAttribute("Name", dr.ItemArray[3].ToString());
                Column.Add(ColAttrib);

                for (int i = 0; i < dr.ItemArray.Count(); i++)
                {
                    string ColumnName = result.Columns[i].ColumnName;

                    XAttribute attribute = new XAttribute(ColumnName, dr.ItemArray[i].ToString());
                    if (ColumnName.StartsWith("src"))
                    {
                        srcColumn.Add(attribute);
                    }
                    else if (ColumnName.StartsWith("trg"))
                    {
                        trgColumn.Add(attribute);
                    }
                    else if (ColumnName.Equals("AddCmd"))
                    {
                        XElement srcDLL = new XElement("srcDDL", dr.ItemArray[i].ToString());
                        ColDDL.Add(srcDLL);
                    }
                    else if (ColumnName.Equals("AddCmd2"))
                    {
                        XElement trgDLL = new XElement("trgDDL", dr.ItemArray[i].ToString());
                        ColDDL.Add(trgDLL);
                    }
                    else if (ColumnName.Equals("AlterCmd"))
                    {
                        XElement trgAlterDLL = new XElement("srcAlterCmd", dr.ItemArray[i].ToString());
                        ColDDL.Add(trgAlterDLL);
                    }
                    else
                    {
                        details.Add(attribute);
                    }
                }

                Column.Add(ColDDL);
                Column.Add(srcColumn);
                Column.Add(trgColumn);
                Column.Add(details);
                misMatchDataTypesXML.Add(Column);
            }


            var colMissingInTrg = result
                       .AsEnumerable()
                       .Where(myRow => myRow.Field<bool>("ColMissingInTrg") == true);

            XElement columnWarningXML = new XElement("DataTypeWarningXML");

            foreach (DataRow dr in colMissingInTrg)
            {

                XElement Column = new XElement("Column");
                XAttribute ColAttrib = new XAttribute("Name", dr.ItemArray[3]?.ToString());
                Column.Add(ColAttrib);
                XElement ColDDL = new XElement("ColDDL");
                XElement srcColumn = new XElement("srcColumn");
                XElement trgColumn = new XElement("trgColumn");
                XElement details = new XElement("Details");

                for (int i = 0; i < dr.ItemArray.Count(); i++)
                {
                    string ColumnName = result.Columns[i].ColumnName;

                    XAttribute attribute = new XAttribute(ColumnName, dr.ItemArray[i]?.ToString());
                    if (ColumnName.StartsWith("src"))
                    {
                        srcColumn.Add(attribute);
                    }
                    else if (ColumnName.StartsWith("trg"))
                    {
                        trgColumn.Add(attribute);
                    }
                    else if (ColumnName.Equals("AddCmd"))
                    {
                        XElement srcDLL = new XElement("srcDDL", dr.ItemArray[i]?.ToString());
                        ColDDL.Add(srcDLL);
                    }
                    else if (ColumnName.Equals("AddCmd2"))
                    {
                        //dr.ItemArray[i].ToString()
                        XElement trgDLL = new XElement("trgDDL", "");
                        ColDDL.Add(trgDLL);
                    }
                    else if (ColumnName.Equals("AlterCmd"))
                    {
                        XElement trgAlterDLL = new XElement("srcAlterCmd", dr.ItemArray[i]?.ToString());
                        ColDDL.Add(trgAlterDLL);
                    }
                    else
                    {
                        details.Add(attribute);
                    }
                }

                Column.Add(ColDDL);
                Column.Add(srcColumn);
                Column.Add(trgColumn);
                Column.Add(details);
                columnWarningXML.Add(Column);

            }

            scc.compData = result;
            scc.DataTypeWarningXML = misMatchDataTypesXML.ToString();
            scc.ColumnWarningXML = columnWarningXML.ToString();
            return scc;
        }

        internal static string EscapeForXml(this string s)
        {
            string toxml = s;
            if (!string.IsNullOrEmpty(toxml))
            {
                // replace literal values with entities
                toxml = toxml.Replace("&", "&amp;");
                toxml = toxml.Replace("'", "&apos;");
                toxml = toxml.Replace("\"", "&quot;");
                toxml = toxml.Replace(">", "&gt;");
                toxml = toxml.Replace("<", "&lt;");
            }
            return toxml;
        }


        internal static DataTable DataTablesOuterJoin(DataTable table1, DataTable table2, string table1PrimaryKey, string table2PrimaryKey)
        {
            DataTable flatDataTable = new DataTable();

            foreach (DataColumn column in table2.Columns)
            {
                flatDataTable.Columns.Add(new DataColumn(column.ToString()));
            }
            foreach (DataColumn column in table1.Columns)
            {
                flatDataTable.Columns.Add(new DataColumn(column.ToString()));
            }

            // Retrun empty table with required columns to generate empty extract
            if (table1.Rows.Count <= 0 && table2.Rows.Count <= 0)
            {
                flatDataTable.Columns.Remove(table2PrimaryKey);
                return flatDataTable;
            }

            var dataBaseTable2 = table2.AsEnumerable();
            var groupDataT2toT1 = dataBaseTable2.GroupJoin(table1.AsEnumerable(),
                                    br => new { id = br.Field<string>(table2PrimaryKey).Trim().ToLower() },
                                    jr => new { id = jr.Field<string>(table1PrimaryKey).Trim().ToLower() },
                                    (baseRow, joinRow) => joinRow.DefaultIfEmpty()
                                        .Select(row => new
                                        {
                                            flatRow = baseRow.ItemArray.Concat(row == null ? new object[table1.Columns.Count] :
                                            row.ItemArray).ToArray()
                                        })).SelectMany(s => s);

            var dataBaseTable1 = table1.AsEnumerable();
            var groupDataT1toT2 = dataBaseTable1.GroupJoin(table2.Select(),
                                    br => new { id = br.Field<string>(table1PrimaryKey).Trim().ToLower() },
                                    jr => new { id = jr.Field<string>(table2PrimaryKey).Trim().ToLower() },
                                    (baseRow, joinRow) => joinRow.DefaultIfEmpty()
                                        .Select(row => new
                                        {
                                            flatRow = row == null ? new object[table2.Columns.Count].ToArray().Concat(baseRow.ItemArray).ToArray() :
                                            row.ItemArray.Concat(baseRow.ItemArray).ToArray()
                                        })).SelectMany(s => s);

            // SMOScriptingOptions the union of both group data to single set
            groupDataT2toT1 = groupDataT2toT1.Union(groupDataT1toT2);

            // Load the grouped data to newly created table 
            foreach (var result in groupDataT2toT1)
            {
                flatDataTable.LoadDataRow(result.flatRow, false);
            }

            // SMOScriptingOptions the distinct rows only
            IEnumerable rows = flatDataTable.Select().Distinct(DataRowComparer.Default);

            // Create a new distinct table with same structure as flatDataTable
            DataTable distinctFlatDataTable = flatDataTable.Clone();
            distinctFlatDataTable.Rows.Clear();

            // Push all the rows into distinct table.
            // Note: There will be two different columns for primary key1 and primary key2. In grouped rows,
            // primary key1 or primary key2 can have empty values. So copy all the primary key2 values to
            // primary key1 only if primary key1 value is empty and then delete the primary key2. So at last
            // we will get only one perimary key. Please make sure the non-deleted key must be present in 
            foreach (DataRow row in rows)
            {
                if (string.IsNullOrEmpty(row[table1PrimaryKey].ToString()))
                    row[table1PrimaryKey] = row[table2PrimaryKey];

                distinctFlatDataTable.ImportRow(row);
            }

            // Sort the table based on primary key.
            DataTable sortedFinaltable = (from orderRow in distinctFlatDataTable.AsEnumerable()
                                          orderby orderRow.Field<string>(table1PrimaryKey)
                                          select orderRow).CopyToDataTable();

            // Remove primary key2 as we have already copied it to primary key1 
            sortedFinaltable.Columns.Remove(table2PrimaryKey);

            return sortedFinaltable;
        }

        internal static string CompareSchemaCmd(string Database, string Schema, string Object, string WithHint)
        {
            string rValue = $@"SELECT table_catalog AS [ObjDatabase],
                               table_schema AS [ObjSchema],
                               table_Name AS [ObjName],
                               column_name AS [ColName],
                               [Ordinal_Position] AS [OrdinalPosition],
                               Data_Type AS DataType,
                               CAST(ISNULL(sch.CHARACTER_MAXIMUM_LENGTH, 0) as int) AS [Length],
                               CAST(ISNULL(sch.NUMERIC_PRECISION, 0) as int) AS [Precision],
                               CAST(ISNULL(sch.Numeric_scale, 0) as int) [Scale],
                               CAST(ISNULL(CHARACTER_MAXIMUM_LENGTH, 0) as int) AS [MaxLength],
                               ISNULL(Column_Default, '') AS [ColumnDefault],
                               ISNULL(Collation_Name, '') AS [CollationName]
                              FROM [{Database}].information_schema.columns sch WITH({WithHint})
                                WHERE Table_Schema = '{Schema}'
                                AND Table_Name = '{Object}';";

            return rValue;
            //causes an index scan.
            //WHERE OBJECT_ID('[' + Table_Schema + '].' + +'[' + Table_Name + ']') =  OBJECT_ID('[{Schema}].[{Object}]');";
        }


        internal static DataTable FetchData(SqlConnection sqlConnection, string query, int commandTimeOutInSek)
        {
            DataTable dt = new DataTable();

            using (SqlCommand cmd = new SqlCommand(query, sqlConnection) { CommandTimeout = commandTimeOutInSek })
            {
                dt.Load(cmd.ExecuteReader());
            }

            return dt;
        }

        internal static bool CheckIfObjectExsists(SqlConnection conn, string ObjectName, int CommandTimeout)
        {
            bool objectExsists = false;

            var cmdSQL = $"SELECT Object_Id('{ObjectName}') ObjectID";

            using (SqlCommand cmd = new SqlCommand() { CommandTimeout = CommandTimeout })
            {
                cmd.CommandText = cmdSQL;
                cmd.Connection = conn;
                cmd.CommandType = CommandType.Text;

                var getObjectId = cmd.ExecuteScalar();

                if (getObjectId != null)
                {
                    object value = getObjectId;
                    if (value != DBNull.Value)
                    {
                        objectExsists = true;
                    }

                    //objectExsists = true;
                    //result = firstColumn.ToString();
                }

                cmd.Dispose();
            }
            return objectExsists;
        }

        internal static bool CheckIfObjectsExsist(string connectionString, int CommandTimeout, List<string> DupeObjects)
        {
            bool objectExsists = false;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                foreach (string item in DupeObjects)
                {
                    var cmdSQL = $"SELECT Object_Id('{item}') ObjectID";

                    using (SqlCommand cmd = new SqlCommand() { CommandTimeout = CommandTimeout })
                    {
                        cmd.CommandText = cmdSQL;
                        cmd.Connection = conn;
                        cmd.CommandType = CommandType.Text;

                        var getObjectId = cmd.ExecuteScalar();

                        if (getObjectId != null)
                        {
                            object value = getObjectId;
                            if (value != DBNull.Value)
                            {
                                objectExsists = true;
                            }
                        }

                        cmd.Dispose();
                    }
                }

                conn.Close();
                conn.Dispose();
            }

            return objectExsists;
        }




        internal static int ExecNonQuery(SqlConnection conn, string cmdTxt, int CommandTimeout)
        {
            int rResult = 0;

            using (SqlCommand cmd = new SqlCommand() { CommandTimeout = CommandTimeout })
            {

                cmd.CommandText = cmdTxt;
                cmd.Connection = conn;
                cmd.CommandType = CommandType.Text;
                //cmd.CommandType = CommandType.StoredProcedure;
                rResult = cmd.ExecuteNonQuery();

                cmd.Dispose();
            }
            return rResult;
        }


        internal static SqlCommand BuildCmdForSPWithParam(SqlConnection conn, string cmdTxt, List<SqlParameter> paramList, int CommandTimeout)
        {
            SqlCommand rResult = null;
            using (SqlCommand cmd = new SqlCommand() { CommandTimeout = CommandTimeout })
            {
                cmd.CommandText = cmdTxt;
                cmd.Connection = conn;
                cmd.CommandType = CommandType.StoredProcedure;
                //cmd.CommandType = CommandType.StoredProcedure;
                foreach (var v in paramList)
                {
                    cmd.Parameters.Add(v);
                }
                rResult = cmd;
            }
            return rResult;
        }

        internal static int ExecDDLScript(SqlConnection connection, string cmdTxt, int commandTimeout, bool dontUseTransaction)
        {
            int result = 0;
            dontUseTransaction = true;
            if (string.IsNullOrEmpty(cmdTxt) || cmdTxt.Length < 10)
            {
                return result;
            }

            // Use a try-catch to handle exceptions and ensure transactions are properly managed
            try
            {
                using (var command = new SqlCommand(cmdTxt, connection) { CommandTimeout = commandTimeout })
                {
                    result = command.ExecuteNonQuery();
                }
            }
            catch (SqlException ex)
            {
                if (ex.Number == 2714) // Object already exists
                {
                    // Log or handle as appropriate
                    return 0;
                }
                throw; // Re-throw other exceptions
            }


            return result;
        }


        internal static string GetFileDateFromNextFlow(DataTable incrTbl, int generalTimeoutInSek)
        {
            string FileDate = "";

            //Fetch file date from related tables
            if (incrTbl.Rows != null)
            {
                if (incrTbl.Rows.Count >= 1)
                {
                    string cmdMax = "";
                    string whereXML = "";
                    DataTable expTbl = new DataTable();
                    string nxtTrgDatabase = (incrTbl.Rows[0]["nxtTrgDatabase"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string nxtTrgSchema = (incrTbl.Rows[0]["nxtTrgSchema"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string nxtTrgObject = (incrTbl.Rows[0]["nxtTrgObject"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string nxtIncrementalColumns = (incrTbl.Rows[0]["nxtIncrementalColumns"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string nxtDateColumn = (incrTbl.Rows[0]["nxtDateColumn"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string nxtIncrementalClauseExp = incrTbl.Rows[0]["nxtIncrementalClauseExp"]?.ToString() ?? string.Empty;
                    int nxtNoOfOverlapDays = int.Parse(incrTbl.Rows[0]["nxtNoOfOverlapDays"]?.ToString() ?? string.Empty);
                    bool nxtTrgIsSynapse = (incrTbl.Rows[0]["nxtTrgIsSynapse"]?.ToString() ?? string.Empty).Equals("True");


                    string nxtTenantId = incrTbl.Rows[0]["nxtTenantId"]?.ToString() ?? string.Empty;
                    string nxtApplicationId = incrTbl.Rows[0]["nxtApplicationId"]?.ToString() ?? string.Empty;
                    string nxtClientSecret = incrTbl.Rows[0]["nxtClientSecret"]?.ToString() ?? string.Empty;
                    string nxtKeyVaultName = incrTbl.Rows[0]["nxtKeyVaultName"]?.ToString() ?? string.Empty;
                    string nxtSecretName = incrTbl.Rows[0]["nxtSecretName"]?.ToString() ?? string.Empty;
                    string nxtStorageAccountName = incrTbl.Rows[0]["nxtStorageAccountName"]?.ToString() ?? string.Empty;
                    string nxtBlobContainer = incrTbl.Rows[0]["nxtBlobContainer"]?.ToString() ?? string.Empty;



                    string nxtConnectionString = incrTbl.Rows[0]["nxtConnectionString"]?.ToString() ?? string.Empty;
                    string nxtTrgWithHint = incrTbl.Rows[0]["nxtTrgWithHint"]?.ToString() ?? string.Empty;


                    List<ObjectName> oIncrementalColumns = ParseObjectNames(nxtIncrementalColumns);


                    cmdMax = GetIncWhereExp("MAX", nxtTrgDatabase, nxtTrgSchema, nxtTrgObject, nxtDateColumn, oIncrementalColumns, "MSSQL", nxtNoOfOverlapDays, nxtTrgIsSynapse, nxtIncrementalClauseExp, nxtTrgWithHint);

                    if (cmdMax.Length > 0)
                    {
                        if (nxtSecretName.Length > 0)
                        {
                            //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(nxtKeyVaultName);
                            AzureKeyVaultManager nxtKeyVaultManager = new AzureKeyVaultManager(
                                nxtTenantId,
                                nxtApplicationId,
                                nxtClientSecret,
                                nxtKeyVaultName);
                            nxtConnectionString = nxtKeyVaultManager.GetSecret(nxtSecretName);
                        }

                        ConStringParser tmpConStringParser = new ConStringParser(nxtConnectionString)
                        {
                            ConBuilderMsSql =
                             {
                                 ApplicationName = "SQLFlow Target"
                             }
                        };




                        string tmpTrgConString = tmpConStringParser.ConBuilderMsSql.ConnectionString;

                        SqlConnection tmpConnection = new SqlConnection(tmpTrgConString);
                        tmpConnection.Open();

                        string chkObject = $"[{nxtTrgSchema}].[{nxtTrgObject}]";
                        if (CheckIfObjectExsists(tmpConnection, chkObject, generalTimeoutInSek))
                        {
                            var expData = new GetData(tmpConnection, cmdMax, generalTimeoutInSek);
                            expTbl = expData.Fetch();

                            if (expTbl != null)
                            {
                                if (expTbl.Columns.Contains("XmlNodes"))
                                {
                                    whereXML = expTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;

                                    XDocument doc = XDocument.Parse(whereXML);
                                    var FileDate_DW = from node in doc.Descendants("Filters")
                                                      where node.Attribute("ColType").Value == "IncCol"
                                                      || node.Attribute("ColName").Value == "FileDate_DW"
                                                      select node.Attribute("Value").Value;

                                    if (FileDate_DW != null)
                                    {
                                        string fValue = FileDate_DW.FirstOrDefault();
                                        if (fValue != null && fValue.Length > 0)
                                        {
                                            FileDate = fValue;
                                        }
                                    }
                                }
                            }

                        }
                        tmpConnection.Close();
                        tmpConnection.Dispose();
                    }
                }

            }

            return FileDate;
        }


        internal static string TruncatePreTable(DataTable prvTbl, int generalTimeoutInSek)
        {
            string FileDate = "";

            //Fetch file date from related tables
            if (prvTbl.Rows != null)
            {
                if (prvTbl.Rows.Count >= 1)
                {
                    DataTable expTbl = new DataTable();
                    string prvTrgDatabase = (prvTbl.Rows[0]["prvTrgDatabase"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string prvTrgSchema = (prvTbl.Rows[0]["prvTrgSchema"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                    string prvTrgObject = (prvTbl.Rows[0]["prvTrgObject"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");


                    string prvTenantId = prvTbl.Rows[0]["prvTenantId"]?.ToString() ?? string.Empty;
                    string prvApplicationId = prvTbl.Rows[0]["prvApplicationId"]?.ToString() ?? string.Empty;
                    string prvClientSecret = prvTbl.Rows[0]["prvClientSecret"]?.ToString() ?? string.Empty;
                    string prvKeyVaultName = prvTbl.Rows[0]["prvKeyVaultName"]?.ToString() ?? string.Empty;
                    string prvSecretName = prvTbl.Rows[0]["prvSecretName"]?.ToString() ?? string.Empty;
                    string prvStorageAccountName = prvTbl.Rows[0]["prvStorageAccountName"]?.ToString() ?? string.Empty;
                    string prvBlobContainer = prvTbl.Rows[0]["prvBlobContainer"]?.ToString() ?? string.Empty;



                    string prvConnectionString = prvTbl.Rows[0]["prvConnectionString"]?.ToString() ?? string.Empty;
                    string prvTrgWithHint = prvTbl.Rows[0]["prvTrgWithHint"]?.ToString() ?? string.Empty;

                    if (prvSecretName.Length > 0)
                    {
                        //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(prvKeyVaultName);
                        AzureKeyVaultManager prvKeyVaultManager = new AzureKeyVaultManager(
                            prvTenantId,
                            prvApplicationId,
                            prvClientSecret,
                            prvKeyVaultName);
                        prvConnectionString = prvKeyVaultManager.GetSecret(prvSecretName);
                    }

                    ConStringParser tmpConStringParser = new ConStringParser(prvConnectionString)
                    {
                        ConBuilderMsSql =
                        {
                            ApplicationName = "SQLFlow Target"
                        }
                    };

                    string tmpTrgConString = tmpConStringParser.ConBuilderMsSql.ConnectionString;

                    SqlConnection tmpConnection = new SqlConnection(tmpTrgConString);
                    tmpConnection.Open();

                    string chkObject = $"[{prvTrgSchema}].[{prvTrgObject}]";
                    if (CheckIfObjectExsists(tmpConnection, chkObject, generalTimeoutInSek))
                    {
                        string trgTruncCmd = $"TRUNCATE TABLE [{prvTrgDatabase}].[{prvTrgSchema}].[{prvTrgObject}]";
                        ExecNonQuery(tmpConnection, trgTruncCmd, generalTimeoutInSek);
                    }

                    tmpConnection.Close();
                    tmpConnection.Dispose();
                }
            }

            return FileDate;
        }

        internal static DbConnection GetDbConnection(string srcConString, string srcDSType)
        {
            DbConnection srcCon = null;

            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                srcCon = new MySqlConnection(srcConString);
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                srcCon = new SqlConnection(srcConString);
            }

            return srcCon;
        }

        internal static DataSet GetDataSetFromSP(SqlConnection conn, string commandText, int CommandTimeout)
        {
            DataSet ds = new DataSet();
            using (SqlCommand cmd = new SqlCommand() { CommandTimeout = CommandTimeout })
            {
                cmd.CommandText = commandText;
                cmd.Connection = conn;
                cmd.CommandType = CommandType.Text;
                //cmd.CommandType = CommandType.StoredProcedure;
                SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                adapter.Fill(ds);
            }
            return ds;
        }

        internal static string IfObjectExsistsDrop(string ObjecType, SqlConnection conn, string trgDatabase, string trgSchema, string trgObject)
        {
            var cmdSQL = $"IF(OBJECT_ID('[{trgDatabase}].[{trgSchema}].[{trgObject}]') IS NOT NULL) BEGIN DROP {ObjecType} [{trgDatabase}].[{trgSchema}].[{trgObject}] END";

            //rResult = ExecDDLScript(conn, cmdSQL, CommandTimeout);
            return cmdSQL;
        }

        public static List<ObjectName> ParseObjectNames(string input)
        {
            var objects = new List<ObjectName>();

            if (string.IsNullOrWhiteSpace(input))
            {
                ObjectName obj = new ObjectName("", "");
                objects.add(obj);
                return objects;
            }

            var regex = new Regex(@"(?:\[([^\]]*)\])|([^,]+)");
            var matches = regex.Matches(input);
            if (matches.Count == 0)
            {
                // Handle case where input doesn't match our regex (e.g., single unquoted name)
                string trimmed = input.Trim();
                objects.Add(new ObjectName($"[{trimmed}]", trimmed));
            }
            else
            {
                foreach (Match match in matches)
                {
                    if (match.Groups[1].Success) // Matched content inside []
                    {
                        string unquoted = match.Groups[1].Value.Trim();
                        string quoted = $"[{unquoted}]";
                        objects.Add(new ObjectName(quoted, unquoted));
                    }
                    else if (match.Groups[2].Success) // Matched content outside []
                    {
                        string unquoted = match.Groups[2].Value.Trim();
                        string quoted = $"[{unquoted}]";
                        objects.Add(new ObjectName(quoted, unquoted));
                    }
                }
            }
            return objects;
        }


        internal static string GetIncWhereExp(string QueryType, string Database, string Schema, string Object, string dateColumn, List<ObjectName> incrementalColumns, string srcDSType, int noOfOverlapDays, bool trgIsSynapse, string IncrementalClauseExp, string WithHint)
        {
            //QueryType -- Min or Max
            string cmdMax = "";

            string WithHintExp = "";
            if (WithHint.Length > 0)
            {
                WithHintExp = $" WITH ({WithHint}) ";
            }

            dateColumn = dateColumn.Replace("[", "").Replace("]", "");

            var maxCols = "";
            var runFullLoadCols = "";

            var incExp = "";
            var incExpForXML = "";
            var dateExp = "";
            var dateExpForXML = "";
            var maxColExp = "";
            var maxColExpForXML = "";

            Hashtable maxColsArray = new Hashtable(StringComparer.InvariantCultureIgnoreCase);

            string Filter = ">";
            if (QueryType == "MIN")
            {
                Filter = ">=";
            }

            bool buildMaxquery = false;
            //Build Max Col Expresion For Incremental
            foreach (var x in incrementalColumns)
            {
                if (x.UnquotedName.Length > 0)
                {
                    buildMaxquery = true;
                    if (maxColsArray.ContainsKey(x.UnquotedName) == false)
                    {
                        maxColsArray.Add(x.UnquotedName, x.UnquotedName);
                        maxCols +=
                            $",{QueryType}([{x.UnquotedName}]) AS [{x.UnquotedName}]";
                        runFullLoadCols += $",CAST({QueryType}([{x.UnquotedName}]) as varchar(255))";
                    }
                    incExp +=
                        @$"+' AND ' + '{GetQuoteColumnName(x.UnquotedName, srcDSType)} {Filter}' + 
                        CASE WHEN (SELECT top 1 NUMERIC_PRECISION FROM [{Database}].INFORMATION_SCHEMA.COLUMNS  {WithHintExp} Where TABLE_CATALOG = '{Database}' AND TABLE_SCHEMA = '{Schema}' AND TABLE_NAME = '{Object}' AND COLUMN_NAME = '{x.UnquotedName}') is not null THEN isnull(cast([{x.UnquotedName}] as varchar(255)),'0') 
                        WHEN (SELECT top 1 IIF(Data_Type='binary' AND CHARACTER_MAXIMUM_LENGTH=8,1,null) FROM [{Database}].INFORMATION_SCHEMA.COLUMNS  {WithHintExp} Where TABLE_CATALOG = '{Database}' AND TABLE_SCHEMA = '{Schema}' AND TABLE_NAME = '{Object}' AND COLUMN_NAME = '{x.UnquotedName}') is not null THEN isnull(CONVERT(VARCHAR(MAX), CAST([{x.UnquotedName}] AS VARBINARY(MAX)), 1),'0x0000000000000000')
                        ELSE  '''' + isnull(cast([{x.UnquotedName}] as varchar(255)),'') + ''''  END  ";

                    incExpForXML +=
                        @$"UNION ALL SELECT '{x.UnquotedName}' as ColName , 
                        CASE WHEN (SELECT top 1 NUMERIC_PRECISION FROM [{Database}].INFORMATION_SCHEMA.COLUMNS  {WithHintExp} Where TABLE_CATALOG = '{Database}' AND TABLE_SCHEMA = '{Schema}' AND TABLE_NAME = '{Object}' AND COLUMN_NAME = '{x.UnquotedName}') is not null THEN isnull(cast([{x.UnquotedName}] as varchar(255)),'0') 
                        WHEN (SELECT top 1 IIF(Data_Type='binary' AND CHARACTER_MAXIMUM_LENGTH=8,1,null) FROM [{Database}].INFORMATION_SCHEMA.COLUMNS  {WithHintExp} Where TABLE_CATALOG = '{Database}' AND TABLE_SCHEMA = '{Schema}' AND TABLE_NAME = '{Object}' AND COLUMN_NAME = '{x.UnquotedName}') is not null THEN isnull(CONVERT(VARCHAR(MAX), CAST([{x.UnquotedName}] AS VARBINARY(MAX)), 1),'0x0000000000000000')
                        ELSE '''' + isnull(cast([{x.UnquotedName}] as varchar(255)),'') + '''' END  as [Value], 'IncCol' as ColType FROM  MaxQuery ";
                }
            }


            if (buildMaxquery)
            {
                incExp = incExp.Substring(10) + " AS IncExp";
                incExpForXML = incExpForXML.Substring(10);
            }

            //Build Full load check expression
            runFullLoadCols += dateColumn.Length > 0
                    ? $",CAST({QueryType}([{dateColumn}]) as varchar(255))"
                    : "";
            runFullLoadCols = ",CASE WHEN COALESCE(" + (runFullLoadCols.Length > 0 ? runFullLoadCols.Substring(1) : "NULL") +
                                ",NULL) IS NULL THEN 1 ELSE 0 END AS RunFullload";

            maxCols = maxCols +
                        (dateColumn.Length > 0
                            ? $",DateAdd(d,-{noOfOverlapDays},{QueryType}([{dateColumn}])) as [{dateColumn}]"
                            : "") + runFullLoadCols;
            maxCols = maxCols.Substring(1);

            //Build Date Col expression
            dateExp = dateColumn.Length > 0
                ? $" '{GetQuoteColumnName(dateColumn, srcDSType)} > ' + '''' + IsNull(FORMAT([{dateColumn}], 'yyyy-MM-dd HH:mm:ss.fff'),'1900-01-01') + '''' as DateExp"
                : "";

            //FORMAT
            dateExpForXML = dateColumn.Length > 0
                ? $" SELECT '[{dateColumn}]' as ColName, IsNull(FORMAT([{dateColumn}], 'yyyy-MM-dd HH:mm:ss.fff'),'1900-01-01 00:00:00.000')  as [Value], 'DateCol' as ColType From MaxQuery"
                : "";

            maxColExp = incExp + (incExp.Length > 0 && dateExp.Length > 0 ? "," : "") + dateExp + ", RunFullLoad,  (SELECT XmlNodes FROM XmlFilters) AS XmlNodes";
            maxColExp = maxColExp.Substring(1);

            maxColExpForXML = incExpForXML + (dateExpForXML.Length > 0 && incExpForXML.Length > 0 ? " UNION ALL" : "") + dateExpForXML;
            //maxColExpForXML = maxColExpForXML.Substring(1);

            if (maxCols.Length > 0 && maxColExpForXML.Length > 0)
            {
                if (trgIsSynapse)
                {
                    cmdMax = $@";With MaxQuery as
                                (SELECT {maxCols} FROM [{Database}].[{Schema}].[{Object}] WHERE 1=1 {IncrementalClauseExp}),
                                XmlDS
                                AS ( {maxColExpForXML} ),
                                XmlFilters as
                                (
                                	SELECT '<SQLFlow>'
                                            + ( SELECT '<Filters ColType=""' + ColType + ' "" ColName=""' + ColName + '""' + ' Value=""' + Value + '"">'
                                                                            + '</Filters>'
                                                                            FROM XmlDS
                                                                            ) +'</SQLFlow>' AS XmlNodes
                                )
                                SELECT {maxColExp}  FROM MaxQuery";

                }
                else
                {
                    cmdMax = $@";With MaxQuery as
                                (SELECT {maxCols} FROM [{Database}].[{Schema}].[{Object}] WHERE 1=1 {IncrementalClauseExp}),
                                XmlDS
                                AS ( {maxColExpForXML} ),
                                XmlFilters as
                                (
                                	SELECT (
                                	    SELECT ColType AS '@ColType', ColName AS '@ColName', [Value] AS '@Value'
                                	    FROM XmlDS
                                	    FOR XML PATH('Filters'), ROOT('SQLFlow')
                                	) AS XmlNodes
                                )
                                SELECT {maxColExp}  FROM MaxQuery";
                }

            }



            return cmdMax;
        }


        internal static string GetQuoteColumnName(string ColumnName, string srcDSType)
        {
            string rValue = "";

            if (srcDSType.Equals("MySQL", StringComparison.InvariantCultureIgnoreCase))
            {
                rValue = $"`{ColumnName}`";
            }

            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                rValue += $"[{ColumnName}]";
            }

            return rValue;
        }

        internal static DataTable RunQueryMySQL(string connectionString, string queryString)
        {
            DataTable dt = new DataTable();
            try
            {
                using (var conn = new MySqlConnection(connectionString))
                {
                    conn.Open();
                    using (var cmd = new MySqlCommand(queryString, conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        using (MySqlDataAdapter sda = new MySqlDataAdapter(cmd))
                        {
                            sda.Fill(dt);
                        }
                    }
                    conn.Close();
                    conn.Dispose();
                }
            }
            catch (Exception)
            {
                throw;
            }
            return dt;
        }

        internal static object ExecuteScalar(string connectionString, string queryString, string srcDSType, int TimeoutInSek)
        {
            object rValue = null;
            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new MySqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new MySqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            rValue = cmd.ExecuteScalar();
                        }
                        conn.Close();
                        conn.Dispose();
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            rValue = cmd.ExecuteScalar();
                        }
                        conn.Close();
                        conn.Dispose();
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            return rValue;
        }

        internal static object ExecuteScalarWithParam(string connectionString, string queryString, string srcDSType, int timeoutInSec, List<ParameterObject> parameters = null)
        {
            object rValue = null;

            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new MySqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new MySqlCommand(queryString, conn) { CommandTimeout = timeoutInSec })
                        {
                            // Add parameters if provided
                            if (parameters != null && parameters.Count > 0)
                            {
                                foreach (var param in parameters)
                                {
                                    if (param.mySqlParameter != null)
                                    {
                                        cmd.Parameters.Add(param.mySqlParameter);
                                    }
                                    else
                                    {
                                        object paramValue = param.Value;

                                        // Use default value if value is null or empty
                                        if (string.IsNullOrEmpty(param.Value) && !string.IsNullOrEmpty(param.DefaultValue))
                                        {
                                            paramValue = param.DefaultValue;
                                        }

                                        // Convert to appropriate type if specified
                                        if (param.DataType != null && paramValue != null)
                                        {
                                            try
                                            {
                                                paramValue = Convert.ChangeType(paramValue, param.DataType);
                                            }
                                            catch
                                            {
                                                // If conversion fails, use as is
                                            }
                                        }

                                        cmd.Parameters.AddWithValue(param.Name, paramValue ?? DBNull.Value);
                                    }
                                }
                            }

                            rValue = cmd.ExecuteScalar();
                        }
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            else if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = timeoutInSec })
                        {
                            // Add parameters if provided
                            if (parameters != null && parameters.Count > 0)
                            {
                                foreach (var param in parameters)
                                {
                                    if (param.sqlParameter != null)
                                    {
                                        cmd.Parameters.Add(param.sqlParameter);
                                    }
                                    else
                                    {
                                        object paramValue = param.Value;

                                        // Use default value if value is null or empty
                                        if (string.IsNullOrEmpty(param.Value) && !string.IsNullOrEmpty(param.DefaultValue))
                                        {
                                            paramValue = param.DefaultValue;
                                        }

                                        // Convert to appropriate type if specified
                                        if (param.DataType != null && paramValue != null)
                                        {
                                            try
                                            {
                                                paramValue = Convert.ChangeType(paramValue, param.DataType);
                                            }
                                            catch
                                            {
                                                // If conversion fails, use as is
                                            }
                                        }

                                        cmd.Parameters.AddWithValue(param.Name, paramValue ?? DBNull.Value);
                                    }
                                }
                            }

                            rValue = cmd.ExecuteScalar();
                        }
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }

            return rValue;
        }


        internal static string ExecuteScalarMSSQL(SqlConnection conn, string queryString, int TimeoutInSek, string defaultValue = "")
        {
            string rValue = defaultValue; // Set default value initially

            try
            {
                using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
                {
                    // Execute the command and store the result
                    var result = cmd.ExecuteScalar();

                    // Only overwrite the default value if the result is not null
                    if (result != null)
                    {
                        rValue = result.ToString();
                    }
                }
            }
            catch (Exception)
            {
                // Consider handling or logging the exception as appropriate for your application.
                throw;
            }
            return rValue;
        }


        internal static int[] GetStrColIndex(DataTable tbl)
        {
            List<int> strOrdValue = new List<int>();
            foreach (DataColumn col in tbl.Columns)
            {
                if (col.DataType == typeof(string) || col.DataType == typeof(char))
                {
                    strOrdValue.Add(col.Ordinal);
                }
            }
            return strOrdValue.ToArray();
        }

        internal static int[] GetStrColIndexFromSchTlb(DataTable tbl)
        {
            List<int> strOrdValue = new List<int>();
            List<string> Types = SQLCharacterTypes.CharacterTypes();
            int x = 0;
            if (tbl != null)
            {
                foreach (DataRow dr in tbl.Rows)
                {
                    var col = dr["DataTypeName"]?.ToString() ?? string.Empty;
                    if (Types.Contains(col, StringComparer.CurrentCultureIgnoreCase))
                    {
                        strOrdValue.Add(x);
                    }
                    x++;
                }
            }
            return strOrdValue.ToArray();
        }

        internal static DataTable GetQuerySchema(SqlConnection conn, string queryString, int TimeoutInSek)
        {
            DataTable dt = new DataTable();

            queryString = @"SET FMTONLY ON; " + queryString;

            using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
            {
                cmd.CommandType = CommandType.Text;
                SqlDataReader sqlReader = cmd.ExecuteReader();
                dt = sqlReader.GetSchemaTable();
                dt?.Dispose();
                cmd.Dispose();
            }

            return dt;
        }

        internal static DataTable GetData(SqlConnection conn, string queryString, int TimeoutInSek)
        {
            DataTable dt = new DataTable();
            using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
            {
                cmd.CommandType = CommandType.Text;
                var da = new SqlDataAdapter(cmd) { FillLoadOption = LoadOption.Upsert };
                da.Fill(dt);
                da.Dispose();
                cmd.Dispose();
            }

            return dt;
        }


        internal static DataTable RunQuery(string connectionString, string queryString, string srcDSType, int TimeoutInSek, List<ParameterObject> paramList)
        {
            DataTable dt = new DataTable();
            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new MySqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new MySqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
                        {
                            cmd.CommandType = CommandType.Text;
                            if (paramList != null)
                            {
                                foreach (var param in paramList)
                                {
                                    cmd.Parameters.Add(param.mySqlParameter);
                                }
                            }

                            using (MySqlDataAdapter sda = new MySqlDataAdapter(cmd))
                            {
                                sda.Fill(dt);
                            }
                        }
                        conn.Close();
                        conn.Dispose();
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase) || srcDSType.Equals("AZDB", StringComparison.InvariantCultureIgnoreCase))
            {
                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        using (var cmd = new SqlCommand(queryString, conn) { CommandTimeout = TimeoutInSek })
                        {
                            cmd.CommandType = CommandType.Text;

                            if (paramList != null)
                            {
                                foreach (var param in paramList)
                                {
                                    cmd.Parameters.Add(param.sqlParameter);
                                }
                            }

                            using (SqlDataAdapter sda = new SqlDataAdapter(cmd))
                            {
                                sda.Fill(dt);
                            }
                        }
                        conn.Close();
                        conn.Dispose();
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            return dt;
        }

        internal static string GetSrcSelect(string srcSelectColumns, string srcDatabase, string srcSchema, string srcObject, string srcWithHint, string srcWhere, string srcDSType)
        {
            string cmdSrcSelect = "";

            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` WHERE 1=1 {srcWhere} ";
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] WITH({srcWithHint}) WHERE 1=1 {srcWhere};";
            }

            return cmdSrcSelect;
        }


        internal static SortedList<int, ExpSegment> GetSrcSelectBatched(string srcSelectColumns, string srcDatabase, string srcSchema, string srcObject, string srcWithHint, string srcWhere, string srcDSType,
              DateTime InitLoadFromDate, DateTime InitLoadToDate, string InitLoadBatchBy, int InitLoadBatchSize, string DateColumn,
              int IncColumnMinValue, int IncColumMaxValue, string IncColumn,
              string trgPath, string trgFileName, string trgFiletype, bool AddTimeStampToFileName, string Subfolderpattern)

        {
            SortedList<int, ExpSegment> list = new SortedList<int, ExpSegment>();
            string cmdSrcSelect = "";
            string batchCMD = "";
            string fileName = "";
            string PrefixFileName = srcObject.Replace("[", "").Replace("]", "");
            DateTime ExportDate = DateTime.Now;

            string ExportDateSTr = "_" + ExportDate.ToString("yyyyMMddHHmmss");

            if (AddTimeStampToFileName == false)
            {
                ExportDateSTr = "";
            }
            //AddTimeStampToFileName
            if (trgFileName.Length > 0)
            {
                PrefixFileName = trgFileName;
            }

            int key = 0;
            if (InitLoadBatchBy == "D" || InitLoadBatchBy == "M")
            {
                var result = Functions.BatchByMonth(InitLoadFromDate, InitLoadToDate, InitLoadBatchSize);

                if (InitLoadBatchBy == "D")
                {
                    result = Functions.BatchByDay(InitLoadFromDate, InitLoadToDate, InitLoadBatchSize);
                }

                if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                {
                    foreach (var dateRange in result)
                    {
                        if (dateRange.Item1.ToString("yyyy-MM-dd") != dateRange.Item2.ToString("yyyy-MM-dd"))
                        {
                            batchCMD = $"AND (`{DateColumn}` >= '{dateRange.Item1.ToString("yyyy-MM-dd")}' AND `{DateColumn}` < '{dateRange.Item2.AddDays(1).ToString("yyyy-MM-dd")}')"; // Adding 1 day to the end date to ensure we get all data for the last day
                            fileName = $"{PrefixFileName}{ExportDateSTr}_{dateRange.Item1.ToString("yyyy-MM-dd")}-{dateRange.Item2.ToString("yyyy-MM-dd")}";
                            cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` src WHERE 1=1 {srcWhere} {batchCMD}";
                        }
                        else
                        {
                            //batchCMD = $"AND (`{DateColumn}` = '{dateRange.Item1.ToString("yyyy-MM-dd")}')";
                            batchCMD = $"AND (`{DateColumn}` >= '{dateRange.Item1.ToString("yyyy-MM-dd")}' AND `{DateColumn}` < '{dateRange.Item1.AddDays(1).ToString("yyyy-MM-dd")}')";
                            fileName = $"{PrefixFileName}{ExportDateSTr}_{dateRange.Item1.ToString("yyyy-MM-dd")}";
                            cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` src WHERE 1=1 {srcWhere} {batchCMD}";

                        }

                        string devider = Functions.ExtractFolderDividers(trgPath);
                        string SubfolderFromPattern = Functions.PatternToSubfolderPath(Subfolderpattern, devider, dateRange.Item1);

                        ExpSegment val = new ExpSegment
                        {
                            SqlCMD = cmdSrcSelect,
                            WhereClause = batchCMD,
                            FileName_DW = fileName,
                            FileType_DW = trgFiletype,
                            FilePath_DW = trgPath,
                            FileSubFolder_DW = SubfolderFromPattern
                        };
                        list.Add(key, val);
                        key++;
                    }

                    //Custom Value For Null Values
                    batchCMD = $"AND (`{DateColumn}` IS NULL)";
                    fileName = $"{PrefixFileName}{ExportDateSTr}_NullRows";
                    cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` src WHERE 1=1 {srcWhere} {batchCMD}";

                    ExpSegment val2 = new ExpSegment
                    {
                        NextExportDate = InitLoadToDate,
                        SqlCMD = cmdSrcSelect,
                        WhereClause = batchCMD,
                        FileName_DW = fileName,
                        FileType_DW = trgFiletype,
                        FilePath_DW = trgPath,
                        FileSubFolder_DW = ""
                    };
                    list.Add(key, val2);
                    key++;
                }
                if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
                {
                    foreach (var dateRange in result)
                    {

                        if (dateRange.Item1.ToString("yyyy-MM-dd") != dateRange.Item2.ToString("yyyy-MM-dd"))
                        {
                            batchCMD = $"AND ([{DateColumn}] >= '{dateRange.Item1.ToString("yyyy-MM-dd")}' AND [{DateColumn}] < '{dateRange.Item2.AddDays(1).ToString("yyyy-MM-dd")}')";
                            fileName = $"{PrefixFileName}{ExportDateSTr}_{dateRange.Item1.ToString("yyyy-MM-dd")}-{dateRange.Item2.ToString("yyyy-MM-dd")}";
                            cmdSrcSelect = cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] src WITH({srcWithHint}) WHERE 1=1 {srcWhere} {batchCMD}";
                        }
                        else
                        {
                            //Date column may contain time stamp and for that. Add one day to the date range to get the next day.
                            batchCMD = $"AND ([{DateColumn}] >= '{dateRange.Item1.ToString("yyyy-MM-dd")}' AND [{DateColumn}] < '{dateRange.Item1.AddDays(1).ToString("yyyy-MM-dd")}')";
                            fileName = $"{PrefixFileName}{ExportDateSTr}_{dateRange.Item1.ToString("yyyy-MM-dd")}";
                            cmdSrcSelect = cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] src WITH({srcWithHint}) WHERE 1=1 {srcWhere} {batchCMD}";
                        }

                        string devider = Functions.ExtractFolderDividers(trgPath);
                        string SubfolderFromPattern = Functions.PatternToSubfolderPath(Subfolderpattern, devider, dateRange.Item1);

                        ExpSegment val = new ExpSegment
                        {
                            NextExportDate = InitLoadToDate,
                            SqlCMD = cmdSrcSelect,
                            WhereClause = batchCMD,
                            FileName_DW = fileName,
                            FileType_DW = trgFiletype,
                            FilePath_DW = trgPath,
                            FileSubFolder_DW = SubfolderFromPattern
                        };
                        list.Add(key, val);
                        key++;
                    }

                    batchCMD = $"AND ([{DateColumn}] IS NULL)";
                    fileName = $"{PrefixFileName}{ExportDateSTr}_NullRows";
                    cmdSrcSelect = cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] src WITH({srcWithHint}) WHERE 1=1 {srcWhere} {batchCMD}";
                    ExpSegment val2 = new ExpSegment
                    {
                        SqlCMD = cmdSrcSelect,
                        WhereClause = batchCMD,
                        FileName_DW = fileName,
                        FileType_DW = trgFiletype,
                        FilePath_DW = trgPath,
                        FileSubFolder_DW = ""
                    };
                    list.Add(key, val2);
                    key++;
                }
            }
            if (InitLoadBatchBy == "K")
            {
                SortedList<int, Tuple<int, int>> KeyRanges = GetKeyRanges(IncColumnMinValue, IncColumMaxValue, InitLoadBatchSize);
                if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                {
                    // First, find the maximum length of Item1 and Item2 values in KeyRanges
                    int maxLen = 0;
                    foreach (var kRange in KeyRanges)
                    {
                        var curVal = kRange.Value;
                        maxLen = Math.Max(maxLen, curVal.Item1.ToString().Length);
                        maxLen = Math.Max(maxLen, curVal.Item2.ToString().Length);
                    }

                    foreach (var kRange in KeyRanges)
                    {
                        Tuple<int, int> curVal = kRange.Value;
                        // Format numbers with leading zeros based on the maximum length
                        string formattedItem1 = curVal.Item1.ToString().PadLeft(maxLen, '0');
                        string formattedItem2 = curVal.Item2.ToString().PadLeft(maxLen, '0');

                        batchCMD = $"AND (`{IncColumn}` >= {curVal.Item1} AND `{IncColumn}` <= {curVal.Item2})";
                        // Use formattedItem1 and formattedItem2 for fileName
                        fileName = $"{PrefixFileName}{ExportDateSTr}_{formattedItem1}-{formattedItem2}";
                        cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` WHERE 1=1 {srcWhere} {batchCMD}";

                        string devider = Functions.ExtractFolderDividers(trgPath);
                        Functions.PatternToSubfolderPath(Subfolderpattern, devider, ExportDate);

                        ExpSegment val = new ExpSegment
                        {
                            SqlCMD = cmdSrcSelect,
                            WhereClause = batchCMD,
                            FileName_DW = fileName,
                            FileType_DW = trgFiletype,
                            FilePath_DW = trgPath
                        };
                        list.Add(key, val);
                        key++;
                    }

                    //Custom Value For Null Values
                    batchCMD = $"AND (`{IncColumn}` IS NULL)";
                    fileName = $"{PrefixFileName}{ExportDateSTr}_NullRows";
                    cmdSrcSelect = $"SELECT {srcSelectColumns} FROM `{srcDatabase}`.`{srcObject}` WHERE 1=1 {srcWhere} {batchCMD}";
                    ExpSegment val2 = new ExpSegment
                    {
                        SqlCMD = cmdSrcSelect,
                        WhereClause = batchCMD,
                        FileName_DW = fileName,
                        FileType_DW = trgFiletype,
                        FilePath_DW = trgPath
                    };
                    list.Add(key, val2);
                    key++;
                }
                if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Determine the maximum length of Item1 and Item2 values in KeyRanges
                    int maxLen = 0;
                    foreach (var kRange in KeyRanges)
                    {
                        var curVal = kRange.Value;
                        maxLen = Math.Max(maxLen, curVal.Item1.ToString().Length);
                        maxLen = Math.Max(maxLen, curVal.Item2.ToString().Length);
                    }

                    foreach (var kRange in KeyRanges)
                    {
                        Tuple<int, int> curVal = kRange.Value;
                        // Format numbers with leading zeros based on the maximum length
                        string formattedItem1 = curVal.Item1.ToString().PadLeft(maxLen, '0');
                        string formattedItem2 = curVal.Item2.ToString().PadLeft(maxLen, '0');

                        batchCMD = $"AND ([{IncColumn}] >= {curVal.Item1} AND [{IncColumn}] <= {curVal.Item2})";
                        // Use formattedItem1 and formattedItem2 for fileName
                        fileName = $"{PrefixFileName}{ExportDateSTr}_{formattedItem1}-{formattedItem2}";
                        cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] WITH({srcWithHint}) WHERE 1=1 {srcWhere} {batchCMD}";

                        ExpSegment val = new ExpSegment
                        {
                            NextExportValue = IncColumMaxValue,
                            SqlCMD = cmdSrcSelect,
                            WhereClause = batchCMD,
                            FileName_DW = fileName,
                            FileType_DW = trgFiletype,
                            FilePath_DW = trgPath
                        };
                        list.Add(key, val);
                        key++;
                    }

                    batchCMD = $"AND ([{IncColumn}] IS NULL)";
                    fileName = $"{PrefixFileName}{ExportDateSTr}_NullRows";
                    cmdSrcSelect = cmdSrcSelect = $"SELECT {srcSelectColumns} FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] WITH({srcWithHint}) WHERE 1=1 {srcWhere} {batchCMD}";
                    ExpSegment val2 = new ExpSegment
                    {
                        SqlCMD = cmdSrcSelect,
                        WhereClause = batchCMD,
                        FileName_DW = fileName,
                        FileType_DW = trgFiletype,
                        FilePath_DW = trgPath
                    };
                    list.Add(key, val2);
                    key++;
                }
            }
            return list;
        }

        internal static string GetColumnList(DataTable Tbl, string srcDSType)
        {
            string selList = "";

            foreach (DataColumn col in Tbl.Columns)
            {
                string ColumnName = col.ColumnName;

                if (col.ColumnName == ColumnName)
                {
                    selList += $",{GetQuoteColumnName(ColumnName, srcDSType)}";
                }

            }
            selList = selList.Substring(1);
            return selList;
        }

        public static SortedList<int, Tuple<int, int>> GetKeyRanges(int start, int end, int maxRowsPerBucket)
        {
            SortedList<int, Tuple<int, int>> result = new SortedList<int, Tuple<int, int>>();

            if (maxRowsPerBucket <= 0 || end < start)
            {
                return result;
            }

            int totalNumbers = end - start + 1;
            // Calculate the number of buckets needed based on the maximum rows per bucket
            int effectiveBucketCount = (int)Math.Ceiling((double)totalNumbers / maxRowsPerBucket);

            int bucketNumber = 1;
            int currentStart = start;

            while (currentStart <= end && bucketNumber <= effectiveBucketCount)
            {
                // Calculate the size for current bucket
                int currentEnd = Math.Min(currentStart + maxRowsPerBucket - 1, end);
                result.Add(bucketNumber, new Tuple<int, int>(currentStart, currentEnd));

                currentStart = currentEnd + 1;
                bucketNumber++;
            }

            return result;
        }
        //public static SortedList<int, Tuple<int, int>> GetKeyRanges(int start, int end, int bucketCount)
        //{
        //    SortedList<int, Tuple<int, int>> result = new SortedList<int, Tuple<int, int>>();

        //    if (bucketCount <= 0 || end < start)
        //    {
        //        return result;
        //    }

        //    int totalNumbers = end - start + 1;
        //    int effectiveBucketCount = Math.Min(bucketCount, totalNumbers); // Use the smaller of totalNumbers or bucketCount
        //    int baseBucketSize = totalNumbers / effectiveBucketCount;
        //    int additionalBuckets = totalNumbers % effectiveBucketCount;

        //    int rangeNumber = 1;
        //    int currentStart = start;

        //    while (currentStart <= end && rangeNumber <= effectiveBucketCount) // Ensure we don't exceed number of necessary buckets
        //    {
        //        int currentBucketSize = baseBucketSize + (additionalBuckets > 0 ? 1 : 0);
        //        int currentEnd = Math.Min(currentStart + currentBucketSize - 1, end);
        //        result.Add(rangeNumber, new Tuple<int, int>(currentStart, currentEnd));

        //        currentStart = currentEnd + 1;
        //        rangeNumber++;
        //        if (additionalBuckets > 0) additionalBuckets--; // Decrement only if there were additional buckets to begin with
        //    }

        //    return result;
        //}

        //static SortedList<int, Tuple<int, int>> GetKeyRanges(int start, int end, int bucketCount)
        //{
        //    SortedList<int, Tuple<int, int>> result = new SortedList<int, Tuple<int, int>>();

        //    if (bucketCount <= 0 || end < start)
        //    {
        //        return result;
        //    }

        //    int totalNumbers = end - start + 1;
        //    int baseBucketSize = totalNumbers / bucketCount;
        //    int additionalBuckets = totalNumbers % bucketCount;

        //    int rangeNumber = 1;
        //    int currentStart = start;

        //    while (currentStart <= end)
        //    {
        //        int currentBucketSize = baseBucketSize + (additionalBuckets > 0 ? 1 : 0);
        //        int currentEnd = Math.Min(currentStart + currentBucketSize - 1, end);
        //        result.Add(rangeNumber, new Tuple<int, int>(currentStart, currentEnd));

        //        currentStart = currentEnd + 1;
        //        rangeNumber++;
        //        additionalBuckets--;
        //    }

        //    return result;
        //}



        static IEnumerable<int> DistributeInteger(int total, int divider)
        {
            if (divider == 0)
            {
                yield return 0;
            }
            else
            {
                int rest = total % divider;
                double result = total / (double)divider;

                for (int i = 0; i < divider; i++)
                {
                    if (rest-- > 0)
                        yield return (int)Math.Ceiling(result);
                    else
                        yield return (int)Math.Floor(result);
                }
            }
        }


        internal static string GetActualRowCountSQL(string srcDatabase, string srcSchema, string srcObject, string srcWithHint, string srcWhere, string srcDSType)
        {
            string cmdRowCount = "";

            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdRowCount = @$"SELECT COUNT(1) as `RowCount` FROM `{srcDatabase}`.`{srcObject}` WHERE 1=1 {srcWhere} ";
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdRowCount = $@"SELECT COUNT_BIG(1) as [RowCount] FROM [{srcDatabase}].[{srcSchema}].[{srcObject}] WHERE 1 = 1 {srcWhere}";
            }

            return cmdRowCount;
        }

        internal static string GetEstimatedRowCountSQL(string srcDatabase, string srcSchema, string srcObject, string srcWithHint, string srcWhere, string srcDSType)
        {
            string cmdRowCount = "";

            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdRowCount = @$" SELECT TABLE_ROWS AS `RowCount` 
                 FROM information_schema.TABLES 
                 WHERE TABLES.TABLE_SCHEMA = '{srcDatabase}'
                 AND TABLE_NAME = '{srcObject}'
                 AND TABLES.TABLE_TYPE = 'BASE TABLE'";
            }
            if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
            {
                cmdRowCount = $@"IF (OBJECT_ID('sys.dm_pdw_nodes_db_partition_stats') IS NOT NULL) 
                                    BEGIN
                                        SELECT COUNT_BIG(1) as [RowCount] FROM [{srcDatabase}].[{srcSchema}].[{srcObject}]  WHERE 1=1 {srcWhere} 
                                    END;
                                    ELSE IF (OBJECT_ID('[sys].[dm_db_partition_stats]') IS NOT NULL AND Exists(SELECT * FROM fn_my_permissions (NULL, 'DATABASE') WHERE  permission_name = 'VIEW DATABASE STATE'))
                                    BEGIN
                                        SELECT SUM(ps.[row_count]) AS [RowCount]
                                            FROM [{srcDatabase}].[sys].[dm_db_partition_stats] as ps WITH({srcWithHint}) 
                                            WHERE [index_id] < 2
                                            AND ps.object_id = OBJECT_ID('[{srcDatabase}].[{srcSchema}].[{srcObject}]')
                                            GROUP BY ps.object_id;
                                    END;
                                    ELSE 
                                    BEGIN
                                        SELECT SUM(sPTN.Rows) AS[RowCount]
                                        FROM [{srcDatabase}].sys.objects AS sOBJ WITH({srcWithHint}) 
                                        INNER JOIN [{srcDatabase}].sys.partitions AS sPTN WITH({srcWithHint}) 
                                        ON sOBJ.object_id = sPTN.object_id
                                        WHERE sOBJ.type = 'U'
                                        AND sOBJ.is_ms_shipped = 0x0
                                        AND index_id< 2-- 0:Heap, 1:Clustered
                                        AND sOBJ.Object_id = OBJECT_ID('[{srcDatabase}].[{srcSchema}].[{srcObject}]')
                                        GROUP BY sOBJ.schema_id,sOBJ.name
                                    END; ";
            }



            return cmdRowCount;
        }


    }
}

