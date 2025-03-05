using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using CsvHelper.Configuration.Attributes;
using SQLFlowCore.Common;
using SQLFlowCore.Services.Xls;

namespace SQLFlowCore.Services
{
    public sealed class ServiceParam
    {
        private static readonly ThreadLocal<ServiceParam> _threadInstance =
            new ThreadLocal<ServiceParam>(() => new ServiceParam());

        /// <summary>
        /// Returns the thread-local instance of <see cref="ServiceParam"/>.
        /// </summary>
        public static ServiceParam Current => _threadInstance.Value;

        // --------------------------------------------------------------------------------
        // All fields as specified (keeping the same case).
        // --------------------------------------------------------------------------------
        public bool Success = false;
        public string srcConString = "";
        public string trgConString = "";
        public string srcDatabase = "";
        public string srcSchema = "";
        public string srcObject = "";
        public string stgSchema = "";
        public string trgDatabase = "";
        public string trgSchema = "";
        public string trgObject = "";
        public string keyColumns = "";
        public string dateColumn = "";
        public string dataSetColumn = "";
        public bool syncSchema = false;
        public string ReplaceInvalidCharsWith = "";
        public bool onSyncCleanColumnName = false;
        public bool onSyncConvertUnicodeDataType = false;
        public bool streamData = false;
        public int noOfOverlapDays = 0;
        public int bulkLoadTimeoutInSek = 0;
        public int generalTimeoutInSek = 0;
        public int bulkLoadBatchSize = 0;
        public int maxRetry = 0;
        public int retryDelayMs = 0;
        public string preProcessOnTrg = "";
        public string postProcessOnTrg = "";
        public string colCleanupSqlRegExp = "";
        public string ignoreColumns = "";
        public bool tokenize = false;
        public string srcFilter = "";
        public bool trgVersioning = false;
        public string sysAlias = "";
        public string cmdSchema = "";
        public bool fullload = false;
        public bool srcIsSynapse = false;
        public bool trgIsSynapse = false;
        public bool truncateTrg = false;
        public bool truncatePreTableOnCompletion = false;
        public string incrementalColumns = "";
        public int noOfThreads = 1;
        public string srcWithHint = "";
        public string trgWithHint = "";
        public string flowBatch = "";
        public bool onErrorResume = true;
        public string identityColumn = "";
        public bool skipUpdateExsisting = false;
        public bool skipInsertNew = false;
        public bool columnStoreIndexOnTrg = false;
        public bool FetchMinValuesFromSrc = true;
        public bool srcFilterIsAppend = true;
        public bool InsertUnknownDimRow = false;
        public bool UseBatchUpsertToAvoideLockEscalation = false;
        public int BatchUpsertRowCount = 0;
        public string srcTenantId = string.Empty;
        public string srcSubscriptionId = string.Empty;
        public string srcApplicationId = string.Empty;
        public string srcClientSecret = string.Empty;
        public string srcKeyVaultName = string.Empty;
        public string srcSecretName = string.Empty;
        public string srcResourceGroup = string.Empty;
        public string srcDataFactoryName = string.Empty;
        public string srcAutomationAccountName = string.Empty;
        public string srcStorageAccountName = string.Empty;
        public string srcBlobContainer = string.Empty;
        public string trgTenantId = string.Empty;
        public string trgSubscriptionId = string.Empty;
        public string trgApplicationId = string.Empty;
        public string trgClientSecret = string.Empty;
        public string trgKeyVaultName = string.Empty;
        public string trgSecretName = string.Empty;
        public string trgResourceGroup = string.Empty;
        public string trgDataFactoryName = string.Empty;
        public string trgAutomationAccountName = string.Empty;
        public string trgStorageAccountName = string.Empty;
        public string trgBlobContainer = string.Empty;
        public string trgDesiredIndex = "";
        public bool InitLoad = false;
        public DateTime InitLoadFromDate = DateTime.Now;
        public DateTime InitLoadToDate = DateTime.Now;
        public string InitLoadBatchBy = "M";
        public int InitLoadBatchSize = 1;
        public int InitLoadKeyMaxValue = 0;
        public string InitLoadKeyColumn = "";
        public string IncrementalClauseExp = "";
        public string HashKeyColumns = "";
        public string HashKeyType = "";
        public string DataType = "";
        public string DataTypeExp = "";
        public string IgnoreColumnsInHashkey = "";
        public bool MatchKeysInSrcTrg = false;
        // The following are for runtime in your code, you can keep them or adjust as needed:
        public string whereKeyExp = "";
        public string whereIncExp = "";
        public string whereDateExp = "";
        public string whereXML = "";
        public string cmdMax = "";
        public string cmdMin = "";
        public string DataTypeWarning = "";
        public string ColumnWarning = "";
        public int srcRowCount = 0;
        public long logFetched = 0;
        public long logInserted = 0;
        public long logUpdated = 0;
        public long logDeleted = 0;
        public string result = "false";

        public string srcPath = "";
        public string srcFile = "";
        public string srcEncoding = "";
        public bool srcDeleteIngested = false;
        public string srcParserCsv = "";

        public bool firstRowHasHeader = false;
        public bool searchSubDirectories = false;
        public long fileDate = 0;

        public string defaultColDataType = "";
        public string viewCmd = "";
        public string viewSelect = "";

        public bool showPathWithFileName = false;
        public string srcPathMask = "";
        public int expectedColumnCount = 0;
        public bool fetchDataTypes = false;
        public bool preIngTransStatus = false;

        public string processedFileList = "";
        public string trgIndexes = "";

        public DateTime createDateTime = DateTime.Now;
        public DateTime modifiedDateTime = DateTime.Now;

        public long logDurationFlow = 0;
        public long logDurationPre = 0;
        public long logDurationPost = 0;
        public long logLength = 0;
        public long logFileReadTime = 0;

        public string logSelectCmd = "";
        public string logRuntimeCmd = "";
        public string logCreateCmd = "";
        public string logErrorInsert = "";
        public string logErrorUpdate = "";
        public string logErrorDelete = "";
        public string logErrorRuntime = "";

        public string execViewCmd = "";
        public string copyToPath = "";

        public string InferDatatypeCmd = "";
        public string Indexes = "";

        public string cmdAlterSQL = "";
        public string cmdCreate = "";
        public string tfColList = "";
        public string currentViewCMD = "";
        public string currentViewSelect = "";

        public string trgDbSchTbl = "";

        public long InitFromFileDate = 0;
        public long InitToFileDate = 0;

        public DateTime logStartTime = DateTime.Now;  // if needed
        public DateTime logEndTime = DateTime.Now;  // if needed

        public string logInsertCmd = "";
        public string logUpdateCmd = "";
        public string logSurrogateKeyCmd = "";

        public bool targetExsits = false;

        public bool altTrgIsEmbedded = false;
        public string logFileDate = "";
        public string JsonToDataTableCode = "";

        public string colCountErrorMsg = "";
        public bool colCountError = false;

        public int FileCounter = 0;
        public Encoding Encoding = Encoding.Default;
        
        public SearchOption srchOption = SearchOption.TopDirectoryOnly;


        public string sheetName = "";
        public string sheetRange = "";
        public bool useSheetIndex = false;
        public bool IncludeFileLineNumber = false;


        public string hierarchyIdentifier = string.Empty;
        public string XmlToDataTableCode = "";

        public string ParamName = string.Empty; 
        public string ParamSelectExp = string.Empty;
        public string ParamConnectionString = string.Empty;
        public string SelectExp = string.Empty;
        public string trgDBSchSP = string.Empty;

        public string SourceType = string.Empty;
        public string Defaultvalue = string.Empty;
        public bool PreFetch = false;

        public string trgConpublic  = string.Empty;
        public string partitionList = string.Empty;
        public string trgWithHpublic  = string.Empty;

        
        public string logFileName = "";
        public string dlTenantId = string.Empty;
        public string dlSubscriptionId = string.Empty;
        public string dlApplicationId = string.Empty;
        public string dlClientSecret = string.Empty;
        public string dlKeyVaultName = string.Empty;
        public string dlSecretName = string.Empty;
        public string dlResourceGroup = string.Empty;
        public string dlDataFactoryName = string.Empty;
        public string dlAutomationAccountName = string.Empty;
        public string dlStorageAccountName = string.Empty;
        public string dlBlobContainer = string.Empty;


        public string invokeType = "";
        public string invokeAlias = "";
        public string invokePath = "";
        public string invokeFile = "";
        public string code = "";
        public string arguments = "";

        public string trgPipelineName = "";
        public string trgRunbookName = "";
        public string trgParameterJSON = "";
        public string srcPipelineName = "";
        public string srcRunbookName = "";
        public string srcParameterJSON = "";


        public string srcDSType = "";
        public string RemoveInColumnName = "";
        public bool FetchMinValuesFromSysLog = false;

        public string tmpSchema = "";
        public string _whereIncExp = "";
        public string _whereDateExp = "";
        public string _whereXML = "";

        //export
        public string SysAlias = "";
        public string Batch = "";
        public string srcConnectionString = "";
        public string srcDBSchTbl = "";
        public SQLObject srcDBSchObj = new SQLObject();

        public int NoOfOverlapDays = 1;
        public string IncrementalColumn = "";
        public string DateColumn = "";
        public DateTime FromDate = DateTime.Now;
        public DateTime ToDate = DateTime.Now;
        public string ExportBy = "";
        public int ExportSize = 1;
        public string trgPath = "";
        public string trgFileName = "";
        public string trgFiletype = "";
        public bool OnErrorResume = true;
        public int NoOfThreads = 1;
        public string compressionType = "Gzip";
        public string FileList = "";
        public bool ProcessNext = true;
        public bool AddTimeStampToFileName = false;
        public string Subfolderpattern = "";

        public DataTable DateTimeFormats { get; set; }

        public DateTime logNextExportDate = FlowDates.Default;
        public int logNextExportValue = 0;

        public DateTime NextExportDate = FlowDates.Default;
        public int NextExportValue = 0;

        public IncObject MinXML = new IncObject();
        public IncObject MaxXML = new IncObject();

        // --------------------------------------------------------------------------------
        // Private constructor to prevent instantiation outside this class.
        // --------------------------------------------------------------------------------
        private ServiceParam()
        {
        }

        // --------------------------------------------------------------------------------
        // Initialize all fields from the first row of the provided paramTbl (if columns exist).
        // Make sure paramTbl is not null or empty before calling.
        // --------------------------------------------------------------------------------
        public void Initialize(DataTable paramTbl)
        {
            if (paramTbl == null || paramTbl.Rows.Count == 0)
            {
                throw new ArgumentException("paramTbl is null or has no rows.");
            }

            // Short helper to check for column existence:
            bool HasColumn(string columnName) => paramTbl.Columns.Contains(columnName);

            // Row 0
            var row = paramTbl.Rows[0];

            // Helper for reading a column as string safely.
            string GetString(string colName) =>
                HasColumn(colName) ? row[colName]?.ToString() ?? string.Empty : string.Empty;

            // Helper for reading bool: only "True" -> true, else false
            bool GetBool(string colName) =>
                HasColumn(colName) &&
                (string.Equals(row[colName]?.ToString() ?? string.Empty, "True", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(row[colName]?.ToString() ?? string.Empty, "1", StringComparison.OrdinalIgnoreCase));

            DateTime GetDateTime(string paramName)
            {
                return HasColumn(paramName)
                    ? Functions.ParseToDateTime(paramTbl.Rows[0][paramName].ToString(), DateTimeFormats)
                    : FlowDates.Default;
            }
            
            // Helper for reading int: parse or default to 0 if empty
            int GetInt(string colName)
            {
                if (!HasColumn(colName)) return 0;
                var strVal = row[colName]?.ToString() ?? string.Empty;
                return string.IsNullOrWhiteSpace(strVal) ? 0 : int.Parse(strVal);
            }

            // Helper for reading DateTime with the "yyyy-MM-dd" format
            DateTime GetDate(string colName)
            {
                if (!HasColumn(colName)) return DateTime.MinValue;
                var strVal = row[colName]?.ToString() ?? string.Empty;
                return DateTime.ParseExact(strVal, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            // Now assign all fields:

            // AAD / Vault / etc
            srcTenantId = GetString("srcTenantId");
            srcApplicationId = GetString("srcApplicationId");
            srcClientSecret = GetString("srcClientSecret");
            srcKeyVaultName = GetString("srcKeyVaultName");
            srcSecretName = GetString("srcSecretName");
            srcStorageAccountName = GetString("srcStorageAccountName");
            srcBlobContainer = GetString("srcBlobContainer");

            trgTenantId = GetString("trgTenantId");
            trgApplicationId = GetString("trgApplicationId");
            trgClientSecret = GetString("trgClientSecret");
            trgKeyVaultName = GetString("trgKeyVaultName");
            trgSecretName = GetString("trgSecretName");
            trgStorageAccountName = GetString("trgStorageAccountName");
            trgBlobContainer = GetString("trgBlobContainer");

            // Connection / schema / object info
            srcConString = GetString("SrcConString");
            trgConString = GetString("TrgConString");
            srcDatabase = GetString("srcDatabase");
            srcSchema = GetString("srcSchema");
            srcObject = GetString("srcObject");
            stgSchema = GetString("stgSchema");
            trgDatabase = GetString("trgDatabase");
            trgSchema = GetString("trgSchema");
            trgObject = GetString("trgObject");
            keyColumns = GetString("KeyColumns");
            dateColumn = GetString("DateColumn");
            dataSetColumn = GetString("DataSetColumn");

            syncSchema = GetBool("SyncSchema");
            ReplaceInvalidCharsWith = GetString("ReplaceInvalidCharsWith");
            onSyncCleanColumnName = GetBool("OnSyncCleanColumnName");
            onSyncConvertUnicodeDataType = GetBool("OnSyncConvertUnicodeDataType");
            streamData = GetBool("StreamData");
            noOfOverlapDays = GetInt("NoOfOverlapDays");
            bulkLoadTimeoutInSek = GetInt("BulkLoadTimeoutInSek");
            generalTimeoutInSek = GetInt("GeneralTimeoutInSek");
            bulkLoadBatchSize = GetInt("BulkLoadBatchSize");

            maxRetry = GetInt("MaxRetry");
            retryDelayMs = GetInt("RetryDelayMS");
            preProcessOnTrg = GetString("PreProcessOnTrg");
            postProcessOnTrg = GetString("PostProcessOnTrg");
            colCleanupSqlRegExp = GetString("ColCleanupSQLRegExp");
            ignoreColumns = GetString("IgnoreColumns");
            tokenize = GetBool("Tokenize");
            srcFilter = GetString("srcFilter");
            trgVersioning = GetBool("trgVersioning");

            sysAlias = GetString("SysAlias");
            cmdSchema = GetString("cmdSchema");

            fullload = GetBool("Fullload");
            srcIsSynapse = GetBool("srcIsSynapse");
            trgIsSynapse = GetBool("trgIsSynapse");

            truncateTrg = GetBool("truncateTrg");
            truncatePreTableOnCompletion = GetBool("truncatePreTableOnCompletion");
            InsertUnknownDimRow = GetBool("InsertUnknownDimRow");

            incrementalColumns = GetString("IncrementalColumns");
            noOfThreads = GetInt("NoOfThreads");
            flowBatch = GetString("Batch");  // from user snippet
            identityColumn = GetString("IdentityColumn");

            HashKeyType = GetString("HashKeyType");
            DataType = GetString("DataType");
            DataTypeExp = GetString("DataTypeExp");
            trgDesiredIndex = GetString("trgDesiredIndex");

            UseBatchUpsertToAvoideLockEscalation = GetBool("UseBatchUpsertToAvoideLockEscalation");
            BatchUpsertRowCount = GetInt("BatchUpsertRowCount");

            InitLoad = GetBool("InitLoad");
            InitLoadFromDate = GetDate("InitLoadFromDate");
            InitLoadToDate = GetDate("InitLoadToDate");
            InitLoadBatchBy = GetString("InitLoadBatchBy");
            InitLoadBatchSize = GetInt("InitLoadBatchSize");
            InitLoadKeyMaxValue = GetInt("InitLoadKeyMaxValue");
            InitLoadKeyColumn = GetString("InitLoadKeyColumn");

            HashKeyColumns = GetString("HashKeyColumns");
            skipUpdateExsisting = GetBool("SkipUpdateExsisting");
            skipInsertNew = GetBool("skipInsertNew");
            onErrorResume = GetBool("onErrorResume");
            columnStoreIndexOnTrg = GetBool("ColumnStoreIndexOnTrg");
            FetchMinValuesFromSrc = GetBool("FetchMinValuesFromSrc");
            srcFilterIsAppend = GetBool("srcFilterIsAppend");
            IncrementalClauseExp = GetString("IncrementalClauseExp");
            IgnoreColumnsInHashkey = GetString("IgnoreColumnsInHashkey");

            // Hints (depends on whether source/target are Synapse)
            srcWithHint = srcIsSynapse ? "nolock" : "readpast";
            trgWithHint = trgIsSynapse ? "nolock" : "readpast";

            MatchKeysInSrcTrg = GetBool("MatchKeysInSrcTrg");

            // Only if the column exists, read it as string:
            srcPath = GetString("srcPath");
            srcFile = GetString("srcFile");
            srcEncoding = GetString("srcEncoding");
            srcDeleteIngested = GetBool("srcDeleteIngested");

            // NOTE: your paramTbl column name is "srcParserXML" but your variable is "srcParserCsv":
            srcParserCsv = GetString("srcParserXML");

            firstRowHasHeader = GetBool("FirstRowHasHeader");
            searchSubDirectories = GetBool("SearchSubDirectories");

            // fileDate is stored as a long:
            if (HasColumn("FileDate"))
            {
                var strFileDate = row["FileDate"]?.ToString() ?? "0";
                fileDate = long.Parse(strFileDate);
            }

            defaultColDataType = GetString("DefaultColDataType");
            viewCmd = GetString("ViewCMD");
            viewSelect = GetString("ViewSelect");
            showPathWithFileName = GetBool("ShowPathWithFileName");
            srcPathMask = GetString("srcPathMask");
            expectedColumnCount = GetInt("ExpectedColumnCount");

            // NOTE: The snippet uses "FetchDataTypes" for both fetchDataTypes and preIngTransStatus:
            fetchDataTypes = GetBool("FetchDataTypes");
            preIngTransStatus = GetBool("FetchDataTypes");

            processedFileList = GetString("processedFileList");
            trgIndexes = GetString("TrgIndexes");

            // Logging-related fields
            logSelectCmd = GetString("logSelectCmd");
            logRuntimeCmd = GetString("logRuntimeCmd");
            logCreateCmd = GetString("logCreateCmd");
            logErrorInsert = GetString("logErrorInsert");
            logErrorUpdate = GetString("logErrorUpdate");
            logErrorDelete = GetString("logErrorDelete");
            logErrorRuntime = GetString("logErrorRuntime");

            execViewCmd = GetString("execViewCmd");
            copyToPath = GetString("copyToPath");
            InferDatatypeCmd = GetString("InferDatatypeCmd");
            Indexes = GetString("Indexes");

            // If your param table provides these as well:
            cmdAlterSQL = GetString("cmdAlterSQL");
            cmdCreate = GetString("cmdCreate");
            tfColList = GetString("tfColList");
            currentViewCMD = GetString("currentViewCMD");
            currentViewSelect = GetString("currentViewSelect");

            // NOTE: paramTbl column is "trgDBSchTbl" but variable is "trgDbSchTbl":
            trgDbSchTbl = GetString("trgDBSchTbl");

            sheetName = GetString("SheetName");
            sheetRange = GetString("SheetRange");
            useSheetIndex = GetBool("UseSheetIndex");
            IncludeFileLineNumber = GetBool("IncludeFileLineNumber");

            hierarchyIdentifier = GetString("hierarchyIdentifier");
            XmlToDataTableCode = GetString("XmlToDataTableCode");

            ParamName = GetString("ParamName");
            ParamSelectExp = GetString("SelectExp");
            ParamConnectionString = GetString("ParamConnectionString");
            trgDBSchSP = GetString("trgDBSchSP");

            SourceType = GetString("SourceType");
            Defaultvalue = GetString("Defaultvalue");
            PreFetch = GetBool("PreFetch");

            dlTenantId = GetString("dlTenantId");
            dlApplicationId = GetString("dlApplicationId");
            dlClientSecret = GetString("dlClientSecret");
            dlKeyVaultName = GetString("dlKeyVaultName");
            dlSecretName = GetString("dlSecretName");
            dlStorageAccountName = GetString("dlStorageAccountName");
            dlBlobContainer = GetString("dlBlobContainer");
            dlSubscriptionId = GetString("dlSubscriptionId");
            dlAutomationAccountName = GetString("dlAutomationAccountName");
            SourceType = GetString("dlDataFactoryName");
            SourceType = GetString("SourceType");
            SourceType = GetString("SourceType");

            invokeType = GetString("InvokeType");
            invokeAlias = GetString("InvokeAlias");
            invokePath = GetString("InvokePath");
            invokeFile = GetString("InvokeFile");
            code = GetString("Code");
            arguments = GetString("Arguments");
            flowBatch = GetString("Batch");
            sysAlias = GetString("SysAlias");

            trgRunbookName = GetString("trgRunbookName");  
            trgParameterJSON = GetString("trgParameterJSON");  
            trgDataFactoryName = GetString("trgDataFactoryName");  
            trgAutomationAccountName = GetString("trgAutomationAccountName");
            trgPipelineName = GetString("trgPipelineName");

            srcRunbookName = GetString("srcRunbookName");
            srcParameterJSON = GetString("srcParameterJSON");
            srcDataFactoryName = GetString("srcDataFactoryName");
            srcAutomationAccountName = GetString("srcAutomationAccountName");
            srcPipelineName = GetString("srcPipelineName");

            srcDSType = GetString("srcDSType");
            RemoveInColumnName = GetString("RemoveInColumnName");
            tmpSchema = GetString("tmpSchema");
            _whereIncExp = GetString("WhereIncExp");
            _whereDateExp = GetString("WhereDateExp");
            _whereXML = GetString("WhereXML");


            // Refactored parameter extraction
            SysAlias = GetString("SysAlias");
            Batch = GetString("Batch");
            srcConnectionString = GetString("srcConnectionString");
            srcDBSchTbl = GetString("srcDBSchTbl");
            NoOfOverlapDays = GetInt("NoOfOverlapDays");
            IncrementalColumn = GetString("IncrementalColumn");
            DateColumn = GetString("DateColumn");
            FromDate = GetDateTime("FromDate");
            ToDate = GetDateTime("ToDate");
            ExportBy = GetString("ExportBy");
            ExportSize = GetInt("ExportSize");
            trgPath = GetString("trgPath");
            trgFileName = GetString("trgFileName");
            trgFiletype = GetString("trgFiletype");
            NoOfThreads = GetInt("NoOfThreads");
            OnErrorResume = GetBool("OnErrorResume");
            compressionType = GetString("CompressionType");
            AddTimeStampToFileName = GetBool("AddTimeStampToFileName");
            Subfolderpattern = GetString("Subfolderpattern");
            srcDBSchObj = CommonDB.SQLObjectFromDBSchobj(srcDBSchTbl);

            NextExportDate = GetDateTime("NextExportDate");
            NextExportValue = GetInt("NextExportValue");
            AddTimeStampToFileName = GetBool("AddTimeStampToFileName");
            Subfolderpattern = GetString("Subfolderpattern");

            // Adding the additional parameters you provided
            ReplaceInvalidCharsWith = GetString("ReplaceInvalidCharsWith");
            onSyncCleanColumnName = GetBool("OnSyncCleanColumnName");
            maxRetry = GetInt("MaxRetry");

            // long columns for InitFromFileDate, InitToFileDate:
            if (HasColumn("InitFromFileDate"))
            {
                var strFromDate = row["InitFromFileDate"]?.ToString() ?? "0";
                InitFromFileDate = long.Parse(strFromDate);
            }
            if (HasColumn("InitToFileDate"))
            {
                var strToDate = row["InitToFileDate"]?.ToString() ?? "0";
                InitToFileDate = long.Parse(strToDate);
            }

            // Subscription / Resource / DataFactory / AutomationAccount:
            srcSubscriptionId = GetString("srcSubscriptionId");
            srcResourceGroup = GetString("srcResourceGroup");
            srcDataFactoryName = GetString("srcDataFactoryName");
            srcAutomationAccountName = GetString("srcAutomationAccountName");

            trgSubscriptionId = GetString("trgSubscriptionId");
            trgResourceGroup = GetString("trgResourceGroup");
           
            // If you want to read them from paramTbl:
            logFetched = HasColumn("logFetched") ? long.Parse(row["logFetched"]?.ToString() ?? "0") : 0;
            logInserted = HasColumn("logInserted") ? long.Parse(row["logInserted"]?.ToString() ?? "0") : 0;

            altTrgIsEmbedded = GetBool("altTrgIsEmbedded");
            logFileDate = GetString("logFileDate");
            JsonToDataTableCode = GetString("JsonToDataTableCode");
        }
    }

}
