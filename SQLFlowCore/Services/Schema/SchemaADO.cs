using DatabaseSchemaReader;
using DatabaseSchemaReader.Compare;
using DatabaseSchemaReader.DataSchema;
using DatabaseSchemaReader.SqlGen;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using SQLFlowCore.Common;
using SQLFlowCore.Logger;
using Microsoft.Extensions.Logging;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents the ADO schema for SQLFlowCore Engine. This class provides functionality for managing 
    /// database schemas, executing commands, and logging operations.
    /// </summary>
    /// <remarks>
    /// This class includes methods for filtering logs by flow ID and getting map dictionaries. It also 
    /// contains properties for managing SQL commands, source columns, and schema tables.
    /// </remarks>
    internal class SchemaADO
    {
        private static readonly object syncLock = new();
        private readonly object dictLock = new();
        private static SrcTargetColMap srcTargetColMap = new();
        //internal ConcurrentDictionary<string, string> ColumnMappings = new ConcurrentDictionary<string, string>();
        internal string CreateCmd = "";
        internal string SyncCmd = "";
        internal string srcColumns = "";
        internal string srcSelectColumns = "";
        internal bool TrgIsInSync = false;
        internal DatabaseTable schemaTable = null;
        internal RealTimeLogger logger;
        internal SchemaADO(RealTimeLogger _logger, SqlConnection _trgSqlCon, DbConnection _srcCon, string _srcDatabase, string _srcSchema, string _srcObject, string _srcDSType, string _trgDatabase, string _trgSchema, string _trgObject, string _ignoreColumns, DataTable _vcTbl, int _generalTimeoutInSek, string _tmpSchema, bool _syncSchema, int _FlowID)
        {
            srcTargetColMap = new SrcTargetColMap();
            //ColumnMappings = new ConcurrentDictionary<string, string>();
            logger = _logger;
            SqlConnection trgSqlCon = _trgSqlCon;
            DbConnection srcCon = _srcCon;
            string srcDatabase = _srcDatabase;
            string srcSchema = _srcSchema;
            string srcObject = _srcObject;
            string srcDSType = _srcDSType;
            string trgDatabase = _trgDatabase;
            string trgSchema = _trgSchema;
            string trgObject = _trgObject;
            string ignoreColumns = _ignoreColumns;
            DataTable vcTbl = _vcTbl;
            int generalTimeoutInSek = _generalTimeoutInSek;
            string tmpSchema = _tmpSchema;
            bool syncSchema = _syncSchema;
            int FlowID = _FlowID;

            var srcDbReader = new DatabaseReader(srcCon);
            if (srcSchema.Length > 0)
            {
                //srcDbReader.Owner = srcSchema;
            }
            else
            {
                srcDbReader.Owner = srcDatabase;
            }

            srcDbReader.AllTables();
            srcDbReader.AllViews();

            var watch = new Stopwatch();
            string cmdSQL = "";

            bool objExsits = false;

            objExsits = srcDbReader.TableExists(srcObject);

            if (objExsits == false)
            {
                objExsits = srcDbReader.ViewExists(srcObject);
            }

            if (objExsits)
            {
                DatabaseTable srcTbl = srcDbReader.AllTables().FirstOrDefault(v => v.Name == srcObject);
                DatabaseView srcView = srcDbReader.AllViews().FirstOrDefault(v => v.Name == srcObject);
                if (srcView != null)
                {
                    srcTbl = srcView;
                }

                //remove all indexes
                srcTbl.Indexes.Clear();
                srcTbl.Triggers.Clear();
                srcTbl.ForeignKeys.Clear();
                srcTbl.CheckConstraints.Clear();

                ChangeInvalidDatatypes(srcTbl);
                ProcessSourceComputedColumns(srcTbl, vcTbl);

                logger.LogInformation($"Source Object Found {srcObject}");

                var trgDbReader = new DatabaseReader(trgSqlCon);
                trgDbReader.Owner = trgSchema;//trgSchema;

                string trgSchObject = $"[{trgSchema}].[{trgObject}]";

                bool trgObjectExsists = CommonDB.CheckIfObjectExsists(trgSqlCon, trgSchObject, generalTimeoutInSek);
                if (trgObjectExsists) //
                {
                    trgDbReader.Table(trgObject);
                    logger.LogInformation($"Target Object Found {trgSchObject}");

                    //#Create tmp Object for comparing
                    DatabaseTable srcTblTrans = srcTbl;
                    srcTblTrans.Name = trgObject;
                    srcTblTrans.SchemaOwner = tmpSchema;
                    //srcTblTrans.PrimaryKey.Name = $"PK_{tmpSchema}{trgObject}";
                    SetSQLFlowFormat(srcTblTrans, vcTbl, ignoreColumns);
                    AddColumnMapping(srcTblTrans, FlowID);
                    srcColumns = GetColumnList(srcTblTrans, srcDSType);
                    srcSelectColumns = GetSelectColumns(srcTblTrans, srcDSType, vcTbl);
                    schemaTable = srcTblTrans;

                    var cmdTrg = new DdlGeneratorFactory(SqlType.SqlServer).TableGenerator(srcTblTrans);
                    cmdTrg.IncludeDefaultValues = false;
                    cmdTrg.IncludeSchema = true;
                    cmdSQL = cmdTrg.Write().Replace(" BTREE ", " ");

                    watch.Restart();
                    //Drop tmp Object before comparing
                    //CommonDB.IfObjectExsistsDrop(trgSqlCon, trgDatabase, tmpSchema, trgObject, generalTimeoutInSek);
                    cmdSQL = $"IF(OBJECT_ID('[{trgDatabase}].[{tmpSchema}].[{trgObject}]') IS NOT NULL) BEGIN DROP TABLE [{trgDatabase}].[{tmpSchema}].[{trgObject}] END;" + cmdSQL;

                    CommonDB.ExecDDLScript(trgSqlCon, cmdSQL, generalTimeoutInSek, false);
                    CreateCmd = cmdSQL;

                    //CommonDB.ExecDDLScript(trgSqlCon, cmdSQL, generalTimeoutInSek);
                    watch.Stop();
                    logger.LogInformation($"Temp object created for comparison {FlowID} ({(watch.ElapsedMilliseconds / 1000).ToString()} sec)");
                    //HandlerSchemaSyncLog(this, $"Info: Temp Object Created Comparison ({(watch.ElapsedMilliseconds / 1000).ToString()} sec) {Environment.NewLine}");

                    if (syncSchema)
                    {
                        var tmpDbReader = new DatabaseReader(trgSqlCon);
                        tmpDbReader.Owner = tmpSchema;//trgSchema;

                        string chkTmpObject = $"[{tmpSchema}].[{trgObject}]";

                        if (CommonDB.CheckIfObjectExsists(trgSqlCon, chkTmpObject, generalTimeoutInSek))
                        {
                            DatabaseTable tmpTbl = tmpDbReader.Table(trgObject);
                            tmpTbl.SchemaOwner = trgSchema;
                            var comparison = new CompareSchemas(trgDbReader.DatabaseSchema, tmpDbReader.DatabaseSchema);
                            var script = comparison.Execute();
                            SyncCmd = script;

                            if (script.Length > 0)
                            {
                                //Target Object Is Not In Sync Drop And Re-create
                                watch.Restart();
                                string dupeTbl = CommonDB.IfObjectExsistsDrop("TABLE", trgSqlCon, trgDatabase, trgSchema, trgObject);
                                srcTblTrans.Name = trgObject;
                                srcTblTrans.SchemaOwner = trgSchema;
                                cmdTrg = new DdlGeneratorFactory(SqlType.SqlServer).TableGenerator(srcTblTrans);
                                cmdTrg.IncludeDefaultValues = false;
                                cmdTrg.IncludeSchema = true;
                                cmdSQL = cmdTrg.Write().Replace(" BTREE ", " ");
                                cmdSQL = dupeTbl + Environment.NewLine + cmdSQL;
                                CommonDB.ExecDDLScript(trgSqlCon, cmdSQL, generalTimeoutInSek, false);
                                watch.Stop();
                                logger.LogInformation($"Target schema syncronized {FlowID} ({(watch.ElapsedMilliseconds / 1000).ToString()} sec)");
                                TrgIsInSync = true;
                            }
                            else
                            {
                                logger.LogInformation($"Target schema is in sync {FlowID}");
                                
                                TrgIsInSync = true;
                            }

                            //Drop tmp Object before closing
                            string dropTmpCMD = CommonDB.IfObjectExsistsDrop("TABLE", trgSqlCon, trgDatabase, tmpSchema, trgObject);
                            CommonDB.ExecDDLScript(trgSqlCon, dropTmpCMD, generalTimeoutInSek, false);
                        }
                    }
                    else
                    {
                        TrgIsInSync = true;
                        logger.LogInformation($"Target schema is in sync {FlowID}");
                    }
                }
                else
                {
                    logger.LogInformation($"Target object not found {FlowID}");
                    new Random();
                    DatabaseTable srcTblTrans = srcTbl;
                    srcTblTrans.Name = trgObject;
                    srcTblTrans.SchemaOwner = trgSchema;
                    //srcTblTrans.PrimaryKey.Name = $"PK_{trgSchema}{trgObject}";

                    SetSQLFlowFormat(srcTblTrans, vcTbl, ignoreColumns);
                    AddColumnMapping(srcTblTrans, FlowID);
                    srcColumns = GetColumnList(srcTblTrans, srcDSType);
                    srcSelectColumns = GetSelectColumns(srcTblTrans, srcDSType, vcTbl);
                    schemaTable = srcTblTrans;

                    var cmdTrg = new DdlGeneratorFactory(SqlType.SqlServer).TableGenerator(srcTblTrans);
                    cmdTrg.IncludeDefaultValues = false;
                    cmdTrg.IncludeSchema = true;


                    cmdSQL = cmdTrg.Write().Replace(" BTREE ", " ");


                    CreateCmd = cmdSQL;
                    watch.Restart();
                    CommonDB.ExecDDLScript(trgSqlCon, cmdSQL, generalTimeoutInSek, false);
                    watch.Stop();
                    logger.LogInformation($"Target table created ({(watch.ElapsedMilliseconds / 1000).ToString()} sec) {Environment.NewLine}", FlowID);
                    TrgIsInSync = true;
                }
            }
            else
            {
                logger.LogInformation($"Source object not found {FlowID.ToString()}");
            }

        }

        /// <summary>
        /// Changes the data types of the columns in the provided database table that are considered invalid.
        /// </summary>
        /// <param name="srcTbl">The source database table whose column data types are to be changed.</param>
        /// <remarks>
        /// This method uses a dictionary to map source data types to target SQL data types. If a column's data type in the source table matches a key in the dictionary, 
        /// the column's data type is changed to the corresponding value in the dictionary.
        /// </remarks>
        private void ChangeInvalidDatatypes(DatabaseTable srcTbl)
        {
            // Dictionary mapping source data types to target SQL data types
            var dataTypeMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "enum", "NVARCHAR(255)" },
                { "guid", "uniqueidentifier" },
                { "char(36)", "uniqueidentifier" },
                //{ "TEXT", "NVARCHAR(MAX)" },
                
            };

            if (srcTbl == null)
            {
                Console.WriteLine("Table is null.");
                return;
            }

            foreach (var column in srcTbl.Columns)
            {
                if (dataTypeMappings.TryGetValue(column.DataType.TypeName, out string targetDataType1))
                {
                    // Change the column's data type to the mapped target data type
                    column.DbDataType = targetDataType1;
                }

                if (dataTypeMappings.TryGetValue(column.DbDataType, out string targetDataType2))
                {
                    // Change the column's data type to the mapped target data type
                    column.DbDataType = targetDataType2;
                }

            }
        }

        /// <summary>
        /// Retrieves a dictionary that maps source columns to target columns for a specific flow.
        /// </summary>
        /// <param name="flowId">The identifier of the flow for which the mapping is retrieved.</param>
        /// <returns>A dictionary where the key is the source column name and the value is the target column name.</returns>

        internal Dictionary<string, string> GetMapDictionary(int flowId)
        {
            return srcTargetColMap.GetMapDictionary(flowId);
        }

        /// <summary>
        /// Adds column mappings for a given database table.
        /// </summary>
        /// <param name="Tbl">The database table for which column mappings are to be added.</param>
        /// <param name="flowID">The ID of the flow process.</param>
        /// <remarks>
        /// This method iterates over each column in the provided table and creates a new FlowMap object for each column.
        /// The FlowMap object maps the source column to the target column with the same name.
        /// </remarks>
        private static void AddColumnMapping(DatabaseTable Tbl, int flowID)
        {
            foreach (DatabaseColumn col in Tbl.Columns)
            {
                FlowMap flowMap = new FlowMap
                {
                    FlowID = flowID,
                    SourceColumn = col.Name,
                    TargetColumn = col.Name
                };
                srcTargetColMap.AddItem(flowMap);

                //if (srcTargetColMap.ItemExists(flowMap) ==false)
                //{

                //}
            }
        }

        /// <summary>
        /// Generates a comma-separated list of column names from the provided DatabaseTable.
        /// </summary>
        /// <param name="Tbl">The DatabaseTable object from which to extract column names.</param>
        /// <param name="srcDSType">The type of the source data store. This is used to format the column names correctly for the specific database type.</param>
        /// <returns>A string containing a comma-separated list of column names.</returns>
        private static string GetColumnList(DatabaseTable Tbl, string srcDSType)
        {
            string colList = "";

            foreach (DatabaseColumn col in Tbl.Columns)
            {
                if (srcDSType.Equals("MySQL", StringComparison.InvariantCultureIgnoreCase))
                {
                    colList += $",`{col.Name}`";
                }

                if (srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
                {
                    colList += $",[{col.Name}]";
                }
            }
            colList = colList.Substring(1);

            return colList;
        }

        /// <summary>
        /// Applies the SQLFlow format to the given database table.
        /// </summary>
        /// <param name="srcTblTrans">The source database table to be formatted.</param>
        /// <param name="vcTbl">The DataTable containing virtual columns.</param>
        /// <param name="ignoreColumns">A string containing columns to be ignored.</param>
        /// <returns>The formatted DatabaseTable.</returns>
        /// <remarks>
        /// This method applies several transformations to the source table, including adding virtual columns, removing identity on columns, removing nullable property, removing ignored columns, removing primary key columns, and removing constraints.
        /// </remarks>
        private static DatabaseTable SetSQLFlowFormat(DatabaseTable srcTblTrans, DataTable vcTbl, string ignoreColumns)
        {
            srcTblTrans = AddVirtualColumns(srcTblTrans, vcTbl);
            srcTblTrans = RemoveIdentityOnColumns(srcTblTrans);
            srcTblTrans = RemoveAllowNulls(srcTblTrans);
            srcTblTrans = RemoveIgnoredColumns(srcTblTrans, ignoreColumns);
            srcTblTrans = RemovePKColumns(srcTblTrans);
            srcTblTrans = RemoveConstraints(srcTblTrans);
            return srcTblTrans;
        }

        /// <summary>
        /// Generates a select list of columns for a SQL query from a given table.
        /// </summary>
        /// <param name="Tbl">The DatabaseTable object representing the table for which the select list is to be generated.</param>
        /// <param name="srcDSType">The source data source type.</param>
        /// <param name="vcTbl">A DataTable containing virtual columns.</param>
        /// <returns>A string representing a select list of columns for a SQL query.</returns>
        private static string GetSelectColumns(DatabaseTable Tbl, string srcDSType, DataTable vcTbl)
        {
            string selList = "";

            foreach (DatabaseColumn col in Tbl.Columns)
            {
                if (col.Tag?.ToString() == "IsVirtual")
                {
                    foreach (DataRow dataRow in vcTbl.Rows)
                    {
                        string ColumnName = (dataRow["ColumnName"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                        string SelectExp = dataRow["SelectExp"]?.ToString() ?? string.Empty;

                        if (col.Name == ColumnName)
                        {
                            selList += $",{SelectExp} as {CommonDB.GetQuoteColumnName(ColumnName, srcDSType)}";
                        }
                    }
                }
                else
                {
                    selList += $",{CommonDB.GetQuoteColumnName(col.Name, srcDSType)}";
                }

            }
            selList = selList.Substring(1);
            return selList;
        }

        /// <summary>
        /// Adds virtual columns to the specified database table.
        /// </summary>
        /// <param name="Tbl">The database table to which virtual columns will be added.</param>
        /// <param name="vcTbl">A DataTable containing the virtual columns to be added. Each DataRow should contain the column name, data type, length, precision, scale, and a tag indicating it is a virtual column.</param>
        /// <returns>The modified database table with the added virtual columns.</returns>
        private static DatabaseTable AddVirtualColumns(DatabaseTable Tbl, DataTable vcTbl)
        {
            foreach (DataRow dr in vcTbl.Rows)
            {
                DatabaseColumn dc = new DatabaseColumn
                {
                    Name = dr["ColumnName"].ToString(),
                    DbDataType = dr["DataType"].ToString(),
                    Tag = "IsVirtual"
                };
                string Length = dr["Length"]?.ToString() ?? string.Empty;
                string Precision = dr["Precision"]?.ToString() ?? string.Empty;
                string Scale = dr["Scale"]?.ToString() ?? string.Empty;

                if (Length.Length > 0)
                {
                    dc.Length = int.Parse(Length);
                }

                if (Precision.Length > 0)
                {
                    dc.Precision = int.Parse(Precision);
                }

                if (Scale.Length > 0)
                {
                    dc.Scale = int.Parse(Scale);
                }

                dc.Nullable = true;
                Tbl.Columns.Add(dc);
            }

            return Tbl;
        }

        /// <summary>
        /// Removes the specified columns from the given database table.
        /// </summary>
        /// <param name="Tbl">The database table from which to remove columns.</param>
        /// <param name="ignoreColumns">A comma-separated string of column names to be removed.</param>
        /// <returns>The updated database table with the specified columns removed.</returns>
        private static DatabaseTable RemoveIgnoredColumns(DatabaseTable Tbl, string ignoreColumns)
        {
            string[] cols = ignoreColumns.Split(',');

            foreach (string col in cols)
            {
                DatabaseColumn dc = Tbl.FindColumn(col);
                Tbl.Columns.Remove(dc);
            }

            return Tbl;
        }

        /// <summary>
        /// Removes the identity specification from all columns in the provided database table.
        /// </summary>
        /// <param name="Tbl">The database table from which to remove the identity specification.</param>
        /// <returns>The modified database table with identity specification removed from all columns.</returns>
        private static DatabaseTable RemoveIdentityOnColumns(DatabaseTable Tbl)
        {
            foreach (DatabaseColumn col in Tbl.Columns)
            {
                col.IsAutoNumber = false;
            }

            return Tbl;
        }

        /// <summary>
        /// Removes the primary key columns from the provided database table.
        /// </summary>
        /// <param name="Tbl">The database table from which to remove the primary key columns.</param>
        /// <returns>The modified database table with the primary key columns removed.</returns>
        /// <remarks>
        /// This method iterates through the indexes of the table to find and remove the primary key index. 
        /// It also sets the IsPrimaryKey property of each column to false and nullifies the PrimaryKey property of the table.
        /// </remarks>
        private static DatabaseTable RemovePKColumns(DatabaseTable Tbl)
        {
            DatabaseIndex pkIndex = null;
            foreach (DatabaseIndex i in Tbl.Indexes)
            {
                if (i.IndexType == "BTREE")
                {
                    pkIndex = i;

                }
            }
            if (pkIndex != null)
            {
                Tbl.Indexes.Remove(pkIndex);
            }

            foreach (DatabaseColumn col in Tbl.Columns)
            {
                if (col.IsPrimaryKey == true)
                {
                    col.IsPrimaryKey = false;
                }
            }
            if (Tbl.PrimaryKey != null)
            {
                Tbl.PrimaryKey.Name = null;
                Tbl.PrimaryKey.SchemaOwner = null;
                Tbl.PrimaryKey.TableName = null;
                Tbl.PrimaryKey = null;
            }
            return Tbl;
        }

        /// <summary>
        /// Removes all check constraints from the specified database table.
        /// </summary>
        /// <param name="Tbl">The database table from which to remove the check constraints.</param>
        /// <returns>The database table after all check constraints have been removed.</returns>
        private static DatabaseTable RemoveConstraints(DatabaseTable Tbl)
        {
            foreach (DatabaseConstraint dc in Tbl.CheckConstraints)
            {
                Tbl.CheckConstraints.Remove(dc);
            }
            Tbl.CheckConstraints.Clear();
            return Tbl;
        }

        /// <summary>
        /// Modifies the provided database table to allow null values in all non-primary key columns.
        /// </summary>
        /// <param name="Tbl">The database table to be modified.</param>
        /// <returns>The modified database table with nullable non-primary key columns.</returns>
        private static DatabaseTable RemoveAllowNulls(DatabaseTable Tbl)
        {
            foreach (DatabaseColumn col in Tbl.Columns)
            {
                if (col.IsPrimaryKey == false)
                {
                    col.Nullable = true;
                }
            }
            return Tbl;
        }

        /// <summary>
        /// Processes computed columns from the source table and adds them to the virtual columns table
        /// </summary>
        /// <param name="srcTbl">The source database table containing computed columns</param>
        /// <param name="vcTbl">The virtual columns table to add the computed columns to</param>
        private void ProcessSourceComputedColumns(DatabaseTable srcTbl, DataTable vcTbl)
        {
            if (srcTbl == null || vcTbl == null)
                return;

            // Find computed columns in source table
            foreach (DatabaseColumn column in srcTbl.Columns)
            {
                // Look for columns that are marked as computed in the source
                if (column.ComputedDefinition != null && !string.IsNullOrEmpty(column.ComputedDefinition))
                {
                    // Check if this column is already in the virtual columns table
                    foreach (DataRow row in vcTbl.Rows)
                    {
                        if (string.Equals(row["ColumnName"]?.ToString(), column.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    // Remove the computed property - it will be added as a regular column
                    column.ComputedDefinition = null;
                }
            }
        }
    }

}