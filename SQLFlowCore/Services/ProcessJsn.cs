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
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.IO;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using SQLFlowCore.Logger;
using Octokit;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides a set of static methods and events to process JSON data in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class cannot be inherited.
    /// </remarks>
    internal static class ProcessJsn
    {
        /// <summary>
        /// Occurs when rows are copied in the process.
        /// </summary>
        /// <remarks>
        /// This event is triggered when the rows are successfully copied during the process. 
        /// The event handler receives an argument of type <see cref="EventArgsRowsCopied"/> 
        /// containing data related to this event.
        /// </remarks>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        //TODO: Improve Limited Parsing
        #region ProcessJsn
        /// <summary>
        /// Executes a process based on the provided parameters.
        /// </summary>
        /// <param name="sqlFlowConString">The SQL Flow connection string.</param>
        /// <param name="flowId">The ID of the flow.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The ID of the batch.</param>
        /// <param name="dbg">Debug mode indicator.</param>
        /// <param name="sqlFlowParam">The SQL Flow item.</param>
        /// <returns>A string indicating the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            PushToSql.OnRowsCopied += OnRowsCopied;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionJsn", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

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
                    
                    DataTable DateTimeFormats = new DataTable();  
                    DataSet ds = new DataSet();
                    DataTable paramTbl = new DataTable();
                    DataTable incrTbl = new DataTable();
                    DataTable procParamTbl = new DataTable();
                    
                    using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
                    {
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowJSN]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                    }

                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        if (sp.trgSecretName.Length > 0)
                        {
                            //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(trgKeyVaultName);
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                sp.trgTenantId,
                                sp.trgApplicationId,
                                sp.trgClientSecret,
                                sp.trgKeyVaultName);
                            sp.trgConString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                        }

                        conStringParser = new ConStringParser(sp.trgConString) {ConBuilderMsSql ={ApplicationName = "SQLFlow Target"}};
                        sp.trgConString = conStringParser.ConBuilderMsSql.ConnectionString;


                        trgSqlCon = new SqlConnection(sp.trgConString);
                        trgSqlCon.Open();

                        string cmdFetchDataTypes = InferDataTypes.GetDataTypeSQL(sqlFlowParam.flowId, sqlFlowParam.flowType, sp.trgSchema, sp.trgObject);
                        sp.InferDatatypeCmd = cmdFetchDataTypes;

                        DataTable tblPreFiles = new DataTable();
                        
                        //SMOScriptingOptions Pre Files. Avoid re-process of data
                        if (sp.altTrgIsEmbedded == false)
                        {
                            tblPreFiles = Shared.GetFilesFromTable(logger, sp, trgSqlCon);
                        }

                        //Fetch file date from related tables
                        if (sp.fileDate > 0 && sp.altTrgIsEmbedded == false)
                        {
                            string fileDateFromNextFlow = Shared.FileDateFromNextFlow(logger, incrTbl, sp);
                        }

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


                        if (sp.cmdSchema.Length > 10)
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
                                logger.LogCodeBlock("PreProcess SQL on target", sp.preProcessOnTrg);
                                cmdOnSrc.Exec();
                            }
                        }

                        var adapter = new PathItemAdapter();
                        var adapter2 = new FileSystemInfoAdapter();
                        IEnumerable<GenericFileItem> genericFileList = new List<GenericFileItem>();

                        
                        if (sp.searchSubDirectories)
                        {
                            sp.srchOption = SearchOption.AllDirectories;
                        }

                        
                        if (sqlFlowParam.sourceIsAzCont)
                        {
                            DataLakeFileSystemClient srcFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                sp.srcTenantId,
                                sp.srcApplicationId,
                                sp.srcClientSecret,
                                sp.srcKeyVaultName,
                                sp.srcSecretName,
                                sp.srcStorageAccountName,
                                sp.srcBlobContainer);

                            using (var operation = logger.TrackOperation("Enumerate data lake files"))
                            {
                                if (!string.IsNullOrEmpty(sqlFlowParam.srcFileWithPath))
                                {
                                    genericFileList = Shared.EnumerateSingelLakeFile(sqlFlowParam, srcFileSystemClient);
                                }
                                else
                                {
                                    genericFileList = Shared.EnumerateMultipleLakeFiles(sp, srcFileSystemClient, logger, adapter);
                                }

                                logger.LogInformation($"Enumerated {genericFileList.Count()} data lake files");
                                logger.LogCodeBlock("CopyToPath:", sp.copyToPath);
                            }
                        }
                        else
                        {
                            using (var operation = logger.TrackOperation("Enumerate local files"))
                            {
                                if (!string.IsNullOrEmpty(sqlFlowParam.srcFileWithPath))
                                {
                                    genericFileList = Shared.EnumerateLocalFile(sqlFlowParam, genericFileList);
                                }
                                else
                                {
                                    genericFileList = Shared.EnumerateMultipleLocalFiles(sp, logger, adapter2);
                                }

                                logger.LogInformation($"Enumerated {genericFileList.Count()} local files");
                                logger.LogCodeBlock("CopyToPath:", sp.copyToPath);
                            }
                        }

                        bool targetExsits = CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);
                        if (targetExsits && sp.altTrgIsEmbedded == false)
                        {
                            Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                        }

                        if (!genericFileList.Any())
                        {
                            Shared.HandelNoFilesFound(sqlFlowParam, sp, logger, trgSqlCon, execTime, sqlFlowCon, logOutput);
                        }
                        else
                        {
                            if (targetExsits && sqlFlowParam.TruncateIsSetOnNextFlow == false)
                            {
                                Shared.CheckAndTruncateTargetTable(sqlFlowParam, logger, sqlFlowCon, sp, trgSqlCon, tblPreFiles);
                            }

                            DataLakeFileSystemClient srcFileSystemClient = null;
                            if (sqlFlowParam.sourceIsAzCont)
                            {
                                srcFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                    sp.srcTenantId,
                                    sp.srcApplicationId,
                                    sp.srcClientSecret,
                                    sp.srcKeyVaultName,
                                    sp.srcSecretName,
                                    sp.srcStorageAccountName,
                                    sp.srcBlobContainer);
                            }

                            using (logger.TrackOperation("Process enumerated files"))
                            {
                                string content = "";
                                foreach (GenericFileItem cFile in genericFileList)
                                {
                                    Dictionary<string, string> columnMappings = new Dictionary<string, string>();
                                    logger.LogInformation($"📄 Current source file {cFile.FullPath}, create/modified date {cFile.LastModified}");

                                    sp.processedFileList = sp.processedFileList + "," + (sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name));

                                    sp.logLength = long.Parse(cFile.ContentLength.ToString());
                                    sp.logFileDate = sp.logFileDate = cFile.CreationTime > cFile.LastWriteTime ? cFile.CreationTime.ToString("yyyyMMddHHmmss") : cFile.LastWriteTime.ToString("yyyyMMddHHmmss");

                                    using (var operation = logger.TrackOperation("Fetch json content"))
                                    {
                                        if (sqlFlowParam.sourceIsAzCont)
                                        {
                                            DataLakeFileClient fileClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                                            Response<FileDownloadInfo> downloadResponse = fileClient.Read();
                                            using (StreamReader sReader = new StreamReader(downloadResponse.Value.Content))
                                            {
                                                content = sReader.ReadToEnd();
                                            }
                                        }
                                        else // Read from local disk
                                        {
                                            using (StreamReader reader = new StreamReader(cFile.FullPath))
                                            {
                                                content = reader.ReadToEnd();
                                            }
                                        }
                                    }

                                    DataTable dataTable;
                                    using (var operation = logger.TrackOperation("Parse json"))
                                    {
                                        dataTable = DynamicDataConverter.ToDataTableDynamically(logger,sp.JsonToDataTableCode, content, Path.GetFileName(cFile.Name), trgSqlCon);
                                    }

                                    if (dataTable.Columns.Count != sp.expectedColumnCount && sp.expectedColumnCount > 0)
                                    {
                                        string eMsg =
                                            $"Error: Expected Column Count miss match (File: {dataTable.Columns.Count.ToString()}, Expected {sp.expectedColumnCount.ToString()}) {cFile.Name} {Environment.NewLine} ";
                                        sp.colCountErrorMsg += eMsg;
                                        sp.colCountError = true;
                                    }
                                    else
                                    {
                                        logger.LogInformation($"ColumnCount (File: {dataTable.Columns.Count}, Expected: {sp.expectedColumnCount})");

                                        string FileDate_DW = cFile.LastModified.ToString("yyyyMMddHHmmss");
                                        string DataSet_DW = FileDate_DW;
                                        string FileName_DW = cFile.Name;
                                        DateTime FileRowDate_DW = DateTime.Now;
                                        long FileSize_DW = long.Parse(cFile.ContentLength.ToString());

                                        DateTime DateTimeFromFileName =
                                            Functions.ExtractDateTimeFromString(FileName_DW, DateTimeFormats);

                                        //Fix for files that have same modified / create date
                                        if (DateTimeFromFileName < cFile.LastModified &&
                                            DateTimeFromFileName != FlowDates.Default)
                                        {
                                            DataSet_DW = DateTimeFromFileName.ToString("yyyyMMddHHmmss");
                                        }

                                        string fName = sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name);

                                        DataRow[] dupeFile = new DataRow[0];
                                        string dupeSQL = $"FileName_DW = '{fName}' AND FileSize_DW = '{FileSize_DW}'";

                                        if (tblPreFiles.Columns.Contains("FileName_DW") && tblPreFiles.Columns.Contains("FileSize_DW"))
                                        {
                                            dupeFile = tblPreFiles.Select(dupeSQL);
                                        }

                                        if (dupeFile.Length > 0 && sp.altTrgIsEmbedded == false)
                                        {
                                            logger.LogInformation($"📄 Current Source File is already in pre table {FileName_DW}");
                                            if (sp.viewCmd.Length > 10)
                                            {
                                                // Track and execute the DDL script for creating the view
                                                Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
                                            }
                                        }
                                        else
                                        {
                                            Shared.WriteToSysFileLog(
                                                connection: sqlFlowCon,
                                                batchId: sqlFlowParam.batchId,
                                                flowId: sqlFlowParam.flowId,
                                                fileDate_DW: FileDate_DW,
                                                dataSet_DW: DataSet_DW,
                                                fileName_DW: cFile.FullPath,
                                                fileRowDate_DW: FileRowDate_DW,
                                                fileSize_DW: FileSize_DW,
                                                fileColumnCount: dataTable.Columns.Count,
                                                expectedColumnCount: sp.expectedColumnCount
                                            );
                                            
                                            logger.LogInformation($"ColumnCount (File: {dataTable.Columns.Count}, Expected: {sp.expectedColumnCount})");
                                            
                                            if (dataTable.Columns.Count > 0)
                                            {
                                                Shared.BuildSchemaAndTransformations(sqlFlowParam, FileDate_DW,
                                                    dataTable, sp,
                                                    cFile, FileRowDate_DW, FileSize_DW, DataSet_DW, logger,
                                                    sqlFlowCon, trgSqlCon, columnMappings,
                                                    retryErrorCodes, srcFileSystemClient);
                                            }
                                            else
                                            {
                                                logger.LogInformation("Current file is invalid");
                                            }

                                            sp.logFetched = sp.logFetched + dataTable.Rows.Count;
                                        }
                                    }
                                }
                            }

                            if (targetExsits)
                            {
                                Shared.FetchTargetTableRowCount(logger, sp, trgSqlCon);

                                if (sp.Indexes.Length > 5 && sp.altTrgIsEmbedded == false)
                                {
                                    using (var operation = logger.TrackOperation("Target Table indexes re-created"))
                                    {
                                        CommonDB.ExecDDLScript(trgSqlCon, sp.Indexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                    }
                                }
                                else if (sp.trgIndexes.Length > 5 && sp.altTrgIsEmbedded == false)
                                {
                                    using (var operation = logger.TrackOperation("Target Table indexes from SysLog re-created"))
                                    {
                                        CommonDB.ExecDDLScript(trgSqlCon, sp.trgIndexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                    }
                                }
                            }

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

                        if (sp.fetchDataTypes && sp.altTrgIsEmbedded == false)
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

                        logger.LogInformation($"Total processing time ({sp.logDurationFlow} sec)");

                        Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                        if (trgSqlCon.State == ConnectionState.Open)
                        {
                            trgSqlCon.Close();
                            trgSqlCon.Dispose();
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

                    if (sqlFlowCon.State == ConnectionState.Open)
                    {
                        sqlFlowCon.Close();
                        sqlFlowCon.Dispose();
                    }
                }
                finally
                {
                    SqlConnection.ClearPool(trgSqlCon);

                    trgSqlCon.Close();
                    trgSqlCon.Dispose();

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }

            }

            return sp.result;
        }
        #endregion ProcessJsn

        /// <summary>
        /// Ensures the provided path string ends with a slash.
        /// </summary>
        /// <param name="input">The path string to be processed.</param>
        /// <returns>The processed path string with a trailing slash.</returns>
        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }


    }
}
