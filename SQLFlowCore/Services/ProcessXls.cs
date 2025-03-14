using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using ExcelDataReader;
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
using SQLFlowCore.Services.Xls;
using SQLFlowCore.Logger;
using Google.Protobuf.WellKnownTypes;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides methods and events to process Excel files in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class is used internally and is not intended to be used directly from your code.
    /// </remarks>
    internal static class ProcessXls
    {
        /// <summary>
        /// Occurs when rows are copied in the ProcessXls class.
        /// </summary>
        /// <remarks>
        /// This event is triggered in multiple places within the SQLFlowCore.Engine namespace, 
        /// including the ExecFlowBatch class. It is used to handle the copying of rows in different processes.
        /// </remarks>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        #region ProcessXls
        /// <summary>
        /// Executes the process for handling XLS files in SQLFlow.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string for SQLFlow.</param>
        /// <param name="flowId">The ID of the flow.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The ID of the batch.</param>
        /// <param name="dbg">Debug mode indicator.</param>
        /// <param name="sqlFlowParam">The SQLFlow item to be processed.</param>
        /// <returns>A string indicating the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            
            XlsRange xlsRange = new XlsRange();
            var result = "false";
            PushToSql.OnRowsCopied += OnRowsCopied;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionXls", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowXLS]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
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
                            string fileDateFromNextFlow = CommonDB.GetFileDateFromNextFlow(incrTbl, sp.generalTimeoutInSek);

                            using (var operation = logger.TrackOperation("FetchFileDateFromNextFlow"))
                            {
                                Shared.FileDateFromNextFlow(logger, incrTbl, sp);
                            }

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

                        //sRange is used to spesify an extract range from the sheet.
                        if (sp.sheetRange.Length > 0)
                        {
                            string[] rangeParts = sp.sheetRange.Split(':');
                            int FromRow = int.Parse(Regex.Match(rangeParts[0], @"\d+").Value);
                            int ToRow = int.Parse(Regex.Match(rangeParts[1], @"\d+").Value);
                            string _FromColName = Regex.Match(rangeParts[0], @"[a-zA-Z]+").Value;
                            string _ToColName = Regex.Match(rangeParts[1], @"[a-zA-Z]+").Value;
                            int FromColName = ColumnLetterToColumnIndex(_FromColName) - 1;
                            int ToColName = ColumnLetterToColumnIndex(_ToColName) - 1;

                            xlsRange.IsRange = true;
                            xlsRange.FromRow = FromRow;
                            xlsRange.ToRow = ToRow;
                            xlsRange.FromColumnIndex = FromColName;
                            xlsRange.ToColumnIndex = ToColName;

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

                            #region EnumerateDataLakeGen2
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
                        if (targetExsits)
                        {
                            Shared.CheckAndLogTargetIndexes(sqlFlowParam, trgSqlCon, sp, sqlFlowCon);
                        }

                        if (!genericFileList.Any())
                        {
                            Shared.HandelNoFilesFound(sqlFlowParam, sp, logger, trgSqlCon, execTime, sqlFlowCon, logOutput);
                        }
                        else
                        {
                            //CheckForError if target table exsits:
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
                                
                                //Connection string to the xls file
                                using (var stream = new MemoryStream())
                                {
                                    if (sqlFlowParam.sourceIsAzCont)
                                    {
                                        DataLakeFileClient fileClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                                        Response<FileDownloadInfo> downloadResponse = fileClient.Read();
                                        downloadResponse.Value.Content.CopyTo(stream);
                                    }
                                    else // Read from local disk
                                    {
                                        using (var fileStream = File.OpenRead(cFile.FullPath))
                                        {
                                            fileStream.CopyTo(stream);
                                        }
                                    }

                                    // Auto-detect format, supports:
                                    //  - Binary Excel files (2.0-2003 format; *.xls)
                                    //  - OpenXml Excel files (2007 format; *.xlsx, *.xlsb)
                                    using (var reader = ExcelReaderFactory.CreateReader(stream))
                                    {
                                        ExcelDataSetConfiguration xlsConfig = new ExcelDataSetConfiguration
                                        {
                                            UseColumnDataType = false,
                                            //xlsConfig.FilterSheet = (tableReader, sheetIndex) => true;
                                            ConfigureDataTable = (tableReader) =>
                                                new ExcelDataTableConfiguration()
                                                {
                                                    // Gets or sets a value indicating the prefix of generated column names.
                                                    EmptyColumnNamePrefix = "Column",
                                                    // Gets or sets a value indicating whether to use a row from the 
                                                    // data as column names.
                                                    UseHeaderRow = sp.firstRowHasHeader,
                                                    FilterColumn = (rowReader, columnIndex) =>
                                                    {
                                                        //string abc = rowReader.Name;
                                                        //int test = rowReader.GetNumberFormatIndex();
                                                        if (xlsRange.FromColumnIndex <= columnIndex &&
                                                            xlsRange.ToColumnIndex >= columnIndex)
                                                        {
                                                            return true;
                                                        }
                                                        else
                                                        {
                                                            return false;
                                                        }
                                                    }
                                                }
                                        };

                                        var xlsDS = reader.AsDataSet(xlsConfig);

                                        DataTable dataTable = new DataTable();
                                        string fetchSheetName = "";
                                        if (sp.useSheetIndex)
                                        {
                                            int sheetIndex;

                                            bool isNumerical = int.TryParse(sp.sheetName, out sheetIndex);

                                            if (isNumerical)
                                            {
                                                dataTable = xlsDS.Tables[sheetIndex];
                                                fetchSheetName = xlsDS.Tables[sheetIndex].TableName;
                                                fetchSheetName = fetchSheetName.Replace("$", "");
                                            }

                                        }
                                        else
                                        {
                                            dataTable = xlsDS.Tables[sp.sheetName];
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
                                            logger.LogInformation($"ColumnCount (File: {dataTable.Columns.Count}, Expected {sp.expectedColumnCount})");

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
                    trgSqlCon.Close();
                    trgSqlCon.Dispose();
                    sqlFlowCon.Close();
                }
            }

            return sp.result;
        }
        #endregion ProcessXls

        /// <summary>
        /// Converts a column letter to a column index.
        /// </summary>
        /// <param name="columnLetter">The column letter to convert.</param>
        /// <returns>The index of the column. The index is 1-based, meaning that 'A' corresponds to 1, 'B' to 2, etc.</returns>
        /// <remarks>
        /// This method is used to convert Excel-style column letters to their corresponding numerical index. 
        /// It supports columns from 'A' to 'ZZZ'.
        /// </remarks>
        internal static int ColumnLetterToColumnIndex(string columnLetter)
        {
            columnLetter = columnLetter.ToUpper();
            int sum = 0;

            for (int i = 0; i < columnLetter.Length; i++)
            {
                sum *= 26;
                sum += columnLetter[i] - 'A' + 1;
            }
            return sum;
        }

        /// <summary>
        /// Ensures the provided path string ends with a slash.
        /// </summary>
        /// <param name="input">The path string to process.</param>
        /// <returns>The processed path string ending with a slash.</returns>
        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }

    }
}
