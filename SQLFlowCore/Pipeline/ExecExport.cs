using Azure.Storage.Files.DataLake;
using CsvHelper;
using CsvHelper.Configuration;
using Parquet;
using Parquet.Schema;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DataColumn = System.Data.DataColumn;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Logger;
using SQLFlowCore.Services;
using Octokit;
using Parquet.Data;

namespace SQLFlowCore.Pipeline
{
    public static class ExecExport
    {
        public static event EventHandler<EventArgsExport> OnFileExported;
        private static int _sharedCounter = 0;
        private static DateOnly _NextExportDate = DateOnly.MinValue;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _fileCreationTracker
            = new(StringComparer.OrdinalIgnoreCase);

        #region ExecExport
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            _sharedCounter = 0;
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionCsv", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

            ServiceParam sp = ServiceParam.Current;
            var trgSqlCon = new SqlConnection();

            var tasks = new List<Task>();
            int _Total = 0;
            SortedList<int, ExpSegment> SrcBatched = new SortedList<int, ExpSegment>();
            DateTime createDateTime = DateTime.Now;

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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVExport]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                        sp.DateTimeFormats = DateTimeFormats;
                    }
                    
                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        if (sp.srcSecretName.Length > 0)
                        {
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                sp.srcTenantId,
                                sp.trgApplicationId,
                                sp.srcClientSecret,
                                sp.srcKeyVaultName);
                            sp.srcConnectionString = srcKeyVaultManager.GetSecret(sp.srcSecretName);
                        }

                        conStringParser = new ConStringParser(sp.srcConnectionString)
                        {
                            ConBuilderMsSql =
                            {
                                ApplicationName = "SQLFlow Target"
                            }
                        };
                        sp.srcConnectionString = conStringParser.ConBuilderMsSql.ConnectionString;

                        srcSqlCon = new SqlConnection(sp.srcConnectionString);
                        srcSqlCon.Open();

                        new Hashtable(StringComparer.InvariantCultureIgnoreCase);


                        if (sp.trgPath.Length > 0)
                        {
                            sp.trgPath = Functions.EnsureEndingSlash(sp.trgPath, "");
                        }

                        logger.LogInformation($"Init Export to {sp.trgPath} file {sp.trgFileName}");

                        string srcWhere = "";
                        string srcDSType = "MSSQL";
                        int KeyMaxValue = 0;

                        DataTable srcDataTable = new DataTable();
                        using (var operation = logger.TrackOperation("Fetched source meta data"))
                        {
                            string cmdSQLDT = $"SELECT * FROM {sp.srcDBSchObj.ObjFullName} WHERE 1 <> 1";
                            logger.LogCodeBlock($"Fetched meta data:", cmdSQLDT);
                            srcDataTable = CommonDB.GetData(srcSqlCon, cmdSQLDT, sp.bulkLoadTimeoutInSek);
                        }

                        string ColList = CommonDB.GetColumnList(srcDataTable, "MSSQL");

                        string whereXML = "";
                        

                        if (sp.ExportBy.Equals("F", StringComparison.InvariantCultureIgnoreCase) == false)
                        {
                            using (var operation = logger.TrackOperation("Fetched runtime Min filters"))
                            {
                                string cmdMin = CommonDB.GetIncWhereExp("MIN", sp.srcDBSchObj.ObjDatabase, sp.srcDBSchObj.ObjSchema, sp.srcDBSchObj.ObjName, sp.DateColumn, CommonDB.ParseObjectNames(sp.IncrementalColumn), "MSSQL", sp.NoOfOverlapDays, sp.srcIsSynapse, "", "nolock");
                                logger.LogCodeBlock("Query To Fetch Runtime Min Values:", cmdMin);
                                DataTable expDataMin = CommonDB.FetchData(srcSqlCon, cmdMin, sp.bulkLoadTimeoutInSek);
                               
                                sp.MinXML = Functions.ParseIncObject(expDataMin, sp.IncrementalColumn, sp.DateColumn, DateTimeFormats);
                                logger.LogCodeBlock("MinXML:", sp.MinXML.XML);
                            }

                            using (var operation = logger.TrackOperation("Fetched runtime Max filters"))
                            {
                                string cmdMax = CommonDB.GetIncWhereExp("MAX", sp.srcDBSchObj.ObjDatabase, sp.srcDBSchObj.ObjSchema, sp.srcDBSchObj.ObjName, sp.DateColumn, CommonDB.ParseObjectNames(sp.IncrementalColumn), "MSSQL", sp.NoOfOverlapDays, sp.srcIsSynapse, "", "nolock");
                                // FIX #1: Changed cmdMin to cmdMax here:
                                logger.LogCodeBlock("Query To Fetch Runtime Max Values:", cmdMax);
                                DataTable expDataMax = CommonDB.FetchData(srcSqlCon, cmdMax, sp.bulkLoadTimeoutInSek);

                                // FIX #2: Changed the text from "Fetched runtime Min filters" to "Fetched runtime Max filters"
                                sp.MaxXML = Functions.ParseIncObject(expDataMax, sp.IncrementalColumn, sp.DateColumn, DateTimeFormats);
                                logger.LogCodeBlock("MaxXML:", sp.MaxXML.XML);
                            }

                            if (sp.DateColumn.Length > 0)
                            {
                                DateTime currentFromDate = sp.FromDate;
                                if (sp.MinXML.DateColVal > currentFromDate)
                                {
                                    currentFromDate = sp.MinXML.DateColVal;
                                }
                                if (sp.NextExportDate > FlowDates.Default)
                                {
                                    currentFromDate = sp.NextExportDate;
                                }
                                DateTime currentToDate = sp.ToDate;
                                if (sp.MaxXML.DateColVal < currentToDate)
                                {
                                    currentToDate = sp.MaxXML.DateColVal;
                                }
                                SrcBatched = CommonDB.GetSrcSelectBatched(
                                    ColList,
                                    sp.srcDBSchObj.ObjDatabase,
                                    sp.srcDBSchObj.ObjSchema,
                                    sp.srcDBSchObj.ObjName,
                                    sp.srcWithHint,
                                    srcWhere,
                                    srcDSType,
                                    currentFromDate,
                                    currentToDate,
                                    sp.ExportBy,
                                    sp.ExportSize,
                                    sp.DateColumn,
                                    0,
                                    KeyMaxValue,
                                    "",
                                    sp.trgPath,
                                    sp.trgFileName,
                                    sp.trgFiletype,
                                    sp.AddTimeStampToFileName,
                                    sp.Subfolderpattern);
                                    sp.logNextExportDate = currentToDate;
                            }
                            else if (sp.IncrementalColumn.Length > 0)
                            {
                                if (sp.MinXML.IncColIsDate)
                                {
                                    DateTime currentFromDate = sp.FromDate;
                                    if (sp.MinXML.IncColValDT > currentFromDate)
                                    {
                                        currentFromDate = sp.MinXML.IncColValDT;
                                    }
                                    if (sp.NextExportDate > FlowDates.Default)
                                    {
                                        currentFromDate = sp.NextExportDate;
                                    }
                                    DateTime currentToDate = sp.ToDate;
                                    if (sp.MaxXML.IncColValDT < currentToDate)
                                    {
                                        currentToDate = sp.MaxXML.IncColValDT;
                                    }
                                    SrcBatched = CommonDB.GetSrcSelectBatched(
                                        ColList,
                                        sp.srcDBSchObj.ObjDatabase,
                                        sp.srcDBSchObj.ObjSchema,
                                        sp.srcDBSchObj.ObjName,
                                        sp.srcWithHint,
                                        srcWhere,
                                        srcDSType,
                                        currentFromDate,
                                        currentToDate,
                                        sp.ExportBy,
                                        sp.ExportSize,
                                        sp.IncrementalColumn,
                                        0,
                                        KeyMaxValue,
                                        "",
                                        sp.trgPath,
                                        sp.trgFileName,
                                        sp.trgFiletype,
                                        sp.AddTimeStampToFileName,
                                        sp.Subfolderpattern);
                                    sp.logNextExportDate = sp.MaxXML.IncColValDT;
                                }
                                else
                                {
                                    int MinValue = sp.MinXML.IncColVal;
                                    int MaxValue = sp.MaxXML.IncColVal;
                                    if (sp.NextExportValue > 0)
                                    {
                                        MinValue = sp.NextExportValue;
                                    }
                                    sp.logNextExportValue = sp.MaxXML.IncColVal;
                                    SrcBatched = CommonDB.GetSrcSelectBatched(
                                        ColList,
                                        sp.srcDBSchObj.ObjDatabase,
                                        sp.srcDBSchObj.ObjSchema,
                                        sp.srcDBSchObj.ObjName,
                                        sp.srcWithHint,
                                        srcWhere,
                                        srcDSType,
                                        sp.FromDate,
                                        sp.ToDate,
                                        sp.ExportBy,
                                        sp.ExportSize,
                                        "",
                                        MinValue,
                                        MaxValue,
                                        sp.IncrementalColumn,
                                        sp.trgPath,
                                        sp.trgFileName,
                                        sp.trgFiletype,
                                        sp.AddTimeStampToFileName,
                                        sp.Subfolderpattern);
                                }
                            }
                        }
                        else
                        {
                            if (sp.ExportBy.Equals("F", StringComparison.InvariantCultureIgnoreCase))
                            {
                                string ExportDate = "_" + DateTime.Now.ToString("yyyyMMddHHmmss");
                                if (sp.AddTimeStampToFileName == false) ExportDate = "";

                                sp.trgFileName = sp.trgFileName + $"{ExportDate}";
                                string cmdSrcSelect = $"SELECT {ColList} FROM [{sp.srcDBSchObj.ObjDatabase}].[{sp.srcDBSchObj.ObjSchema}].[{sp.srcDBSchObj.ObjName}] WHERE 1=1";
                                ExpSegment val2 = new ExpSegment
                                {
                                    SqlCMD = cmdSrcSelect,
                                    WhereClause = "",
                                    FileName_DW = sp.trgFileName,
                                    FileType_DW = sp.trgFiletype,
                                    FilePath_DW = sp.trgPath
                                };
                                SrcBatched.Add(0, val2);
                            }
                        }

                        _Total = SrcBatched.Count();

                        logger.LogInformation(@$"SysLog Incremental Date: {sp.NextExportDate:yyyy-MM-dd}, IncrmentalKey: {sp.NextExportValue}");

                        if (_Total <= 1)
                        {
                            logger.LogInformation($"No data to export");
                        }

                        string AllSQL = "";
                        foreach (var item in SrcBatched)
                        {
                            AllSQL += item.Value.SqlCMD + Environment.NewLine + Environment.NewLine;
                        }
                        sp.logSelectCmd = AllSQL;
                        if (_Total < sp.NoOfThreads)
                        {
                            sp.NoOfThreads = _Total;
                        }
                        logger.LogCodeBlock("Query To Fetch Runtime Max Values:", AllSQL);

                        using (var concurrencySemaphore = new Semaphore(sp.NoOfThreads, sp.NoOfThreads))
                        {
                            if (sp.trgTenantId.Length > 2)
                            {
                                // Azure Data Lake path
                                DataLakeFileSystemClient trgFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                    sp.trgTenantId,
                                    sp.trgApplicationId,
                                    sp.trgClientSecret,
                                    sp.trgKeyVaultName,
                                    sp.trgSecretName,
                                    sp.trgStorageAccountName,
                                    sp.trgBlobContainer);

                                int _Rowcount = 0;
                                foreach (var item in SrcBatched)
                                {
                                    ExpSegment _item = item.Value;
                                    string _FullFileName = _item.GetFileNameWithPath();
                                    bool _OnErrorResume = sp.OnErrorResume;
                                    string _SysAlias = sp.SysAlias;
                                    string _Batch = sp.Batch;
                                    int _FlowID = sqlFlowParam.flowId;
                                    var _logStack = logger;
                                    long _FileSize = 0;

                                    // FIX #3: Use .Unwrap() so the async block is properly awaited:
                                    var tUnwrapped = Task.Factory.StartNew(async () =>
                                    {
                                        concurrencySemaphore.WaitOne();
                                        try
                                        {
                                            if (!_fileCreationTracker.TryAdd(_FullFileName, 0))
                                            {
                                                _logStack.LogInformation($"Skipping duplicate file creation for {_FullFileName}");
                                                return;
                                            }

                                            if (sp.ProcessNext)
                                            {
                                                var execTime = new Stopwatch();
                                                execTime.Start();

                                                // (Data Lake creates the 'directory' automatically.)
                                                DataLakeFileClient TrgFileClient = trgFileSystemClient.GetFileClient(_FullFileName);
                                                TrgFileClient.DeleteIfExists();
                                                TrgFileClient.CreateIfNotExists();

                                                if (sp.trgFiletype == "csv")
                                                {
                                                    bool hasData = false;
                                                    using (Stream dataLakeStream = TrgFileClient.OpenWrite(true))
                                                    using (StreamWriter csvFile = new StreamWriter(dataLakeStream, Encoding.UTF8))
                                                    using (var taskSqlCon = new SqlConnection(sp.srcConnectionString))
                                                    {
                                                        taskSqlCon.Open();
                                                        using (var command = new SqlCommand(_item.SqlCMD, taskSqlCon))
                                                        using (var reader = command.ExecuteReader())
                                                        {
                                                            if (reader.HasRows)
                                                            {
                                                                hasData = true;
                                                                var schemaTable = reader.GetSchemaTable();
                                                                int[] strColIndex = CommonDB.GetStrColIndexFromSchTlb(schemaTable);
                                                                var columnNames = schemaTable?.Rows.OfType<DataRow>().Select(r => r["ColumnName"]?.ToString() ?? string.Empty).ToList() ?? new List<string>();

                                                                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                                                                {
                                                                    HasHeaderRecord = true,
                                                                    Delimiter = ";",
                                                                    ShouldQuote = args => args.Row.Row > 1 && strColIndex.Contains(args.Row.Index)
                                                                };

                                                                using (var xWriter = new CsvWriter(csvFile, config))
                                                                {
                                                                    foreach (var columnName in columnNames)
                                                                    {
                                                                        xWriter.WriteField(columnName);
                                                                    }
                                                                    xWriter.NextRecord();

                                                                    int RowCounter = 0;
                                                                    while (reader.Read())
                                                                    {
                                                                        for (int i = 0; i < reader.FieldCount; i++)
                                                                        {
                                                                            xWriter.WriteField(reader.GetValue(i));
                                                                        }
                                                                        xWriter.NextRecord();
                                                                        RowCounter++;
                                                                    }
                                                                    _Rowcount += RowCounter;
                                                                    sp.logFetched = _Rowcount;
                                                                    sp.logInserted = _Rowcount;
                                                                    xWriter.Flush();
                                                                }
                                                            }
                                                        }
                                                    }
                                                    if (!hasData)
                                                    {
                                                        TrgFileClient.DeleteIfExists();
                                                    }
                                                }
                                                else if (sp.trgFiletype == "parquet")
                                                {
                                                    bool hasData = false;
                                                    using (Stream dataLakeStream = TrgFileClient.OpenWrite(true))
                                                    using (var taskSqlCon = new SqlConnection(sp.srcConnectionString))
                                                    {
                                                        taskSqlCon.Open();
                                                        var localDataTable = srcDataTable.Clone();
                                                        var schemaX = BuildSchema(localDataTable);

                                                        using (var command = new SqlCommand(_item.SqlCMD, taskSqlCon))
                                                        using (var reader = command.ExecuteReader())
                                                        {
                                                            if (reader.HasRows)
                                                            {
                                                                hasData = true;
                                                                ParquetOptions parquetOptions = new ParquetOptions();
                                                                using (ParquetWriter writer = await ParquetWriter.CreateAsync(schemaX, dataLakeStream, parquetOptions, false))
                                                                {
                                                                    writer.CompressionMethod = ParseCompressionType(sp.compressionType);
                                                                    int bufferSize = 100000;
                                                                    DataField[] df = schemaX.DataFields;
                                                                    int Counter = 0;

                                                                    while (reader.Read())
                                                                    {
                                                                        DataRow rTbl = localDataTable.NewRow();
                                                                        for (int i = 0; i < reader.FieldCount; i++)
                                                                        {
                                                                            if (!reader.IsDBNull(i))
                                                                            {
                                                                                rTbl[i] = reader.GetValue(i);
                                                                            }
                                                                        }
                                                                        localDataTable.Rows.Add(rTbl);
                                                                        Counter++;

                                                                        if (localDataTable.Rows.Count >= bufferSize)
                                                                        {
                                                                            using (ParquetRowGroupWriter groupWriter = writer.CreateRowGroup())
                                                                            {
                                                                                foreach (DataColumn column in localDataTable.Columns)
                                                                                {
                                                                                    Array dataArray = Array.CreateInstance(df[column.Ordinal].ClrNullableIfHasNullsType, localDataTable.Rows.Count);
                                                                                    for (int rowIndex = 0; rowIndex < localDataTable.Rows.Count; rowIndex++)
                                                                                    {
                                                                                        if (localDataTable.Rows[rowIndex][column] != DBNull.Value)
                                                                                        {
                                                                                            dataArray.SetValue(localDataTable.Rows[rowIndex][column], rowIndex);
                                                                                        }
                                                                                    }
                                                                                    Parquet.Data.DataColumn dc = new Parquet.Data.DataColumn(df[column.Ordinal], dataArray);
                                                                                    await groupWriter.WriteColumnAsync(dc);
                                                                                }
                                                                            }
                                                                            localDataTable.Clear();
                                                                        }
                                                                    }

                                                                    _Rowcount += Counter + localDataTable.Rows.Count;
                                                                    sp.logFetched = _Rowcount;
                                                                    sp.logInserted = _Rowcount;

                                                                    if (localDataTable.Rows.Count > 0)
                                                                    {
                                                                        using (ParquetRowGroupWriter groupWriter = writer.CreateRowGroup())
                                                                        {
                                                                            foreach (DataColumn column in localDataTable.Columns)
                                                                            {
                                                                                Array dataArray = Array.CreateInstance(df[column.Ordinal].ClrNullableIfHasNullsType, localDataTable.Rows.Count);
                                                                                for (int rowIndex = 0; rowIndex < localDataTable.Rows.Count; rowIndex++)
                                                                                {
                                                                                    if (localDataTable.Rows[rowIndex][column] != DBNull.Value)
                                                                                    {
                                                                                        dataArray.SetValue(localDataTable.Rows[rowIndex][column], rowIndex);
                                                                                    }
                                                                                }
                                                                                Parquet.Data.DataColumn dc = new Parquet.Data.DataColumn(df[column.Ordinal], dataArray);
                                                                                await groupWriter.WriteColumnAsync(dc);
                                                                            }
                                                                        }
                                                                        localDataTable.Clear();
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    if (!hasData)
                                                    {
                                                        TrgFileClient.DeleteIfExists();
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            sp.logErrorRuntime = e.Message;
                                            if (_OnErrorResume == false)
                                            {
                                                throw;
                                            }
                                        }
                                        finally
                                        {
                                            _fileCreationTracker.TryRemove(_FullFileName, out _);
                                            concurrencySemaphore.Release();
                                        }

                                    }, TaskCreationOptions.LongRunning).Unwrap();

                                    tUnwrapped.ContinueWith(_ =>
                                    {
                                        SubExportCompleted(_item, _FlowID, _Batch, _SysAlias, _OnErrorResume, _Total, _FullFileName, _logStack, _Rowcount, sqlFlowParam.sqlFlowConString, _FileSize);
                                    }, TaskContinuationOptions.OnlyOnRanToCompletion);

                                    tasks.Add(tUnwrapped);
                                }
                            }
                            else
                            {
                                // Local filesystem path
                                int _Rowcount = 0;
                                foreach (var item in SrcBatched)
                                {
                                    ExpSegment _item = item.Value;
                                    string _FullFileName = _item.GetFileNameWithPath();
                                    bool _OnErrorResume = sp.OnErrorResume;
                                    string _SysAlias = sp.SysAlias;
                                    string _Batch = sp.Batch;
                                    int _FlowID = sqlFlowParam.flowId;
                                    var _logStack = logger;
                                    long _FileSize = 0;

                                    // FIX #3 repeated for local file system
                                    var tUnwrapped = Task.Factory.StartNew(async () =>
                                    {
                                        concurrencySemaphore.WaitOne();
                                        try
                                        {
                                            if (!_fileCreationTracker.TryAdd(_FullFileName, 0))
                                            {
                                                _logStack.LogInformation($"Skipping duplicate file creation for {_FullFileName}");
                                                return;
                                            }

                                            if (sp.ProcessNext)
                                            {
                                                // <-- ADDED: Ensure local directory exists
                                                string localDir = Path.GetDirectoryName(_FullFileName);
                                                if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                                                {
                                                    Directory.CreateDirectory(localDir);
                                                }

                                                var execTime = new Stopwatch();
                                                execTime.Start();

                                                if (sp.trgFiletype == "csv")
                                                {
                                                    bool hasData = false;
                                                    using (var streamWriter = new StreamWriter(_item.GetFileNameWithPath()))
                                                    using (var taskSqlCon = new SqlConnection(sp.srcConnectionString))
                                                    {
                                                        taskSqlCon.Open();
                                                        using (var command = new SqlCommand(_item.SqlCMD, taskSqlCon) { CommandTimeout = sp.bulkLoadTimeoutInSek })
                                                        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                                                        {
                                                            if (reader.HasRows)
                                                            {
                                                                hasData = true;
                                                                var schemaTable = reader.GetSchemaTable();
                                                                int[] strColIndex = CommonDB.GetStrColIndexFromSchTlb(schemaTable);
                                                                var columnNames = schemaTable?.Rows.OfType<DataRow>().Select(r => r["ColumnName"]?.ToString() ?? string.Empty).ToList() ?? new List<string>();

                                                                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                                                                {
                                                                    HasHeaderRecord = true,
                                                                    Delimiter = ";",
                                                                    ShouldQuote = args => args.Row.Row > 1 && strColIndex.Contains(args.Row.Index)
                                                                };

                                                                using (var xWriter = new CsvWriter(streamWriter, config))
                                                                {
                                                                    foreach (var columnName in columnNames)
                                                                    {
                                                                        xWriter.WriteField(columnName);
                                                                    }
                                                                    xWriter.NextRecord();

                                                                    int RowCounter = 1;
                                                                    while (reader.Read())
                                                                    {
                                                                        for (int i = 0; i < reader.FieldCount; i++)
                                                                        {
                                                                            xWriter.WriteField(reader.GetValue(i));
                                                                        }
                                                                        xWriter.NextRecord();
                                                                        RowCounter++;
                                                                    }

                                                                    _Rowcount += RowCounter;
                                                                    sp.logFetched = _Rowcount;
                                                                    sp.logInserted = _Rowcount;
                                                                    xWriter.Flush();
                                                                }
                                                            }
                                                        }
                                                    }
                                                    if (!hasData)
                                                    {
                                                        File.Delete(_item.GetFileNameWithPath());
                                                    }
                                                }
                                                else if (sp.trgFiletype == "parquet")
                                                {
                                                    bool hasData = false;
                                                    using (Stream dataLakeStream = new FileStream(_item.GetFileNameWithPath(), System.IO.FileMode.OpenOrCreate))
                                                    using (var taskSqlCon = new SqlConnection(sp.srcConnectionString))
                                                    {
                                                        taskSqlCon.Open();
                                                        var localDataTable = srcDataTable.Clone();
                                                        var schemaX = BuildSchema(localDataTable);

                                                        using (var command = new SqlCommand(_item.SqlCMD, taskSqlCon))
                                                        using (var reader = command.ExecuteReader())
                                                        {
                                                            if (reader.HasRows)
                                                            {
                                                                hasData = true;
                                                                ParquetOptions parquetOptions = new ParquetOptions();
                                                                using (ParquetWriter writer = await ParquetWriter.CreateAsync(schemaX, dataLakeStream, parquetOptions, false))
                                                                {
                                                                    writer.CompressionMethod = ParseCompressionType(sp.compressionType);
                                                                    int bufferSize = 100000;
                                                                    DataField[] df = schemaX.DataFields;
                                                                    int Counter = 0;

                                                                    while (reader.Read())
                                                                    {
                                                                        DataRow rTbl = localDataTable.NewRow();
                                                                        for (int i = 0; i < reader.FieldCount; i++)
                                                                        {
                                                                            if (!reader.IsDBNull(i))
                                                                            {
                                                                                rTbl[i] = reader.GetValue(i);
                                                                            }
                                                                        }
                                                                        localDataTable.Rows.Add(rTbl);
                                                                        Counter++;

                                                                        if (localDataTable.Rows.Count >= bufferSize)
                                                                        {
                                                                            using (ParquetRowGroupWriter groupWriter = writer.CreateRowGroup())
                                                                            {
                                                                                foreach (DataColumn column in localDataTable.Columns)
                                                                                {
                                                                                    Array dataArray = Array.CreateInstance(df[column.Ordinal].ClrNullableIfHasNullsType, localDataTable.Rows.Count);
                                                                                    for (int rowIndex = 0; rowIndex < localDataTable.Rows.Count; rowIndex++)
                                                                                    {
                                                                                        if (localDataTable.Rows[rowIndex][column] != DBNull.Value)
                                                                                        {
                                                                                            dataArray.SetValue(localDataTable.Rows[rowIndex][column], rowIndex);
                                                                                        }
                                                                                    }
                                                                                    Parquet.Data.DataColumn dc = new Parquet.Data.DataColumn(df[column.Ordinal], dataArray);
                                                                                    await groupWriter.WriteColumnAsync(dc);
                                                                                }
                                                                            }
                                                                            localDataTable.Clear();
                                                                        }
                                                                    }

                                                                    _Rowcount += Counter + localDataTable.Rows.Count;
                                                                    sp.logFetched = _Rowcount;
                                                                    sp.logInserted = _Rowcount;

                                                                    if (localDataTable.Rows.Count > 0)
                                                                    {
                                                                        using (ParquetRowGroupWriter groupWriter = writer.CreateRowGroup())
                                                                        {
                                                                            foreach (DataColumn column in localDataTable.Columns)
                                                                            {
                                                                                Array dataArray = Array.CreateInstance(df[column.Ordinal].ClrNullableIfHasNullsType, localDataTable.Rows.Count);
                                                                                for (int rowIndex = 0; rowIndex < localDataTable.Rows.Count; rowIndex++)
                                                                                {
                                                                                    if (localDataTable.Rows[rowIndex][column] != DBNull.Value)
                                                                                    {
                                                                                        dataArray.SetValue(localDataTable.Rows[rowIndex][column], rowIndex);
                                                                                    }
                                                                                }
                                                                                Parquet.Data.DataColumn dc = new Parquet.Data.DataColumn(df[column.Ordinal], dataArray);
                                                                                await groupWriter.WriteColumnAsync(dc);
                                                                            }
                                                                        }
                                                                        localDataTable.Clear();
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    if (!hasData)
                                                    {
                                                        File.Delete(_item.GetFileNameWithPath());
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            sp.logErrorRuntime = e.Message;
                                            if (_OnErrorResume == false)
                                            {
                                                throw;
                                            }
                                        }
                                        finally
                                        {
                                            _fileCreationTracker.TryRemove(_FullFileName, out _);
                                            concurrencySemaphore.Release();
                                        }

                                    }, TaskCreationOptions.LongRunning).Unwrap();

                                    tUnwrapped.ContinueWith(_ =>
                                    {
                                        SubExportCompleted(_item, _FlowID, _Batch, _SysAlias, _OnErrorResume, _Total, _FullFileName, _logStack, _Rowcount, sqlFlowParam.sqlFlowConString, _FileSize);
                                    }, TaskContinuationOptions.OnlyOnRanToCompletion);

                                    tasks.Add(tUnwrapped);
                                }
                            }

                            try
                            {
                                Task.WaitAll(tasks.ToArray());
                            }
                            catch (AggregateException ae)
                            {
                                foreach (var innerException in ae.InnerExceptions)
                                {
                                    sp.logErrorRuntime += innerException.Message + Environment.NewLine;
                                }
                            }
                        }

                        logger.LogInformation($"PreProcess executed on target ({sp.logDurationPre.ToString()} sec)");
                        logger.LogInformation($"Total processing time ({sp.logDurationFlow.ToString()} sec)");

                        Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                        if (srcSqlCon.State == ConnectionState.Open)
                        {
                            srcSqlCon.Close();
                            srcSqlCon.Dispose();
                            SqlConnection.ClearPool(srcSqlCon);
                        }

                        if (sqlFlowCon.State == ConnectionState.Open)
                        {
                            sqlFlowCon.Close();
                            sqlFlowCon.Dispose();
                            SqlConnection.ClearPool(sqlFlowCon);
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
                    try
                    {
                        if (srcSqlCon != null && srcSqlCon.State == ConnectionState.Open)
                        {
                            srcSqlCon.Close();
                            srcSqlCon.Dispose();
                            SqlConnection.ClearPool(srcSqlCon);
                        }
                    }
                    catch { /* ignore */ }

                    try
                    {
                        if (sqlFlowCon != null && sqlFlowCon.State == ConnectionState.Open)
                        {
                            sqlFlowCon.Close();
                            sqlFlowCon.Dispose();
                            SqlConnection.ClearPool(sqlFlowCon);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            return sp.result;
        }
        #endregion ExecExport

        static void SubExportCompleted(
            ExpSegment _item,
            int _FlowID,
            string _Batch,
            string _SysAlias,
            bool _OnErrorResume,
            int _Total,
            string _FullFileName,
            RealTimeLogger _logger,
            int _Rowcount,
            string sqlFlowConString,
            long _FileSize)
        {
            _logger.LogInformation($"Exported {_Rowcount} rows to {_FullFileName}{Environment.NewLine}");

            Interlocked.Increment(ref _sharedCounter);
            double Status = _sharedCounter / (double)_Total;

            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                sqlFlowCon.Open();
                using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogExport]", sqlFlowCon))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.Add("@BatchID", SqlDbType.VarChar).Value = _Batch;
                    cmd.Parameters.Add("@FlowID", SqlDbType.VarChar).Value = _FlowID;
                    cmd.Parameters.Add("@SqlCMD", SqlDbType.VarChar).Value = _item.SqlCMD;
                    cmd.Parameters.Add("@WhereClause", SqlDbType.VarChar).Value = _item.WhereClause;
                    cmd.Parameters.Add("@FilePath_DW", SqlDbType.VarChar).Value = _item.FilePath_DW;
                    cmd.Parameters.Add("@FileName_DW", SqlDbType.VarChar).Value = _item.FileName_DW;
                    cmd.Parameters.Add("@FileSize_DW", SqlDbType.Decimal).Value = _FileSize;
                    cmd.Parameters.Add("@FileRows_DW", SqlDbType.Int).Value = _Rowcount;
                    if (_item.NextExportDate != FlowDates.Default)
                    {
                        cmd.Parameters.Add("@NextExportDate", SqlDbType.DateTime).Value = _item.NextExportDate.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    }
                    if (_item.NextExportValue > 0)
                    {
                        cmd.Parameters.Add("@NextExportValue", SqlDbType.Int).Value = _item.NextExportValue;
                    }
                    cmd.ExecuteNonQuery();
                }
            }

            EventArgsExport arg = new EventArgsExport
            {
                FullFileName = _FullFileName + " " + Thread.CurrentThread.ManagedThreadId,
                Batch = _Batch,
                FlowID = _FlowID,
                FlowType = "exp",
                SysAlias = _SysAlias,
                OnErrorResume = _OnErrorResume,
                InTotal = _Total,
                InQueue = _Total - _sharedCounter,
                Processed = _sharedCounter,
                Status = Status,
                Rowcount = _Rowcount
            };
            OnFileExported?.Invoke(Thread.CurrentThread, arg);
        }

        private static ParquetSchema BuildSchema(DataTable dataTable)
        {
            var fields = new List<Field>();
            foreach (DataColumn col in dataTable.Columns)
            {
                DataField df = GetParquetField(col);
                fields.Add(df);
            }
            return new ParquetSchema(fields);
        }

        private static DataField GetParquetField(DataColumn column)
        {
            return new DataField(column.ColumnName, column.DataType, isNullable: true);
        }

        private static CompressionMethod ParseCompressionType(string compressionType)
        {
            CompressionMethod compressionMethod = CompressionMethod.Gzip;
            if (compressionType.Equals("None", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.None;
            else if (compressionType.Equals("Gzip", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Gzip;
            else if (compressionType.Equals("Snappy", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Snappy;
            else if (compressionType.Equals("Lzo", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Lzo;
            else if (compressionType.Equals("Brotli", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Brotli;
            else if (compressionType.Equals("LZ4", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.LZ4;
            else if (compressionType.Equals("Lz4Raw", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Lz4Raw;
            else if (compressionType.Equals("Zstd", StringComparison.InvariantCultureIgnoreCase))
                compressionMethod = CompressionMethod.Zstd;
            else
                compressionMethod = CompressionMethod.Gzip;

            return compressionMethod;
        }

        private static object MapColType(object inputObject)
        {
            var outputObject = inputObject;
            if (inputObject == DBNull.Value) outputObject = null;
            if (inputObject is DateTime time) outputObject = new DateTimeOffset(time, new TimeSpan(0, 0, 0));
            return outputObject;
        }

        private static async Task WriteToParquet(ParquetWriter parquetWriter, DataField[] df, DataTable bufferTable)
        {
            using (ParquetRowGroupWriter groupWriter = parquetWriter.CreateRowGroup())
            {
                foreach (DataColumn column in bufferTable.Columns)
                {
                    Array dataArray = Array.CreateInstance(df[column.Ordinal].ClrNullableIfHasNullsType, bufferTable.Rows.Count);
                    for (int i = 0; i < bufferTable.Rows.Count; i++)
                    {
                        if (bufferTable.Rows[i][column] != DBNull.Value)
                        {
                            dataArray.SetValue(bufferTable.Rows[i][column], i);
                        }
                    }
                    Parquet.Data.DataColumn dc = new Parquet.Data.DataColumn(df[column.Ordinal], dataArray);
                    await groupWriter.WriteColumnAsync(dc);
                }
            }
        }

        internal static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        private static string CodeStackSection(string header, string tsql)
        {
            var tempStr = new StringBuilder();
            if (tsql != null)
                tempStr.AppendFormat(
                    "{0}--################################### {1} ###################################{0}{2}{0}",
                    Environment.NewLine, header, tsql);
            return tempStr.ToString();
        }
    }
}
