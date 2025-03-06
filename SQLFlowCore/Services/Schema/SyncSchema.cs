using LibGit2Sharp;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using Octokit;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text;
using SQLFlowCore.Common;
using Microsoft.SqlServer.Management.Common;
using SQLFlowCore.Logger;
using Microsoft.Extensions.Logging;

//using SQLFlowCore.DataExt;
//using System.Data;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents a class that synchronizes the schema between a source and target database.
    /// </summary>
    /// <remarks>
    /// This class is responsible for fetching schema information from the source and target databases, 
    /// comparing them, and synchronizing the target database schema with the source database schema.
    /// </remarks>
    internal class SyncSchema
    {
        internal bool TrgExists = false;
        internal SyncOutput syncOutput;

        public SyncOutput getSyncOutput()
        {
            return syncOutput;
        }

        internal SyncSchema(
            RealTimeLogger logger,
            SqlConnection sqlFlowCon,
            Server smoSrc,
            Database srcDatabaseObj,
            Server smoTrg,
            Database trgDatabaseObj,
            SqlConnection trgConnection,
            SqlConnection srcConnection,
            SyncInput syncInput)
        {

            #region Init
            smoTrg.ConnectionContext.SqlExecutionModes = SqlExecutionModes.CaptureSql;
            
            StringCollection capturedSql;
            // Retrieve the captured SQL script.
            capturedSql = smoTrg.ConnectionContext.CapturedSql.Text;
        
            syncOutput = new SyncOutput();
            //Pre Fetch Schema Info. Yields better performance
        
            CompareOptions cOpt = new CompareOptions
            {
                IgnoreColumns = syncInput.IgnoreColumnList.GetUnquotedNamesList()

            };

            string srcType = "";
            View srcView = null;
            Table srcTable = null;
            Table trgTable = null;

            try
            {
                var objUrn = SmoHelper.CreateTableUrnFromComponents(smoSrc.NetName, srcDatabaseObj.Name, syncInput.SrcSchema,
                    syncInput.SrcObject);
                var smoObject = smoSrc.GetSmoObject(objUrn);
                if (smoObject is Table)
                {
                    srcType = "Table";
                    syncOutput.srcType = srcType;
                    srcTable = smoObject as Table;  //SmoHelper.GetTable(srcDatabaseObj, syncInput.SrcSchema, syncInput.SrcObject);
                }
            }
            catch { }

            try
            {
                var objUrn = SmoHelper.CreateViewUrnFromComponents(smoSrc.NetName, srcDatabaseObj.Name, syncInput.SrcSchema,
                    syncInput.SrcObject);
                var smoObject = smoSrc.GetSmoObject(objUrn);
                if (smoObject is View)
                {
                    srcType = "View";
                    syncOutput.srcType = srcType;
                    srcView =
                        smoObject as View; //SmoHelper.GetView(srcDatabaseObj, syncInput.SrcSchema, syncInput.SrcObject);
                }
            }
            catch { }


            if (srcView == null && srcTable == null)
            {
                throw new Exception($"Source object {syncInput.SrcSchema}.{syncInput.SrcObject} not found on server {smoSrc.NetName}");
            }

            try
            {
                var trgObjUrn = SmoHelper.CreateTableUrnFromComponents(smoTrg.NetName, trgDatabaseObj.Name, syncInput.TrgSchema, syncInput.TrgObject);
                var trgSmoObject = smoSrc.GetSmoObject(trgObjUrn);
                if (trgSmoObject is Table)
                {
                    trgTable = trgSmoObject as Table;  //SmoHelper.GetTable(srcDatabaseObj, syncInput.SrcSchema, syncInput.SrcObject);

                    // Attempt to get the target table
                    //trgTable = SmoHelper.GetTable(trgDatabaseObj, syncInput.TrgSchema, syncInput.TrgObject);
                }
            }
            catch { }


            // Allways create staging table 
            if (syncInput.TrgIsStaging && trgTable != null)
            {
                //trgTable.DropIfExists();
                trgTable = null;
            }
            #endregion

            #region InitNewTableForCompare
            // Add Index on Identity Column. Start with this as reordering is not possible
            List<string> ignoreForSelect = new List<string>();
            List<string> ignoreForUpdate = new List<string>();
            List<string> ignoreForChecksum = new List<string>();
            List<string> nonNullableColumns = new List<string>();
            
            Table newTbl = new Table
            {
                Parent = trgDatabaseObj,
                Schema = syncInput.TrgSchema,
                Name = syncInput.TrgObject
            };

            // Check if KeyColumnList is not null or empty
            if (syncInput.KeyColumnList != null && syncInput.KeyColumnList.Any())
            {
                ignoreForUpdate = Functions.AddItemsToList(ignoreForUpdate, syncInput.KeyColumnList.GetUnquotedNamesList());
            }

            if (srcType == "View")
            {
                //Sync Src View With The Target Table
                newTbl = SmoHelper.SyncSrcVewTrgTbl(srcView, newTbl, cOpt);
            }
            else
            {
                newTbl = SmoHelper.SyncSrcTblTrgTbl(srcTable, newTbl, cOpt);
            }

            //Make All Cols Nullable In Target Table
            newTbl = SmoHelper.MakeAllColsNullable(srcTable, newTbl, nonNullableColumns, srcConnection);

            //If ConvUnicodeDataType to varchar
            if (syncInput.ConvUnicodeDt)
            {
                newTbl = SmoHelper.ConvertUnicodeDt(newTbl);
            }

            //Transform the table schema in light of the transformations
            if (syncInput.TransformTbl != null)
            {
                foreach (DataRow dr in syncInput.TransformTbl.Rows)
                {
                    string ColumnName = Functions.RemoveBrackets(dr["ColumnName"].ToString());
                    string DataTypeExp = dr["DataTypeExp"].ToString();

                    if (newTbl.Columns.Contains(ColumnName))
                    {
                        DataType dataType = SmoHelper.ParseDataTypeFromString(DataTypeExp);
                        newTbl.Columns[ColumnName].DataType = dataType;
                    }
                }
            }

            // Change the data type of timestamp columns to binary(8)
            foreach (Column dc in newTbl.Columns)
            {
                if (dc.DataType.Name.Equals(SqlDataType.Timestamp.ToString(), StringComparison.InvariantCultureIgnoreCase))
                {
                    // Change the data type
                    dc.DataType = DataType.Binary(8);

                    // Alter the column
                    //dc.Alter();
                }

                if (dc.DataType.Name.Equals(SqlDataType.Image.ToString(), StringComparison.InvariantCultureIgnoreCase))
                {
                    syncOutput.ImageDataTypeFound = true;
                }
            }
            #endregion

            string newTableCmd = "";
            // Transform only if the target is a staging table. Staging to target is a direct copy.
            if (syncInput.TrgIsStaging == true)
            {
                // Retrieve the collations of the source and target databases

                string srcDatabaseCollation = srcDatabaseObj?.Collation;
                string trgDatabaseCollation = trgDatabaseObj?.Collation;

                #region BuildTargetTableForStaging
                if (syncInput.VirtualColsTbl.Rows != null)
                {
                    var virtualColsExpList = new List<string>();
                    var virtualColsList = new List<string>();

                    foreach (DataRow dr in syncInput.VirtualColsTbl.Rows)
                    {
                        string columnName = Functions.RemoveBrackets(dr["ColumnName"].ToString());

                        string dataTypeExp = dr["DataTypeExp"].ToString();
                        string selectExp = dr["SelectExp"].ToString();

                        virtualColsExpList.Add($"{selectExp} as [{columnName}]");
                        virtualColsList.Add($"[{columnName}]");

                        DataType dataType = SmoHelper.ParseDataTypeFromString(dataTypeExp);
                        Column c = new Column();
                        c.Parent = newTbl;
                        c.Name = columnName;
                        c.Nullable = true;
                        c.DataType = dataType;
                        AddColToTable.AddColumn(newTbl, c);
                        ignoreForSelect.Add(columnName);

                        if (columnName == "HashKey_DW")
                        {
                            syncOutput.HashKeyColumnFound = true;
                        }
                    }

                    syncOutput.SrcVirtualColsExp = string.Join(", ", virtualColsExpList);
                    syncOutput.SrcVirtualCols = string.Join(", ", virtualColsList);
                }

                //Fetch Relevant Source Columns from the new table
                syncOutput.SrcColumns = SmoHelper.ColumnsFromTable(newTbl, ignoreForSelect);
                syncOutput.SrcColumnsWithSrc = SmoHelper.ColumnsWithSrcFromTable(newTbl, ignoreForSelect);

                syncOutput.SrcColumnsExp = SmoHelper.SelectExpFromTable(newTbl, syncInput.TransformTbl, ignoreForSelect, srcDatabaseCollation, trgDatabaseCollation);

                //If CleanColumnName is true rename columns to remove special characters
                if (syncInput.CleanColumnName)
                {
                    newTbl = SmoHelper.CleanupColumnName(newTbl, syncInput.ColCleanupSqlRegExp, syncInput.ReplaceInvalidCharsWith);

                    //Fetch Relevant Source Columns from the new table
                    syncOutput.SrcCleanColumns = SmoHelper.ColumnsFromTable(newTbl, ignoreForSelect);
                    syncOutput.SrcCleanColumnsWithSrc = SmoHelper.ColumnsWithSrcFromTable(newTbl, ignoreForSelect);
                    syncOutput.SrcCleanColumnsExp = SmoHelper.SelectExpFromTable(newTbl, syncInput.TransformTbl, ignoreForSelect, srcDatabaseCollation, trgDatabaseCollation);
                }

                //Fetch Relevant Target Columns from the new table
                syncOutput.TrgColumns = SmoHelper.ColumnsFromTable(newTbl, ignoreForSelect);
                syncOutput.TrgColumnsWithTrg = SmoHelper.ColumnsWithTrgFromTable(newTbl, ignoreForSelect);


                syncOutput.TrgColumns += syncOutput.SrcVirtualCols != null ? ", " + syncOutput.SrcVirtualCols : "";
                syncOutput.SrcColumns += syncOutput.SrcVirtualCols != null ? ", " + syncOutput.SrcVirtualCols : "";
                syncOutput.SelectColumns = syncOutput.SrcColumnsExp + (syncOutput.SrcVirtualColsExp != null ? ", " + syncOutput.SrcVirtualColsExp : "");

                //Create Src Target Mapping for Bulk Load. Using Source since there can be a column clean up.
                syncOutput.SrcTrgMapping = Functions.BuildDictionary(syncOutput.SrcColumns, syncOutput.TrgColumns);
                #endregion
            }
            else
            {
                var identitySettings = new Dictionary<string, (bool, bool, long, long)>();
                #region BuildTargetTable
                if (!string.IsNullOrEmpty(syncInput.IdentityColumn))
                {
                    nonNullableColumns.Add(syncInput.IdentityColumn);
                    DataType dataType = DataType.Int;
                    Column c = new Column();
                    c.Parent = newTbl;
                    c.Name = syncInput.IdentityColumn;
                    c.Nullable = false;
                    c.Identity = true;
                    c.IdentityIncrement = 1;
                    c.IdentitySeed = 1;
                    c.DataType = dataType;
                    AddColToTable.AddColumn(newTbl, c);

                    identitySettings.Add(c.Name, (
                        c.Nullable,
                        c.Identity,
                        c.IdentityIncrement,
                        c.IdentitySeed));

                    ignoreForSelect.Add(syncInput.IdentityColumn);
                    ignoreForUpdate.Add(syncInput.IdentityColumn);
                    ignoreForChecksum.Add(syncInput.IdentityColumn);
                }

                //Fetch Relevant Source Columns from the new table
                syncOutput.SrcColumns = SmoHelper.ColumnsFromTable(newTbl, ignoreForSelect);
                syncOutput.SrcColumnsWithSrc = SmoHelper.ColumnsWithSrcFromTable(newTbl, ignoreForSelect);

                Column[] reorderedColumns = SmoHelper.ReorderColumns(newTbl.Columns);

                foreach (Column col in reorderedColumns)
                {
                    if (col.Identity == false)
                    {
                        newTbl.Columns.Remove(col);
                    }
                    
                }

                foreach (Column col in reorderedColumns)
                {
                    if (col.Identity == false)
                    {
                        newTbl.Columns.Add(col);
                    }
                }

                //Reapply identity settings
                SmoHelper.ReapplyIdentitySettings(newTbl, identitySettings);

                syncOutput.SrcColumnsExp = syncOutput.SrcColumnsWithSrc;

                int NameHash = Functions.GetStableHash(newTbl.Name);

                // Check if IdentityColumn is not null or empty
                if (!string.IsNullOrEmpty(syncInput.IdentityColumn))
                {
                    List<string> columnList = new List<string>();
                    columnList.Add(syncInput.IdentityColumn);
                    newTableCmd = newTableCmd + Environment.NewLine + SmoHelper.SyncTableIndexScript(newTbl, $"PK_{newTbl.Name}{NameHash}", columnList, IndexType.ClusteredIndex,
                        IndexKeyType.DriPrimaryKey);
                }

                // Check if DateColumn is not null or empty
                if (!string.IsNullOrEmpty(syncInput.DateColumn))
                {
                    List<string> columnList = new List<string>();
                    columnList.Add(syncInput.DateColumn);
                    newTableCmd = newTableCmd + Environment.NewLine + SmoHelper.SyncTableIndexScript(newTbl, $"NCI_DateColumn{NameHash}", columnList, IndexType.NonClusteredIndex, IndexKeyType.None);
                }

                // Check if KeyColumnList is not null or empty
                if (syncInput.KeyColumnList != null && syncInput.KeyColumnList.Any())
                {
                    if (!string.IsNullOrEmpty(syncInput.KeyColumnList.GetQuotedNames()))
                    {
                        newTableCmd = newTableCmd + Environment.NewLine + SmoHelper.SyncTableIndexScript(newTbl, $"NCI_KeyColumn{NameHash}", syncInput.KeyColumnList.GetUnquotedNamesList(), IndexType.NonClusteredIndex, IndexKeyType.DriUniqueKey);
                    }
                }

                // Check if DataSetColumn is not null or empty
                if (!string.IsNullOrEmpty(syncInput.DataSetColumn))
                {
                    List<string> columnList = new List<string>();
                    columnList.Add(syncInput.DataSetColumn);
                    newTableCmd = newTableCmd + Environment.NewLine + SmoHelper.SyncTableIndexScript(newTbl, $"NCI_DataSetColumn{NameHash}", columnList, IndexType.NonClusteredIndex, IndexKeyType.None);
                }

                // Check if newTbl contains "UpdatedDate_DW" column
                if (newTbl.Columns.Contains("UpdatedDate_DW"))
                {
                    List<string> columnList = new List<string>();
                    columnList.Add("UpdatedDate_DW");
                    newTableCmd = newTableCmd + Environment.NewLine + SmoHelper.SyncTableIndexScript(newTbl, $"NCI_UpdatedDate_DW{NameHash}", columnList, IndexType.NonClusteredIndex, IndexKeyType.None);
                }

                //Fetch Relevant Target Columns from the new table
                syncOutput.TrgColumns = SmoHelper.ColumnsFromTable(newTbl, ignoreForSelect);
                syncOutput.TrgColumnsWithTrg = SmoHelper.ColumnsWithTrgFromTable(newTbl, ignoreForSelect);
                syncOutput.TrgColumnsWithSrc = SmoHelper.ColumnsWithSrcFromTable(newTbl, ignoreForSelect);

                syncOutput.SelectColumns = syncOutput.TrgColumnsWithSrc;
                //Create Src Target Mapping for Bulk Load
                syncOutput.SrcTrgMapping = Functions.BuildDictionary(syncOutput.TrgColumns, syncOutput.TrgColumns);
                #endregion
            }

            //target table not found
            if (trgTable == null)
            {
                #region CreateNewTargetTable
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

                StringCollection scriptCollection = newTbl.Script(scriptingOptions);

                foreach (string line in scriptCollection)
                {
                    script.AppendLine(line);
                }

                syncOutput.CreateCmd = script.ToString();
                
                string createScript = @$"IF OBJECT_ID('[{syncInput.TrgSchema}].[{syncInput.TrgObject}]', 'U') IS NULL
                BEGIN
                    {script.ToString()}{Environment.NewLine}{newTableCmd}
                END
                ";
                
                newTableCmd = createScript;
                
                if (newTableCmd.Length > 0)
                {
                    CommonDB.ExecDDLScript(trgConnection, newTableCmd, 512, false);
                }

                if (syncInput.trgVersioning && syncInput.TrgIsStaging == false)
                {
                    #region trgVersioning
                    string versionCmd =
                        $@"exec flw.GetVersioningScript @baseTable= '[{syncInput.TrgDatabase}].[{syncInput.TrgSchema}].[{syncInput.TrgObject}]', @Mode='DS', @dbg=0";

                    var versionScriptData = new GetData(sqlFlowCon, versionCmd, 360);
                    DataTable tsTbl = versionScriptData.Fetch();
                    versionCmd = tsTbl.Rows[0]["VersionCMD"].ToString();

                    if (versionCmd.Length > 0)
                    {
                        CommonDB.ExecDDLScript(trgConnection, versionCmd, 360, syncInput.TrgIsSynapse);
                    }
                    script.AppendLine(versionCmd);
                    syncOutput.CreateCmd = script.ToString();
                    #endregion trgVersioning
                }

                //HashKeyColumns and KeyColumns Should Be Ignored In Update Statement
                List<string> IgnoreUpdateColumns = Functions.AddItemsToList(syncInput.HashKeyColumnList.GetUnquotedNamesList(), syncInput.KeyColumnList.GetUnquotedNamesList());
                IgnoreUpdateColumns = Functions.AddItemsToList(syncInput.HashKeyColumnList.GetUnquotedNamesList(), ignoreForUpdate);
                syncOutput.ValidUpdateColumns = SmoHelper.ColumnsFromTable(newTbl, IgnoreUpdateColumns);

                //HashKeyColumns and IgnoreChecksumColumns Should Be Ignored In Checksum Calculation
                List<string> IgnoreChecksumColumns = Functions.AddItemsToList(syncInput.HashKeyColumnList.GetUnquotedNamesList(), syncInput.IgnoreChecksumColumns);
                IgnoreChecksumColumns = Functions.AddItemsToList(IgnoreChecksumColumns, syncInput.IgnoreColumnsInHashkey.GetUnquotedNamesList());
                syncOutput.ValidChkSumColumns = SmoHelper.ColumnsFromTable(newTbl, IgnoreChecksumColumns, syncInput.InvalidChecksumDataTypes);

                #endregion
            }
            else
            {
                #region SyncTargetTable
                SMOTableComparison smotc = new SMOTableComparison();
                //trgTable.;
                string deltaScript = smotc.AddMissingElements(newTbl, trgTable);

                if (deltaScript.Length > 10)
                {
                    // Execute the delta script
                    CommonDB.ExecDDLScript(trgConnection, deltaScript.ToString(), 360, syncInput.TrgIsSynapse);

                    // Refresh the target table to reflect the changes
                    //trgTable.Refresh();

                    trgTable.Refresh();
                    trgTable.Columns.Refresh();
                }
                
                string alterScript = "";

                //HashKeyColumns and KeyColumns Should Be Ignored In Update Statement
                List<string> IgnoreUpdateColumns = Functions.AddItemsToList(syncInput.HashKeyColumnList.GetUnquotedNamesList(), syncInput.KeyColumnList.GetUnquotedNamesList());
                IgnoreUpdateColumns = Functions.AddItemsToList(IgnoreUpdateColumns, ignoreForUpdate);
                syncOutput.ValidUpdateColumns = SmoHelper.ColumnsFromTable(trgTable, IgnoreUpdateColumns);

                //HashKeyColumns and IgnoreChecksumColumns Should Be Ignored In Checksum Calculation
                List<string> IgnoreChecksumColumns = Functions.AddItemsToList(syncInput.HashKeyColumnList.GetUnquotedNamesList(), syncInput.IgnoreChecksumColumns);
                IgnoreChecksumColumns = Functions.AddItemsToList(IgnoreChecksumColumns, syncInput.IgnoreColumnsInHashkey.GetUnquotedNamesList());
                IgnoreChecksumColumns = Functions.AddItemsToList(IgnoreChecksumColumns, ignoreForChecksum);
                syncOutput.ValidChkSumColumns = SmoHelper.ColumnsFromTable(trgTable, IgnoreChecksumColumns, syncInput.InvalidChecksumDataTypes);

                //ScriptingOptions scriptingOptions = new ScriptingOptions
                //{
                //    ScriptDrops = false,
                //    WithDependencies = false,
                //    Indexes = true,
                //    DriPrimaryKey = true,
                //    DriIndexes = true,
                //    DriUniqueKeys = true,
                //    SchemaQualify = true,
                //    IncludeHeaders = true,
                //    ToFileOnly = false
                //}; scriptingOptions

                //StringCollection scriptCollection = trgTable.Script();
                //StringBuilder script = new StringBuilder();
                //foreach (string line in scriptCollection)
                //{
                //    script.AppendLine(line);
                //}

                //string createTableScript = script.ToString();
                // Add missing indexes


                //using (logger.TrackOperation(" - Generate Alter Script"))
                //{
                //    //smoTrg.ConnectionContext.SqlExecutionModes = SqlExecutionModes.CaptureSql;
                //    //trgTable.Alter();
                //}

                if (alterScript.Length >= 2)
                {
                    // Execute the delta script
                    CommonDB.ExecDDLScript(trgConnection, alterScript, 512, syncInput.TrgIsSynapse);
                }

                #endregion
            }
            
            syncOutput.TrgExists = true;
            syncOutput.ValidUpdateColumns += syncOutput.SrcVirtualCols != null ? ", " + syncOutput.SrcVirtualCols : "";

            // Combine srcCols and sysCols
            var srcCols = syncOutput.SrcColumns.Split(',').Select(c => c.Trim()).ToList();
            var trgCols = syncOutput.TrgColumns.Split(',').Select(c => c.Trim()).ToList();
            var combinedSrcSysCols = string.Join(",", srcCols.Concat(syncInput.SysColNames).Distinct());

            // Find common columns
            var commonCols = Functions.FindCommonColumns(combinedSrcSysCols, syncOutput.TrgColumns);

            // Filter validUpdateColumns
            var validUpdateColumnsFiltered = Functions.FindCommonColumns(syncOutput.ValidUpdateColumns, commonCols);
            var updateColumns = new StringBuilder();

            List<ObjectName> validUpdateColumns = CommonDB.ParseObjectNames(validUpdateColumnsFiltered);
            foreach (var column in validUpdateColumns)
            {
                switch (column.QuotedName)
                {
                    case "[InsertedDate_DW]":
                        updateColumns.Append(", trg.InsertedDate_DW = CASE WHEN trg.InsertedDate_DW IS NULL THEN Getdate() ELSE trg.InsertedDate_DW END");
                        break;
                    case "[UpdatedDate_DW]":
                        updateColumns.Append(", trg.UpdatedDate_DW = Getdate()");
                        break;
                    case "[RowStatus_DW]":
                        updateColumns.Append(", trg.RowStatus_DW = 'U'");
                        break;
                    default:
                        updateColumns.AppendFormat($", trg.{column.QuotedName} = src.{column.QuotedName}");
                        break;
                }
            }

            syncOutput.UpdateColumnsSrcTrg = updateColumns.ToString().Substring(2);

            // Filter validChecksumColumns
            var validChkSumColumnsFiltered = Functions.FindCommonColumns(syncOutput.ValidChkSumColumns, commonCols);

            List<ObjectName> validChecksumColumns = CommonDB.ParseObjectNames(validChkSumColumnsFiltered);

            var srcCheckSumExp = string.Join(",", validChecksumColumns.Select(col => $"src.{col.QuotedName}"));
            var trgCheckSumExp = string.Join(",", validChecksumColumns.Select(col => $"trg.{col.QuotedName}"));

            syncOutput.CheckSumColumnsSrc = srcCheckSumExp;
            syncOutput.CheckSumColumnsTrg = trgCheckSumExp;

            smoTrg.ConnectionContext.SqlExecutionModes = SqlExecutionModes.ExecuteSql;
        }


    }
}