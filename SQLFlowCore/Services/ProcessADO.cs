using DatabaseSchemaReader.DataSchema;
using MySql.Data.MySqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Logger;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Represents a static class that provides functionality for processing ADO.NET operations in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class contains methods and events for executing SQLFlow commands, escaping paths for connection strings, and handling events related to rows copied and load batch steps.
    /// </remarks>
    internal static class ProcessADO
    {
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;
        internal static event EventHandler<EventArgsInitLoadBatchStep> OnInitLoadBatchStepOnDone;

        private static EventArgsSchema schArgs = new();
        private static int _BatchTaskCounter = 1;

        /// <summary>
        /// Executes the SQL Flow process.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string for the SQL Flow.</param>
        /// <param name="flowId">The identifier for the flow.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The batch identifier.</param>
        /// <param name="dbg">Debug mode indicator.</param>
        /// <param name="sqlFlowParam">The SQL Flow item to be processed.</param>
        /// <returns>A string representing the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            PushToSql.OnRowsCopied += OnRowsCopied;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionCsv", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

            ServiceParam sp = ServiceParam.Current;
            var trgSqlCon = new SqlConnection();
            
            using (var sqlFlowCon = new SqlConnection(sqlFlowParam.sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();

                    var retryErrorCodes = new Hashtable(StringComparer.InvariantCultureIgnoreCase);
                    RetryCodes rCodes = new RetryCodes();
                    retryErrorCodes = rCodes.HashTbl;

                    DataTable paramTbl = new DataTable();
                    DataTable incrTbl = new DataTable();
                    DataTable DateTimeFormats = new DataTable();
                    DataTable procParamTbl = new DataTable();

                    using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
                    {
                        Shared.GetFilePipelineMetadata("[flw].[GetRVFlowADO]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                    }

                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        sp.srcWithHint = sp.srcIsSynapse ? "nolock" : "readpast";
                        sp.trgWithHint = sp.trgIsSynapse ? "nolock" : "readpast";

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

                            conStringParser = new ConStringParser(sp.srcConString);

                            if (sp.srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                            {
                                sp.srcConString = conStringParser.ConBuilderMySql.ConnectionString;
                            }
                            else
                            {
                                sp.srcConString = conStringParser.ConBuilderMsSql.ConnectionString;
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

                            conStringParser = new ConStringParser(sp.trgConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow Target" }};
                            sp.trgConString = conStringParser.ConBuilderMsSql.ConnectionString;
                            trgSqlCon.ConnectionString = sp.trgConString;
                                
                            DbConnection srcCon = null;
                            //Dump App Assemblies
                            #region DumpAssemblies
                            if (sqlFlowParam.dbg > 2)
                            {
                                var Assemblies = AppDomain.CurrentDomain.GetAssemblies();
                                string AsembList = "";
                                foreach (Assembly asb in Assemblies)
                                {
                                    //string filePath = (!asb.CodeBase) ? new Uri(asb.CodeBase).LocalPath : "";
                                    AsembList = AsembList + Environment.NewLine + asb.FullName; //+ " :: " + Path.GetDirectoryName(filePath);
                                }
                                logger.LogCodeBlock("Assembly List:", AsembList);
                            }
                            #endregion DumpAssemblies

                            sp.srcRowCount = 0;
                            try
                            {
                                trgSqlCon.Open();
                                var srcWhere = "";

                                List<ObjectName> oIncrementalColumns2 = CommonDB.ParseObjectNames(sp.incrementalColumns);

                                sp.incrementalColumns = RemoveSquareBrackets(sp.incrementalColumns);
                                sp.dateColumn = RemoveSquareBrackets(sp.dateColumn);
                                sp.srcDatabase = RemoveSquareBrackets(sp.srcDatabase);
                                sp.srcSchema = RemoveSquareBrackets(sp.srcSchema);
                                sp.srcObject = RemoveSquareBrackets(sp.srcObject);
                                sp.stgSchema = RemoveSquareBrackets(sp.stgSchema);
                                sp.trgDatabase = RemoveSquareBrackets(sp.trgDatabase);
                                sp.trgSchema = RemoveSquareBrackets(sp.trgSchema);
                                sp.trgObject = RemoveSquareBrackets(sp.trgObject);
                                sp.ignoreColumns = RemoveSquareBrackets(sp.ignoreColumns);
                                sp.InitLoadKeyColumn = RemoveSquareBrackets(sp.InitLoadKeyColumn);

                                string[] iCols = sp.ignoreColumns.Split(',');
                                sp.ignoreColumns = "''";
                                if (iCols.Length > 0)
                                {
                                    List<string> myList = iCols.ToList();
                                    sp.ignoreColumns = string.Join(",", myList.ConvertAll(m => $"'{m}'").ToArray());
                                }

                                //Init LogStack
                                logger.LogInformation(
                                    $"Info: Init data pipeline from [{sp.srcDatabase}].[{sp.srcObject}] to [{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}] {sp.onErrorResume}");

                                if (sp.cmdSchema.Length > 2)
                                {
                                    using (var operation = logger.TrackOperation("CreateSchema"))
                                    {
                                        logger.LogCodeBlock("cmdSchema", sp.cmdSchema);
                                        
                                        CommonDB.ExecDDLScript(trgSqlCon, sp.cmdSchema, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);

                                        // Log completion message for schema creation
                                        logger.LogInformation($"Creating schema on target");
                                    }
                                }

                                //Execute preprosess on target 
                                if (sp.preProcessOnTrg.Length > 2)
                                {
                                    using (var operation = logger.TrackOperation("PreProcessOnTarget"))
                                    {
                                        var cmdOnSrc = new ExecNonQuery(trgSqlCon, sp.preProcessOnTrg, sp.bulkLoadTimeoutInSek);
                                        cmdOnSrc.Exec();

                                        // Log completion of PreProcess execution
                                        logger.LogInformation($"PreProcess executed on target");
                                    }
                                }

                                // Fetch VirtualColumnSchema
                                #region VirtualColumns
                                logger.LogInformation($"Fetch Virtual Schema");
                                string virtualColumnCmd =
                                    $@"exec [flw].[GetVirtualADOColumns] @FlowID= {sqlFlowParam.flowId.ToString()}";
                                logger.LogCodeBlock("GetVirtualColumnsADO", virtualColumnCmd);

                                var virtualSchemaData = new GetData(sqlFlowCon, virtualColumnCmd, sp.generalTimeoutInSek);
                               
                                DataTable vcTbl = virtualSchemaData.Fetch();
                                #endregion VirtualSchema 

                                #region FetchSourceSchemaAndSync
                                SchemaADO sADO;
                                using (var operation = logger.TrackOperation("Schema Synchronization"))
                                {
                                    srcCon = CommonDB.GetDbConnection(sp.srcConString, sp.srcDSType);
                                    sADO = new SchemaADO(logger, trgSqlCon, srcCon, sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcDSType, sp.trgDatabase, sp.trgSchema, sp.trgObject, sp.ignoreColumns, vcTbl, sp.generalTimeoutInSek, sp.tmpSchema, sp.syncSchema, sqlFlowParam.flowId);

                                    logger.LogCodeBlock("Target Create Script:", sADO.CreateCmd);
                                    logger.LogCodeBlock("Target Sync Script:", sADO.SyncCmd);
                                }
                                #endregion FetchSourceSchemaAndSync

                                // If Target Is In Sync
                                var runFullload = true;
                                DataTable expTbl = null;
                                bool runFullloadQueryRes = false;
                                if (sADO.TrgIsInSync)
                                {
                                    //Now Fetch the Max Values in target
                                    sp.logCreateCmd = sADO.CreateCmd;

                                    //Fetched Runtime Where filters
                                    if (incrTbl.Rows.Count >= 1)
                                    {
                                        string nxtTrgDatabase = GetValueFromRow(incrTbl.Rows[0], "nxtTrgDatabase");
                                        string nxtTrgSchema = GetValueFromRow(incrTbl.Rows[0], "nxtTrgSchema");
                                        string nxtTrgObject = GetValueFromRow(incrTbl.Rows[0], "nxtTrgObject");
                                        string nxtIncrementalColumns = GetValueFromRow(incrTbl.Rows[0], "nxtIncrementalColumns");
                                        string nxtDateColumn = GetValueFromRow(incrTbl.Rows[0], "nxtDateColumn");
                                        string nxtIncrementalClauseExp = GetValueFromRow(incrTbl.Rows[0], "nxtIncrementalClauseExp");
                                        int nxtNoOfOverlapDays = GetIntFromRow(incrTbl.Rows[0], "nxtNoOfOverlapDays");
                                        bool nxtTrgIsSynapse = GetBoolFromRow(incrTbl.Rows[0], "nxtTrgIsSynapse");

                                        string nxtTenantId = incrTbl.Rows[0]["nxtTenantId"]?.ToString() ?? string.Empty;
                                        string nxtApplicationId = incrTbl.Rows[0]["nxtApplicationId"]?.ToString() ?? string.Empty;
                                        string nxtClientSecret = incrTbl.Rows[0]["nxtClientSecret"]?.ToString() ?? string.Empty;
                                        string nxtKeyVaultName = incrTbl.Rows[0]["nxtKeyVaultName"]?.ToString() ?? string.Empty;
                                        string nxtSecretName = incrTbl.Rows[0]["nxtSecretName"]?.ToString() ?? string.Empty;
                                        string nxtStorageAccountName = incrTbl.Rows[0]["nxtStorageAccountName"]?.ToString() ?? string.Empty;
                                        string nxtBlobContainer = incrTbl.Rows[0]["nxtBlobContainer"]?.ToString() ?? string.Empty;


                                        string nxtConnectionString = GetValueFromRow(incrTbl.Rows[0], "nxtConnectionString");
                                        string nxtTrgWithHint = GetValueFromRow(incrTbl.Rows[0], "nxtTrgWithHint");

                                        List<ObjectName> oIncrementalColumns = CommonDB.ParseObjectNames(nxtIncrementalColumns);

                                        sp.cmdMax = CommonDB.GetIncWhereExp("MAX", nxtTrgDatabase, nxtTrgSchema, nxtTrgObject, nxtDateColumn, oIncrementalColumns, sp.srcDSType, nxtNoOfOverlapDays, nxtTrgIsSynapse, nxtIncrementalClauseExp, nxtTrgWithHint);

                                        if (sp.cmdMax.Length > 0)
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

                                            ConStringParser tmpConStringParser = new ConStringParser(nxtConnectionString) {ConBuilderMsSql = { ApplicationName = "SQLFlow Target" }};
                                            string tmpTrgConString = tmpConStringParser.ConBuilderMsSql.ConnectionString;

                                            SqlConnection tmpConnection = new SqlConnection(tmpTrgConString);
                                            tmpConnection.Open();

                                            string chkObject = $"[{nxtTrgSchema}].[{nxtTrgObject}]";
                                            if (CommonDB.CheckIfObjectExsists(tmpConnection, chkObject, sp.generalTimeoutInSek))
                                            {
                                                using (var operation = logger.TrackOperation("FetchIncrementalData"))
                                                {
                                                    var expData = new GetData(tmpConnection, sp.cmdMax, sp.generalTimeoutInSek);
                                                    expTbl = expData.Fetch();

                                                    // Log completion of incremental data fetch
                                                    logger.LogInformation($"Incremental data fetched from next flow");
                                                }
                                            }
                                            tmpConnection.Close();
                                            tmpConnection.Dispose();
                                        }
                                    }
                                    else
                                    {
                                        sp.cmdMax = CommonDB.GetIncWhereExp("MAX", sp.trgDatabase, sp.trgSchema, sp.trgObject, sp.dateColumn, oIncrementalColumns2, sp.srcDSType, sp.noOfOverlapDays, sp.trgIsSynapse, sp.IncrementalClauseExp, sp.trgWithHint);
                                        if (sp.cmdMax.Length > 0)
                                        {
                                            using (var operation = logger.TrackOperation("FetchIncrementalData"))
                                            {
                                                logger.LogCodeBlock("GetIncrementalWhereExpression", sp.cmdMax);

                                                var expData = new GetData(trgSqlCon, sp.cmdMax, sp.generalTimeoutInSek);
                                                expTbl = expData.Fetch();

                                                // Log completion of the data fetch operation
                                                logger.LogInformation($"Incremental data fetched");
                                            }
                                        }
                                    }
                                    logger.LogCodeBlock("Query To Fetch Runtime Max Values:", sp.cmdMax);
                                }

                                if (expTbl != null)
                                {
                                    if (sp.FetchMinValuesFromSysLog && sp._whereXML.Length > 0)
                                    {
                                        if (Functions.IsWhereXMLLess(sp._whereXML, expTbl, DateTimeFormats))
                                        {
                                            runFullloadQueryRes = false;
                                            sp.whereXML = sp._whereXML;
                                        }
                                    }
                                    else
                                    {
                                        if (expTbl.Columns.Contains("IncExp"))
                                            //Set Final WhereKeyExp
                                            sp._whereIncExp = " AND " + expTbl.Rows[0]["IncExp"];

                                        if (expTbl.Columns.Contains("DateExp"))
                                            //Set Final DateExp
                                            sp._whereDateExp = " AND " + expTbl.Rows[0]["DateExp"];

                                        if (expTbl.Columns.Contains("RunFullload"))
                                            //Set Fulload CheckForError
                                            runFullloadQueryRes = expTbl.Rows[0]["RunFullload"].ToString() == "1" ? true : false;

                                        if (expTbl.Columns.Contains("XmlNodes"))
                                            //Set Final DateExp
                                            sp.whereXML = expTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                                    }
                                }

                                logger.LogInformation($"Fetched runtime filters");

                                //Init Load when source table contains data
                                if (sp._whereIncExp.Length > 0)
                                    sp.whereIncExp = sp._whereIncExp;

                                if (sp._whereDateExp.Length > 0)
                                    sp.whereDateExp = sp._whereDateExp;

                                if (sp.whereIncExp.Length == 0 && sp.whereDateExp.Length == 0)
                                {
                                    runFullload = true;
                                }
                                else if (runFullloadQueryRes) //Empty Table
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
                                else if (runFullload && sp.whereIncExp.Length < 2 && sp.whereDateExp.Length < 1)
                                    srcWhere = "";
                                // IncColumns without datecolumn or key column
                                else if (sp.whereDateExp.Length > 0)
                                    srcWhere = sp.whereDateExp;
                                else if (sp.whereIncExp.Length > 0)
                                    srcWhere = sp.whereIncExp;

                                if (sp.srcFilter.Length > 0 & sp.srcFilterIsAppend)
                                {
                                    srcWhere = srcWhere + " " + sp.srcFilter;
                                }

                                string filterExpressionBase = $"TruncateTrg: {sp.truncateTrg}{Environment.NewLine}Full load onFlow: {sp.fullload.ToString()}{Environment.NewLine}SrcFilter onFlow: {sp.srcFilter}{Environment.NewLine}RunFullload from MaxQuery: {runFullload}{Environment.NewLine}WhereDateExp from MaxQuery: {sp.whereDateExp}{Environment.NewLine}WhereKeyExp from MaxQuery: {sp.whereKeyExp}{Environment.NewLine}WhereIncExp from MaxQuery: {sp.whereIncExp}{Environment.NewLine}Final srcWhere: {srcWhere}{Environment.NewLine}";
                                
                                logger.LogCodeBlock("Filter Expression Values", filterExpressionBase);
                                logger.LogInformation($"Runtime filters: {srcWhere}");

                                if (sADO.TrgIsInSync)
                                {
                                    #region TruncateTarget
                                    using (var operation = logger.TrackOperation("Truncate target table"))
                                    {
                                        Shared.TruncateTargetTable(sp, logger, trgSqlCon);
                                    }
                                    #endregion TruncateTarget

                                    #region FetchIndexes
                                    using (var operation = logger.TrackOperation("Log table indexes"))
                                    {
                                        Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                                    }
                                    #endregion FetchIndexes

                                    //Ingestion to Staging
                                    #region InitLoad
                                    if (sp.InitLoad == true)
                                    {
                                        //If noOfThreads = 1  then use current connection
                                        bool dataLoaded = false;
                                        var tasks = new List<Task>();
                                        sp.srcRowCount = 1000000; //Sample Rowcount
                                        logger.LogInformation("Initial Load Started");
                                        logger.LogCodeBlock("Source Data Load Method:", "Initial Load Started");

                                        StreamToSql.OnRowsCopied += HandlerOnRowsCopied;
                                        SortedList<int, ExpSegment> SrcBatched = CommonDB.GetSrcSelectBatched(sADO.srcSelectColumns, sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcWithHint, srcWhere, sp.srcDSType, sp.InitLoadFromDate, sp.InitLoadToDate, sp.InitLoadBatchBy, sp.InitLoadBatchSize, sp.dateColumn, 0, sp.InitLoadKeyMaxValue, sp.InitLoadKeyColumn, "", "", "", false, "");

                                        foreach (var SelectB in SrcBatched)
                                        {
                                            ExpSegment tuple = SelectB.Value;
                                            var _srcSelect = tuple.SqlCMD;
                                            var _srcSelectRange = tuple.WhereClause;
                                            logger.LogCodeBlock($"InitLoad Range: {_srcSelectRange}", $"{_srcSelect}");
                                        }

                                        using (var concurrencySemaphore = new Semaphore(sp.noOfThreads, sp.noOfThreads)) //
                                        {
                                            int _totalTaskCounter = SrcBatched.Count;

                                            foreach (var SelectB in SrcBatched)
                                            {
                                                ExpSegment tuple = SelectB.Value;
                                                var _srcSelect = tuple.SqlCMD;
                                                var _srcSelectRange = tuple.WhereClause;
                                                var _logStack = logger;
                                                var taskWatch = new Stopwatch();
                                                taskWatch.Start();

                                                string _taskDuration = "";

                                                var t = Task.Factory.StartNew(() =>
                                                {
                                                    concurrencySemaphore.WaitOne();

                                                    if (sp.srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                                                    {
                                                        using (MySqlConnection MysqlConnection = new MySqlConnection(sp.srcConString))
                                                        {
                                                            try
                                                            {
                                                                MysqlConnection.Open();
                                                                using (var cmd = new MySqlCommand(_srcSelect, MysqlConnection) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                                {
                                                                    using (var srcReader = cmd.ExecuteReader())
                                                                    {
                                                                        StreamToSql.OnRowsCopied += HandlerOnRowsCopied;
                                                                        var bulk = new StreamToSql(sp.trgConString,
                                                                        $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]",
                                                                        $"[{sp.srcDatabase}].[{sp.srcSchema}].[{sp.srcObject}]",
                                                                        sADO.GetMapDictionary(sqlFlowParam.flowId),
                                                                        sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                                        retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount); // BatchSize
                                                                        
                                                                        bulk.StreamWithRetries(srcReader);
                                                                        dataLoaded = true;
                                                                        srcReader.Close();
                                                                        srcReader.Dispose();
                                                                    }
                                                                    cmd.Dispose();
                                                                }

                                                                MysqlConnection.Close();
                                                                MysqlConnection.Dispose();
                                                                dataLoaded = true; //CheckForError if this is required
                                                                                   //taskWatch.Stop(); 
                                                            }
                                                            catch (Exception)
                                                            {
                                                                throw;
                                                            }
                                                            finally
                                                            {
                                                                taskWatch.Stop();
                                                                _taskDuration = (taskWatch.ElapsedMilliseconds / 1000).ToString();
                                                                //concurrencySemaphore.Release();
                                                            }
                                                        }
                                                    }
                                                    if (sp.srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
                                                    {
                                                        using (SqlConnection sqlConnection = new SqlConnection(sp.srcConString))
                                                        {
                                                            try
                                                            {
                                                                sqlConnection.Open();
                                                                using (var cmd = new SqlCommand(_srcSelect, sqlConnection) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                                {
                                                                    using (var srcReader = cmd.ExecuteReader())
                                                                    {
                                                                        var bulk = new StreamToSql(sp.trgConString,
                                                                        $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", $"[{sp.srcDatabase}].[{sp.srcSchema}].[{sp.srcObject}]", sADO.GetMapDictionary(sqlFlowParam.flowId),
                                                                        sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                                        retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount);
                                                                       
                                                                        bulk.StreamWithRetries(srcReader);
                                                                        dataLoaded = true;
                                                                        srcReader.Close();
                                                                        srcReader.Dispose();
                                                                        
                                                                    }
                                                                    cmd.Dispose();
                                                                }
                                                                sqlConnection.Close();
                                                                sqlConnection.Dispose();
                                                                dataLoaded = true; //CheckForError if this is required
                                                            }
                                                            catch (Exception)
                                                            {
                                                                throw;
                                                            }
                                                            finally
                                                            {
                                                                taskWatch.Stop();
                                                                _taskDuration = (taskWatch.ElapsedMilliseconds / 1000).ToString();
                                                                //concurrencySemaphore.Release();
                                                            }
                                                        }
                                                    }

                                                    concurrencySemaphore.Release();

                                                }, TaskCreationOptions.LongRunning);

                                                t.ContinueWith(_ => { batchStepCompleted(_logStack, _srcSelectRange, _taskDuration, _totalTaskCounter); }, TaskContinuationOptions.OnlyOnRanToCompletion);

                                                tasks.Add(t);
                                            }

                                            Task.WaitAll(tasks.ToArray());

                                            if (dataLoaded)
                                            {
                                                // Prepare SQL command for fetching estimated row count.
                                                var cmdRowCount = CommonDB.GetEstimatedRowCountSQL(sp.trgDatabase, sp.trgSchema, sp.trgObject, sp.srcWithHint, "", "MSSQL");

                                                // Track the operation of fetching the target row count.
                                                using (logger.TrackOperation("FetchTargetRowCount"))
                                                {
                                                    logger.LogCodeBlock("Target Table RowCount:", cmdRowCount);
                                                    var cCount = CommonDB.ExecuteScalar(sp.trgConString, cmdRowCount, "MSSQL", sp.bulkLoadTimeoutInSek);
                                                    sp.srcRowCount = int.Parse(cCount.ToString());
                                                    sp.logFetched = sp.srcRowCount;
                                                    sp.logInserted = sp.srcRowCount;
                                                }
                                            }
                                            else
                                            {
                                                sp.logFetched = 0;
                                                sp.logInserted = 0;
                                            }
                                        }
                                    }
                                    #endregion InitLoad
                                    #region MultiThread
                                    //If full load then transfer directly to target schema. Riktig tenkt men feil. :)
                                    else if (sp.streamData == false)
                                    {
                                        logger.LogCodeBlock("Source Data Load Method:", "Read To Memory");

                                        var srcSelectTbl = CommonDB.GetSrcSelect(sADO.srcSelectColumns, sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcWithHint, srcWhere, sp.srcDSType);
                                        logger.LogCodeBlock("Load Source Data:", srcSelectTbl);

                                        sp.logSelectCmd = srcSelectTbl;

                                        // Declare variables outside the using block
                                        List<ParameterObject> cmdParams = new List<ParameterObject>();
                                        DataTable srcTbl;

                                        using (logger.TrackOperation("LoadSourceData"))
                                        {
                                            logger.LogCodeBlock("Source Select Query", srcSelectTbl);
                                            srcTbl = CommonDB.RunQuery(sp.srcConString, srcSelectTbl, sp.srcDSType, sp.generalTimeoutInSek, cmdParams);
                                        }

                                        //Calculate optimal batch size
                                        sp.srcRowCount = srcTbl.Rows.Count;
                                        var batchSize = sp.srcRowCount / sp.noOfThreads;

                                        batchSize = batchSize == 0 ? sp.srcRowCount : batchSize;

                                        logger.LogInformation("srcRowCount fetched from in Memory Table");
                                        sp.logFetched = sp.srcRowCount;

                                        var batches = DataTableSplitter.SplitDataTable(srcTbl, batchSize);
                                        logger.LogInformation($"Batches created for {sp.srcRowCount} rows with {batchSize} as batch size");

                                        var finalBatchSize = sp.bulkLoadBatchSize == -1 ? batchSize : sp.bulkLoadBatchSize;

                                        PushToSql.OnRowsCopied += HandlerOnRowsCopied;

                                        var concurrencySemaphore = new Semaphore(sp.noOfThreads, sp.noOfThreads);
                                        var tasks = new List<Task>();

                                        foreach (DataTable dt in batches)
                                        {
                                            int _totalBatchCount = batches.Count();
                                            int _currentRowCount = dt.Rows.Count;
                                            var _logger = logger;
                                            string _taskDuration = "";
                                            var _taskWatch = new Stopwatch();
                                            _taskWatch.Start();

                                            var t = Task.Factory.StartNew(() =>
                                            {
                                                concurrencySemaphore.WaitOne();

                                                var bulk = new PushToSql(sp.trgConString,
                                                $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", $"[{sp.srcDatabase}].[{sp.srcObject}]", sADO.GetMapDictionary(sqlFlowParam.flowId),
                                                sp.bulkLoadTimeoutInSek, finalBatchSize, logger, sp.maxRetry, sp.retryDelayMs,
                                                retryErrorCodes, sqlFlowParam.dbg, ref _currentRowCount); // BatchSize

                                                bulk.WriteWithRetries(dt);
                                                dt.Clear();
                                                dt.Dispose();
                                                concurrencySemaphore.Release();
                                            }, TaskCreationOptions.LongRunning);

                                            t.ContinueWith(_ =>
                                            {

                                                _taskWatch.Stop();
                                                _taskDuration = (_taskWatch.ElapsedMilliseconds / 1000).ToString();
                                                inMemStepCompleted(_logger, _totalBatchCount, _currentRowCount, _taskDuration);
                                            }, TaskContinuationOptions.OnlyOnRanToCompletion);
                                            tasks.Add(t);
                                        }

                                        Task.WaitAll(tasks.ToArray());
                                        

                                        //Release unused memory
                                        srcTbl.Rows.Clear();
                                        srcTbl.Dispose();
                                        srcTbl = null;

                                        sp.logInserted =+ sp.srcRowCount;

                                        logger.LogInformation($"Bulkloaded {sp.srcRowCount} rows to target");

                                    }
                                    #endregion MultiThread
                                    #region SingelThread    
                                    else
                                    {
                                        var cmdRowCount = "";
                                        logger.LogCodeBlock("Source Data Load Method:", "Stream with multiple sorted reads");

                                        //Estimated rows basedon meta data
                                        // Is there a point doing a count when count can be fetched from target table?
                                        if (srcWhere.Length == 0)
                                        {
                                            cmdRowCount = CommonDB.GetEstimatedRowCountSQL(sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcWithHint, srcWhere, sp.srcDSType);

                                            // Track the operation of fetching the estimated source row count.
                                            using (logger.TrackOperation("FetchEstimatedSrcRowCount"))
                                            {
                                                logger.LogCodeBlock("Estimated source rowcount cmd:", cmdRowCount);
                                                var cCount = CommonDB.ExecuteScalar(sp.srcConString, cmdRowCount, sp.srcDSType, sp.bulkLoadTimeoutInSek);
                                                if (cCount != null)
                                                {
                                                    sp.srcRowCount = int.Parse(cCount.ToString());
                                                }
                                            }
                                        }

                                        if (sp.truncateTrg == false || sp.fullload == false) //Not sure if its a valid check
                                        {
                                            //If source is view or we have incremental load
                                            // Is there a point doing a count when count can be fetched from target table?
                                            if (sp.srcRowCount == 0)
                                            {
                                                cmdRowCount = CommonDB.GetActualRowCountSQL(sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcWithHint, srcWhere, sp.srcDSType);

                                                using (logger.TrackOperation("FetchActualSrcRowCount"))
                                                {
                                                    logger.LogCodeBlock("Calculate source rowcount on cmd:", cmdRowCount);
                                                    var cCount = CommonDB.ExecuteScalar(sp.srcConString, cmdRowCount, sp.srcDSType, sp.bulkLoadTimeoutInSek);
                                                    sp.srcRowCount = int.Parse(cCount.ToString());
                                                }
                                            }

                                            sp.logFetched = sp.srcRowCount;
                                        }

                                        //If noOfThreads = 1  then use current connection
                                        bool dataLoaded = false;

                                        logger.LogCodeBlock("Source Data Load Method:", "Stream with singel read");

                                        var srcSelectStream = CommonDB.GetSrcSelect(sADO.srcSelectColumns, sp.srcDatabase, sp.srcSchema, sp.srcObject, sp.srcWithHint, srcWhere, sp.srcDSType);
                                        logger.LogCodeBlock("Select Statement Data:", sp.logSelectCmd);

                                        sp.logSelectCmd = srcSelectStream;

                                        if (sp.srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                                        {
                                            using (MySqlConnection MysqlConnection = new MySqlConnection(sp.srcConString))
                                            {
                                                MysqlConnection.Open();
                                                using (var cmd = new MySqlCommand(srcSelectStream, MysqlConnection) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                {
                                                    using (var srcReader = cmd.ExecuteReader())
                                                    {
                                                        StreamToSql.OnRowsCopied += HandlerOnRowsCopied;
                                                        var bulk = new StreamToSql(sp.trgConString,
                                                        $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", $"[{sp.srcDatabase}].[{sp.srcSchema}].[{sp.srcObject}]", sADO.GetMapDictionary(sqlFlowParam.flowId),
                                                        sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                        retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount); // BatchSize
                                                        
                                                        bulk.StreamWithRetries(srcReader);
                                                        dataLoaded = true;
                                                        srcReader.Close();
                                                        srcReader.Dispose();
                                                        
                                                    }
                                                    cmd.Dispose();
                                                }

                                                MysqlConnection.Close();
                                                MysqlConnection.Dispose();
                                                dataLoaded = true; //CheckForError if this is required
                                            }
                                        }
                                        if (sp.srcDSType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase))
                                        {
                                            using (SqlConnection sqlConnection = new SqlConnection(sp.srcConString))
                                            {
                                                sqlConnection.Open();
                                                using (var cmd = new SqlCommand(srcSelectStream, sqlConnection) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                {
                                                    using (var srcReader = cmd.ExecuteReader())
                                                    {
                                                        StreamToSql.OnRowsCopied += HandlerOnRowsCopied;
                                                        var bulk = new StreamToSql(sp.trgConString,
                                                        $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", $"[{sp.srcDatabase}].[{sp.srcSchema}].[{sp.srcObject}]", sADO.GetMapDictionary(sqlFlowParam.flowId),
                                                        sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                        retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount);
                                                       
                                                        bulk.StreamWithRetries(srcReader);
                                                        dataLoaded = true;
                                                        srcReader.Close();
                                                        srcReader.Dispose();
                                                        
                                                    }
                                                    cmd.Dispose();
                                                }

                                                sqlConnection.Close();
                                                sqlConnection.Dispose();
                                                dataLoaded = true; //CheckForError if this is required
                                            }
                                        }

                                        if (dataLoaded)
                                        {
                                            cmdRowCount = CommonDB.GetEstimatedRowCountSQL(sp.trgDatabase, sp.trgSchema, sp.trgObject, sp.srcWithHint, "", "MSSQL");

                                            using (logger.TrackOperation("FetchTargetRowCount"))
                                            {
                                                logger.LogCodeBlock("Target Table RowCount:", cmdRowCount);
                                                var cCount = CommonDB.ExecuteScalar(sp.trgConString, cmdRowCount, "MSSQL", sp.bulkLoadTimeoutInSek);
                                                sp.srcRowCount = int.Parse(cCount.ToString());
                                                sp.logFetched = sp.srcRowCount;
                                                sp.logInserted = sp.srcRowCount;
                                            }
                                        }
                                        else
                                        {
                                            sp.logFetched = 0;
                                            sp.logInserted = 0;
                                        }

                                    }
                                    #endregion SingelThread 

                                    if (sp.Indexes.Length > 5)
                                    {
                                        using (logger.TrackOperation("RecreateTargetTableIndexes"))
                                        {
                                            CommonDB.ExecDDLScript(trgSqlCon, sp.Indexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                        }
                                    }
                                    else if (sp.trgIndexes.Length > 5)
                                    {
                                        using (logger.TrackOperation("RecreateTargetTableIndexesFromSysLog"))
                                        {
                                            CommonDB.ExecDDLScript(trgSqlCon, sp.trgIndexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                        }
                                    }
                                }
                                else
                                {
                                    logger.LogInformation("Unable to fetch src meta data. Please check config");
                                }

                                #region postProcessOnTrg
                                if (sp.postProcessOnTrg.Length > 2)
                                {
                                    using (var operation = logger.TrackOperation("Post-Process Target Execution"))
                                    {
                                        logger.LogCodeBlock("Post Process Details", sp.postProcessOnTrg);

                                        var cmdOnTrg = new ExecNonQuery(trgSqlCon, sp.postProcessOnTrg, sp.bulkLoadTimeoutInSek);
                                        cmdOnTrg.Exec();
                                    }
                                }
                                #endregion postProcessOnTrg

                                #region MaxValueForSysLog
                                sp.cmdMax = CommonDB.GetIncWhereExp("MAX", sp.trgDatabase, sp.trgSchema, sp.trgObject, sp.dateColumn, oIncrementalColumns2, sp.srcDSType, sp.noOfOverlapDays, sp.trgIsSynapse, sp.IncrementalClauseExp, sp.trgWithHint);
                                logger.LogCodeBlock("Query To Fetch Max Values:", sp.cmdMax);
                                if (sp.cmdMax.Length > 0)
                                {
                                    // Fetch Where filters.Values stored in SysLog
                                    var expData = new GetData(trgSqlCon, sp.cmdMax, sp.generalTimeoutInSek);
                                    
                                    // Operation and execution inside
                                    using (var operation = logger.TrackOperation("Fetch Filters for Incremental Load"))
                                    {
                                        logger.LogCodeBlock("Executing Query to Fetch Filters", sp.cmdMax);
                                        expTbl = expData.Fetch();
                                    }

                                    if (expTbl != null)
                                    {
                                        if (expTbl.Columns.Contains("IncExp"))
                                            //Set Final WhereKeyExp
                                            sp.whereIncExp = " AND " + expTbl.Rows[0]["IncExp"];

                                        if (expTbl.Columns.Contains("DateExp"))
                                            //Set Final DateExp
                                            sp.whereDateExp = " AND " + expTbl.Rows[0]["DateExp"];

                                        if (expTbl.Columns.Contains("RunFullload"))
                                            //Set Fullload CheckForError
                                            runFullload = expTbl.Rows[0]["RunFullload"].ToString() == "1" ? true : false;

                                        if (expTbl.Columns.Contains("XmlNodes"))
                                            //Set Final DateExp
                                            sp.whereXML = expTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                                    }

                                    logger.LogCodeBlock("XML To Fetch Max Values:", sp.whereXML);
                                }
                                #endregion MaxValueForSysLog

                                #region IngestionTransformationView
                                if (sADO.schemaTable != null)
                                {
                                    foreach (DatabaseColumn dbc in sADO.schemaTable.Columns)
                                    {
                                        string ColName = dbc.Name;
                                        string ColAlias = dbc.Name;

                                        if (sp.RemoveInColumnName.Length > 0)
                                        {
                                            ColAlias = ColAlias.Replace(sp.RemoveInColumnName, string.Empty);
                                        }

                                        if (sp.onSyncCleanColumnName)
                                        {
                                            ColAlias = Regex.Replace(ColAlias, sp.colCleanupSqlRegExp, string.Empty);
                                        }

                                        string cmdFlowTransExp = $"exec flw.UpdPreIngTransfromExp @FlowID={sqlFlowParam.flowId}, @FlowType='ado', @ColName='[{ColName}]', @ColAlias='[{ColAlias}]'";
                                        CommonDB.ExecNonQuery(sqlFlowCon, cmdFlowTransExp, sp.bulkLoadTimeoutInSek);
                                    }

                                    string vCMD = $"SELECT * FROM [flw].[GetPreViewCmd]({sqlFlowParam.flowId})";
                                    GetData AddGetView = new GetData(sqlFlowCon, vCMD, sp.bulkLoadTimeoutInSek);
                                    DataTable resTable = AddGetView.Fetch();

                                    if (resTable.Rows != null)
                                    {
                                        if (resTable.Rows.Count > 0)
                                        {
                                            string viewCmd = resTable.Rows[0]["ViewCMD"]?.ToString() ?? string.Empty;
                                            string execViewCmd = "";
                                            if (viewCmd.Length > 10)
                                            {
                                                if (viewCmd.Length > 10)
                                                {
                                                    execViewCmd = "DECLARE @val nvarchar(max) " +
                                                                   " set @val = '" + viewCmd.Replace("'", "''") + "'" +
                                                                   " exec  [" + sp.trgDatabase + "].sys.sp_executesql @val";
                                                }
                                                logger.LogCodeBlock("View CMD:", viewCmd);
                                                logger.LogCodeBlock("Create View CMD:", execViewCmd);

                                                CommonDB.ExecDDLScript(trgSqlCon, execViewCmd, sp.generalTimeoutInSek, sp.trgIsSynapse);
                                            }
                                        }
                                    }
                                }
                                #endregion IngestionTransformationView

                                #region trgDesiredIndex
                                if (sp.trgDesiredIndex.Length > 0)
                                {
                                    IndexManagement im = new IndexManagement();
                                    string indexLog;

                                    using (var operation = logger.TrackOperation("Synchronize Database Indexes"))
                                    {
                                        indexLog = im.EnsureIndexes(sp.trgConString, sp.trgDesiredIndex);

                                        // Only log if there are specific index changes to report
                                        if (!string.IsNullOrEmpty(indexLog))
                                        {
                                            logger.LogCodeBlock("Index Changes", indexLog);
                                        }
                                    }
                                }
                                #endregion trgDesiredIndex

                                execTime.Stop();
                                // Calculate duration and throughput
                                double durationInSeconds = execTime.ElapsedMilliseconds / 1000.0;
                                double rowsPerSecond = sp.srcRowCount / (durationInSeconds > 0 ? durationInSeconds : 1);
                                string endTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                                logger.LogInformation($"Total processing time, duration: {durationInSeconds:N2} sec, throughput: {rowsPerSecond:N0} rows/sec");
                                Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                                if (trgSqlCon.State == ConnectionState.Open)
                                {
                                    trgSqlCon.Close();
                                    trgSqlCon.Dispose();
                                }

                                if (sqlFlowCon.State == ConnectionState.Open)
                                {
                                    sqlFlowCon.Close();
                                }

                            }
                            catch (Exception e)
                            {
                                sp.result = Shared.LogException(sqlFlowParam, e, logOutput, logger, sqlFlowCon, sp);

                                if (trgSqlCon.State == ConnectionState.Open)
                                {
                                    trgSqlCon.Close();
                                    trgSqlCon.Dispose();
                                }

                                if (sqlFlowCon.State == ConnectionState.Open)
                                {
                                    sqlFlowCon.Close();
                                }
                            }
                            finally
                            {
                                if (srcCon != null)
                                {
                                    srcCon.Close();
                                    srcCon.Dispose();
                                }

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

        /// <summary>
        /// Handles the event when rows are copied in the data loading process.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsRowsCopied that contains the event data.</param>
        private static void HandlerOnRowsCopied(object sender, EventArgsRowsCopied e)
        {
            OnRowsCopied?.Invoke(sender, e);
        }

        /// <summary>
        /// Removes square brackets from the provided input string.
        /// </summary>
        /// <param name="input">The string from which square brackets are to be removed.</param>
        /// <returns>A string with all square brackets removed.</returns>
        private static string RemoveSquareBrackets(string input)
        {
            return input.Replace("[", "").Replace("]", "");
        }

        /// <summary>
        /// Handles the completion of a batch step in the SQLFlow process.
        /// </summary>
        /// <param name="logStack">The log stack to which the method appends information about the processed range.</param>
        /// <param name="srcSelectRange">The range of the source selection that was processed in the batch step.</param>
        /// <param name="taskDuration">The duration of the task that completed the batch step.</param>
        /// <param name="totalTaskCounter">The total number of tasks in the batch.</param>
        /// <remarks>
        /// This method is invoked when a task that processes a batch step completes. It updates the log stack with information about the processed range and the task duration. It also invokes the OnInitLoadBatchStepOnDone event, passing it an instance of EventArgsInitLoadBatchStep that contains information about the batch step.
        /// </remarks>
        static void batchStepCompleted(RealTimeLogger logger, string srcSelectRange, string taskDuration, int totalTaskCounter)
        {

            lock (logger)
            {
                string taskStatusCounter = $"{_BatchTaskCounter}/{totalTaskCounter}";
                logger.LogInformation($"processed range {srcSelectRange}");

                EventArgsInitLoadBatchStep eventArgsInitLoadBatchStep = new EventArgsInitLoadBatchStep
                {
                    srcSelectRange = srcSelectRange,
                    RangeTimeSpan = taskDuration,
                    totalTaskCounter = totalTaskCounter,
                    taskStatusCounter = taskStatusCounter
                };
                OnInitLoadBatchStepOnDone?.Invoke(null, eventArgsInitLoadBatchStep);
                _BatchTaskCounter = _BatchTaskCounter + 1;
            }
        }

        /// <summary>
        /// Handles the completion of an in-memory step in the SQLFlow process.
        /// </summary>
        /// <param name="logStack">The StringBuilder instance used for logging.</param>
        /// <param name="_totalBatchCount">The total number of batches in the current SQLFlow process.</param>
        /// <param name="_currentRowCount">The current number of rows processed in the batch.</param>
        /// <param name="_taskDuration">The duration of the task execution.</param>
        /// <remarks>
        /// This method is invoked when an in-memory step of the SQLFlow process is completed. It updates the logStack with the status of the task and invokes the OnInitLoadBatchStepOnDone event.
        /// </remarks>
        static void inMemStepCompleted(RealTimeLogger logger, int _totalBatchCount, int _currentRowCount, string _taskDuration)
        {
            lock (logger)
            {
                string taskStatusCounter = $"{_BatchTaskCounter}/{_totalBatchCount}";
                logger.LogInformation($"Processed subset {taskStatusCounter}");


                EventArgsInitLoadBatchStep eventArgsInitLoadBatchStep = new EventArgsInitLoadBatchStep
                {
                    RangeTimeSpan = _taskDuration,
                    totalTaskCounter = _totalBatchCount,
                    taskStatusCounter = taskStatusCounter
                };
                OnInitLoadBatchStepOnDone?.Invoke(null, eventArgsInitLoadBatchStep);
                _BatchTaskCounter = _BatchTaskCounter + 1;
            }
        }

        /// <summary>
        /// Retrieves the value from a specified column in a DataRow and returns it as a string.
        /// </summary>
        /// <param name="row">The DataRow from which the value is to be retrieved.</param>
        /// <param name="columnName">The name of the column in the DataRow.</param>
        /// <returns>The value from the specified column in the DataRow, with square brackets removed, as a string.</returns>
        private static string GetValueFromRow(DataRow row, string columnName)
        {
            return row[columnName].ToString().Replace("[", "").Replace("]", "");
        }

        /// <summary>
        /// Retrieves an integer value from a specified column in the provided DataRow.
        /// </summary>
        /// <param name="row">The DataRow from which to retrieve the value.</param>
        /// <param name="columnName">The name of the column in the DataRow from which to retrieve the value.</param>
        /// <returns>The integer value from the specified column. If the value cannot be converted to an integer, returns 0.</returns>
        private static int GetIntFromRow(DataRow row, string columnName)
        {
            int.TryParse(row[columnName].ToString(), out int result);
            return result;
        }

        /// <summary>
        /// Retrieves a boolean value from a DataRow based on the specified column name.
        /// </summary>
        /// <param name="row">The DataRow from which to retrieve the value.</param>
        /// <param name="columnName">The name of the column in the DataRow.</param>
        /// <returns>Returns true if the value in the specified column equals "True", otherwise returns false.</returns>
        private static bool GetBoolFromRow(DataRow row, string columnName)
        {
            return row[columnName].ToString().Equals("True");
        }


    }
}


