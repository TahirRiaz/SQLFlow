using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;
using CompareOptions = SQLFlowCore.Services.Schema.CompareOptions;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Pipeline;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using SQLFlowCore.Logger;
using Octokit;
using Microsoft.ML.TorchSharp.AutoFormerV2;
using Tensorboard;
using Mysqlx.Crud;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides a set of static methods and events for processing data ingestion in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class is primarily used in the SQLFlow engine for handling data ingestion tasks such as writing to streams, executing SQLFlow items, distributing integers, and converting data tables to hash tables.
    /// </remarks>
    internal static class ProcessIngestion
    {
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;
        internal static event EventHandler<EventArgsInitLoadBatchStep> OnInitLoadBatchStepOnDone;
        private static EventArgsSchema schArgs = new();
        private static int _batchTaskCounter = 1;
        
        #region ProcessIngestion
        /// <summary>
        /// Executes the ingestion process for a SQLFlow item.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string for the SQLFlow database.</param>
        /// <param name="flowId">The identifier of the flow to be processed.</param>
        /// <param name="execMode">The execution mode for the ingestion process.</param>
        /// <param name="BatchID">The identifier of the batch to be processed.</param>
        /// <param name="dbg">Debug mode indicator.</param>
        /// <param name="sqlFlowParam">The SQLFlow item to be processed.</param>
        /// <returns>A string indicating the result of the execution. Returns "false" if the execution fails.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("Ingestion", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);
            var execTime = new Stopwatch();
            execTime.Start();

            ServiceParam sp = ServiceParam.Current;

            using (var sqlFlowCon = new SqlConnection(sqlFlowParam.sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();
                    // Build the SQL command string

                    DataTable DateTimeFormats = FlowDates.GetDateTimeFormats(sqlFlowCon);
                    DataSet ds = new DataSet();

                    // Wrap the significant DB call in a tracked operation
                    using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
                    {
                        string flowParamCmd =
                            $@" exec [flw].[GetRVFlowING] @FlowID = {sqlFlowParam.flowId}, @ExecMode = '{sqlFlowParam.execMode}', @dbg = {sqlFlowParam.dbg} {Environment.NewLine}";

                        // Log the SQL command as a code block at Debug level
                        logger.LogCodeBlock("Flow Parameter Command", flowParamCmd);

                        // Execute the stored procedure
                        ds = CommonDB.GetDataSetFromSP(sqlFlowCon, flowParamCmd, 360);
                    }

                    // Retrieve tables from the DataSet
                    DataTable paramTbl = ds.Tables[0];
                    DataTable SkeyTbl = ds.Tables[1];
                    DataTable prevTbl = ds.Tables[2];
                    DataTable transformTbl = ds.Tables[3];

                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        if (sp.trgDatabase.Length > 0)
                        {
                            sp.logStartTime = DateTime.Now;
                            

                            if (sp.srcSecretName.Length > 0)
                            {
                                AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                    sp.srcTenantId,
                                    sp.srcApplicationId,
                                    sp.srcClientSecret,
                                    sp.srcKeyVaultName);
                                sp.srcConString = srcKeyVaultManager.GetSecret(sp.srcSecretName);
                            }

                            if (sp.trgSecretName.Length > 0)
                            {
                                AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                    sp.trgTenantId,
                                    sp.trgApplicationId,
                                    sp.trgClientSecret,
                                    sp.trgKeyVaultName);
                                sp.trgConString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                            }

                            conStringParser = new ConStringParser(sp.srcConString)
                            {
                                ConBuilderMsSql =
                                {
                                    ApplicationName = "SQLFlow Source"
                                }
                            };
                            sp.srcConString = conStringParser.ConBuilderMsSql.ConnectionString;

                            conStringParser = new ConStringParser(sp.trgConString)
                            {
                                ConBuilderMsSql =
                                {
                                    ApplicationName = "SQLFlow Target"
                                }
                            };
                            sp.trgConString = conStringParser.ConBuilderMsSql.ConnectionString;

                            //srcConString = new ConStringParser(srcConString).ConBuilder.ConnectionString;
                            //trgConString = new ConStringParser(trgConString).ConBuilder.ConnectionString;
                            var srcSqlCon = new SqlConnection(sp.srcConString);
                            var trgSqlCon = new SqlConnection(sp.trgConString);

                            SqlConnection smoSqlConSrc = new SqlConnection(sp.srcConString);
                            SqlConnection smoSqlConTrg = new SqlConnection(sp.trgConString);

                            //Dump App Assemblies
                            #region DumpAssemblies
                            if (sqlFlowParam.dbg > 2)
                            {
                                // Retrieve all assemblies and build a list of their full names
                                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                                string assemblyList = string.Join(Environment.NewLine, assemblies.Select(a => a.FullName));

                                // Log the assembly list as a code block at Debug level
                                logger.LogCodeBlock("Assembly List", assemblyList);
                            }
                            #endregion DumpAssemblies

                            sp.srcRowCount = 0;

                            ObjectName oDateColumn = CommonDB.ParseObjectNames(sp.dateColumn).FirstOrDefault();
                            ObjectName oDataSetColumn = CommonDB.ParseObjectNames(sp.dataSetColumn).FirstOrDefault();
                            ObjectName oSrcDatabase = CommonDB.ParseObjectNames(sp.srcDatabase).FirstOrDefault();
                            ObjectName oSrcSchema = CommonDB.ParseObjectNames(sp.srcSchema).FirstOrDefault();
                            ObjectName oSrcObject = CommonDB.ParseObjectNames(sp.srcObject).FirstOrDefault();
                            ObjectName oStgSchema = CommonDB.ParseObjectNames(sp.stgSchema).FirstOrDefault();
                            ObjectName oTrgDatabase = CommonDB.ParseObjectNames(sp.trgDatabase).FirstOrDefault();
                            ObjectName oTrgSchema = CommonDB.ParseObjectNames(sp.trgSchema).FirstOrDefault();
                            ObjectName oTrgObject = CommonDB.ParseObjectNames(sp.trgObject).FirstOrDefault();
                            ObjectName oIdentityColumn = CommonDB.ParseObjectNames(sp.identityColumn).FirstOrDefault();

                            List<ObjectName> oIncrementalColumns = CommonDB.ParseObjectNames(sp.incrementalColumns);
                            List<ObjectName> oKeyColumns = CommonDB.ParseObjectNames(sp.keyColumns);
                            List<ObjectName> oHashKeyColumns = CommonDB.ParseObjectNames(sp.HashKeyColumns);
                            List<ObjectName> oIgnoreColumns = CommonDB.ParseObjectNames(sp.ignoreColumns);
                            List<ObjectName> oIgnoreColumnsInHashkey = CommonDB.ParseObjectNames(sp.IgnoreColumnsInHashkey);

                            string keyColumnsQuoted = ObjectNameProcessor.GetQuotedNames(oKeyColumns);
                            string HashKeyColumnsQuoted = ObjectNameProcessor.GetQuotedNames(oHashKeyColumns);
                            string stagingTableName = GetStagingTableName(oTrgObject.UnquotedName, sqlFlowParam.flowId);

                            //List<string> incrementalColumnsList = incrementalColumns.Split(',').ToList();
                            //List<string> keyColumnsList = keyColumns.Split(',').ToList();
                            //List<string> ignoreColumnsList = ignoreColumns.Split(',').ToList();
                            //List<string> ignoreColumnsInHashkeyList = IgnoreColumnsInHashkey.Split(',').ToList();

                            try
                            {
                                srcSqlCon.Open();
                                trgSqlCon.Open();

                                var srcWhere = "";

                                // Initialize SMO connection and server objects
                                ServerConnection smoSrcCon;
                                Server smoSrc;
                                Database srcDatabaseObj;

                                // Declare variables for later use
                                ServerConnection smoTrgCon;
                                Server smoTrg;
                                Database trgDatabaseObj;

                                // Track the operation of initializing the SMO source connection
                                using (var operation = logger.TrackOperation("Initialize source and target"))
                                {
                                    smoSrcCon = new ServerConnection(smoSqlConTrg);
                                    smoSrc = new Server(smoSrcCon);
                                    srcDatabaseObj = smoSrc.Databases[oSrcDatabase.UnquotedName];
                                    //smoSrc.SetDefaultInitFields(true);
                                    //smoSrc.SetDefaultInitFields(typeof(Table), "Name", "Schema");
                                    //smoSrc.SetDefaultInitFields(typeof(View), "Name", "Schema");

                                    smoTrgCon = new ServerConnection(smoSqlConTrg);
                                    smoTrg = new Server(smoTrgCon);
                                    trgDatabaseObj = smoTrg.Databases[oTrgDatabase.UnquotedName];
                                    //smoTrg.SetDefaultInitFields(true);
                                    //smoTrg.SetDefaultInitFields(typeof(Table), "Name", "Schema");
                                    //smoTrg.SetDefaultInitFields(typeof(View), "Name", "Schema");
                                }

                                HashSet<string> datasetDupeCol = new HashSet<string>(ObjectNameProcessor.GetUnquotedNamesList(oKeyColumns));
                                datasetDupeCol.Add(oDataSetColumn?.UnquotedName);

                                List<string> tempList = new List<string>(datasetDupeCol);

                                // Apply brackets to each element and then join them
                                string datasetDupeColList = "[" + string.Join("],[", tempList) + "]";
                                string datasetDupeColListWithSrc = "src.[" + string.Join("],src.[", tempList) + "]";

                                sp.ignoreColumns = "''";
                                if (oIgnoreColumns.Count > 0)
                                {
                                    List<string> myList = ObjectNameProcessor.GetUnquotedNamesList(oIgnoreColumns);
                                    sp.ignoreColumns = string.Join(",", myList.ConvertAll(m => $"'{m}'").ToArray());
                                }

                                logger.LogInformation($"Init data pipeline from [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] to [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}] {sp.onErrorResume}");

                                DataTable tb = CommonDB.GetData(srcSqlCon, "SELECT DB_NAME() AS [Current Database]",
                                    240);

                                if (sp.cmdSchema.Length > 2)
                                {
                                    // Track the operation of creating the schema on the target
                                    using (var operation = logger.TrackOperation("Ensure target schemas"))
                                    {
                                        // Log the SQL command as a code block at Debug level
                                        logger.LogCodeBlock("cmdSchema", sp.cmdSchema);
                                        CommonDB.ExecNonQuery(trgSqlCon, sp.cmdSchema, sp.bulkLoadTimeoutInSek);
                                    }
                                }

                                string HashKey_DW = "";
                                if (HashKeyColumnsQuoted.Length > 0)
                                {
                                    // Track the operation of adding the HashKey as a virtual column
                                    using (var operation = logger.TrackOperation("Adding HashKey Virtual Column"))
                                    {
                                        // Build the HashKey_DW expression by concatenating non-empty trimmed columns
                                        var hashKeyColumns = HashKeyColumnsQuoted
                                            .Split(',')
                                            .Select(col => col.Trim())
                                            .Where(col => !string.IsNullOrEmpty(col));
                                        string concatenatedColumns = string.Join(",", hashKeyColumns);
                                        HashKey_DW = $"HASHBYTES(''{sp.HashKeyType}'', CONCAT({concatenatedColumns}))";

                                        // Construct the SQL command for adding the virtual column
                                        string cmdFlowTransExp = $"exec [flw].[AddIngestionVirtual] @FlowID={sqlFlowParam.flowId}, @ColumnName='[HashKey_DW]', @DataType='{sp.DataType}', @DataTypeExp='{sp.DataTypeExp}', @SelectExp='{HashKey_DW}'";

                                        // Log the SQL command as a code block at Debug level
                                        logger.LogCodeBlock("HashKey Virtual Column Command", cmdFlowTransExp);

                                        CommonDB.ExecNonQuery(sqlFlowCon, cmdFlowTransExp, sp.bulkLoadTimeoutInSek);
                                    }
                                }

                                // Ensure that the source object view is refreshed
                                Shared.RefreshSourceView(oSrcDatabase, oSrcSchema, oSrcObject, logger, srcSqlCon, sp.bulkLoadTimeoutInSek);

                                // Log and execute the pre-process command on the target
                                Shared.LogAndExecutePreProcess(sp.preProcessOnTrg, logger, trgSqlCon, sp.bulkLoadTimeoutInSek);

                                //NonCritical Retry Error Codes from target Database
                                var retryErrorCodes = new Hashtable(StringComparer.InvariantCultureIgnoreCase);
                                RetryCodes rCodes = new RetryCodes();
                                retryErrorCodes = rCodes.HashTbl;

                                // Fetch VirtualColumnSchema
                                #region VirtualSchema
                                // Track the operation of fetching the virtual schema
                                DataTable vsTbl;
                                vsTbl = Shared.FetchVirtualSchema(sqlFlowParam, logger, sqlFlowCon, sp.generalTimeoutInSek);
                                #endregion VirtualSchema 

                                // Declare variables needed later
                                SyncOutput stgShm;
                                SyncOutput trgShm;
                                string stgIndexes = "";

                                //Verify Target Staging Schema

                                #region StagingSchema
                                using (var operation = logger.TrackOperation("Synchronize staging schema"))
                                {
                                    SyncInput syncInput = new SyncInput();
                                    syncInput.SrcDatabase = oSrcDatabase.UnquotedName;
                                    syncInput.SrcSchema = oSrcSchema.UnquotedName;
                                    syncInput.SrcObject = oSrcObject.UnquotedName;
                                    syncInput.TrgDatabase = oTrgDatabase.UnquotedName;
                                    syncInput.TrgSchema = oStgSchema.UnquotedName;
                                    syncInput.TrgObject = stagingTableName;
                                    syncInput.trgVersioning = false;
                                    syncInput.TrgIsStaging = true;
                                    syncInput.SrcIsStaging = false;
                                    syncInput.TrgIsSynapse = sp.trgIsSynapse;
                                    syncInput.SrcIsSynapse = sp.srcIsSynapse;
                                    syncInput.DateColumn = oDateColumn.UnquotedName;
                                    syncInput.DataSetColumn = oDataSetColumn.UnquotedName;
                                    syncInput.IdentityColumn = oIdentityColumn.UnquotedName;
                                    syncInput.KeyColumnList = new EnhancedObjectNameList(oKeyColumns);
                                    syncInput.IncrementalColumnList = new EnhancedObjectNameList(oIncrementalColumns);
                                    syncInput.IgnoreColumnsInHashkey =
                                        new EnhancedObjectNameList(oIgnoreColumnsInHashkey);
                                    syncInput.IgnoreColumnList = new EnhancedObjectNameList(oIgnoreColumns);
                                    syncInput.HashKeyColumnList = new EnhancedObjectNameList(oHashKeyColumns);
                                    syncInput.SyncSchema = sp.syncSchema;
                                    syncInput.CleanColumnName = sp.onSyncCleanColumnName;
                                    syncInput.ConvUnicodeDt = sp.onSyncConvertUnicodeDataType;
                                    syncInput.CreateIndexes = false;
                                    syncInput.ColCleanupSqlRegExp = sp.colCleanupSqlRegExp;
                                    syncInput.VirtualColsTbl = vsTbl;
                                    syncInput.TransformTbl = transformTbl;
                                    syncInput.ReplaceInvalidCharsWith = sp.ReplaceInvalidCharsWith;
                                    
                                    // Retrieve indexes from the staging table
                                    stgIndexes = Services.Schema.ObjectIndexes.GetObjectIndexes(
                                        sp.trgConString,
                                        oTrgDatabase.UnquotedName,
                                        oStgSchema.UnquotedName,
                                        stagingTableName,
                                        true
                                    );

                                    // Log the staging indexes as a code block at Debug level
                                    logger.LogCodeBlock("Staging Indexes", stgIndexes);

                                    using (SqlCommand cmd = new SqlCommand("[flw].[AddObjectIndexes]", sqlFlowCon))
                                    {
                                        cmd.CommandType = CommandType.StoredProcedure;
                                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = sqlFlowParam.flowId;
                                        cmd.Parameters.Add("@TrgIndexes", SqlDbType.VarChar).Value = stgIndexes;
                                        cmd.ExecuteNonQuery();
                                    }

                                    // If target is staging and already exists, drop it.
                                    if (syncInput.TrgIsStaging)
                                    {
                                        CommonDB.DropTable(syncInput.TrgSchema, syncInput.TrgObject, trgSqlCon);
                                    }

                                    SyncSchema stgShmObj = new SyncSchema(logger, sqlFlowCon, smoSrc, srcDatabaseObj,
                                        smoTrg, trgDatabaseObj, trgSqlCon, srcSqlCon, syncInput);
                                    stgShm = stgShmObj.getSyncOutput();

                                    
                                    string sColMap = string.Join(";", stgShm.SrcTrgMapping.Select(x => x.Key + "=" + x.Value).ToArray());
                                    logger.LogCodeBlock("ColumnMappings:", sColMap);

                                }
                                #endregion StagingSchema

                                var colMap = stgShm.SrcTrgMapping;

                                //Verify target Schema 
                                #region TargetSchema
                                // Declare variable outside the using block
                                using (var operation = logger.TrackOperation("Synchronize target schema"))
                                {
                                    SyncInput syncInputTrg = new SyncInput();
                                    syncInputTrg.SrcDatabase = oTrgDatabase.UnquotedName;
                                    syncInputTrg.SrcSchema = oStgSchema.UnquotedName;
                                    syncInputTrg.SrcObject = stagingTableName;
                                    syncInputTrg.TrgDatabase = oTrgDatabase.UnquotedName;
                                    syncInputTrg.TrgSchema = oTrgSchema.UnquotedName;
                                    syncInputTrg.TrgObject = oTrgObject.UnquotedName;
                                    syncInputTrg.trgVersioning = sp.trgVersioning;
                                    syncInputTrg.TrgIsStaging = false;
                                    syncInputTrg.SrcIsStaging = false;
                                    syncInputTrg.TrgIsSynapse = sp.trgIsSynapse;
                                    syncInputTrg.SrcIsSynapse = sp.srcIsSynapse;
                                    syncInputTrg.DateColumn = oDateColumn.UnquotedName;
                                    syncInputTrg.DataSetColumn = oDataSetColumn.UnquotedName;
                                    syncInputTrg.IdentityColumn = oIdentityColumn.UnquotedName;

                                    syncInputTrg.KeyColumnList = new EnhancedObjectNameList(oKeyColumns);
                                    syncInputTrg.IncrementalColumnList = new EnhancedObjectNameList(oIncrementalColumns);
                                    syncInputTrg.IgnoreColumnsInHashkey = new EnhancedObjectNameList(oIgnoreColumnsInHashkey);
                                    syncInputTrg.IgnoreColumnList = new EnhancedObjectNameList(oIgnoreColumns);
                                    syncInputTrg.HashKeyColumnList = new EnhancedObjectNameList(oHashKeyColumns);

                                    syncInputTrg.SyncSchema = sp.syncSchema;
                                    syncInputTrg.CleanColumnName = sp.onSyncCleanColumnName;
                                    syncInputTrg.ConvUnicodeDt = sp.onSyncConvertUnicodeDataType;
                                    syncInputTrg.CreateIndexes = true;
                                    syncInputTrg.ColCleanupSqlRegExp = sp.colCleanupSqlRegExp;
                                    syncInputTrg.VirtualColsTbl = vsTbl;
                                    syncInputTrg.TransformTbl = transformTbl;
                                    
                                    SyncSchema trgShmObj = new SyncSchema(logger,sqlFlowCon, smoTrg, trgDatabaseObj, smoTrg, trgDatabaseObj, trgSqlCon, srcSqlCon, syncInputTrg);
                                    trgShm = trgShmObj.getSyncOutput();
                                    trgShm.keyColumnsQuoted = keyColumnsQuoted;
                                    trgShm.dateColumnQuoted = oDateColumn.QuotedName;
                                    trgShm.dataSetColumnQuoted = oDataSetColumn.QuotedName;
                                    sp.logCreateCmd = trgShm.CreateCmd;

                                    // Log the SQL command/script details if necessary
                                    if (!string.IsNullOrWhiteSpace(sp.logCreateCmd))
                                    {
                                        logger.LogCodeBlock("Target Schema Create Command", sp.logCreateCmd);
                                    }
                                }
                                #endregion TargetSchema

                                // TODO: Need to port this to .net from CLR and cross db support
                                // Use Created Staging Schema and merge with TokenSchema
                                //#region TokenSchema
                                //if (tokenize)
                                //{
                                //    logStack.Append($"Fetch Token Schema {Environment.NewLine}");
                                //    string tokenSchemaCmd =
                                //        $@"exec flw.GetTokenSchema  @mode = 1, @rawTable= '[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]', @dbg={sqlFlowParam.dbg.ToString()}";
                                //    codeStack.AppendIf(sqlFlowParam.dbg > 1, CodeStackSection("GetTokenSchema :", tokenSchemaCmd));

                                //    var tokenSchemaData = new GetData(sqlFlowCon, tokenSchemaCmd, generalTimeoutInSek);
                                //    watch.Restart();
                                //    DataTable tsTbl = tokenSchemaData.Fetch();
                                //    tokenSchemaXml = tsTbl.Rows[0]["TokenSchema"]?.ToString() ?? string.Empty;
                                //    tokenSchemaForCTE = tsTbl.Rows[0]["TokenSchemaForCTE"]?.ToString() ?? string.Empty;
                                //    codeStack.AppendIf(sqlFlowParam.dbg > 1, CodeStackSection("Token Schema Dataset:", tokenSchemaXml));
                                //}
                                //#endregion TokenSchema


                                //CheckForError Source Target DataTypes
                                #region CompColDataTypes
                                // Declare variable outside the using block
                                SrcTrgDataTypeStatus scc;
                                using (logger.TrackOperation("Compare Source and Target Column DataTypes"))
                                {
                                    scc = CommonDB.CompareColDataTypeSrcTrg(
                                        trgSqlCon, trgSqlCon, sp.generalTimeoutInSek,
                                        oTrgDatabase.UnquotedName, oStgSchema.UnquotedName, stagingTableName,
                                        sp.srcWithHint, oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName,
                                        oTrgObject.UnquotedName, sp.trgWithHint,
                                        oHashKeyColumns, oKeyColumns, oDateColumn,
                                        oIgnoreColumns, oIncrementalColumns, oIdentityColumn);

                                    sp.ColumnWarning = scc.ColumnWarningXML;
                                    sp.DataTypeWarning = scc.DataTypeWarningXML;

                                    if (scc.CriticalMismatch)
                                    {
                                        sp.logErrorRuntime = "Critical datatype mismatch on columns that are a part of hash key comparison";
                                    }
                                }
                                #endregion CompColDataTypes

                                //Target exsists define mappings and run upload
                                if (scc.CriticalMismatch == false & (stgShm.TrgExists && trgShm.TrgExists || stgShm.TrgExists && sp.tokenize))
                                {
                                    //Clean KeyColNames
                                    var runFullload = true;
                                    var joinExp = "";
                                    var outerJoinExp = "";
                                    var keyColMaxArray = keyColumnsQuoted.Split(','); //KeyColumns.Split(',');

                                    if (keyColumnsQuoted.Length > 0) //If key columns a not defined no incremental load is possible
                                    {
                                        //Build Max Col Expression For Key
                                        if (keyColumnsQuoted.Length > 0)
                                        {
                                            for (var x = 0; x < keyColMaxArray.Length; x++)
                                            {
                                                joinExp +=
                                                    $" AND src.{keyColMaxArray[x].Trim()} = trg.{keyColMaxArray[x].Trim()}";

                                                outerJoinExp = $" trg.{keyColMaxArray[x].Trim()}";
                                            }

                                            //Key should be used for join only
                                            //keyExp = keyExp.Substring(10) + " AS KeyExp";
                                            //keyExpForXML = keyExpForXML.Substring(10);
                                            joinExp = joinExp.Substring(4);
                                        }
                                    }

                                    if (oIncrementalColumns.FirstOrDefault().QuotedName.Length > 1 || oDateColumn.UnquotedName.Length > 0) //If key columns a not defined no incremental load is possible
                                    {
                                        var expTblMaxMin = new DataTable();

                                        using (logger.TrackOperation("Fetched runtime Min/Max filters"))
                                        {
                                            sp.cmdMax = CommonDB.GetIncWhereExp("MAX", oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName, oTrgObject.UnquotedName, oDateColumn.UnquotedName, oIncrementalColumns, "MSSQL", sp.noOfOverlapDays, sp.trgIsSynapse, sp.IncrementalClauseExp, sp.trgWithHint);
                                            logger.LogCodeBlock("Query To Fetch Runtime Max Values:", sp.cmdMax);

                                            if (sp.FetchMinValuesFromSrc)
                                            {
                                                sp.cmdMin = CommonDB.GetIncWhereExp("MIN", oSrcDatabase.UnquotedName, oSrcSchema.UnquotedName, oSrcObject.UnquotedName, oDateColumn.UnquotedName, oIncrementalColumns, "MSSQL", sp.noOfOverlapDays, sp.srcIsSynapse, sp.IncrementalClauseExp, sp.srcWithHint);
                                                logger.LogCodeBlock("Query To Fetch Runtime Min Values:", sp.cmdMin);

                                                var expDataMin = new GetData(srcSqlCon, sp.cmdMin, sp.bulkLoadTimeoutInSek);
                                                expTblMaxMin = expDataMin.Fetch();
                                            }

                                            var _whereIncExp = "";
                                            var _whereDateExp = "";
                                            //Fetched Runtime Where filters
                                            if (sp.cmdMax.Length > 0 || sp.cmdMin.Length > 0)
                                            {
                                                
                                                var expDataMax = new GetData(trgSqlCon, sp.cmdMax, sp.bulkLoadTimeoutInSek);
                                                var expTblMax = expDataMax.Fetch();

                                                DataTable incTableDS = Functions.GetMinIncTblFromSrcTrg(expTblMaxMin, expTblMax, sp.FetchMinValuesFromSrc, DateTimeFormats);

                                                if (incTableDS != null)
                                                {
                                                    if (incTableDS.Columns.Contains("IncExp"))
                                                        //Set Final WhereKeyExp
                                                        _whereIncExp = " AND " + incTableDS.Rows[0]["IncExp"];

                                                    if (incTableDS.Columns.Contains("DateExp"))
                                                        //Set Final DateExp
                                                        _whereDateExp = " AND " + incTableDS.Rows[0]["DateExp"];

                                                    if (incTableDS.Columns.Contains("RunFullload"))
                                                        //Set Fulload CheckForError
                                                        runFullload = incTableDS.Rows[0]["RunFullload"].ToString() == "1" ? true : false;

                                                    if (incTableDS.Columns.Contains("XmlNodes"))
                                                        //Set Final DateExp
                                                        sp.whereXML = expTblMax.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                                                }
                                            }

                                            if (_whereIncExp.Length > 0)
                                                sp.whereIncExp = _whereIncExp;

                                            if (_whereDateExp.Length > 0)
                                                sp.whereDateExp = _whereDateExp;

                                            if (keyColumnsQuoted.Length == 0 && sp.whereIncExp.Length == 0 && sp.whereDateExp.Length == 0)
                                            {
                                                runFullload = true;
                                            }
                                            else
                                            {
                                                runFullload = false;
                                            }

                                            // Set the final where statement
                                            if (sp.srcFilter.Length > 0 && sp.srcFilterIsAppend == false)
                                            {
                                                srcWhere = sp.srcFilter;
                                            }
                                            //else if (truncateTrg) //Its better to have truncate set as config
                                            //{
                                            //    srcWhere = "";
                                            //}
                                            else if (sp.fullload)
                                            {
                                                srcWhere = "";
                                            }
                                            else if (runFullload && sp.whereIncExp.Length == 0 && sp.whereDateExp.Length == 0)
                                                srcWhere = "";
                                            // IncColumns without datecolumn or key column
                                            else if (sp.whereIncExp.Length > 0)
                                                srcWhere = sp.whereIncExp;
                                            else if (sp.whereDateExp.Length > 0)
                                                srcWhere = sp.whereDateExp;

                                            if (sp.srcFilterIsAppend && sp.srcFilter.Length > 0)
                                            {
                                                srcWhere = srcWhere + " " + sp.srcFilter;
                                            }

                                            logger.LogInformation($"Runtime filters: {srcWhere}");
                                            string filterExpressionBase = $"TruncateTrg: {sp.truncateTrg}{Environment.NewLine}Full load onFlow: {sp.fullload.ToString()}{Environment.NewLine}SrcFilter onFlow: {sp.srcFilter}{Environment.NewLine}RunFullload from MaxQuery: {runFullload}{Environment.NewLine}WhereDateExp from MaxQuery: {sp.whereDateExp}{Environment.NewLine}WhereKeyExp from MaxQuery: {sp.whereKeyExp}{Environment.NewLine}WhereIncExp from MaxQuery: {sp.whereIncExp}{Environment.NewLine}Final srcWhere: {srcWhere}{Environment.NewLine}";
                                            logger.LogCodeBlock("Filter expression values:", filterExpressionBase);
                                        }
                                    }



                                    //Ingestion to Staging
                                    #region InitLoad
                                    if (sp.InitLoad)
                                    {
                                        bool dataLoaded = false;
                                        var tasks = new List<Task>();
                                        sp.srcRowCount = 1000000; // Sample Rowcount

                                        StreamToSql.OnRowsCopied += HandlerOnRowsCopied;
                                        
                                        // Log the start of the initial load
                                        using (logger.TrackOperation($"Initial load"))
                                        {
                                            SortedList<int, ExpSegment> SrcBatched = CommonDB.GetSrcSelectBatched(
                                                stgShm.SelectColumns,
                                                oSrcDatabase.UnquotedName,
                                                oSrcSchema.UnquotedName,
                                                oSrcObject.UnquotedName,
                                                "", "", "MSSQL",
                                                sp.InitLoadFromDate, sp.InitLoadToDate,
                                                sp.InitLoadBatchBy, sp.InitLoadBatchSize,
                                                oDateColumn.UnquotedName, 0,
                                                sp.InitLoadKeyMaxValue, sp.InitLoadKeyColumn,
                                                "", "", "", false, "");

                                            foreach (var selectB in SrcBatched)
                                            {
                                                ExpSegment tuple = selectB.Value;
                                                var srcSelect = tuple.SqlCMD;
                                                var srcSelectRange = tuple.WhereClause;
                                                // Log each SQL segment; debug level is now handled by the logger
                                                logger.LogCodeBlock($"InitLoad range: {srcSelectRange}", srcSelect);
                                            }

                                            // Declare the semaphore outside the task loop for controlling concurrency.
                                            using (var concurrencySemaphore = new Semaphore(sp.noOfThreads, sp.noOfThreads))
                                            {
                                                int totalTaskCounter = SrcBatched.Count;

                                                foreach (var selectB in SrcBatched)
                                                {
                                                    ExpSegment tuple = selectB.Value;
                                                    var srcSelect = tuple.SqlCMD;
                                                    var srcSelectRange = tuple.WhereClause;

                                                    // Each task processes a single batch segment.
                                                    var t = Task.Factory.StartNew(() =>
                                                    {
                                                        concurrencySemaphore.WaitOne();

                                                        // Wrap the bulk load operation in a TrackOperation
                                                        using (var op = logger.TrackOperation($"BulkLoad segment - {srcSelectRange}"))
                                                        {
                                                            using (SqlConnection sqlConnection = new SqlConnection(sp.srcConString))
                                                            {
                                                                try
                                                                {
                                                                    sqlConnection.Open();
                                                                    using (var cmd = new SqlCommand(srcSelect, sqlConnection) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                                    {
                                                                        // Log the SQL command being executed.
                                                                        logger.LogCodeBlock("Executing SQL", srcSelect);
                                                                        using (var srcReader = cmd.ExecuteReader())
                                                                        {
                                                                            var bulk = new StreamToSql(
                                                                                sp.trgConString,
                                                                                $"[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]",
                                                                                $"[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]",
                                                                                colMap,
                                                                                sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                                                retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount);
                                                                            bulk.StreamWithRetries(srcReader);
                                                                            dataLoaded = true;
                                                                        }
                                                                    }
                                                                }
                                                                catch (Exception)
                                                                {
                                                                    throw;
                                                                }
                                                            }
                                                        }
                                                        concurrencySemaphore.Release();

                                                    }, TaskCreationOptions.LongRunning);

                                                    // Instead of using manual task timing, we now call the completion routine without a duration.
                                                    t.ContinueWith(_ =>
                                                    {
                                                        batchStepCompleted(logger, srcSelectRange, string.Empty, totalTaskCounter);
                                                    }, TaskContinuationOptions.OnlyOnRanToCompletion);

                                                    tasks.Add(t);
                                                }

                                                Task.WaitAll(tasks.ToArray());

                                                if (dataLoaded)
                                                {
                                                    var cmdRowCount = CommonDB.GetEstimatedRowCountSQL(
                                                        oTrgDatabase.UnquotedName,
                                                        oStgSchema.UnquotedName,
                                                        stagingTableName,
                                                        "", "", "MSSQL");
                                                    logger.LogCodeBlock("Target Table RowCount Query", cmdRowCount);

                                                    // Wrap the target row count retrieval in a TrackOperation
                                                    using (var op = logger.TrackOperation("Fetch Target Table RowCount"))
                                                    {
                                                        var cCount = CommonDB.ExecuteScalar(sp.trgConString, cmdRowCount, "MSSQL", sp.bulkLoadTimeoutInSek);
                                                        sp.srcRowCount = int.Parse(cCount.ToString());
                                                        sp.logFetched = sp.srcRowCount;
                                                        sp.logInserted = sp.srcRowCount;
                                                    }

                                                    logger.LogInformation("Estimated target row count fetched successfully");
                                                    logger.LogInformation("Source data streamed to target successfully");
                                                }
                                                else
                                                {
                                                    sp.logFetched = 0;
                                                    sp.logInserted = 0;
                                                }
                                            }
                                        }
                                    }
                                    #endregion InitLoad

                                    //Ingestion to Staging
                                    #region IngestStaging
                                    // If full load then transfer directly to target schema.
                                    else if (sp.streamData == false)
                                    {
                                        // Wrap the fetch operation in a tracked operation (timing handled automatically)
                                        DataTable srcTbl = null;
                                        using (var op = logger.TrackOperation("Load source data into memory with applied filters"))
                                        {
                                            var srcSelectTbl = $"SELECT  {stgShm.SelectColumns} FROM [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] src WHERE 1=1 {srcWhere};";
                                            logger.LogCodeBlock("Load source data:", srcSelectTbl);
                                            sp.logSelectCmd = srcSelectTbl;
                                            
                                            var srcData = new GetData(srcSqlCon, srcSelectTbl, sp.bulkLoadTimeoutInSek);
                                            srcTbl = srcData.Fetch();

                                            logger.LogInformation($"srcRowCount {srcTbl.Rows?.Count} fetched from in-memory table");
                                        }

                                        // Split the DataTable into batches using a tracked operation.
                                        List<DataTable> batches = null;
                                        var finalBatchSize = 0;
                                        using (var op = logger.TrackOperation("Split DataTable into batches"))
                                        {
                                            // Calculate optimal batch size
                                            sp.srcRowCount = srcTbl.Rows.Count;
                                            var batchSize = sp.srcRowCount / sp.noOfThreads;
                                            batchSize = batchSize == 0 ? sp.srcRowCount : batchSize;

                                            sp.logFetched = sp.srcRowCount;
                                            
                                            batches = DataTableSplitter.SplitDataTable(srcTbl, batchSize);
                                            finalBatchSize = sp.bulkLoadBatchSize == -1 ? batchSize : sp.bulkLoadBatchSize;
                                            logger.LogInformation($"Batches created for {sp.srcRowCount} rows with batch size {batchSize}.");
                                        }

                                        // Execute bulk load in parallel wrapped in a tracked operation.
                                        using (var op = logger.TrackOperation("Bulk load data to staging (memory load)"))
                                        {
                                            PushToSql.OnRowsCopied += OnRowsCopied;
                                            Parallel.ForEach(batches, table =>
                                            {
                                                var currentRowCount = table.Rows.Count;
                                                var bulk = new PushToSql(sp.trgConString,
                                                    $"[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]",
                                                    $"[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]",
                                                    colMap, sp.bulkLoadTimeoutInSek, finalBatchSize, logger, sp.maxRetry, sp.retryDelayMs,
                                                    retryErrorCodes, sqlFlowParam.dbg, ref currentRowCount); // BatchSize

                                                bulk.WriteWithRetries(table);
                                                table.Clear();
                                                table.Dispose();
                                            });

                                            logger.LogInformation($"Bulkloaded {sp.srcRowCount} rows to staging area.");
                                        }

                                        // Release unused memory
                                        srcTbl.Rows.Clear();
                                        srcTbl.Dispose();
                                        srcTbl = null;
                                    }
                                    else
                                    {
                                        var cmdRowCount = "";
                                        if (trgShm.srcType == "Table")
                                        {
                                            // Calculate number of rows in source if no filter and key columns exist.
                                            if (srcWhere.Length == 0 && keyColumnsQuoted.Length > 0)
                                            {
                                                using (var op = logger.TrackOperation("Fetch row count from sys objects"))
                                                {
                                                    cmdRowCount = CommonDB.GetRowCountCMD(oSrcDatabase.UnquotedName, oSrcSchema.UnquotedName, oSrcObject.UnquotedName, sp.srcWithHint);
                                                    logger.LogCodeBlock("Calculate source table rowcount on cmd 1:", cmdRowCount);
                                                    using (SqlCommand srcRowCountCMD = new SqlCommand(cmdRowCount, srcSqlCon))
                                                    {
                                                        sp.srcRowCount = Convert.ToInt32(srcRowCountCMD.ExecuteScalar());
                                                    }

                                                    logger.LogInformation($"srcRowCount fetched from sys objects  {sp.srcRowCount.ToString()}");
                                                }
                                            }
                                        }

                                        // If still zero then use an alternate query (for views)
                                        if (sp.srcRowCount == 0)
                                        {
                                            using (var op = logger.TrackOperation("Fetch row count from view"))
                                            {
                                                cmdRowCount = $"SELECT COUNT_BIG(1) FROM [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] WHERE 1=1 {srcWhere};";
                                                logger.LogCodeBlock("Calculate source rowcount on cmd:", cmdRowCount);
                                                using (SqlCommand srcRowCountCMD = new SqlCommand(cmdRowCount, srcSqlCon) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                {
                                                    sp.srcRowCount = Convert.ToInt32(srcRowCountCMD.ExecuteScalar());
                                                }
                                                logger.LogInformation($"srcRowCount fetched from {oSrcObject.UnquotedName} {sp.srcRowCount.ToString()}");
                                            }
                                        }

                                        sp.logFetched = sp.srcRowCount;

                                        var batchSize = sp.srcRowCount / sp.noOfThreads;
                                        batchSize = batchSize == 0 ? sp.srcRowCount : batchSize;

                                        int OffsetValue = 0;
                                        int LoopValue = batchSize;
                                        string cmdOrderByOfset = "";
                                        var cmdOfsetlist = new List<string>();

                                        // Build offset expressions
                                        foreach (var batch in DistributeInteger(sp.srcRowCount, sp.noOfThreads))
                                        {
                                            if (LoopValue <= sp.srcRowCount)
                                            {
                                                cmdOrderByOfset = " ORDER BY " + keyColumnsQuoted + " OFFSET " + OffsetValue + " ROWS FETCH NEXT " + batch + " ROWS ONLY ";
                                                cmdOfsetlist.Add(cmdOrderByOfset);
                                                sp.logSelectCmd += cmdOrderByOfset + Environment.NewLine;
                                                LoopValue += batch;
                                                OffsetValue += batch;
                                            }
                                        }

                                        sp.noOfThreads = cmdOfsetlist.Count;
                                        // Reverse to optimize buffer pool reads.
                                        cmdOfsetlist.Reverse();
                                        logger.LogCodeBlock("Source data offsets:", string.Join(Environment.NewLine, cmdOfsetlist));

                                        sp.logSelectCmd = $"SELECT {stgShm.SelectColumns} FROM [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] src WHERE 1=1 {srcWhere} ;";
                                        sp.logSelectCmd = sp.logSelectCmd.Replace("&amp;", "&").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&apos;", "'");
                                        logger.LogCodeBlock("Select statement data:", sp.logSelectCmd);

                                        // If multiple threads should be used for streaming
                                        bool dataLoaded = false;
                                        if (cmdOfsetlist.Count > 1 && sp.srcRowCount > 0 && keyColumnsQuoted.Length > 0 && sp.srcIsSynapse == false)
                                        {
                                            StreamToSql.OnRowsCopied += OnRowsCopied;
                                            using (var op = logger.TrackOperation("Stream data in parallel"))
                                            {
                                                logger.LogInformation($"Source dataset contains {sp.srcRowCount} rows, parallelized in {cmdOfsetlist.Count} threads with batch size {batchSize}.");
                                                Parallel.ForEach(cmdOfsetlist, (s) =>
                                                {
                                                    var srcSelectStream = $"SELECT {stgShm.SelectColumns} FROM [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] src WHERE 1=1 {srcWhere} {s};";
                                                    srcSelectStream = srcSelectStream.Replace("&amp;", "&").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&apos;", "'");
                                                    using (var localSrcSqlCon = new SqlConnection(sp.srcConString))
                                                    {
                                                        localSrcSqlCon.Open();
                                                        using (var cmd = new SqlCommand(srcSelectStream, localSrcSqlCon))
                                                        {
                                                            cmd.CommandTimeout = sp.bulkLoadTimeoutInSek;
                                                            using (var srcReader = cmd.ExecuteReader())
                                                            {
                                                                var bulk = new StreamToSql(sp.trgConString,
                                                                    $"[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]",
                                                                    $"[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]",
                                                                    colMap, sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                                    retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount);
                                                                bulk.StreamWithRetries(srcReader);
                                                            }
                                                        }
                                                    }
                                                });
                                            }
                                            dataLoaded = true;
                                        }
                                        else if (sp.srcRowCount > 0)
                                        {
                                            using (var op = logger.TrackOperation("Stream data with single read"))
                                            {
                                                var srcSelectStream = $"SELECT  {stgShm.SelectColumns} FROM [{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}] src WHERE 1=1 {srcWhere};";
                                                srcSelectStream = srcSelectStream.Replace("&amp;", "&").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&apos;", "'");
                                                logger.LogCodeBlock("Source Data Load Method:", srcSelectStream);

                                                using (var cmd = new SqlCommand(srcSelectStream, srcSqlCon) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                {
                                                    using (var srcReader = cmd.ExecuteReader())
                                                    {
                                                        StreamToSql.OnRowsCopied += OnRowsCopied;
                                                        var bulk = new StreamToSql(sp.trgConString,
                                                            $"[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]",
                                                            $"[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]",
                                                            colMap, sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                            retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount);
                                                        bulk.StreamWithRetries(srcReader);
                                                    }
                                                }
                                            }
                                            dataLoaded = true;
                                        }

                                        // Recreate Indexes on Staging Table (if provided)
                                        if (stgIndexes.Length > 5)
                                        {
                                            if (sp.trgIsSynapse == false)
                                            {
                                                using (var op = logger.TrackOperation("Recreate indexes on staging table"))
                                                {
                                                    CommonDB.ExecDDLScript(trgSqlCon, stgIndexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                                }
                                            }
                                            else
                                            {
                                                using (var op = logger.TrackOperation("Recreate indexes on synapse staging table"))
                                                {
                                                    CommonDB.ExecNonQuery(trgSqlCon, stgIndexes, sp.bulkLoadTimeoutInSek);
                                                }
                                            }
                                        }

                                        if (dataLoaded)
                                        {
                                            using (var op = logger.TrackOperation("Fetch staging table row count"))
                                            {
                                                // Get staging table row count using a multi-branch query.
                                                cmdRowCount = $@"IF (OBJECT_ID('sys.dm_pdw_nodes_db_partition_stats') IS NOT NULL)
BEGIN
    SELECT  COUNT_BIG(1) as [RowCount]  from [{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}] 
END;
ELSE IF (OBJECT_ID('[sys].[dm_db_partition_stats]') IS NOT NULL AND Exists(SELECT * FROM fn_my_permissions (NULL, 'DATABASE') WHERE  permission_name = 'VIEW DATABASE STATE'))
BEGIN
    SELECT SUM(ps.[row_count]) AS [RowCount]
      FROM [{oTrgDatabase.UnquotedName}].[sys].[dm_db_partition_stats] as ps WITH({sp.trgWithHint}) 
     WHERE [index_id]   < 2
       AND ps.object_id = OBJECT_ID('[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]')
     GROUP BY ps.object_id;
END;
ELSE 
BEGIN
    SELECT SUM(sPTN.Rows) AS [RowCount]
    FROM [{oTrgDatabase.UnquotedName}].sys.objects AS sOBJ WITH({sp.trgWithHint}) 
    INNER JOIN [{oTrgDatabase.UnquotedName}].sys.partitions AS sPTN WITH({sp.trgWithHint}) 
    ON sOBJ.object_id = sPTN.object_id
    WHERE sOBJ.type = 'U'
    AND sOBJ.is_ms_shipped = 0x0
    AND index_id< 2
    AND sOBJ.Object_id = OBJECT_ID('[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]')
    GROUP BY sOBJ.schema_id,sOBJ.name
END;
";
                                                logger.LogCodeBlock("Staging table rowcount:", cmdRowCount);

                                                using (SqlCommand commandRowCount = new SqlCommand(cmdRowCount, trgSqlCon) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                {
                                                    sp.logFetched = Convert.ToInt32(commandRowCount.ExecuteScalar());
                                                }
                                                logger.LogInformation("Source data streamed to staging.");
                                            }
                                            
                                        }
                                        else
                                        {
                                            sp.logFetched = 0;
                                            sp.logInserted = 0;
                                        }
                                    }
                                    #endregion IngestStaging


                                    //truncate target table 
                                    #region TruncateTarget
                                    if (!sp.trgVersioning && sp.truncateTrg)
                                    {
                                        // Wrap the DDL execution in a tracked operation
                                        using (var op = logger.TrackOperation("Truncate Target Table"))
                                        {
                                            // Prepare the truncate command
                                            var cmdTruncTrg = $"TRUNCATE TABLE [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}]; " +
                                                              $"ALTER TABLE [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}] REBUILD;";
                                            try
                                            {
                                                sp.logInserted = CommonDB.ExecDDLScript(trgSqlCon, cmdTruncTrg, sp.generalTimeoutInSek, sp.trgIsSynapse);
                                            }
                                            catch (SqlException ex)
                                            {
                                                logger.LogError("Error truncating target table", ex);
                                                sp.logErrorInsert = ex.Message;
                                            }
                                        }
                                    }
                                    #endregion TruncateTarget


                                    //Ingestion to Target
                                    #region IngestTarget
                                    if (sp.tokenize)
                                    {
                                        logger.LogInformation("Not implemented");
                                    }
                                    else
                                    {
                                        // Full load branch: either fullload or incremental with FullLoad Tag should not default to this section
                                        if (srcWhere.Length < 3 || runFullload)
                                        {
                                            // Execute the full load operation using TrackOperation (manual timing removed)
                                            using (var op = logger.TrackOperation("Full Load: Staging transferred to target"))
                                            {
                                                // Build full load SQL command
                                                string cmdTrg = $"INSERT INTO [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}] " +
                                                                $"({trgShm.TrgColumns}) SELECT {trgShm.TrgColumnsWithSrc} " +
                                                                $"FROM [{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}] src";

                                                if (joinExp.Length > 0 && outerJoinExp.Length > 0)
                                                {
                                                    cmdTrg = $@"INSERT INTO [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}] ({trgShm.TrgColumns})
                                                                SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {trgShm.TrgColumnsWithSrc} 
                                                                FROM  [{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}] src
                                                                LEFT OUTER JOIN  [{oTrgDatabase.UnquotedName}].[{oTrgSchema.UnquotedName}].[{oTrgObject.UnquotedName}] trg
                                                                ON {joinExp}
                                                                WHERE {outerJoinExp} IS NULL;";
                                                }

                                                // Log the SQL command as a code block at Debug level
                                                logger.LogCodeBlock("Full load from source, staging transferred to target", cmdTrg);
                                                // Incremental load branch: run merge
                                                logger.LogCodeBlock("trgShm.SelectColumns", trgShm.SelectColumns);
                                                logger.LogCodeBlock("trgShm.TrgColumnsWithSrc", trgShm.TrgColumnsWithSrc);
                                                logger.LogCodeBlock("trgShm.ValidUpdateColumns", trgShm.ValidUpdateColumns);
                                                logger.LogCodeBlock("trgShm.ValidChkSumColumns", trgShm.ValidChkSumColumns);
                                                logger.LogCodeBlock("trgShm.UpdateColumnsSrcTrg", trgShm.UpdateColumnsSrcTrg);
                                                logger.LogCodeBlock("trgShm.CheckSumColumnsSrc", trgShm.CheckSumColumnsSrc);
                                                logger.LogCodeBlock("trgShm.CheckSumColumnsTrg", trgShm.CheckSumColumnsTrg);
                                                logger.LogCodeBlock("joinExp", joinExp);
                                                logger.LogCodeBlock("outerJoinExp", outerJoinExp);
                                                sp.logInsertCmd = cmdTrg;

                                                try
                                                {
                                                    // Execute the non-query command
                                                    sp.logInserted = new ExecNonQuery(trgSqlCon, cmdTrg, sp.bulkLoadTimeoutInSek).Exec();
                                                }
                                                catch (SqlException ex)
                                                {
                                                    sp.logErrorInsert = ex.Message;
                                                    logger.LogError(ex, "Error during full load execution");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            using (logger.TrackOperation("Full Load: Staging transferred to target"))
                                            {
                                                // Incremental load branch: run merge
                                                logger.LogCodeBlock("trgShm.SelectColumns", trgShm.SelectColumns);
                                                logger.LogCodeBlock("trgShm.TrgColumnsWithSrc", trgShm.TrgColumnsWithSrc);
                                                logger.LogCodeBlock("trgShm.ValidUpdateColumns", trgShm.ValidUpdateColumns);
                                                logger.LogCodeBlock("trgShm.ValidChkSumColumns", trgShm.ValidChkSumColumns);
                                                logger.LogCodeBlock("trgShm.UpdateColumnsSrcTrg", trgShm.UpdateColumnsSrcTrg);
                                                logger.LogCodeBlock("trgShm.CheckSumColumnsSrc", trgShm.CheckSumColumnsSrc);
                                                logger.LogCodeBlock("trgShm.CheckSumColumnsTrg", trgShm.CheckSumColumnsTrg);
                                                logger.LogCodeBlock("joinExp", joinExp);
                                                logger.LogCodeBlock("outerJoinExp", outerJoinExp);

                                                // DataSet processing – if staging has many datasets each must be processed individually
                                                if (oDataSetColumn.QuotedName.Length > 0)
                                                {
                                                    logger.LogInformation($"Processing staging data based on DataSetColumn {oDataSetColumn.QuotedName}");

                                                    string srcDS4Update = $@"[{oTrgDatabase.UnquotedName}].[{oStgSchema.UnquotedName}].[{stagingTableName}]";
                                                    string updateLable = "flw" + sqlFlowParam.flowId + "_Update";
                                                    string insertLable = "flw" + sqlFlowParam.flowId + "_Insert";
                                                    string cmdRowCountUpdate = ProcessIngestionToTarget.GetRowCountUpdateCommand(sp.trgIsSynapse, updateLable);
                                                    string cmdRowCountInsert = ProcessIngestionToTarget.GetRowCountInsertCommand(sp.trgIsSynapse, insertLable);

                                                    string dsCMD = "";
                                                    if (sp.UseBatchUpsertToAvoideLockEscalation)
                                                    {
                                                        string updateStgTrgCmd = ProcessIngestionToTarget.GetUpdateStgTrgCommandBatch(
                                                            trgShm, srcDS4Update, oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName,
                                                            oTrgObject.UnquotedName, oDataSetColumn.QuotedName, joinExp, sp.BatchUpsertRowCount);

                                                        string insertStgTrgCmd = ProcessIngestionToTarget.GetInsertStgTrgCommandBatch(
                                                            trgShm, oTrgDatabase.UnquotedName, oStgSchema.UnquotedName,
                                                            stagingTableName, oTrgSchema.UnquotedName, oTrgObject.UnquotedName,
                                                            oDataSetColumn.QuotedName, sp.BatchUpsertRowCount);

                                                        dsCMD = ProcessIngestionToTarget.GetDsCommandBatch(
                                                            trgShm, oTrgDatabase.UnquotedName, oStgSchema.UnquotedName,
                                                            stagingTableName, oTrgSchema.UnquotedName, oTrgObject.UnquotedName,
                                                            oDataSetColumn.QuotedName, datasetDupeColList, datasetDupeColListWithSrc,
                                                            updateStgTrgCmd, insertStgTrgCmd, outerJoinExp, joinExp);

                                                        logger.LogInformation($"Upsert using {sp.BatchUpsertRowCount} rows for each insert and update");
                                                    }
                                                    else
                                                    {
                                                        string updateStgTrgCmd = ProcessIngestionToTarget.GetUpdateStgTrgCommand(
                                                            trgShm, srcDS4Update, oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName,
                                                            oTrgObject.UnquotedName, joinExp, oDataSetColumn.QuotedName,
                                                            cmdRowCountUpdate, sp.skipUpdateExsisting);

                                                        string insertStgTrgCmd = ProcessIngestionToTarget.GetInsertStgTrgCommand(
                                                            trgShm, oTrgDatabase.UnquotedName, oStgSchema.UnquotedName,
                                                            stagingTableName, oTrgSchema.UnquotedName, oTrgObject.UnquotedName,
                                                            joinExp, oDataSetColumn.QuotedName, outerJoinExp,
                                                            cmdRowCountInsert, sp.skipInsertNew);

                                                        dsCMD = ProcessIngestionToTarget.GetDsCommand(
                                                            trgShm, oTrgDatabase.UnquotedName, oStgSchema.UnquotedName,
                                                            stagingTableName, oDataSetColumn.QuotedName,
                                                            datasetDupeColList, datasetDupeColListWithSrc,
                                                            updateStgTrgCmd, insertStgTrgCmd);

                                                        logger.LogInformation($"Upsert using {sp.BatchUpsertRowCount} rows for each insert and update");
                                                    }

                                                    logger.LogCodeBlock("DataSet Processing", dsCMD);
                                                    sp.logInsertCmd = dsCMD;
                                                    sp.logUpdateCmd = dsCMD;

                                                    // Merge the delta set – operation timing handled automatically via TrackOperation
                                                    var dsCmdRes = new GetData(trgSqlCon, dsCMD, sp.bulkLoadTimeoutInSek);
                                                    using (var op = logger.TrackOperation("Merge staging rows with target table"))
                                                    {
                                                        try
                                                        {
                                                            if (!sp.truncateTrg)
                                                            {
                                                                DataTable InsUp = dsCmdRes.Fetch();
                                                                sp.logUpdated = long.Parse(InsUp.Rows[0]["Updates"]?.ToString() ?? string.Empty);
                                                                sp.logInserted = long.Parse(InsUp.Rows[0]["Inserts"]?.ToString() ?? string.Empty);
                                                            }
                                                        }
                                                        catch (SqlException ex)
                                                        {
                                                            sp.logErrorUpdate = ex.Message;
                                                            sp.logErrorInsert = ex.Message;
                                                            logger.LogError(ex, "Error during merge operation");
                                                        }
                                                        logger.LogInformation($"Staging rows merged with target table. Inserts: {sp.logInserted}, Updates: {sp.logUpdated}");
                                                    }
                                                }
                                                else
                                                {
                                                    using (var op = logger.TrackOperation(
                                                               "Merge staging rows with target table"))
                                                    {
                                                        if (sp.UseBatchUpsertToAvoideLockEscalation)
                                                        {
                                                            ProcessIngestionToTarget.ProcessBatchedWithStagingJoin(
                                                                sp.skipUpdateExsisting, trgShm, oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName,
                                                                oTrgObject.UnquotedName, joinExp, sp.skipInsertNew, stagingTableName, outerJoinExp,
                                                                oStgSchema.UnquotedName, sqlFlowParam, logger, trgSqlCon,
                                                                sp.bulkLoadTimeoutInSek, sp.truncateTrg, ref sp.logUpdateCmd, ref sp.logUpdated,
                                                                ref sp.logErrorUpdate, ref sp.logInsertCmd, ref sp.logInserted, ref sp.logErrorInsert,
                                                                sp.BatchUpsertRowCount);

                                                            logger.LogInformation($"Upsert using {sp.BatchUpsertRowCount} rows for each insert and update");
                                                        }
                                                        else
                                                        {
                                                            ProcessIngestionToTarget.ProcessWithoutDataSetColumn(
                                                                sp.skipUpdateExsisting, trgShm, oTrgDatabase.UnquotedName, oTrgSchema.UnquotedName,
                                                                oTrgObject.UnquotedName, joinExp, sp.skipInsertNew, stagingTableName, outerJoinExp,
                                                                oStgSchema.UnquotedName, sqlFlowParam, logger, trgSqlCon,
                                                                sp.bulkLoadTimeoutInSek, sp.truncateTrg, ref sp.logUpdateCmd, ref sp.logUpdated,
                                                                ref sp.logErrorUpdate, ref sp.logInsertCmd, ref sp.logInserted, ref sp.logErrorInsert);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    #endregion IngestTarget
                                    
                                    if (sp.postProcessOnTrg.Length > 2)
                                    {
                                        // Track the post-process operation; operation timing is handled automatically.
                                        using (var op = logger.TrackOperation("Post-process executed on target"))
                                        {
                                            // Log the post-process SQL command as a code block at Debug level.
                                            logger.LogCodeBlock("Post Process SQL", sp.postProcessOnTrg);
                                            new ExecNonQuery(trgSqlCon, sp.postProcessOnTrg, sp.bulkLoadTimeoutInSek).Exec();
                                        }
                                    }

                                    if (sp.cmdMax.Length > 0)
                                    {
                                        DataTable expTbl = null;
                                        using (var op = logger.TrackOperation("Fetch filters for next incremental load"))
                                        {
                                            // Log the max values query as a code block at Debug level.
                                            logger.LogCodeBlock("Query To Fetch Max Values", sp.cmdMax);
                                            // Fetch Where filters.Values stored in SysLog using TrackOperation for automatic timing.
                                            var expData = new GetData(trgSqlCon, sp.cmdMax, sp.bulkLoadTimeoutInSek);
                                            expTbl = expData.Fetch();

                                            if (expTbl != null)
                                            {
                                                if (expTbl.Columns.Contains("KeyExp"))
                                                    // Set Final WhereKeyExp.
                                                    sp.whereKeyExp = " AND " + expTbl.Rows[0]["KeyExp"];

                                                if (expTbl.Columns.Contains("IncExp"))
                                                    // Set Final Incremental Expression.
                                                    sp.whereIncExp = " AND " + expTbl.Rows[0]["IncExp"];

                                                if (expTbl.Columns.Contains("DateExp"))
                                                    // Set Final Date Expression.
                                                    sp.whereDateExp = " AND " + expTbl.Rows[0]["DateExp"];

                                                if (expTbl.Columns.Contains("RunFullload"))
                                                    // Set Full Load Check for Error.
                                                    runFullload = expTbl.Rows[0]["RunFullload"].ToString() == "1";

                                                if (expTbl.Columns.Contains("XmlNodes"))
                                                    // Set Final XML Expression.
                                                    sp.whereXML = expTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                                            }

                                            // Log the XML details as a code block at Debug level.
                                            logger.LogCodeBlock("XML To Fetch Max Values", sp.whereXML);
                                        }
                                    }

                                    #region GenerateSkey
                                    foreach (DataRow dr in SkeyTbl.Rows)
                                    {
                                        string baseDatabase = dr["baseDatabase"]?.ToString() ?? string.Empty;
                                        string baseSchema = dr["baseSchema"]?.ToString() ?? string.Empty;
                                        string baseObject = dr["baseObject"]?.ToString() ?? string.Empty;
                                        string baseTmpSchema = dr["baseTmpSchema"]?.ToString() ?? string.Empty;

                                        string SKeyDatabase = dr["SKeyDatabase"]?.ToString() ?? string.Empty;
                                        string SKeySchema = dr["SKeySchema"]?.ToString() ?? string.Empty;
                                        string SKeyObject = dr["SKeyObject"]?.ToString() ?? string.Empty;
                                        string SKeyTmpSchema = dr["SKeyTmpSchema"]?.ToString() ?? string.Empty;

                                        EnhancedObjectNameList KeyColumnsList = new EnhancedObjectNameList(CommonDB.ParseObjectNames(dr["KeyColumns"]?.ToString() ?? string.Empty));
                                        EnhancedObjectNameList sKeyColumnsList;
                                        string sKeyColumns = dr["sKeyColumns"]?.ToString() ?? string.Empty;

                                        if (string.IsNullOrWhiteSpace(sKeyColumns))
                                        {
                                            sKeyColumnsList = new EnhancedObjectNameList();
                                        }
                                        else
                                        {
                                            sKeyColumnsList = new EnhancedObjectNameList(CommonDB.ParseObjectNames(sKeyColumns));
                                        }

                                        string SurrogateColumn = Functions.CleanupColumns(dr["SurrogateColumn"]?.ToString() ?? string.Empty);
                                        bool SKeyIsRemote = (dr["SKeyIsRemote"]?.ToString() ?? string.Empty).Equals("True");

                                        string SKeyConString = dr["SKeyConString"]?.ToString() ?? string.Empty;
                                        string skeyTenantId = dr["skeyTenantId"]?.ToString() ?? string.Empty;
                                        string skeySubscriptionId = dr["skeySubscriptionId"]?.ToString() ?? string.Empty;
                                        string skeyApplicationId = dr["skeyApplicationId"]?.ToString() ?? string.Empty;
                                        string skeyClientSecret = dr["skeyClientSecret"]?.ToString() ?? string.Empty;
                                        string skeyKeyVaultName = dr["skeyKeyVaultName"]?.ToString() ?? string.Empty;
                                        string skeySecretName = dr["skeySecretName"]?.ToString() ?? string.Empty;
                                        string skeyResourceGroup = dr["skeyResourceGroup"]?.ToString() ?? string.Empty;
                                        string skeyDataFactoryName = dr["skeyDataFactoryName"]?.ToString() ?? string.Empty;
                                        string skeyAutomationAccountName = dr["skeyAutomationAccountName"]?.ToString() ?? string.Empty;
                                        string skeyStorageAccountName = dr["skeyStorageAccountName"]?.ToString() ?? string.Empty;
                                        string skeyBlobContainer = dr["skeyBlobContainer"]?.ToString() ?? string.Empty;

                                        if (skeySecretName.Length > 0)
                                        {
                                            AzureKeyVaultManager sKeyVaultManager = new AzureKeyVaultManager(
                                                skeyTenantId,
                                                skeyApplicationId,
                                                skeyClientSecret,
                                                skeyKeyVaultName);
                                            SKeyConString = sKeyVaultManager.GetSecret(skeySecretName);
                                        }

                                        string PreProcess = dr["PreProcess"]?.ToString() ?? string.Empty;
                                        string PostProcess = dr["PostProcess"]?.ToString() ?? string.Empty;

                                        //temp objects for the sync
                                        string tmpBaseDbSchObj = CommonDB.BuildFullObjectName(baseDatabase, baseTmpSchema, baseObject);
                                        string tmpBaseDbSchObjOnSkey = CommonDB.BuildFullObjectName(SKeyDatabase, baseTmpSchema, baseObject);

                                        //Actual Objects
                                        string BaseDbSchObj = CommonDB.BuildFullObjectName(baseDatabase, baseSchema, baseObject);
                                        string SkeyDbSchObj = CommonDB.BuildFullObjectName(SKeyDatabase, SKeySchema, SKeyObject);

                                        ConStringParser objConStringParser = new ConStringParser(SKeyConString);
                                        string conStrParsed = objConStringParser.ConBuilderMsSql.ConnectionString;

                                        var sKeySqlCon = new SqlConnection(conStrParsed);
                                        sKeySqlCon.Open();

                                        if (PreProcess.Length > 2)
                                        {
                                            var cmdOnSrc = new ExecNonQuery(sKeySqlCon, PreProcess, sp.bulkLoadTimeoutInSek);
                                            using (var op = logger.TrackOperation("PreProcess executed on sKeyTable"))
                                            {
                                                logger.LogCodeBlock("PreProcess SQL", PreProcess);
                                                cmdOnSrc.Exec();
                                            }
                                        }

                                        if (SKeyIsRemote == false)
                                        {
                                            using (var operation = logger.TrackOperation("Skey generation"))
                                            {
                                                SqlConnection smoSkeySqlCon = new SqlConnection(conStrParsed);
                                                ServerConnection smoSkeySrvCon = new ServerConnection(smoSkeySqlCon);
                                                Server smoSrvSKey = new Server(smoSkeySrvCon);
                                                Database smoDbSKey = smoSrvSKey.Databases[SKeyDatabase];

                                                if (smoDbSKey != null)
                                                {
                                                    //Sync Schema Before Generation and Pushback
                                                    if (sKeyColumnsList.Count == 0)
                                                    {
                                                        SmoHelper.LookupObject(smoSrvSKey, smoDbSKey, baseSchema,
                                                            baseObject,
                                                            out Table lkpTable, out View lkpView,
                                                            out StoredProcedure lkpSp);

                                                        Table baseTable = lkpTable;

                                                        if (baseTable != null)
                                                        {
                                                            // Track the sync operation; timing is handled internally.
                                                            using (var op1 = logger.TrackOperation("Sync Schema Before Generation and Pushback"))
                                                            {
                                                                SmoHelper.SyncSkeyTable(
                                                                    smoSkeySqlCon,
                                                                    smoSrvSKey,
                                                                    baseTable,
                                                                    smoDbSKey,
                                                                    KeyColumnsList.GetUnquotedNamesList(),
                                                                    SKeyDatabase,
                                                                    SKeySchema,
                                                                    SKeyObject,
                                                                    SurrogateColumn,
                                                                    sp.generalTimeoutInSek);
                                                            }
                                                        }
                                                    }


                                                    // Build SKey and Pushback
                                                    string cmdSkeyPush = CommonDB.BuildSKeyGenPushCmd(
                                                        BaseDbSchObj,
                                                        SkeyDbSchObj,
                                                        KeyColumnsList.GetUnquotedNamesList(),
                                                        sKeyColumnsList.GetUnquotedNamesList(),
                                                        SurrogateColumn);
                                                    sp.logSurrogateKeyCmd = cmdSkeyPush;

                                                    using (var op2 = logger.TrackOperation("Build SKey and Pushback"))
                                                    {
                                                        logger.LogCodeBlock("Surrogate Key Cmd:", sp.logSurrogateKeyCmd);
                                                        CommonDB.ExecNonQuery(sKeySqlCon, cmdSkeyPush, sp.bulkLoadTimeoutInSek);
                                                    }

                                                    smoSrvSKey.ConnectionContext.Disconnect();
                                                    smoSkeySrvCon.Disconnect();
                                                    smoSkeySqlCon.Close();
                                                    smoSkeySqlCon.Dispose();
                                                }
                                            }
                                        }
                                        else
                                        {
                                            using (var operation = logger.TrackOperation("Remote skey generation"))
                                            {
                                                //Connect to Trg Database
                                                SqlConnection smoBaseConSkey = new SqlConnection(conStrParsed);
                                                ServerConnection smoBaseSrvCon = new ServerConnection(smoBaseConSkey);
                                                Server smoBaseSrv = new Server(smoBaseSrvCon);
                                                Database smoBaseDbS = smoBaseSrv.Databases[baseDatabase];

                                                SqlConnection smoSkeySqlCon = new SqlConnection(conStrParsed);
                                                ServerConnection smoSkeySrvCon = new ServerConnection(smoSkeySqlCon);
                                                Server smoSrvSKey = new Server(smoSkeySrvCon);
                                                Database smoDbSKey = smoSrvSKey.Databases[SKeyDatabase];

                                                Table baseTable = smoBaseDbS.Tables[baseObject, baseSchema];

                                                // Sync Schema Before Generation and Pushback
                                                if (sKeyColumnsList.Count == 0)
                                                {
                                                    // Wrap the sync operation in a TrackOperation block for automatic timing and logging.
                                                    using (var op = logger.TrackOperation("Sync Schema for surrogate key generation"))
                                                    {
                                                        SmoHelper.SyncSkeyTable(
                                                            smoSkeySqlCon,
                                                            smoBaseSrv,
                                                            baseTable,
                                                            smoDbSKey,
                                                            KeyColumnsList.GetUnquotedNamesList(),
                                                            SKeyDatabase,
                                                            SKeySchema,
                                                            SKeyObject,
                                                            SurrogateColumn,
                                                            sp.generalTimeoutInSek);
                                                    }
                                                }

                                                //Create Temp Tables
                                                Table tempBaseObject = new Table(smoBaseDbS, baseObject, baseTmpSchema);
                                                Table tempBaseObjectOnSkey = new Table(smoDbSKey, baseObject, SKeyTmpSchema);

                                                CompareOptions cmpOpt = new CompareOptions();
                                                tempBaseObject = SmoHelper.SyncSrcTblTrgTbl(baseTable, tempBaseObject, cmpOpt);
                                                tempBaseObjectOnSkey = SmoHelper.SyncSrcTblTrgTbl(baseTable, tempBaseObjectOnSkey, cmpOpt);

                                                var dupeTmpLocal = smoBaseDbS.Tables[baseObject, baseTmpSchema];
                                                var dupeTmpRemote = smoDbSKey.Tables[baseObject, SKeyTmpSchema];

                                                if (dupeTmpLocal != null)
                                                {
                                                    dupeTmpLocal.Drop();
                                                }

                                                if (dupeTmpRemote != null)
                                                {
                                                    dupeTmpRemote.Drop();
                                                }

                                                tempBaseObject.Create();
                                                tempBaseObjectOnSkey.Create();
                                                // Log that temp tables have been created.
                                                logger.LogInformation("Temp tables created for Skey generation and fetching.");

                                                // Operation: Transfer dim data to SKey Database Temp Object
                                                using (var op = logger.TrackOperation("Transfer dim data to SKey Database Temp Object"))
                                                {
                                                    string selectCMD = $"SELECT {SmoHelper.ColumnsFromTable(baseTable)} FROM {BaseDbSchObj} ;";
                                                    logger.LogCodeBlock("SQL Command", selectCMD);

                                                    Dictionary<string, string> map = SmoHelper.SqlBulkCopyMapping(baseTable);

                                                    using (var cmd = new SqlCommand(selectCMD, trgSqlCon))
                                                    {
                                                        cmd.CommandTimeout = sp.bulkLoadTimeoutInSek;
                                                        using (var srcReader = cmd.ExecuteReader())
                                                        {
                                                            // Note: Adjust the StreamToSql constructor as needed to accept ILogger instead of ref logStack.
                                                            var bulk = new StreamToSql(conStrParsed,
                                                                                       $"[{SKeyDatabase}].[{baseTmpSchema}].[{baseObject}]",
                                                                                       $"[{baseDatabase}].[{baseSchema}].[{baseObject}]",
                                                                                       map,
                                                                                       sp.bulkLoadTimeoutInSek, 0,
                                                                                       logger, // replaced logStack with logger
                                                                                       sp.maxRetry, sp.retryDelayMs,
                                                                                       retryErrorCodes, sqlFlowParam.dbg,
                                                                                       ref sp.srcRowCount);
                                                            bulk.StreamWithRetries(srcReader);
                                                        }
                                                    }
                                                }

                                                // Operation: Build SKey and Pushback to temp table
                                                using (var op = logger.TrackOperation("Build SKey and Pushback - Temp Table"))
                                                {
                                                    string cmdSkeyPush = CommonDB.BuildSKeyGenPushCmd(
                                                                              tmpBaseDbSchObjOnSkey, SkeyDbSchObj,
                                                                              KeyColumnsList.GetQuotedNamesList(),
                                                                              sKeyColumnsList.GetQuotedNamesList(),
                                                                              SurrogateColumn);
                                                    sp.logSurrogateKeyCmd = cmdSkeyPush;
                                                    logger.LogCodeBlock("Surrogate Key Cmd:", cmdSkeyPush);
                                                    CommonDB.ExecNonQuery(sKeySqlCon, cmdSkeyPush, sp.bulkLoadTimeoutInSek);
                                                }

                                                // Operation: Transfer dim data to Target Database Temp Table
                                                using (var op = logger.TrackOperation("Transfer dim data to Target Database Temp Table"))
                                                {
                                                    string selectTmpCMD = $"SELECT {SmoHelper.ColumnsFromTable(baseTable)} FROM {tmpBaseDbSchObjOnSkey} ;";
                                                    logger.LogCodeBlock("SQL Command", selectTmpCMD);

                                                    using (var cmd = new SqlCommand(selectTmpCMD, sKeySqlCon))
                                                    {
                                                        cmd.CommandTimeout = sp.bulkLoadTimeoutInSek;
                                                        using (var srcReader = cmd.ExecuteReader())
                                                        {
                                                            var bulk = new StreamToSql(sp.trgConString,
                                                                                       $"[{baseDatabase}].[{baseTmpSchema}].[{baseObject}]",
                                                                                       $"[{SKeyDatabase}].[{baseTmpSchema}].[{baseObject}]",
                                                                                       colMap,
                                                                                       sp.bulkLoadTimeoutInSek, 0,
                                                                                       logger, // replaced logStack with logger
                                                                                       sp.maxRetry, sp.retryDelayMs,
                                                                                       retryErrorCodes, sqlFlowParam.dbg,
                                                                                       ref sp.srcRowCount);
                                                            bulk.StreamWithRetries(srcReader);
                                                        }
                                                    }
                                                }

                                                // Operation: Build SKey and Pushback to source table
                                                using (var op = logger.TrackOperation("Build SKey and Pushback - Source Table"))
                                                {
                                                    string cmdSkeyPushTobase = CommonDB.BuildSKeyPushCmd(
                                                                                   BaseDbSchObj, tmpBaseDbSchObj,
                                                                                   KeyColumnsList.GetQuotedNamesList(),
                                                                                   sKeyColumnsList.GetQuotedNamesList(),
                                                                                   SurrogateColumn);
                                                    sp.logSurrogateKeyCmd = cmdSkeyPushTobase;
                                                    logger.LogCodeBlock("Surrogate Key Cmd:", cmdSkeyPushTobase);
                                                    CommonDB.ExecNonQuery(trgSqlCon, cmdSkeyPushTobase, sp.bulkLoadTimeoutInSek);
                                                }

                                                tempBaseObject.DropIfExists();
                                                tempBaseObjectOnSkey.DropIfExists();

                                                smoSrvSKey.ConnectionContext.Disconnect();
                                                smoSkeySrvCon.Disconnect();
                                                smoSkeySqlCon.Close();
                                                smoSkeySqlCon.Dispose();

                                                smoBaseSrv.ConnectionContext.Disconnect();
                                                smoBaseSrvCon.Disconnect();
                                                smoBaseConSkey.Close();
                                                smoBaseConSkey.Dispose();
                                            }
                                        }

                                        if (PostProcess.Length > 2)
                                        {
                                            using (var op = logger.TrackOperation("PostProcess Execution on sKeyTable"))
                                            {
                                                var cmdOnSrc = new ExecNonQuery(sKeySqlCon, PostProcess, sp.bulkLoadTimeoutInSek);
                                                cmdOnSrc.Exec();
                                            }

                                        }

                                        sKeySqlCon.Close();
                                        sKeySqlCon.Dispose();
                                    }
                                    #endregion GenerateSkey

                                    #region MatchKeysSrcTrg
                                    if (sp.MatchKeysInSrcTrg)
                                    {
                                        using (var op = logger.TrackOperation("ExecMatchKey Execution"))
                                        {
                                            var sqlFlowMkey = new SqlFlowParam()
                                            {
                                                batch = string.Empty,
                                                flowId = sqlFlowParam.flowId,
                                                matchKeyId = 0,
                                                dbg = 0,
                                                batchId = sqlFlowParam.batchId,
                                                sqlFlowConString = sqlFlowParam.sqlFlowConString
                                            };

                                            string _result = ExecMatchKey.Exec(sqlFlowMkey);
                                            logger.LogInformation(_result);
                                        }
                                    }
                                    #endregion

                                    #region GenerateGeoCode
                                    //foreach (DataRow dr in GeoCodeTbl.Rows)
                                    //{
                                    //    string GoogleAPIKey = GeoCodeTbl.Rows[0]["GoogleAPIKey"]?.ToString() ?? string.Empty;
                                    //    string KeyColumn = GeoCodeTbl.Rows[0]["KeyColumn"]?.ToString() ?? string.Empty;
                                    //    string LonColumn = GeoCodeTbl.Rows[0]["LonColumn"]?.ToString() ?? string.Empty;
                                    //    string LatColumn = GeoCodeTbl.Rows[0]["LatColumn"]?.ToString() ?? string.Empty;
                                    //    string AddressColumn = GeoCodeTbl.Rows[0]["AddressColumn"]?.ToString() ?? string.Empty;
                                    //    string geoDBSchTbl = GeoCodeTbl.Rows[0]["trgDBSchTbl"]?.ToString() ?? string.Empty;

                                    //    SQLObject geoObj = CommonDB.SQLObjectFromDBSchobj(geoDBSchTbl);

                                    //    Microsoft.Data.SqlClient.SqlConnection smoGeoCodeSqlCon = new Microsoft.Data.SqlClient.SqlConnection(trgConString);
                                    //    ServerConnection smoGeoCodeSrvCon = new ServerConnection(smoGeoCodeSqlCon);
                                    //    Server smoSrvGeoCode = new Server(smoGeoCodeSrvCon);
                                    //    Database smoDbGeoCode = smoSrvGeoCode.Databases[geoObj.ObjDatabase];

                                    //    if (smoDbGeoCode != null)
                                    //    {
                                    //        ////Sync Schema Before Generation and Pushback
                                    //        //if (GeoCodeColumnsList.Count == 0)
                                    //        //{
                                    //        //    watch.Restart();
                                    //        //    Table baseTable = smoDbGeoCode.Tables[baseObject, baseSchema];
                                    //        //    SmoHelper.SyncGeoCodeTable(baseTable, smoDbGeoCode, KeyColumnsList, GeoCodeDatabase, GeoCodeSchema, GeoCodeObject, SurrogateColumn, generalTimeoutInSek);
                                    //        //    watch.Stop();
                                    //        //    logStack.AppendFormat("Schema changes for surrogate key generation synchronized ({0} sec) {1}", (watch.ElapsedMilliseconds / 1000).ToString(), Environment.NewLine);
                                    //        //}

                                    //        ////Build GeoCode and Pushback
                                    //        //watch.Restart();
                                    //        //string cmdGeoCodePush = CommonDB.BuildGeoCodeGenPushCmd(BaseDbSchObj, GeoCodeDbSchObj, KeyColumnsList, GeoCodeColumnsList, SurrogateColumn);
                                    //        //logSurrogateKeyCmd = cmdGeoCodePush;
                                    //        //codeStack.AppendIf(sqlFlowParam.dbg > 1, CodeStackSection("Surrogate Key Cmd:", logSurrogateKeyCmd));
                                    //        //CommonDB.ExecNonQuery(GeoCodeSqlCon, cmdGeoCodePush, bulkLoadTimeoutInSek);
                                    //        //watch.Stop();
                                    //        //logStack.AppendFormat("Surrogate key generated and pushed back to source table ({0} sec) {1}", (watch.ElapsedMilliseconds / 1000).ToString(), Environment.NewLine);

                                    //        smoDbGeoCode = null;
                                    //        smoSrvGeoCode = null;
                                    //        smoGeoCodeSrvCon.Disconnect();
                                    //        smoGeoCodeSqlCon.Close();
                                    //        smoGeoCodeSqlCon.Dispose();

                                    //    }
                                    //}


                                    #endregion GenerateGeoCode

                                    #region AddUnknownDimElement
                                    if (sp.InsertUnknownDimRow)
                                    {
                                        using (var op = logger.TrackOperation("Insert unknown row to Dim operation"))
                                        {
                                            string cmdInsertUknRow = CommonDB.InsertCmdUknownDimRow(
                                                sp.trgConString,
                                                oTrgDatabase.UnquotedName,
                                                oTrgSchema.UnquotedName,
                                                oTrgObject.UnquotedName,
                                                ObjectNameProcessor.GetUnquotedNamesList(oKeyColumns));

                                            logger.LogCodeBlock("Insert unknown row to dim Cmd:", cmdInsertUknRow);
                                            CommonDB.ExecNonQuery(trgSqlCon, cmdInsertUknRow, sp.bulkLoadTimeoutInSek);
                                        }
                                    }
                                    #endregion AddUnknownDimElement

                                    #region trgDesiredIndex
                                    if (sp.trgDesiredIndex.Length > 0)
                                    {
                                        using (var op = logger.TrackOperation("Synchronize desired indexes"))
                                        {
                                            IndexManagement im = new IndexManagement();
                                            string indexLog = im.EnsureIndexes(trgDatabaseObj, sp.trgDesiredIndex);
                                            if (!string.IsNullOrEmpty(indexLog))
                                            {
                                                logger.LogInformation(indexLog);
                                            }
                                        }

                                    }
                                    #endregion trgDesiredIndex

                                    execTime.Stop();
                                    var totDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                    var throughput = sp.srcRowCount / (totDurationFlow > 0 ? totDurationFlow : 1);
                                    logger.LogInformation($"Total processing time ({totDurationFlow} sec) {throughput} (rows/sec)");
                                    
                                }

                                sp.logRuntimeCmd = logOutput.ToString();

                                Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                                #region TruncatePreTable
                                if (sp.truncatePreTableOnCompletion && sp.Success)
                                {
                                    using (var op = logger.TrackOperation("Truncate PreTable"))
                                    {
                                        CommonDB.TruncatePreTable(prevTbl, sp.bulkLoadTimeoutInSek);
                                    }
                                }
                                #endregion TruncatePreTable

                                sp.result = logOutput.ToString() + Environment.NewLine;
                            }
                            catch (Exception e)
                            {
                                sp.result = Shared.LogException(sqlFlowParam, e, logOutput, logger, sqlFlowCon, sp);
                            }
                            finally
                            {
                                smoSqlConSrc.Close();
                                smoSqlConSrc.Dispose();

                                smoSqlConTrg.Close();
                                smoSqlConTrg.Dispose();

                                srcSqlCon.Close();
                                srcSqlCon.Dispose();

                                trgSqlCon.Close();
                                trgSqlCon.Dispose();

                                sqlFlowCon.Close();
                            }
                        }
                    }

                    return sp.result;
                }
                catch (Exception e)
                {
                    sp.result = Shared.LogOuterError(sqlFlowParam, e, sp, logger, logOutput, sqlFlowCon);
                }
                finally
                {
                    sqlFlowCon.Close();
                }
            }

            return sp.result;
        }

        static void batchStepCompleted(RealTimeLogger logger, string srcSelectRange, string taskDuration, int totalTaskCounter)
        {
            // Declare the task status counter outside any logging blocks.
            string taskStatusCounter = $"{_batchTaskCounter}/{totalTaskCounter}";

            // Log the processed range using the RealTimeLogger.
            logger.LogInformation($"Processed range {srcSelectRange}");

            // Build the event arguments with the provided details.
            var eventArgsInitLoadBatchStep = new EventArgsInitLoadBatchStep
            {
                srcSelectRange = srcSelectRange,
                RangeTimeSpan = taskDuration,
                totalTaskCounter = totalTaskCounter,
                taskStatusCounter = taskStatusCounter
            };

            // Invoke the event to signal that the batch step is completed.
            OnInitLoadBatchStepOnDone?.Invoke(null, eventArgsInitLoadBatchStep);

            // Increment the batch task counter.
            _batchTaskCounter++;
        }

        private static void HandlerOnRowsCopied(object sender, EventArgsRowsCopied e)
        {
            OnRowsCopied?.Invoke(sender, e);
        }
         
        private static string GetStagingTableName(string trgObject, int flowId)
        {
            return $"{trgObject}_{flowId}";
        }

        /// <summary>
        /// Distributes an integer value into a specified number of parts.
        /// </summary>
        /// <param name="total">The total integer value to be distributed.</param>
        /// <param name="divider">The number of parts to divide the total into.</param>
        /// <returns>An IEnumerable of integers representing the divided parts of the total. If the divider is zero, returns an IEnumerable containing a single zero.</returns>
        internal static IEnumerable<int> DistributeInteger(int total, int divider)
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

        #endregion ProcessIngestion
    }
}


