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
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.IO;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Logger;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// The ProcessXml class is a static class responsible for processing XML data in the SQLFlow engine.
    /// </summary>
    /// <remarks>
    /// This class provides an event for tracking the progress of rows copied during the XML processing operation.
    /// It also provides a method for executing a specific flow within the SQLFlow engine.
    /// </remarks>
    /// <example>
    /// An example of using this class might be:
    /// <code>
    /// ProcessXml.OnRowsCopied += HandlerOnRowsCopied;
    /// string result = ProcessXml.Exec(sqlFlowConString, FlowID, execMode, BatchID, dbg, sqlFlowItem);
    /// </code>
    /// </example>
    internal static class ProcessXml
    {
        /// <summary>
        /// Occurs when rows are copied during the execution of a flow batch.
        /// </summary>
        /// <remarks>
        /// This event is triggered in multiple processes such as ProcessIngestion, ProcessCsv, ProcessXls, ProcessXml, ProcessPrq, and ProcessPrc.
        /// It is used to handle the rows copied event in these processes.
        /// </remarks>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        #region ProcessXml
        /// <summary>
        /// Executes the SQL Flow process.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string for the SQL Flow.</param>
        /// <param name="flowId">The identifier for the Flow.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The identifier for the Batch.</param>
        /// <param name="dbg">The debug mode flag.</param>
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
            var logger = new RealTimeLogger("PreIngestionXml", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

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

                    DataSet ds = new DataSet();
                    DataTable paramTbl = new DataTable();
                    DataTable incrTbl = new DataTable();
                    DataTable DateTimeFormats = new DataTable();
                    DataTable procParamTbl = new DataTable();

                    using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
                    {
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowXML]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
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

                        using (var operation = logger.TrackOperation("Log table indexes"))
                        {
                            Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
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
                                    genericFileList = Shared.EnumerateSingelLakeFile(sqlFlowParam, srcFileSystemClient);
                                }
                                else
                                {
                                    genericFileList = Shared.EnumerateMultipleLakeFiles(sp, srcFileSystemClient, logger, adapter);
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

                        bool targetExsits = CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);
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

                            foreach (GenericFileItem cFile in genericFileList)
                            {
                                Dictionary<string, string> columnMappings = new Dictionary<string, string>();
                                logger.LogInformation($"📄 Current source file {cFile.Name} with last create/modified date {cFile.LastModified} and modified date");

                                sp.processedFileList = sp.processedFileList + "," + (sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name));

                                logger.LogCodeBlock("Current source file:", cFile.Name);

                                sp.logLength = long.Parse(cFile.ContentLength.ToString());
                                sp.logFileDate = cFile.CreationTime > cFile.LastWriteTime ? cFile.CreationTime.ToString("yyyyMMddHHmmss") : cFile.LastWriteTime.ToString("yyyyMMddHHmmss");

                                
                                string xmlContent;
                                if (sqlFlowParam.sourceIsAzCont)
                                {
                                    DataLakeFileClient fileClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                                    Response<FileDownloadInfo> downloadResponse = fileClient.Read();

                                    using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
                                    {
                                        xmlContent = reader.ReadToEnd(); // Read XML content into string
                                    }
                                }
                                else // Read from local disk
                                {
                                    using (StreamReader reader = new StreamReader(cFile.FullPath))
                                    {
                                        xmlContent = reader.ReadToEnd(); // Read XML content into string
                                    }
                                }

                                DataTable dataTable = new DataTable();

                                using (var operation = logger.TrackOperation("XML flattening"))
                                {
                                    if (sp.XmlToDataTableCode.Length > 150)
                                    {
                                        dataTable = DynamicDataConverter.ToDataTableDynamically(logger, sp.XmlToDataTableCode, xmlContent, Path.GetFileName(cFile.Name), trgSqlCon);
                                    }
                                    else
                                    {
                                        DataSet dsXML = new DataSet();
                                        dsXML.ReadXml(new StringReader(xmlContent)); // Load into DataSet
                                        FlattenDataset fds = new FlattenDataset(dsXML, sp.hierarchyIdentifier);
                                        dataTable = fds.FlattenDataSet();
                                    }
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
                                    logger.LogInformation($"ColumnCount (File: {dataTable.Columns.Count.ToString()}, Expected {sp.expectedColumnCount.ToString()})");

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

                        execTime.Stop();
                        logger.LogInformation($"Total processing time ({sp.logDurationFlow} sec)");

                        Shared.EvaluateAndLogExecution(sqlFlowCon,sqlFlowParam, logOutput, sp, logger);

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
                    }
                }
                finally
                {
                    SqlConnection.ClearPool(trgSqlCon);

                    trgSqlCon.Close();
                    trgSqlCon.Dispose();

                    sqlFlowCon.Close();
                }

            }

            return sp.result;
        }
        #endregion ProcessXml
        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }
       
    }
}
