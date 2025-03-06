using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Parquet;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.IO;
using SQLFlowCore.Services.Prq;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using SQLFlowCore.Logger;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Represents a static class that provides functionality for executing SQL Flows.
    /// </summary>
    /// <remarks>
    /// This class contains an event that is triggered when rows are copied and a method for executing SQL Flows.
    /// </remarks>
    internal static class ProcessPrq
    {
        /// <summary>
        /// Occurs when rows have been copied in the process.
        /// </summary>
        /// <remarks>
        /// This event is triggered in various processes such as ingestion, CSV processing, XLS processing, XML processing, PRQ processing, and PRC processing.
        /// </remarks>
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        #region ProcessPrq
        /// <summary>
        /// Executes the SQL Flow process.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string for the SQL Flow.</param>
        /// <param name="flowId">The ID of the Flow to be executed.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The ID of the batch to be processed.</param>
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

            // Create the logger.
            var logger = new RealTimeLogger("PreIngestionPrq", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVPreFlowPRQ]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
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

                        //SMOScriptingOptions Pre Files. Avoid re-process of data
                        var tblPreFiles = Shared.GetFilesFromTable(logger, sp, trgSqlCon);

                        //Fetch file date from related tables
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
                        
                        if (sp.partitionList.Length > 0)
                        {
                            
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
                            // CheckForError if target table exists:
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

                            using (logger.TrackOperation("Process enumerated files"))
                            {
                                foreach (GenericFileItem cFile in genericFileList)
                                {
                                    Dictionary<string, string> columnMappings = new Dictionary<string, string>();
                                    logger.LogInformation($"📄 Current source file {cFile.Name} with last create/modified date {cFile.LastModified}");

                                    sp.processedFileList = sp.processedFileList + "," + (sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name));

                                    logger.LogCodeBlock("Current source file:", cFile.Name);

                                    sp.logLength = long.Parse(cFile.ContentLength.ToString());
                                    sp.logFileDate = sp.logFileDate = cFile.CreationTime > cFile.LastWriteTime ? cFile.CreationTime.ToString("yyyyMMddHHmmss") : cFile.LastWriteTime.ToString("yyyyMMddHHmmss");

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

                                        using (ParquetReader prqReader = ParquetReader.CreateAsync(stream).Result)
                                        {
                                            DataTable prqSchemaTable = ParquetHelper.GetParquetColumns(prqReader, sp.defaultColDataType);

                                            if (prqSchemaTable.Rows.Count != sp.expectedColumnCount && sp.expectedColumnCount > 0)
                                            {
                                                string eMsg =
                                                    $"Error: Expected column Count miss match (File: {prqSchemaTable.Rows.Count.ToString()}, Expected {sp.expectedColumnCount.ToString()}) {cFile.Name} {Environment.NewLine} ";
                                                sp.colCountErrorMsg += eMsg;
                                                sp.colCountError = true;
                                            }
                                            else
                                            {
                                                logger.LogInformation($"ColumnCount (File: {prqSchemaTable.Rows.Count}, Expected {sp.expectedColumnCount})");

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
                                                    logger.LogInformation($"📄 Current source file is already in pre table {FileName_DW}");

                                                    if (sp.viewCmd.Length > 10)
                                                    {
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
                                                        fileColumnCount: prqSchemaTable.Rows.Count,
                                                        expectedColumnCount: sp.expectedColumnCount
                                                    );
                                                    
                                                    SortedList<int, DataColumn> virtualColumns = new SortedList<int, DataColumn>();
                                                    int OrdinalVirtual = prqSchemaTable.Rows.Count;

                                                    if (prqSchemaTable.Rows.Count > 0)
                                                    {
                                                        DataColumn dc = new DataColumn();
                                                        dc.ColumnName = "FileDate_DW";
                                                        dc.DataType = typeof(string);
                                                        dc.DefaultValue = FileDate_DW;
                                                        virtualColumns.Add(OrdinalVirtual + 1, dc);

                                                        DataColumn dc2 = new DataColumn();
                                                        dc2.ColumnName = "FileName_DW";
                                                        dc2.DataType = typeof(string);
                                                        dc2.DefaultValue = sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name);
                                                        virtualColumns.Add(OrdinalVirtual + 2, dc2);

                                                        DataColumn dc3 = new DataColumn();
                                                        dc3.ColumnName = "FileRowDate_DW";
                                                        dc3.DataType = typeof(string);
                                                        dc3.DefaultValue = FileRowDate_DW.ToString("yyyy-MM-dd HH:mm:ss");
                                                        dc3.DefaultValue = FileRowDate_DW;
                                                        virtualColumns.Add(OrdinalVirtual + 3, dc3);

                                                        DataColumn dc4 = new DataColumn();
                                                        dc4.ColumnName = "FileSize_DW";
                                                        dc4.DataType = typeof(string);
                                                        dc4.DefaultValue = FileSize_DW.ToString();
                                                        virtualColumns.Add(OrdinalVirtual + 4, dc4);

                                                        DataColumn dc5 = new DataColumn();
                                                        dc5.ColumnName = "DataSet_DW";
                                                        dc5.DataType = typeof(string);
                                                        dc5.DefaultValue = DataSet_DW;
                                                        virtualColumns.Add(OrdinalVirtual + 5, dc5);

                                                        logger.LogInformation($"Source file read into memory");

                                                        string columnMappingList = "";

                                                        string[] fileColNames = prqSchemaTable.AsEnumerable().Select(row => row.Field<string>("ColumnName")).ToArray();

                                                        List<string> list = fileColNames.ToList();

                                                        string fileColumnList = string.Join(",", list);
                                                        logger.LogCodeBlock("File Column List:", fileColumnList);
                                                        Dictionary<string, string> colDic = new Dictionary<string, string>();

                                                        DataTable prqSchemaWithVirtualColTbl = ParquetHelper.GetParquetColumns(prqReader, virtualColumns, sp.defaultColDataType);
                                                        foreach (DataRow row in prqSchemaWithVirtualColTbl.Rows)
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

                                                            prqSchemaWithVirtualColTbl.AcceptChanges();

                                                            SyncInput si = new SyncInput();
                                                        }

                                                        string cmdColumns = "";
                                                        string columnList = "";
                                                        string columnDataTypeList = "";
                                                        string columnExpList = "";

                                                        cmdColumns = string.Join(",", prqSchemaWithVirtualColTbl.AsEnumerable().Select(r => r.Field<string>("ColumnCMD")));
                                                        columnList = string.Join(",", prqSchemaWithVirtualColTbl.AsEnumerable().Select(r => "[" + r.Field<string>("ColumnNameCleaned") + "]"));
                                                        columnDataTypeList = string.Join(";", prqSchemaWithVirtualColTbl.AsEnumerable().Select(r => r.Field<string>("SqlDataType")));
                                                        columnExpList = string.Join(";", prqSchemaWithVirtualColTbl.AsEnumerable().Select(r => r.Field<string>("SQLFlowExp")));

                                                        logger.LogCodeBlock("cmdColumns:", cmdColumns);
                                                        logger.LogCodeBlock("columnList:", columnList);
                                                        logger.LogCodeBlock("columnDataTypeList", columnDataTypeList);
                                                        logger.LogCodeBlock("columnExpList:", columnExpList);

                                                        string cmdCreateTransformations =
                                                            $"exec flw.AddPreIngTransfrom @FlowID={sqlFlowParam.flowId.ToString()}, @FlowType='prq', @ColList='{columnList}', @DataTypeList='{columnDataTypeList}', @ExpList='{columnExpList}'";
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

                                                        logger.LogInformation($"Number of Pre Ingestion Transform Columns {tfColumnDic.Count()}");

                                                        string trgTblCmd = "";

                                                        if (sp.syncSchema)
                                                        {
                                                            if (sp.FileCounter == 0)
                                                            {
                                                                trgTblCmd = sp.cmdCreate;
                                                                sp.targetExsits = true;
                                                            }
                                                            else
                                                            {
                                                                trgTblCmd = sp.cmdAlterSQL;
                                                            }
                                                        }

                                                        if (trgTblCmd.Length > 0)
                                                        {
                                                            using (var operation = logger.TrackOperation("Target Table Command Preparation"))
                                                            {
                                                                logger.LogCodeBlock("Target table prepare command:", trgTblCmd);
                                                                // Execute the command
                                                                CommonDB.ExecDDLScript(trgSqlCon, trgTblCmd, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                                                            }
                                                        }

                                                        if (sp.currentViewCMD.Length > 10 || sp.viewCmd.Length > 10)
                                                        {
                                                            Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
                                                        }

                                                        if (sp.FileCounter == 0)
                                                        {
                                                            using (var operation = logger.TrackOperation("Delete Target Table Indexes"))
                                                            {
                                                                SQLObject sObj = CommonDB.SQLObjectFromDBSchobj(sp.trgDbSchTbl);
                                                                Services.Schema.ObjectIndexes.DropObjectIndexes(sp.trgConString, sObj.ObjDatabase, sObj.ObjSchema, sObj.ObjName);
                                                            }
                                                        }

                                                        foreach (DataRow row in prqSchemaWithVirtualColTbl.Rows)
                                                        {
                                                            var columnName = "[" + row["ColumnName"] + "]";
                                                            var ColumnNameCleaned = "[" + row["ColumnNameCleaned"] + "]";

                                                            if (columnMappings.ContainsKeyIgnoreCase(columnName) == false && tfColumnDic.ContainsKeyIgnoreCase(columnName) == true)
                                                            {
                                                                columnMappings.Add(columnName, ColumnNameCleaned);
                                                            }
                                                        }

                                                        columnMappingList = string.Join(", ", columnMappings.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                                                        logger.LogCodeBlock("Column Mapping List:", columnMappingList);

                                                        sp.logFetched = sp.logFetched + prqReader.Metadata.NumRows;
                                                        
                                                        if (sp.noOfThreads > prqReader.RowGroupCount)
                                                        {
                                                            sp.noOfThreads = prqReader.RowGroupCount;
                                                        }

                                                        //string cmd = "";
                                                        int cCounter = 0;

                                                        for (int x = 0; x < prqReader.RowGroupCount; x++)
                                                        {
                                                            ParquetReader _prqReader = prqReader;
                                                            int _x = x;

                                                            var currentRowCount = cCounter;

                                                            using (var dataReader = new ParquetDataReaderWithVirtualColumns(_prqReader, _x, false, virtualColumns))
                                                            {
                                                                var bulk = new StreamToSql(sp.trgConString,
                                                                   $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", columnMappings,
                                                                   sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                                                                    retryErrorCodes, sqlFlowParam.dbg, ref currentRowCount); // BatchSize

                                                                bulk.StreamWithRetries(dataReader);
                                                            }
                                                            cCounter = cCounter + 1;
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

                                                        if (sp.srcDeleteIngested)
                                                        {
                                                            if (sqlFlowParam.sourceIsAzCont)
                                                            {
                                                                DataLakeFileClient delClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                                                                delClient.DeleteIfExists();
                                                                logger.LogInformation($"Deleting Source File");
                                                            }
                                                            else
                                                            {
                                                                if (File.Exists(cFile.FullPath))
                                                                {
                                                                    // If file found, delete it    
                                                                    File.Delete(cFile.FullPath);
                                                                    logger.LogInformation($"Deleting Source File");
                                                                }
                                                            }
                                                        }

                                                        sp.FileCounter += 1;
                                                    }
                                                    else
                                                    {
                                                        logger.LogInformation($"Current file is invalid");
                                                    }
                                                }
                                            }
                                        }
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

                            Shared.WriteToSysLog(
                                connection: sqlFlowCon,
                                flowId: sqlFlowParam.flowId,
                                flowType: sqlFlowParam.flowType,
                                execMode: sqlFlowParam.execMode,
                                startTime: sp.logStartTime,
                                endTime: sp.logEndTime,
                                durationFlow: sp.logDurationFlow,
                                durationPre: sp.logDurationPre,
                                durationPost: sp.logDurationPost,
                                fetched: sp.logFetched > 0 ? sp.logFetched : 0,
                                inserted: sp.logInserted,
                                fileName: sp.processedFileList,
                                fileSize: sp.logLength,
                                fileDate: sp.InitFromFileDate == 0
                                    ? (sp.createDateTime > sp.modifiedDateTime
                                        ? sp.createDateTime.ToString("yyyyMMddHHmmss")
                                        : sp.modifiedDateTime.ToString("yyyyMMddHHmmss"))
                                    : "",
                                selectCmd: sp.logSelectCmd,
                                createCmd: sp.logCreateCmd,
                                errorRuntime: sp.logErrorRuntime,
                                debug: sqlFlowParam.dbg,
                                noOfThreads: sp.noOfThreads,
                                batch: sp.flowBatch,
                                sysAlias: sp.sysAlias,
                                batchId: sqlFlowParam.batchId,
                                traceLog: logOutput.ToString(),
                                inferDatatypeCmd: sp.InferDatatypeCmd.ToString(),
                                logger: logger                                        // Assuming logger is ILogger
                            );

                        }

                        if (sp.Indexes.Length > 5)
                        {
                            using (var operation = logger.TrackOperation("Re-create Target Table Indexes"))
                            {
                                logger.LogCodeBlock("Indexes Script", sp.Indexes);
                                CommonDB.ExecDDLScript(trgSqlCon, sp.Indexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                            }
                        }
                        else if (sp.trgIndexes.Length > 5)
                        {
                            using (var operation = logger.TrackOperation("Re-create Target Table Indexes from SysLog"))
                            {
                                logger.LogCodeBlock("SysLog Indexes Script", sp.trgIndexes);
                                CommonDB.ExecDDLScript(trgSqlCon, sp.trgIndexes, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
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
                    sqlFlowCon.Dispose();
                }
            }

            return sp.result;
        }
        #endregion ProcessPrq

        private static string GetFullPathWithEndingSlashes(string input)
        {
            return input.TrimEnd('/') + "/";
        }


        // Add this function to the ProcessPrq class to handle file deletion
        private static void EnsureParquetFileDeleted(string filePath, ILogger logger)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    logger.LogInformation($"Deleting existing Parquet file before processing: {filePath}");
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error deleting Parquet file {filePath}: {ex.Message}");
                throw new InvalidOperationException($"Unable to delete existing Parquet file. Please ensure no other process is using it: {filePath}", ex);
            }
        }

        // For Azure Data Lake files
        private static async Task EnsureDataLakeParquetFileDeleted(DataLakeFileSystemClient fileSystemClient, string filePath, ILogger logger)
        {
            try
            {
                DataLakeFileClient fileClient = fileSystemClient.GetFileClient(filePath);
                var exists = await fileClient.ExistsAsync();

                if (exists)
                {
                    logger.LogInformation($"Deleting existing Parquet file from Data Lake before processing: {filePath}");
                    await fileClient.DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error deleting Parquet file from Data Lake {filePath}: {ex.Message}");
                throw new InvalidOperationException($"Unable to delete existing Parquet file from Data Lake: {filePath}", ex);
            }
        }
    }
}
