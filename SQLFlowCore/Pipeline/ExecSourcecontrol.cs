using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Specialized;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Git;
using SQLFlowCore.Services.Schema;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a class that provides functionality to execute source control operations.
    /// </summary>
    /// <remarks>
    /// This class contains methods for executing source control operations, 
    /// such as scripting database objects and writing the scripts to a log file. 
    /// It also provides an event that is triggered when an object is scripted.
    /// </remarks>
    public class ExecSourceControl : EventArgs
    {
        /// <summary>
        /// Occurs when an object has been scripted.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of the `Exec` method, each time an object is scripted.
        /// The event handler receives an argument of type `EventArgsSourceControl` containing data related to the event.
        /// This data includes the total number of objects, the number of processed objects, the URN value of the object, the number of objects in the queue, and the status of the processing.
        /// </remarks>
        public static event EventHandler<EventArgsSourceControl> OnObjectScripted;
        private static string _lineageLog = "";
        private static int _objectCounter = 0;
        private static int _total = 1;

        #region ExecSourceControl
        /// <summary>
        /// Executes the source control process.
        /// </summary>
        /// <param name="logWriter">The StreamWriter object to write logs.</param>
        /// <param name="sqlFlowConString">The connection string for the SQLFlow database.</param>
        /// <param name="scAlias">The alias for the source control (optional).</param>
        /// <param name="batch">The batch number or identifier (optional).</param>
        public static void Exec(StreamWriter logWriter, string sqlFlowConString, string scAlias = "", string batch = "")
        {
            _lineageLog = "";
            new object();
            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            int commandTimeOutInSek = 180;

            string srcTenantId = string.Empty;
            string srcSubscriptionId = string.Empty;
            string srcApplicationId = string.Empty;
            string srcClientSecret = string.Empty;
            string srcKeyVaultName = string.Empty;
            string srcSecretName = string.Empty;
            string srcResourceGroup = string.Empty;
            string srcDataFactoryName = string.Empty;
            string srcAutomationAccountName = string.Empty;
            string srcStorageAccountName = string.Empty;
            string srcBlobContainer = string.Empty;

            string trgTenantId = string.Empty;
            string trgSubscriptionId = string.Empty;
            string trgApplicationId = string.Empty;
            string trgClientSecret = string.Empty;
            string trgKeyVaultName = string.Empty;
            string trgSecretName = string.Empty;
            string trgResourceGroup = string.Empty;
            string trgDataFactoryName = string.Empty;
            string trgAutomationAccountName = string.Empty;
            string trgStorageAccountName = string.Empty;
            string trgBlobContainer = string.Empty;

            string KeyVaultApiKey = "";

            string scDsCmd = $@"exec [flw].[GetSourceControl] @SCAlias = '{scAlias}', @batch = '{batch}'";

            _lineageLog += "## " + scDsCmd + Environment.NewLine;
            logWriter.Write("## " + scDsCmd + Environment.NewLine);
            logWriter.Flush();
            long logDurationPre = 0;
            var totalTime = new Stopwatch();
            new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();

                    var ObjectDS = new GetData(sqlFlowCon, scDsCmd, 720);
                    DataTable ObjectTbl = ObjectDS.Fetch();
                    bool DirCleanup = true;

                    //Calculate total number of objects
                    foreach (DataRow dr in ObjectTbl.Rows)
                    {
                        string ConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                        string ScriptDataForTables = (dr["ScriptDataForTables"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");

                        srcTenantId = dr["srcTenantId"]?.ToString() ?? string.Empty;
                        srcApplicationId = dr["srcApplicationId"]?.ToString() ?? string.Empty;
                        srcClientSecret = dr["srcClientSecret"]?.ToString() ?? string.Empty;
                        srcKeyVaultName = dr["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        srcSecretName = dr["srcSecretName"]?.ToString() ?? string.Empty;
                        srcStorageAccountName = dr["srcStorageAccountName"]?.ToString() ?? string.Empty;
                        srcBlobContainer = dr["srcBlobContainer"]?.ToString() ?? string.Empty;

                        trgTenantId = dr["trgTenantId"]?.ToString() ?? string.Empty;
                        trgApplicationId = dr["trgApplicationId"]?.ToString() ?? string.Empty;
                        trgClientSecret = dr["trgClientSecret"]?.ToString() ?? string.Empty;
                        trgKeyVaultName = dr["trgKeyVaultName"]?.ToString() ?? string.Empty;
                        trgSecretName = dr["trgSecretName"]?.ToString() ?? string.Empty;
                        trgStorageAccountName = dr["trgStorageAccountName"]?.ToString() ?? string.Empty;
                        trgBlobContainer = dr["trgBlobContainer"]?.ToString() ?? string.Empty;


                        string[] DataTbls = ScriptDataForTables.Split(',');



                        if (trgSecretName.Length > 0)
                        {
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                trgTenantId,
                                trgApplicationId,
                                trgClientSecret,
                                trgKeyVaultName);
                            KeyVaultApiKey = trgKeyVaultManager.GetSecret(trgSecretName);
                        }

                        if (srcSecretName.Length > 0)
                        {
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                srcTenantId,
                                srcApplicationId,
                                srcClientSecret,
                                srcKeyVaultName);
                            ConnectionString = srcKeyVaultManager.GetSecret(srcSecretName);
                        }


                        ConStringParser objConStringParser = new ConStringParser(ConnectionString);
                        string conStrParsed = objConStringParser.ConBuilderMsSql.ConnectionString;
                        new SqlConnection(conStrParsed);

                        string ObjectCountCMD = @"SELECT '[' + OBJECT_SCHEMA_NAME(Object_id) + '].' + '[' + OBJECT_NAME(Object_id) + ']' ObjectName
                                                    FROM sys.objects
                                                    WHERE is_ms_shipped = 0
                                                          AND type_desc IN ( 'USER_TABLE', 'VIEW', 'SQL_STORED_PROCEDURE', 'SQL_TABLE_VALUED_FUNCTION',
                                                                             'SQL_SCALAR_FUNCTION', 'SEQUENCE_OBJECT','SYNONYM'
                                                                           );";

                        using (var tmpCon = new SqlConnection(ConnectionString))
                        {
                            tmpCon.Open();
                            DataTable tmpTbl = CommonDB.FetchData(tmpCon, ObjectCountCMD, commandTimeOutInSek);
                            tmpCon.Close();
                            tmpCon.Dispose();
                            _total = _total + tmpTbl.Rows.Count + DataTbls.Length;
                        }

                    }

                    logWriter.Write($"## Total number of objects {_total.ToString()} {Environment.NewLine}");
                    logWriter.Flush();

                    foreach (DataRow dr in ObjectTbl.Rows)
                    {
                        string DBName = (dr["DBName"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                        string RepoName = dr["RepoName"]?.ToString() ?? string.Empty;
                        string ScriptToPath = dr["ScriptToPath"]?.ToString() ?? string.Empty;

                        string ScriptDataForTables = (dr["ScriptDataForTables"]?.ToString() ?? string.Empty).Replace("[", "").Replace("]", "");
                        string SourceControlType = dr["SourceControlType"]?.ToString() ?? string.Empty;
                        string Username = dr["Username"]?.ToString() ?? string.Empty;
                        string AccessToken = dr["AccessToken"]?.ToString() ?? string.Empty;
                        string ConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                        string WorkSpaceName = dr["WorkSpaceName"]?.ToString() ?? string.Empty;
                        string ProjectName = dr["ProjectName"]?.ToString() ?? string.Empty;
                        string ProjectKey = dr["ProjectKey"]?.ToString() ?? string.Empty;
                        string ConsumerKey = dr["ConsumerKey"]?.ToString() ?? string.Empty;
                        string ConsumerSecret = dr["ConsumerSecret"]?.ToString() ?? string.Empty;
                        bool IsSynapse = (dr["IsSynapse"]?.ToString() ?? string.Empty).Equals("True");

                        bool CreateWrkProjRepo = (dr["CreateWrkProjRepo"]?.ToString() ?? string.Empty).Equals("True");


                        srcTenantId = dr["srcTenantId"]?.ToString() ?? string.Empty;
                        srcApplicationId = dr["srcApplicationId"]?.ToString() ?? string.Empty;
                        srcClientSecret = dr["srcClientSecret"]?.ToString() ?? string.Empty;
                        srcKeyVaultName = dr["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        srcSecretName = dr["srcSecretName"]?.ToString() ?? string.Empty;
                        srcStorageAccountName = dr["srcStorageAccountName"]?.ToString() ?? string.Empty;
                        srcBlobContainer = dr["srcBlobContainer"]?.ToString() ?? string.Empty;

                        trgTenantId = dr["trgTenantId"]?.ToString() ?? string.Empty;
                        trgApplicationId = dr["trgApplicationId"]?.ToString() ?? string.Empty;
                        trgClientSecret = dr["trgClientSecret"]?.ToString() ?? string.Empty;
                        trgKeyVaultName = dr["trgKeyVaultName"]?.ToString() ?? string.Empty;
                        trgSecretName = dr["trgSecretName"]?.ToString() ?? string.Empty;
                        trgStorageAccountName = dr["trgStorageAccountName"]?.ToString() ?? string.Empty;
                        trgBlobContainer = dr["trgBlobContainer"]?.ToString() ?? string.Empty;

                        if (trgSecretName.Length > 0)
                        {
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                trgTenantId,
                                trgApplicationId,
                                trgClientSecret,
                                trgKeyVaultName);
                            KeyVaultApiKey = trgKeyVaultManager.GetSecret(trgSecretName);
                        }

                        if (srcSecretName.Length > 0)
                        {
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                srcTenantId,
                                srcApplicationId,
                                srcClientSecret,
                                srcKeyVaultName);
                            ConnectionString = srcKeyVaultManager.GetSecret(srcSecretName);
                        }

                        ScriptDataForTables = ScriptDataForTables.ToLower().Replace("[", "").Replace("]", "");
                        string[] DataTbls = ScriptDataForTables.Split(',');

                        ScriptToPath = Functions.GetFullPathWithEndingSlashes(ScriptToPath);
                        string ScriptToPathWithDBName = ScriptToPath + $"{Functions.GetValidFileDirName(DBName)}{Path.DirectorySeparatorChar}";

                        if (Directory.Exists(ScriptToPath) == false)
                        {
                            Directory.CreateDirectory(ScriptToPath);
                        }

                        if (SourceControlType.ToLower() == "bitbucket" && DirCleanup == true)
                        {
                            //Directory Cleanup To Avoid Conflict. Run only for the first bitbucket repo
                            DirCleanup = false;
                            BitBucket.DirCleanUp(ScriptToPath);
                            BitBucket.initLocalGit(CreateWrkProjRepo, ScriptToPath, Username, RepoName, DBName, WorkSpaceName, ProjectName, ProjectKey, ConsumerKey, ConsumerSecret);
                        }

                        //Create Database Directory
                        if (Directory.Exists(ScriptToPathWithDBName) == false)
                        {
                            Directory.CreateDirectory(ScriptToPathWithDBName);
                        }
                        else
                        {
                            BitBucket.DirCleanUp(ScriptToPathWithDBName);
                        }

                        ConStringParser objConStringParser = new ConStringParser(ConnectionString);
                        string conStrParsed = objConStringParser.ConBuilderMsSql.ConnectionString;
                        SqlConnection sqlCon = new SqlConnection(conStrParsed);
                        ServerConnection srvCon = new ServerConnection(sqlCon);
                        Server srv = new Server(srvCon);
                        try
                        {
                            Database db = srv.Databases[DBName];

                            if (db != null)
                            {
                                ScriptingOptions sOpt = SmoHelper.SmoScriptingOptions();
                                db.PrefetchObjects(typeof(Table), sOpt);
                                db.PrefetchObjects(typeof(View), sOpt);
                                db.PrefetchObjects(typeof(StoredProcedure), sOpt);
                                db.PrefetchObjects(typeof(UserDefinedFunction), sOpt);
                                db.PrefetchObjects(typeof(Schema), sOpt);

                                //db.PrefetchObjects(typeof(UserDefinedDataType), sOpt);
                                //db.PrefetchObjects(typeof(DatabaseRole), sOpt);
                                //db.PrefetchObjects(typeof(ApplicationRole), sOpt);
                                //db.PrefetchObjects(typeof(DatabaseDdlTrigger), sOpt);
                                //db.PrefetchObjects(typeof(Synonym), sOpt);
                                ////db.PrefetchObjects(typeof(PlanGuide), sOpt);
                                ////db.PrefetchObjects(typeof(UserDefinedType), sOpt);
                                ////db.PrefetchObjects(typeof(UserDefinedAggregate), sOpt);
                                ////db.PrefetchObjects(typeof(FullTextCatalog), sOpt);
                                ////db.PrefetchObjects(typeof(UserDefinedTableType), sOpt);
                                ////db.PrefetchObjects(typeof(SecurityPolicy), sOpt);

                                SmoHelper.ScriptFolders();

                                var localContentFolders = Directory.GetDirectories(ScriptToPathWithDBName);
                                UrnCollection col = new UrnCollection();

                                if (DataTbls.Length > 0)
                                {
                                    foreach (Table obj in db.Tables)
                                    {
                                        if (obj.IsSystemObject == false)
                                        {
                                            string oName = obj.Schema.Replace("[", "").Replace("]", "") + "." + obj.Name.Replace("[", "").Replace("]", "");
                                            oName = oName.ToLower();

                                            if (DataTbls.Contains(oName))
                                            {
                                                SmoHelper.SmoScriptingOptions();

                                                var scripter = new Scripter(srv)
                                                {
                                                    Options =
                                                    {
                                                        IncludeIfNotExists = false,
                                                        ScriptSchema = false,
                                                        ScriptData = true
                                                    }
                                                };

                                                string typePath = ScriptToPathWithDBName + $"Data{Path.DirectorySeparatorChar}";
                                                if (localContentFolders.Contains("Data") == false)
                                                {
                                                    Directory.CreateDirectory(typePath);
                                                }
                                                //System.Collections.Specialized.StringCollection scripts = obj.Script(sOpt2);
                                                //string scrs = "";
                                                //foreach (string s in scripter.EnumScript(new Urn[] { obj.Urn }))
                                                //  scrs += s + "\n\n"; ;

                                                Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                                string Filename = @$"{typePath}\{FileFolder.Item1}";
                                                File.WriteAllLines(Filename, scripter.EnumScript(new Urn[] { obj.Urn }));
                                                col.Add(obj.Urn);

                                                _lineageLog += $"## Data Scripted ({obj.Urn.Value}) {Environment.NewLine}";
                                                logWriter.Write($"## Data Scripted ({obj.Urn.Value}) {Environment.NewLine}");
                                                logWriter.Flush();
                                                _objectCounter++;

                                                EventArgsSourceControl arg = new EventArgsSourceControl
                                                {
                                                    Total = _total,
                                                    Processed = _objectCounter,
                                                    ObjectUrn = obj.Urn.Value,
                                                    InQueue = _total - _objectCounter,
                                                    Status = _objectCounter / (double)_total
                                                };
                                                OnObjectScripted?.Invoke(Thread.CurrentThread, arg);


                                            }

                                        }
                                    }
                                }

                                foreach (Table obj in db.Tables)
                                {
                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Table{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Table") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script(sOpt);
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }
                                }

                                foreach (View obj in db.Views)
                                {
                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"View{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("View") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);

                                    }
                                }

                                foreach (StoredProcedure obj in db.StoredProcedures)
                                {

                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"StoredProcedure{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("StoredProcedure") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }
                                }

                                //Scripter scripterX = new Scripter(srv);
                                //scripterX.ScriptingProgress += Scripter_ScriptingProgress1;
                                //var Scripts  = scripterX.Script(col);


                                foreach (UserDefinedFunction obj in db.UserDefinedFunctions)
                                {
                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"UserDefinedFunction{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("UserDefinedFunction") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }
                                }

                                foreach (User obj in db.Users)
                                {
                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"User{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("User") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();


                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }
                                }

                                foreach (Schema obj in db.Schemas)
                                {
                                    if (obj.IsSystemObject == false)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Schema{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Schema") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();


                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }
                                }


                                if (IsSynapse == false)
                                {
                                    foreach (Sequence obj in db.Sequences)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Sequence{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Sequence") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (UserDefinedDataType obj in db.UserDefinedDataTypes)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"UserDefinedDataType{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("UserDefinedDataType") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (Microsoft.SqlServer.Management.Smo.Rule obj in db.Rules)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Rule{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Rule") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);

                                    }

                                    foreach (DatabaseRole obj in db.Roles)
                                    {
                                        if (obj.IsFixedRole == false)
                                        {
                                            string typePath = ScriptToPathWithDBName + $"DatabaseRole{Path.DirectorySeparatorChar}";
                                            if (localContentFolders.Contains("DatabaseRole") == false)
                                            {
                                                Directory.CreateDirectory(typePath);
                                            }
                                            StringCollection scripts = obj.Script();
                                            Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                            string Filename = @$"{typePath}\{FileFolder.Item1}";
                                            File.WriteAllLines(Filename, scripts.Cast<string>());
                                            col.Add(obj.Urn);

                                            _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                            logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                            logWriter.Flush();

                                            EventArgsSourceControl arg = new EventArgsSourceControl
                                            {
                                                Total = _total,
                                                Processed = _objectCounter,
                                                ObjectUrn = obj.Urn.Value,
                                                InQueue = _total - _objectCounter,
                                                Status = _objectCounter / (double)_total
                                            };
                                            OnObjectScripted?.Invoke(Thread.CurrentThread, arg);

                                        }
                                    }

                                    foreach (ApplicationRole obj in db.ApplicationRoles)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"ApplicationRole{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("ApplicationRole") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (DatabaseDdlTrigger obj in db.Triggers)
                                    {
                                        if (obj.IsSystemObject == false)
                                        {
                                            string typePath = ScriptToPathWithDBName + $"DatabaseDdlTrigger{Path.DirectorySeparatorChar}";
                                            if (localContentFolders.Contains("DatabaseDdlTrigger") == false)
                                            {
                                                Directory.CreateDirectory(typePath);
                                            }
                                            StringCollection scripts = obj.Script();
                                            Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                            string Filename = @$"{typePath}\{FileFolder.Item1}";
                                            File.WriteAllLines(Filename, scripts.Cast<string>());
                                            col.Add(obj.Urn);

                                            _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                            logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                            logWriter.Flush();

                                            EventArgsSourceControl arg = new EventArgsSourceControl
                                            {
                                                Total = _total,
                                                Processed = _objectCounter,
                                                ObjectUrn = obj.Urn.Value,
                                                InQueue = _total - _objectCounter,
                                                Status = _objectCounter / (double)_total
                                            };
                                            OnObjectScripted?.Invoke(Thread.CurrentThread, arg);

                                        }
                                    }

                                    foreach (Synonym obj in db.Synonyms)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Synonym{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Synonym") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();
                                        _objectCounter++;

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (XmlSchemaCollection obj in db.XmlSchemaCollections)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"XmlSchemaCollection{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("XmlSchemaCollection") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (Default obj in db.Defaults)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"Default{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("Default") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (PlanGuide obj in db.PlanGuides)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"PlanGuide{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("PlanGuide") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (UserDefinedType obj in db.UserDefinedTypes)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"UserDefinedType{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("UserDefinedType") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);

                                    }

                                    foreach (UserDefinedAggregate obj in db.UserDefinedAggregates)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"UserDefinedAggregate{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("UserDefinedAggregate") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (FullTextCatalog obj in db.FullTextCatalogs)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"FullTextCatalog{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("FullTextCatalog") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (UserDefinedTableType obj in db.UserDefinedTableTypes)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"UserDefinedTableType{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("UserDefinedTableType") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                    foreach (SecurityPolicy obj in db.SecurityPolicies)
                                    {
                                        string typePath = ScriptToPathWithDBName + $"SecurityPolicy{Path.DirectorySeparatorChar}";
                                        if (localContentFolders.Contains("SecurityPolicy") == false)
                                        {
                                            Directory.CreateDirectory(typePath);
                                        }
                                        StringCollection scripts = obj.Script();
                                        Tuple<string, string> FileFolder = GetFileNameAndFolder(obj.Urn);
                                        string Filename = @$"{typePath}\{FileFolder.Item1}";
                                        File.WriteAllLines(Filename, scripts.Cast<string>());
                                        col.Add(obj.Urn);

                                        _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                        logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                        logWriter.Flush();

                                        EventArgsSourceControl arg = new EventArgsSourceControl
                                        {
                                            Total = _total,
                                            Processed = _objectCounter,
                                            ObjectUrn = obj.Urn.Value,
                                            InQueue = _total - _objectCounter,
                                            Status = _objectCounter / (double)_total
                                        };
                                        OnObjectScripted?.Invoke(Thread.CurrentThread, arg);
                                    }

                                }

                                if (SourceControlType.ToLower() == "github")
                                {
                                    if (KeyVaultApiKey.Length > 0)
                                    {
                                        AccessToken = KeyVaultApiKey;
                                    }
                                    GitHub.pushToGitHub(logWriter, AccessToken, ScriptToPath, Username, RepoName, DBName);
                                }

                                if (SourceControlType.ToLower() == "bitbucket")
                                {
                                    if (KeyVaultApiKey.Length > 0)
                                    {
                                        ConsumerSecret = KeyVaultApiKey;
                                    }

                                    BitBucket.PushToGit(logWriter, ScriptToPath, Username, RepoName, DBName, WorkSpaceName, ProjectName, ProjectKey, ConsumerKey, ConsumerSecret);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                            logWriter.Write($"## Error Log " + Environment.NewLine + $"{e.Message}");
                            logWriter.Flush();
                        }
                        finally
                        {
                            srv.ConnectionContext.Disconnect();
                            srvCon.Disconnect();
                            sqlCon.Close();
                            sqlCon.Dispose();
                        }
                    }

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    _lineageLog += e.Message + Environment.NewLine + e.StackTrace + Environment.NewLine;
                    logWriter.Write(e.Message);
                }

                totalTime.Stop();
                logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                _lineageLog += Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine);
                logWriter.Write(Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine));
                logWriter.Flush();
            }

            //return result;
        }

        /// <summary>
        /// Generates a tuple containing the file name and the folder name for a given URN.
        /// </summary>
        /// <param name="urn">The URN for which the file name and folder name are to be generated.</param>
        /// <returns>A tuple where the first item is the file name and the second item is the folder name.</returns>
        internal static Tuple<string, string> GetFileNameAndFolder(Urn urn)
        {
            //StringBuilder builder = new StringBuilder();
            string Schema = "";
            string Name = urn.GetAttribute("Name");
            string Type = urn.Type;

            foreach (char ch in Path.GetInvalidFileNameChars())
            {
                Name = Name.Replace(ch, ' ');
            }

            if (urn.GetAttribute("Schema") != null)
            {
                Schema = urn.GetAttribute("Schema") + ".";
            }
            Tuple<string, string> tp = new Tuple<string, string>($"{Schema}{Name}.sql", Type);
            return tp;
        }

        private static void Scripter_ScriptingProgress1(object sender, ProgressReportEventArgs e)
        {
            Console.WriteLine($"{e.Current.Value} {e.TotalCount}/{e.Total}");
        }

        private static void ScripterX_ScriptingProgress(object sender, ProgressReportEventArgs e)
        {
            Console.WriteLine($"{e.Current.Value} {e.TotalCount}/{e.Total}");

        }

        internal static int containsIgnoreCase(StringCollection stringCollection, string value)
        {
            int index = -1;
            if (stringCollection == null || value == null) return index;
            if (value.Length == 0) return index;

            int counter = 0;
            foreach (string val in stringCollection)
            {
                if (val.Length > 60)
                {
                    int chk = val.IndexOf(value, StringComparison.OrdinalIgnoreCase);
                    if (chk != -1)
                    {
                        index = counter;
                        break;
                    }
                }

                counter += 1;
            }

            return index;
        }

        #endregion ExecSourceControl
    }
}