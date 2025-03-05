using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Logger;

namespace SQLFlowCore.Services
{

    /// <summary>
    /// Represents a static class that provides functionality for executing SQL flows.
    /// </summary>
    /// <remarks>
    /// This class contains methods and events related to the execution of SQL flows. 
    /// It includes an event for when rows are copied and an event for when a procedure is executed.
    /// </remarks>
    internal static class ProcessPrc
    {
        /// <summary>
        /// Occurs when rows are copied during the execution of a SQL flow.
        /// </summary>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        /// <summary>
        /// Occurs when a procedure is executed during the execution of a SQL flow.
        /// </summary>
        internal static event EventHandler<EventArgsPrc> OnPrcExecuted;
        #region ProcessPrc

        /// <summary>
        /// Executes a SQL flow.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQL flow database.</param>
        /// <param name="flowId">The ID of the flow to be executed.</param>
        /// <param name="execMode">The execution mode of the flow.</param>
        /// <param name="batchId">The ID of the batch in which the flow is executed.</param>
        /// <param name="dbg">A flag indicating whether debugging is enabled.</param>
        /// <param name="sqlFlowParam">The SQL flow item to be executed.</param>
        /// <returns>A string representing the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            PushToSql.OnRowsCopied += OnRowsCopied;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionPrc", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

            ServiceParam sp = ServiceParam.Current;
            var trgSqlCon = new SqlConnection();
            var srcSqlCon = new SqlConnection();

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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowPRC]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                    }

                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        if (sp.trgSecretName.Length > 0)
                        {
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                sp.trgTenantId,
                                sp.trgApplicationId,
                                sp.trgClientSecret,
                                sp.trgKeyVaultName);
                            sp.trgConString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                        }

                        conStringParser = new ConStringParser(sp.trgConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow Target" } };
                        sp.trgConString = conStringParser.ConBuilderMsSql.ConnectionString;
                        
                        trgSqlCon = new SqlConnection(sp.trgConString);
                        trgSqlCon.Open();

                        if (sp.srcSecretName.Length > 0)
                        {
                            //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(srcKeyVaultName);
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                sp.srcTenantId,
                                sp.srcApplicationId,
                                sp.srcClientSecret,
                                sp.srcKeyVaultName);
                            sp.srcConString = srcKeyVaultManager.GetSecret(sp.srcSecretName);
                        }
                        conStringParser = new ConStringParser(sp.srcConString) {ConBuilderMsSql = { ApplicationName = "SQLFlow Source" }};
                        sp.srcConString = conStringParser.ConBuilderMsSql.ConnectionString;

                        srcSqlCon = new SqlConnection(sp.srcConString);
                        srcSqlCon.Open();

                        string cmdFetchDataTypes = InferDataTypes.GetDataTypeSQL(sqlFlowParam.flowId, sqlFlowParam.flowType, sp.trgSchema, sp.trgObject);
                        sp.InferDatatypeCmd = cmdFetchDataTypes;

                        //Fetch file date from related tables 
                        //ToDo:  check if this is needed for prc flows
                        if (sp.fileDate > 0)
                        {
                            Shared.GetFildateFromNextFlow(logger, incrTbl, sp);
                        }

                        //Ensure valid copy Path
                        if (sp.copyToPath.Length > 0)
                        {
                            sp.copyToPath = GetFullPathWithEndingSlashes(sp.copyToPath);
                        }

                        if (sp.srcPath.Length > 0)
                        {
                            sp.srcPath = GetFullPathWithEndingSlashes(sp.srcPath);
                        }

                        //Init LogStack
                        logger.LogInformation($"Init data pipeline from {sp.srcPath} for {sp.srcFile} to {sp.trgDbSchTbl}");

                        if (sp.cmdSchema.Length > 2)
                        {
                            using (var operation = logger.TrackOperation("Creating schema on target"))
                            {
                                logger.LogCodeBlock("Create Schema:", sp.cmdSchema);
                                CommonDB.ExecDDLScript(trgSqlCon, sp.cmdSchema, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                            }
                        }

                        if (sp.preProcessOnTrg.Length > 2)
                        {
                            using (var operation = logger.TrackOperation("PreProcess executed on target"))
                            {
                                var cmdOnSrc = new ExecNonQuery(trgSqlCon, sp.preProcessOnTrg, sp.bulkLoadTimeoutInSek);
                                cmdOnSrc.Exec();
                            }
                        }

                        string procSRC = "";
                        string FileFullPath = sp.srcPath + sp.srcFile;

                        if (sqlFlowParam.sourceIsAzCont)
                        {
                            DataLakeFileSystemClient dlFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                sp.dlTenantId,
                                sp.dlApplicationId,
                                sp.dlClientSecret,
                                sp.dlKeyVaultName,
                                sp.dlSecretName,
                                sp.dlStorageAccountName,
                                sp.dlBlobContainer);
                            DataLakeFileClient fileClient = dlFileSystemClient.GetFileClient(FileFullPath);
                            Response<FileDownloadInfo> downloadResponse = fileClient.Read();
                            using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
                            {
                                procSRC = reader.ReadToEnd();
                                sp.logLength = long.Parse(procSRC.Length.ToString());
                                sp.logFileDate = "0";
                            }
                        }
                        else
                        {
                            using (StreamReader reader = new StreamReader(FileFullPath))
                            {
                                procSRC = reader.ReadToEnd();
                                sp.logLength = long.Parse(procSRC.Length.ToString());
                                sp.logFileDate = "0";
                            }
                        }

                        bool targetExsits = CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);
                        if (targetExsits)
                        {
                            using (var operation = logger.TrackOperation("Log table indexes"))
                            {
                                Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                            }
                        }

                        Dictionary<string, string> columnMappings = new Dictionary<string, string>();
                        logger.LogInformation($"Current source file {sp.srcFile}");
                        sp.logFileName = FileFullPath;

                        sp.processedFileList = sp.processedFileList + "," + (sp.showPathWithFileName ? sp.srcFile : sp.srcPath);
                        logger.LogCodeBlock("Current source file:", FileFullPath);

                        List<ParameterObject> prefetchParams = new List<ParameterObject>();
                        //Fetch Parameters with PreFetch = True

                        logger.LogInformation("Init PreFetch Parameters");

                        foreach (DataRow dr in procParamTbl.Rows)
                        {
                            string ParamName = dr["ParamName"]?.ToString() ?? string.Empty;
                            string SelectExp = dr["SelectExp"]?.ToString() ?? string.Empty;
                            string SourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            string Defaultvalue = dr["Defaultvalue"]?.ToString() ?? string.Empty;
                            bool PreFetch = (dr["PreFetch"]?.ToString() ?? string.Empty).Equals("True");
                            string pConnectionString = dr["trgConnectionString"]?.ToString() ?? string.Empty;
                            string pSourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            string pTenantId = dr["trgTenantId"]?.ToString() ?? string.Empty;
                            string pSubscriptionId = dr["trgSubscriptionId"]?.ToString() ?? string.Empty;
                            string pApplicationId = dr["trgApplicationId"]?.ToString() ?? string.Empty;
                            string pClientSecret = dr["trgClientSecret"]?.ToString() ?? string.Empty;
                            string pKeyVaultName = dr["trgKeyVaultName"]?.ToString() ?? string.Empty;
                            string pSecretName = dr["trgSecretName"]?.ToString() ?? string.Empty;

                            if (pKeyVaultName.Length > 0)
                            {
                                //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(trgKeyVaultName);
                                AzureKeyVaultManager pKeyVaultManager = new AzureKeyVaultManager(
                                    pTenantId,
                                    pApplicationId,
                                    pClientSecret,
                                    pKeyVaultName);
                                pConnectionString = pKeyVaultManager.GetSecret(pSecretName);
                            }

                            

                            TypeInfo vInfo = TypeDeterminer.GetValueType(Defaultvalue, DateTimeFormats);

                            //Fetch Param Value
                            conStringParser = new ConStringParser(pConnectionString)
                            {
                                ConBuilderMsSql =
                                    {
                                        ApplicationName = "SQLFlow SP Param"
                                    }
                            };

                            string paramConString = conStringParser.ConBuilderMsSql.ConnectionString;
                            if (PreFetch)
                            {
                                //Fetch Param Value
                                logger.LogCodeBlock("SelectExp for PreFetch Parameter", SelectExp);

                                ParserTables parserTables = new ParserTables();
                                var ReferencedTables = parserTables.GetReferencedTables(SelectExp);
                                bool dupeSrcObjects = CommonDB.CheckIfObjectsExsist(paramConString, sp.generalTimeoutInSek, ReferencedTables);

                                object rValue = null;
                                DbType dbType = DbType.String;

                                if (dupeSrcObjects)
                                {
                                    rValue = CommonDB.ExecuteScalar(paramConString, SelectExp, SourceType, 360);
                                    dbType = CommonDB.GetSqlDbTypeFromObject(rValue);
                                }
                                else
                                {
                                    rValue = vInfo.ParsedValue;
                                    dbType = vInfo.DbType;
                                }

                                if (ParserParameters.DupeParameter(prefetchParams, ParamName) == false)
                                {
                                    SqlParameter p = new SqlParameter
                                    {
                                        ParameterName = ParamName,
                                        Value = rValue,
                                        DbType = dbType
                                    };
                                   
                                    logger.LogInformation($"Prefetched {ParamName} value {rValue}");

                                    ParameterObject pinfo = new ParameterObject();
                                    pinfo.Name = ParamName;
                                    pinfo.Value = rValue.ToString();
                                    pinfo.sqlParameter = p;
                                    pinfo.ParameterType = SourceType;
                                    prefetchParams.Add(pinfo);
                                }
                            }
                        }

                        foreach (DataRow dr in procParamTbl.Rows)
                        {
                            string ParamName = dr["ParamName"]?.ToString() ?? string.Empty;
                            string SelectExp = dr["SelectExp"]?.ToString() ?? string.Empty;
                            string SourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            string Defaultvalue = dr["Defaultvalue"]?.ToString() ?? string.Empty;
                            bool PreFetch = (dr["PreFetch"]?.ToString() ?? string.Empty).Equals("True");
                            string pConnectionString = dr["trgConnectionString"]?.ToString() ?? string.Empty;
                            string pSourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            string pTenantId = dr["trgTenantId"]?.ToString() ?? string.Empty;
                            string pSubscriptionId = dr["trgSubscriptionId"]?.ToString() ?? string.Empty;
                            string pApplicationId = dr["trgApplicationId"]?.ToString() ?? string.Empty;
                            string pClientSecret = dr["trgClientSecret"]?.ToString() ?? string.Empty;
                            string pKeyVaultName = dr["trgKeyVaultName"]?.ToString() ?? string.Empty;
                            string pSecretName = dr["trgSecretName"]?.ToString() ?? string.Empty;

                            TypeInfo vInfo = TypeDeterminer.GetValueType(Defaultvalue, DateTimeFormats);


                            if (pKeyVaultName.Length > 0)
                            {
                                //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(trgKeyVaultName);
                                AzureKeyVaultManager pKeyVaultManager = new AzureKeyVaultManager(
                                    pTenantId,
                                    pApplicationId,
                                    pClientSecret,
                                    pKeyVaultName);
                                pConnectionString = pKeyVaultManager.GetSecret(pSecretName);
                            }

                            logger.LogInformation($"{ParamName}");

                            //Fetch Param Value
                            conStringParser = new ConStringParser(pConnectionString)
                            {
                                ConBuilderMsSql =
                                    {
                                        ApplicationName = "SQLFlow SP Param"
                                    }
                            };

                            string paramConString = conStringParser.ConBuilderMsSql.ConnectionString;
                            logger.LogInformation("Init Parameters");

                            if (PreFetch == false)
                            {
                                List<ParameterObject> paramLookup = ParserParameters.GetParametersFromSql(SelectExp);
                                List<ParameterObject> cmdParam = new List<ParameterObject>();

                                //CheckForError if Param SQL contains a Prefetch Reference
                                foreach (ParameterObject p in paramLookup)
                                {
                                    foreach (ParameterObject p2 in prefetchParams)
                                    {
                                        if (p.Name == p2.Name)
                                        {
                                            cmdParam.Add(p2);
                                            logger.LogInformation($"Prefetched value used for {p2.Name}={p2.Value}");
                                        }
                                        else
                                        {
                                            p.sqlParameter.Value = vInfo.ParsedValue;
                                            p.sqlParameter.DbType = vInfo.DbType;
                                            cmdParam.Add(p);
                                            logger.LogInformation($"Parameter added with default value {p.Name}={p.DefaultValue}");
                                        }
                                    }
                                }

                                //Fetch Param Value
                                logger.LogCodeBlock("SelectExp for Parameter", SelectExp);

                                DataTable dynamicParamTbl;
                                using (var operation = logger.TrackOperation("Run dynamic parameter query"))
                                {
                                    dynamicParamTbl = CommonDB.RunQuery(paramConString, SelectExp, pSourceType, sp.bulkLoadTimeoutInSek, cmdParam);
                                }

                                List<ParameterObject> srcParams = ParserParameters.GetParametersFromSql(procSRC);

                                if (targetExsits)
                                {
                                    SQLObject sObj = CommonDB.SQLObjectFromDBSchobj(sp.trgDbSchTbl);
                                    sp.Indexes = Services.Schema.ObjectIndexes.GetObjectIndexes(sp.trgConString, sObj.ObjDatabase, sObj.ObjSchema, sObj.ObjName, true);

                                    using (var operation = logger.TrackOperation("Script and log target table indexes"))
                                    {
                                        using (SqlCommand cmd = new SqlCommand("[flw].[AddObjectIndexes]", sqlFlowCon))
                                        {
                                            cmd.CommandType = CommandType.StoredProcedure;
                                            cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = sqlFlowParam.flowId;
                                            cmd.Parameters.Add("@TrgIndexes", SqlDbType.VarChar).Value = sp.Indexes;
                                            cmd.ExecuteNonQuery();
                                        }
                                    }
                                }


                                if (dynamicParamTbl.Rows.Count == 0)
                                {
                                    logger.LogInformation("No Parameters values found for further processing.");

                                    if (targetExsits)
                                    {
                                        if (sp.viewCmd.Length > 10)
                                        {
                                            // Track and execute the DDL script for creating the view
                                            Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
                                        }
                                    }

                                    execTime.Stop();
                                    sp.logDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                    sp.logEndTime = DateTime.Now;

                                    Shared.WriteToSysLog(
                                        connection: sqlFlowCon,
                                        flowId: sqlFlowParam.flowId,
                                        flowType: sqlFlowParam.flowType,
                                        execMode: sqlFlowParam.execMode,
                                        startTime: sp.logStartTime,
                                        endTime: sp.logEndTime,
                                        durationFlow: sp.logDurationFlow,
                                        runtimeCmd: sp.viewCmd,
                                        errorRuntime: "",
                                        debug: sqlFlowParam.dbg,
                                        noOfThreads: sp.noOfThreads,
                                        batch: sp.flowBatch,
                                        sysAlias: sp.sysAlias,
                                        batchId: sqlFlowParam.batchId,
                                        traceLog: logOutput.ToString(),
                                        inferDatatypeCmd: sp.InferDatatypeCmd.ToString()
                                    );


                                }
                                else
                                {
                                    //CheckForError if target table exsits:
                                    if (sp.targetExsits && sqlFlowParam.TruncateIsSetOnNextFlow == false)
                                    {
                                        Shared.CheckAndTruncateTargetTable(sqlFlowParam, logger, sqlFlowCon, sp, trgSqlCon, new DataTable());
                                    }

                                    int paramLoopCounter = 0;

                                    if (DataTableSchema.DoesColumnExistCaseInsensitive(dynamicParamTbl, "ParamName") && DataTableSchema.DoesColumnExistCaseInsensitive(dynamicParamTbl, "ParamValue"))
                                    {
                                        foreach (DataRow pRow in dynamicParamTbl.Rows)
                                        {
                                            object pValue = pRow["ParamValue"];
                                            string paramName = pRow["ParamName"]?.ToString() ?? string.Empty;
                                            SqlParameter p = new SqlParameter
                                            {
                                                ParameterName = paramName,
                                                Value = pValue.ToString(),
                                                DbType = CommonDB.GetSqlDbTypeFromObject(pValue)
                                            };

                                            ParameterObject pinfo = new ParameterObject();
                                            pinfo.Name = paramName;
                                            pinfo.Value = pValue.ToString();
                                            pinfo.sqlParameter = p;
                                            pinfo.ParameterType = pSourceType;
                                            prefetchParams.Add(pinfo);

                                            List<ParameterObject> srcCmdParams = new List<ParameterObject>();

                                            //Dupe the parameters for srcCode
                                            foreach (ParameterObject srcP in srcParams)
                                            {
                                                if (srcP.Name.Equals(paramName, StringComparison.InvariantCultureIgnoreCase))
                                                {
                                                    srcCmdParams.Add(pinfo);
                                                }
                                            }

                                            //Get srcCode Schema
                                            DataTable srcSchemaTbl = SQLReaderSchema.GetSchemaFromSQLReader(sp.srcConString, procSRC, srcCmdParams);
                                            logger.LogInformation($"ColumnCount (Proc: {srcSchemaTbl.Rows.Count}, Expected {sp.expectedColumnCount})");

                                            if (srcSchemaTbl.Rows.Count != sp.expectedColumnCount && sp.expectedColumnCount > 0)
                                            {
                                                string eMsg =
                                                    $"Error: Expected Column Count miss match (File: {srcSchemaTbl.Rows.Count.ToString()}, Expected {sp.expectedColumnCount.ToString()}) {sp.srcFile} {Environment.NewLine} ";
                                                sp.colCountErrorMsg += eMsg;
                                                sp.colCountError = true;
                                            }
                                            else
                                            {
                                                if (srcSchemaTbl.Rows.Count > 0)
                                                {
                                                    if (paramLoopCounter == 0)
                                                    {
                                                        SQLObject sObj = CommonDB.SQLObjectFromDBSchobj(sp.trgDbSchTbl);

                                                        using (var operation = logger.TrackOperation("Delete target table indexes"))
                                                        {
                                                            Services.Schema.ObjectIndexes.DropObjectIndexes(sp.trgConString, sObj.ObjDatabase, sObj.ObjSchema, sObj.ObjName);
                                                        }
                                                    }

                                                    string columnMappingList = "";
                                                    string[] fileColNames = srcSchemaTbl.AsEnumerable().Select(row => row.Field<string>("ColumnName")).ToArray();

                                                    List<string> list = fileColNames.ToList();

                                                    logger.LogCodeBlock("File Column List:", string.Join(",", list));
                                                    Dictionary<string, string> colDic = new Dictionary<string, string>();

                                                    foreach (DataRow row in srcSchemaTbl.Rows)
                                                    {
                                                        var columnName = row["ColumnName"]?.ToString() ?? string.Empty;
                                                        columnName = Regex.Replace(columnName, sp.colCleanupSqlRegExp, "_");

                                                        if (colDic.ContainsKeyIgnoreCase(columnName))
                                                        {
                                                            columnName = columnName + int.Parse(row["ColumnOrdinal"]?.ToString() ?? string.Empty);
                                                            colDic.Add(columnName, columnName);
                                                            row["ColumnNameCleaned"] = columnName;
                                                            row["ColumnCMD"] = columnName + " AS " + row["SqlDataType"] + " NULL";
                                                        }
                                                        else
                                                        {
                                                            colDic.Add(columnName, columnName);
                                                            row["ColumnNameCleaned"] = columnName;
                                                            row["ColumnCMD"] = columnName + " AS " + row["SqlDataType"] + " NULL";
                                                        }

                                                        srcSchemaTbl.AcceptChanges();
                                                    }

                                                    string cmdColumns = "";
                                                    string columnList = "";
                                                    string columnDataTypeList = "";
                                                    string columnExpList = "";

                                                    cmdColumns = string.Join(",", srcSchemaTbl.AsEnumerable().Select(r => r.Field<string>("ColumnCMD")));
                                                    columnList = string.Join(",", srcSchemaTbl.AsEnumerable().Select(r => "[" + r.Field<string>("ColumnNameCleaned") + "]"));
                                                    columnDataTypeList = string.Join(";", srcSchemaTbl.AsEnumerable().Select(r => r.Field<string>("SqlDataType")));
                                                    columnExpList = string.Join(";", srcSchemaTbl.AsEnumerable().Select(r => r.Field<string>("SQLFlowExp")));

                                                    logger.LogCodeBlock("cmdColumns:", cmdColumns);
                                                    logger.LogCodeBlock("columnList:", columnList);
                                                    logger.LogCodeBlock("columnDataTypeList", columnDataTypeList);
                                                    logger.LogCodeBlock("columnExpList:", columnExpList);

                                                    string cmdCreateTransformations =
                                                        $"exec flw.AddPreIngTransfrom @FlowID={sqlFlowParam.flowId.ToString()}, @FlowType='{sqlFlowParam.flowType}', @ColList='{columnList}', @DataTypeList='{columnDataTypeList}', @ExpList='{columnExpList}'";
                                                    logger.LogCodeBlock("Add Column Transformations:", cmdCreateTransformations);

                                                    var cmdAlter = new GetData(sqlFlowCon, cmdCreateTransformations, 360);
                                                    DataTable cmdTbl = cmdAlter.Fetch();

                                                    sp.cmdAlterSQL = cmdTbl.Rows[0]["alterCmd"]?.ToString() ?? string.Empty;
                                                    sp.cmdCreate = cmdTbl.Rows[0]["cmdCreate"]?.ToString() ?? string.Empty;
                                                    sp.tfColList = cmdTbl.Rows[0]["tfColList"]?.ToString() ?? string.Empty;
                                                    sp.currentViewCMD = cmdTbl.Rows[0]["viewCMD"]?.ToString() ?? string.Empty;
                                                    sp.currentViewSelect = cmdTbl.Rows[0]["viewSelect"]?.ToString() ?? string.Empty;
                                                    new Dictionary<string, string>();

                                                    logger.LogCodeBlock("alterCmd", sp.cmdAlterSQL);
                                                    logger.LogCodeBlock("cmdCreate", sp.cmdCreate);
                                                    logger.LogCodeBlock("tfColList", sp.tfColList);

                                                    var tfColumnDic = sp.tfColList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                        .Select((str, idx) => new { str, idx })
                                                        .ToDictionary(x => x.str, x => x.idx);

                                                    string trgTblCmd = "";

                                                    if (sp.syncSchema)
                                                    {
                                                        if (paramLoopCounter == 0)
                                                        {
                                                            trgTblCmd = sp.cmdCreate;
                                                            targetExsits = true;
                                                        }
                                                        else
                                                        {
                                                            trgTblCmd = sp.cmdAlterSQL;
                                                        }
                                                    }

                                                    logger.LogCodeBlock("Target table prepare command:", trgTblCmd);

                                                    if (trgTblCmd.Length > 0)
                                                    {
                                                        using (var operation = logger.TrackOperation("Prepare command execution on target table"))
                                                        {
                                                            CommonDB.ExecDDLScript(trgSqlCon, trgTblCmd, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                                        }
                                                        targetExsits = true;
                                                    }

                                                    if (sp.currentViewCMD.Length > 10 || sp.viewCmd.Length > 10)
                                                    {
                                                        string v = sp.currentViewCMD.Length > 10 ? sp.currentViewCMD : sp.viewCmd;
                                                        sp.execViewCmd = "DECLARE @val nvarchar(max) " +
                                                                         " set @val = '" + v.Replace("'", "''") + "'" +
                                                                         " exec [" + sp.trgDatabase + "].sys.sp_executesql @val";

                                                    }

                                                    sp.logSelectCmd = sp.currentViewSelect.Length > 0 ? sp.currentViewSelect : sp.viewSelect;
                                                    sp.logCreateCmd = trgTblCmd;

                                                    logger.LogCodeBlock("View CMD:", sp.viewCmd);
                                                    logger.LogCodeBlock("Create View CMD:", sp.execViewCmd);

                                                    CommonDB.ExecDDLScript(trgSqlCon, sp.execViewCmd, sp.generalTimeoutInSek, sp.trgIsSynapse);

                                                    //Create mappings for sqlbulk
                                                    foreach (DataRow row in srcSchemaTbl.Rows)
                                                    {
                                                        var columnName = "[" + row["ColumnName"] + "]";
                                                        var ColumnNameCleaned = "[" + row["ColumnNameCleaned"] + "]";

                                                        if (columnMappings.ContainsKeyIgnoreCase(columnName) == false && tfColumnDic.ContainsKeyIgnoreCase(columnName) == true)
                                                        {
                                                            columnMappings.Add(columnName, ColumnNameCleaned);
                                                            //columnMappingList = columnList + $",{columnName}";
                                                        }
                                                    }

                                                    columnMappingList = string.Join(", ", columnMappings.Select(kvp => $"{kvp.Value}"));
                                                    logger.LogCodeBlock("Column Mapping List:", columnMappingList);


                                                    var currentRowCount = 0;

                                                    string paramValues = "";
                                                    using (var operation = logger.TrackOperation("Ingest dataset"))
                                                    {
                                                        using (SqlCommand cmd = new SqlCommand())
                                                        {
                                                            cmd.Connection = srcSqlCon;
                                                            cmd.CommandText = procSRC;
                                                            cmd.CommandTimeout = sp.bulkLoadTimeoutInSek;
                                                            sp.logCreateCmd = procSRC;

                                                            foreach (var pa in srcCmdParams)
                                                            {
                                                                cmd.Parameters.Add(pa.sqlParameter);
                                                                paramValues += $"{pa.sqlParameter.ParameterName}={pa.sqlParameter.Value},";
                                                            }

                                                            double Status = paramLoopCounter / (double)procParamTbl.Rows.Count;
                                                            string description = $" {sp.logFileName} executed with {paramValues} ({paramLoopCounter}/{procParamTbl.Rows.Count})";

                                                            EventArgsPrc arg = new EventArgsPrc
                                                            {
                                                                Description = description,
                                                                ParamValues = paramValues,
                                                                Batch = sqlFlowParam.batchId,
                                                                FlowID = sqlFlowParam.flowId,
                                                                FlowType = sqlFlowParam.flowType,
                                                                SysAlias = sp.sysAlias,
                                                                OnErrorResume = sp.onErrorResume,
                                                                InTotal = procParamTbl.Rows.Count,
                                                                InQueue = procParamTbl.Rows.Count - paramLoopCounter,
                                                                Processed = paramLoopCounter,
                                                                Status = Status
                                                            };
                                                            OnPrcExecuted?.Invoke(Thread.CurrentThread, arg);

                                                            using (SqlDataReader reader = cmd.ExecuteReader())
                                                            {
                                                                var bulk = new StreamToSql(
                                                                    sp.trgConString,
                                                                    $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]",
                                                                    $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]",
                                                                    columnMappings,
                                                                    sp.bulkLoadTimeoutInSek,
                                                                    0,
                                                                    logger,
                                                                    sp.maxRetry,
                                                                    sp.retryDelayMs,
                                                                    retryErrorCodes,
                                                                    sqlFlowParam.dbg,
                                                                    ref currentRowCount);
                                                                bulk.StreamWithRetries(reader);
                                                            }
                                                        }
                                                    }


                                                }
                                            }

                                            paramLoopCounter = paramLoopCounter + 1;
                                        }

                                        Shared.FetchTargetTableRowCount(logger, sp, trgSqlCon);

                                    }

                                    if (sp.Indexes.Length > 5)
                                    {
                                        using (var operation = logger.TrackOperation("Target Table indexes re-created"))
                                        {
                                            CommonDB.ExecDDLScript(trgSqlCon, sp.Indexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                        }

                                    }
                                    else if (sp.trgIndexes.Length > 5)
                                    {
                                        using (var operation = logger.TrackOperation("Target Table indexes from SysLog re-created"))
                                        {
                                            CommonDB.ExecDDLScript(trgSqlCon, sp.trgIndexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                        }
                                    }

                                    if (sp.currentViewCMD.Length > 10 || sp.viewCmd.Length > 10)
                                    {
                                        Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
                                    }


                                    //Connection string to the xls file
                                    if (sp.postProcessOnTrg.Length > 2)
                                    {
                                        using (var operation = logger.TrackOperation("Post-process executed on target"))
                                        {
                                            var cmdOnTrg = new ExecNonQuery(trgSqlCon, sp.postProcessOnTrg, sp.bulkLoadTimeoutInSek);
                                            cmdOnTrg.Exec();
                                        }
                                    }

                                    if (sp.colCountError)
                                    {
                                        throw new InvalidOperationException(sp.colCountErrorMsg);
                                    }


                                    execTime.Stop();
                                    sp.logDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                    sp.logEndTime = DateTime.Now;
                                    sp.logRuntimeCmd = logOutput.ToString();

                                }
                            }
                        }

                        if (sp.fetchDataTypes)
                        {
                            if (sp.preIngTransStatus)
                            {
                                Shared.FetchDataTypes(sqlFlowParam, logger, sp, sqlFlowCon, trgSqlCon);
                            }
                        }

                        #region trgDesiredIndex
                        if (sp.trgDesiredIndex.Length > 0)
                        {
                            using (var operation = logger.TrackOperation("Desired index(s) synchronized"))
                            {
                                IndexManagement im = new IndexManagement();
                                string indexLog = im.EnsureIndexes(sp.trgConString, sp.trgDesiredIndex);
                                if (!string.IsNullOrEmpty(indexLog))
                                {
                                    logger.LogInformation(indexLog);
                                }
                            }
                        }
                        #endregion trgDesiredIndex

                        execTime.Stop();
                        sp.logDurationFlow = execTime.ElapsedMilliseconds / 1000;
                        sp.logEndTime = DateTime.Now;
                        logger.LogInformation($"Total processing time ({sp.logDurationFlow} sec)");

                        Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                        if (trgSqlCon.State == ConnectionState.Open)
                        {
                            trgSqlCon.Close();
                            trgSqlCon.Dispose();
                        }

                        if (srcSqlCon.State == ConnectionState.Open)
                        {
                            srcSqlCon.Close();
                            srcSqlCon.Dispose();
                        }

                        if (sqlFlowCon.State == ConnectionState.Open)
                        {
                            sqlFlowCon.Close();
                            sqlFlowCon.Dispose();
                        }

                    }

                    return sp.result;
                }
                catch (Exception e)
                {
                    Shared.LogFileException(sqlFlowParam, sp, logger, e, sqlFlowCon, logOutput);
                    if (trgSqlCon.State == ConnectionState.Open)
                    {
                        trgSqlCon.Close();
                        trgSqlCon.Dispose();
                    }

                    if (srcSqlCon.State == ConnectionState.Open)
                    {
                        srcSqlCon.Close();
                        srcSqlCon.Dispose();
                    }
                    
                    if (sqlFlowCon.State == ConnectionState.Open)
                    {
                        sqlFlowCon.Close();
                    }
                }
                finally
                {
                    srcSqlCon.Close();
                    srcSqlCon.Dispose();

                    trgSqlCon.Close();
                    trgSqlCon.Dispose();

                    sqlFlowCon.Close();
                    
                }
            }

            return sp.result;
        }
        #endregion ProcessPrc

        /// <summary>
        /// Ensures the provided path string ends with a slash.
        /// </summary>
        /// <param name="input">The path string to process.</param>
        /// <returns>The input string with a trailing slash, if it was not present.</returns>
        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }

    }
}
