using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using GenericParsing;
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
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Pipeline;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.IO;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Logger;
using Octokit;
using LoggerExtensions = SQLFlowCore.Logger.LoggerExtensions;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides a set of static methods and events for processing CSV data in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class is primarily used in conjunction with the <see cref="ExecFlowBatch"/> class.
    /// </remarks>
    internal static class ProcessCsv
    {
        /// <summary>
        /// Occurs when rows are copied in the CSV processing.
        /// </summary>
        /// <remarks>
        /// This event is triggered in multiple places within the SQLFlowCore.Engine, 
        /// such as during the execution of a batch flow, ingestion process, and other data processing tasks.
        /// </remarks>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        #region ProcessCsv
        /// <summary>
        /// Executes a SQLFlow process for a given CSV file.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="flowId">The unique identifier of the flow.</param>
        /// <param name="execMode">The execution mode for the process.</param>
        /// <param name="batchId">The unique identifier of the batch.</param>
        /// <param name="dbg">The debug mode flag (1 for debug mode, 0 for normal mode).</param>
        /// <param name="sqlFlowParam">The SQLFlow item to be processed.</param>
        /// <returns>A string indicating the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            PushToSql.OnRowsCopied += OnRowsCopied;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) {ConBuilderMsSql ={ApplicationName = "SQLFlow App"}};
            
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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowCSV]",  sqlFlowParam,  logger, sqlFlowCon, out  paramTbl, out  incrTbl, out  DateTimeFormats, out procParamTbl);
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

                        string cmdFetchDataTypes = InferDataTypes.GetDataTypeSQL(sqlFlowParam.flowId, sqlFlowParam.flowType, sp.trgSchema, sp.trgObject);
                        sp.InferDatatypeCmd = cmdFetchDataTypes;

                        var tblPreFiles = Shared.GetFilesFromTable(logger, sp, trgSqlCon);

                        //Fetch file date from related tables
                        if (sp.fileDate > 0)
                        {
                            Shared.FileDateFromNextFlow(logger, incrTbl, sp);
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

                        //Set correct file encoding
                        sp.Encoding = Shared.GetFileEncoding(sp); 
                        
                        //Init LogStack
                        logger.LogInformation($"Init data pipeline from {sp.srcPath} for {sp.srcFile} to {sp.trgDbSchTbl}");

                        if (sp.cmdSchema.Length > 2)
                        {
                            using (var operation = logger.TrackOperation("Create schema on target"))
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

                        bool targetExsits = CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);
                        if (targetExsits)
                        {
                            using (var operation = logger.TrackOperation("Log table indexes"))
                            {
                                Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                            }
                        }
                        
                    
                        if (sp.srcParserCsv.Length > 0)
                        {
                            using (Stream cfgStream = GenerateStreamFromString(sp.srcParserCsv))
                            {
                                using (GenericParserAdapter parser = new GenericParserAdapter())
                                {
                                    parser.Load(cfgStream); //+ @"\"

                                    var adapter = new PathItemAdapter();
                                    var adapter2 = new FileSystemInfoAdapter();
                                    IEnumerable<GenericFileItem> genericFileList = new List<GenericFileItem>();

                                    if (sp.searchSubDirectories)
                                    {
                                        sp.srchOption = SearchOption.AllDirectories;
                                    }

                                    if (sqlFlowParam.sourceIsAzCont)
                                    {
                                        #region EnumerateDataLakeGen2
                                        using (var operation = logger.TrackOperation("Enumerate data lake files"))
                                        {
                                            DataLakeFileSystemClient srcFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                                sp.srcTenantId,
                                                sp.srcApplicationId,
                                                sp.srcClientSecret,
                                                sp.srcKeyVaultName,
                                                sp.srcSecretName,
                                                sp.srcStorageAccountName,
                                                sp.srcBlobContainer);

                                            if (!string.IsNullOrEmpty(sqlFlowParam.srcFileWithPath))
                                            {
                                                genericFileList = Shared.EnumerateSingelLakeFile(sqlFlowParam,srcFileSystemClient);
                                            }
                                            else
                                            {
                                                genericFileList = Shared.EnumerateMultipleLakeFiles(sp, srcFileSystemClient,logger, adapter);
                                            }
                                            logger.LogInformation($"Enumerated {genericFileList.Count()} data lake files");
                                            logger.LogCodeBlock("CopyToPath:", sp.copyToPath);
                                        }
                                        #endregion
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

                                    sp.targetExsits = CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);
                                    if (sp.targetExsits)
                                    {
                                        Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                                    }

                                    if (!genericFileList.Any())
                                    {
                                        Shared.HandelNoFilesFound(sqlFlowParam, sp, logger, trgSqlCon, execTime, sqlFlowCon, logOutput);
                                    }
                                    else
                                    {
                                        //CheckForError if target table exists:
                                        if (sp.targetExsits && sqlFlowParam.TruncateIsSetOnNextFlow == false)
                                        {
                                            Shared.CheckAndTruncateTargetTable(sqlFlowParam, logger, sqlFlowCon, sp, trgSqlCon, tblPreFiles);
                                        }

                                        parser.ExpectedColumnCount = 0;
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
                                            foreach (GenericFileItem cFile in genericFileList)
                                            {
                                                Dictionary<string, string> columnMappings = new Dictionary<string, string>();
                                                sp.cmdAlterSQL = "";
                                                sp.cmdCreate = "";
                                                sp.tfColList = "";
                                                sp.currentViewCMD = "";
                                                sp.currentViewSelect = "";

                                                logger.LogInformation($"📄 Current Source File {cFile.Name} with last create/modified date {cFile.LastModified}");

                                                sp.logLength = long.Parse(cFile.ContentLength.ToString());

                                                sp.createDateTime = cFile.LastModified;
                                                sp.modifiedDateTime = cFile.LastModified;

                                                sp.processedFileList = sp.processedFileList + "," + (sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name));

                                                DataTable dataTable = new DataTable();

                                                if (sqlFlowParam.sourceIsAzCont)
                                                {
                                                    DataLakeFileClient fileClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                                                    Response<FileDownloadInfo> downloadResponse = fileClient.Read();
                                                    StreamReader reader = new StreamReader(downloadResponse.Value.Content, sp.Encoding);
                                                    parser.SetDataSource(reader);
                                                    dataTable = parser.GetDataTable();
                                                }
                                                else // Read from local disk
                                                {
                                                    StreamReader reader = new StreamReader(cFile.FullPath, sp.Encoding);
                                                    parser.SetDataSource(reader);
                                                    dataTable = parser.GetDataTable();
                                                }

                                                string FileDate_DW = cFile.LastModified.ToString("yyyyMMddHHmmss");
                                                string DataSet_DW = FileDate_DW;
                                                string FileName_DW = cFile.Name;

                                                DateTime DateTimeFromFileName = Functions.ExtractDateTimeFromString(FileName_DW, DateTimeFormats);

                                                //Fix for files that have same modified / create date
                                                if (DateTimeFromFileName < cFile.LastModified &&
                                                    DateTimeFromFileName != FlowDates.Default)
                                                {
                                                    DataSet_DW = DateTimeFromFileName.ToString("yyyyMMddHHmmss");
                                                }

                                                DateTime FileRowDate_DW = DateTime.Now;
                                                long FileSize_DW = long.Parse(cFile.ContentLength.ToString());

                                                string fName = sp.showPathWithFileName ? cFile.FullPath : Path.GetFileName(cFile.Name);
                                                string dupeSQL = $"FileName_DW = '{fName}' AND FileSize_DW = '{FileSize_DW}'";
                                                DataRow[] dupeFile = tblPreFiles.Select(dupeSQL);

                                                if (dupeFile.Length > 0)
                                                {
                                                    logger.LogInformation($"📄 Current Source File is already in pre table {cFile.Name}");

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
                                                }
                                            }
                                        }

                                        if (sp.targetExsits)
                                        {
                                            Shared.FetchTargetTableRowCount(logger, sp, trgSqlCon);

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
                                    }
                                }
                            }
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
                    }
                }
                finally
                {
                    trgSqlCon.Close();
                    trgSqlCon.Dispose();

                    sqlFlowCon.Close();
                }
            }

            return sp.result;
        }
        #endregion ProcessCsv

        /// <summary>
        /// Generates a stream from a given string.
        /// </summary>
        /// <param name="s">The string to convert into a stream.</param>
        /// <returns>A stream representing the input string.</returns>
        internal static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Ensures the provided path string ends with a slash.
        /// </summary>
        /// <param name="input">The path string to process.</param>
        /// <returns>The processed path string with a trailing slash.</returns>
        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }

    }
}
