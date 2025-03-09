using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using SQLFlowCore.Lineage;
using Renci.SshNet.Common;
using System.Reflection;
using SQLFlowCore.Logger;
using System.Text;
using Tensorflow.IO;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Services;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a class that provides functionality to execute SQL statements in parallel on a remote server.
    /// </summary>
    /// <remarks>
    /// This class also provides an event that is triggered when the lineage is calculated.
    /// </remarks>
    public class ExecLineageMap : EventArgs
    {
        /// <summary>
        /// Occurs when the lineage is calculated.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of SQL statements when the lineage information is computed.
        /// It provides detailed information about the current status of the lineage calculation process.
        /// </remarks>
        public static event EventHandler<EventArgsLineage> OnLineageCalculated;
       
        private static int _objectCounter = 0;
        private static int _total = 1;

        #region ExecLineageMap
        /// <summary>
        /// Executes the lineage map and returns the result as a string.
        /// </summary>
        /// <param name="logOutput">The StreamWriter object to write logs.</param>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="all">Optional parameter to specify whether to fetch lineage information for all objects. Default is "0".</param>
        /// <param name="alias">Optional parameter to specify the alias for fetching lineage information for flow objects. Default is an empty string.</param>
        /// <param name="execMode">Optional parameter to specify the execution mode. Default is "adf".</param>
        /// <param name="noOfThreads">Optional parameter to specify the number of threads for fetching lineage. Default is 4.</param>
        /// <param name="dbg">Optional parameter to specify the debug mode. Default is 0.</param>
        /// <returns>A string representing the result of the lineage map execution.</returns>
        /// <remarks>
        /// This method also updates the `_lineageLog` and `_objectCounter` static fields of the `ExecLineageMap` class.
        /// </remarks>
        public static void Exec(StreamWriter logOutput, string sqlFlowConString, string all = "0", string alias = "", string execMode = "adf", int noOfThreads = 4, int dbg = 0)
        {
            _objectCounter = 0;
            _total = 1;

            var logger = RealTimeLogger.CreateFromStream(
                        "LineageLogger",
                        logOutput,
                        leaveOpen: true, // The logger will own and dispose the stream
                        LogLevel.Information,
                        debugLevel: dbg);

            ConStringParser conStringParser = new ConStringParser(sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" }};
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            logger.LogInformation("Init Lineage Calculation");
            
            string LineageDsCmd = $@"exec [flw].[GetLineageObjects] @alias = '{alias}', @all = '{all}', @dbg = {dbg.ToString()}";

            
            logger.Flush();

            long logDurationPre = 0;
            var totalTime = new Stopwatch();
            
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();


                    DataSet ds = new DataSet();
                    using (logger.TrackOperation("Fetch lineage base data"))
                    {
                        logger.LogInformation(LineageDsCmd);
                        ds = CommonDB.GetDataSetFromSP(sqlFlowCon, LineageDsCmd, 720);
                    }
                    DataTable ObjectTbl = ds.Tables[0];
                    DataTable SubscriberTbl = ds.Tables[1];
                    DataTable SubscriberRelationTbl = ds.Tables[2];
                    _total = ObjectTbl.Rows.Count;

                    using (logger.TrackOperation("Execute lineage preparation step [flw].[CalcLineagePre]"))
                    {
                        using (SqlCommand cmd = new SqlCommand("[flw].[CalcLineagePre]", sqlFlowCon))
                        {
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@Alias", SqlDbType.VarChar).Value = alias;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    logger.Flush();

                    List<DataTable> DTSplitOnAlias = ObjectTbl.AsEnumerable()
                                    .AsParallel().AsOrdered()
                                    .OrderBy(row => row.Field<string>("Alias"))
                                    .GroupBy(row => row.Field<string>("Alias"))
                                    .Select(g => g.CopyToDataTable())
                                    .ToList();

                    List<Tuple<Urn, string>> tableIndexes = new List<Tuple<Urn, string>>();
                    List<string> Rows = new List<string>();

                    string inValidObjectMK = "";
                    string ValidObjectMK = "";


                    foreach (DataTable _tb in DTSplitOnAlias)
                    {
                        string dbase = _tb.Rows[0]["Database"]?.ToString() ?? string.Empty;
                        logger.LogInformation($"Database {dbase} ({_tb.Rows.Count.ToString()} objects)");
                        logOutput.Flush();
                    }

                    logger.LogInformation($"Fetched objects from meta data ({ObjectTbl.Rows.Count.ToString()})");
                    logOutput.Flush();

                    Dictionary<string, string> _relationJson = new Dictionary<string, string>();

                    foreach (DataTable _tb in DTSplitOnAlias)
                    {
                        //concurrencySemaphore.Wait();
                        DataTable tb = _tb;

                        string Database = tb.Rows[0]["Database"]?.ToString() ?? string.Empty;
                        string ConnectionString = tb.Rows[0]["ConnectionString"]?.ToString() ?? string.Empty;

                        string srcTenantId = tb.Rows[0]["srcTenantId"]?.ToString() ?? string.Empty;
                        string srcSubscriptionId = tb.Rows[0]["srcSubscriptionId"]?.ToString() ?? string.Empty;
                        string srcApplicationId = tb.Rows[0]["srcApplicationId"]?.ToString() ?? string.Empty;
                        string srcClientSecret = tb.Rows[0]["srcClientSecret"]?.ToString() ?? string.Empty;
                        string srcKeyVaultName = tb.Rows[0]["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        string srcSecretName = tb.Rows[0]["srcSecretName"]?.ToString() ?? string.Empty;
                        string srcResourceGroup = tb.Rows[0]["srcResourceGroup"]?.ToString() ?? string.Empty;
                        string srcDataFactoryName = tb.Rows[0]["srcDataFactoryName"]?.ToString() ?? string.Empty;
                        string srcAutomationAccountName = tb.Rows[0]["srcAutomationAccountName"]?.ToString() ?? string.Empty;
                        string srcStorageAccountName = tb.Rows[0]["srcStorageAccountName"]?.ToString() ?? string.Empty;
                        string srcBlobContainer = tb.Rows[0]["srcBlobContainer"]?.ToString() ?? string.Empty;

                        if (srcSecretName.Length > 0)
                        {
                            //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(srcKeyVaultName);
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                srcTenantId,
                                srcApplicationId,
                                srcClientSecret,
                                srcKeyVaultName);
                            ConnectionString = srcKeyVaultManager.GetSecret(srcSecretName);
                        }

                        ConStringParser objConStringParser = new ConStringParser(ConnectionString);
                        string conStrParsed = objConStringParser.ConBuilderMsSql.ConnectionString;

                        using (logger.TrackOperation($"Processing database {Database}"))
                        {
                            SqlConnection sqlCon = new SqlConnection(conStrParsed);
                            ServerConnection srvCon = new ServerConnection(sqlCon);
                            Server srv = new Server(srvCon);

                         
                            try
                            {
                                Database db = srv.Databases[Database];
                                Scripter scripter = new Scripter(srv)
                                {
                                    Options =
                                        {
                                            ScriptDrops = false,
                                            WithDependencies = false,
                                            Indexes = false,
                                            DriAllConstraints = false,
                                            NoCommandTerminator = false,
                                            AllowSystemObjects = false,
                                            Permissions = false
                                        }
                                };
                                scripter.Options.DriAllConstraints = false;
                                scripter.Options.SchemaQualify = true;
                                scripter.Options.DriIndexes = false;
                                scripter.Options.DriClustered = false;
                                scripter.Options.DriNonClustered = false;
                                scripter.Options.NonClusteredIndexes = false;
                                scripter.Options.ClusteredIndexes = false;
                                scripter.Options.FullTextIndexes = false;
                                scripter.Options.EnforceScriptingOptions = false;
                                scripter.Options.IncludeHeaders = false;
                                scripter.Options.ScriptBatchTerminator = false;
                                scripter.Options.Triggers = false;
                                scripter.Options.NoCollation = true;
                                scripter.ScriptingProgress += (sender, e) => Scripter_ScriptingProgress(sender, e, logger);

                                try
                                {
                                    if (db != null)
                                    {
                                        ScriptingOptions opt = SmoHelper.SmoScriptingOptionsBasic();
                                        db.PrefetchObjects(typeof(Table), opt);
                                        db.PrefetchObjects(typeof(View), opt);
                                        db.PrefetchObjects(typeof(StoredProcedure), opt);

                                        //List<Tuple<string, SqlSmoObject>> scriptedObjects = new List<Tuple< string, SqlSmoObject>>();
                                        new List<Tuple<string, int, string, bool>>();

                                        UrnCollection baseObjects = new UrnCollection();
                                        UrnCollection allObjects = new UrnCollection();
                                        Dictionary<string, string> relationJson = _relationJson;

                                        new List<Tuple<Urn, UrnCollection>>();
                                        string ObjectMK = "";
                                        string ObjectName = "";

                                        string Schema = "";
                                        string Object = "";
                                        string SysAlias = "";

                                        bool IsDependencyObject = false;

                                        using (logger.TrackOperation($"## Enumerating objects"))
                                        {
                                            // First, validate all objects in bulk
                                            DataTable validatedObjects = SmoHelper.ValidateObjectsBulk(sqlCon, tb);

                                            // Process each row in the original table
                                            foreach (DataRow dr in tb.Rows)
                                            {
                                                ObjectMK = dr["ObjectMK"]?.ToString() ?? string.Empty;
                                                ObjectName = dr["ObjectName"]?.ToString() ?? string.Empty;
                                                Schema = dr["Schema"]?.ToString() ?? string.Empty;
                                                Object = dr["Object"]?.ToString() ?? string.Empty;
                                                SysAlias = dr["SysAlias"]?.ToString() ?? string.Empty;

                                                // Look up this object in the validated objects table
                                                DataRow[] matchingRows = validatedObjects.Select(
                                                    $"Schema = '{Schema.Replace("'", "''")}' AND Object = '{Object.Replace("'", "''")}'");

                                                if (matchingRows.Length > 0 && (bool)matchingRows[0]["IsValid"])
                                                {
                                                    DataRow validatedRow = matchingRows[0];
                                                    string objectType = validatedRow["ObjectTypeDescription"].ToString();
                                                    string urnString = validatedRow["URN"].ToString();
                                                    Urn objectUrn = new Urn(urnString);

                                                    // Handle based on the object type
                                                    switch (objectType)
                                                    {
                                                        case "View":
                                                            if (FetchLineageDep.DupeUrnInCollection(baseObjects, objectUrn) == false)
                                                            {
                                                                baseObjects.Add(objectUrn);
                                                                allObjects.Add(objectUrn);
                                                                ValidObjectMK = ValidObjectMK + "," + ObjectMK;
                                                                logger.LogInformation($"Identified: {objectUrn}");

                                                                // For views, we still need the SMO object for the ObjectRelations.Parse method
                                                                View view = (View)srv.GetSmoObject(objectUrn);
                                                                SQLObject s = FetchLineageDep.SQLObjectFromUrn(objectUrn);
                                                                string rJson = ObjectRelations.Parse(ConnectionString, "View", s.ObjFullName, view, "");
                                                                relationJson.Add(s.ObjFullName, rJson);
                                                            }
                                                            else
                                                            {
                                                                logger.LogInformation($"Skipping duplicate view {objectUrn}");
                                                            }
                                                            break;

                                                        case "Table":
                                                            if (FetchLineageDep.DupeUrnInCollection(baseObjects, objectUrn) == false)
                                                            {
                                                                baseObjects.Add(objectUrn);
                                                                allObjects.Add(objectUrn);
                                                                ValidObjectMK = ValidObjectMK + "," + ObjectMK;
                                                                logger.LogInformation($"Identified: {objectUrn}");

                                                                //// Convert StringCollection to a single string
                                                                //string indexScriptsString = string.Join(";" + Environment.NewLine + Environment.NewLine, indexScripts.Cast<string>().ToArray());
                                                                //// Add the table's Urn and indexes to the tableIndexes list
                                                                //tableIndexes.Add(Tuple.Create(table.Urn, indexScriptsString));

                                                                //// Retrieve index scripts for the table
                                                                //StringCollection indexScripts = new StringCollection();
                                                                //foreach (Microsoft.SqlServer.Management.Smo.Index index in table.Indexes)
                                                                //{
                                                                //    foreach (string script in index.Script())
                                                                //    {
                                                                //        indexScripts.Add(script);
                                                                //    }
                                                                //}
                                                                //// Convert StringCollection to a single string
                                                                //string indexScriptsString = string.Join(";" + Environment.NewLine + Environment.NewLine, indexScripts.Cast<string>().ToArray());
                                                                //// Add the table's Urn and indexes to the tableIndexes list
                                                                //tableIndexes.Add(Tuple.Create(table.Urn, indexScriptsString));

                                                            }
                                                            else
                                                            {
                                                                logger.LogInformation($"Skipping duplicate table {objectUrn}");
                                                            }
                                                            break;

                                                        case "Stored Procedure":
                                                            if (FetchLineageDep.DupeUrnInCollection(baseObjects, objectUrn) == false)
                                                            {
                                                                baseObjects.Add(objectUrn);
                                                                allObjects.Add(objectUrn);
                                                                ValidObjectMK = ValidObjectMK + "," + ObjectMK;
                                                                logger.LogInformation($"Identified: {objectUrn}");

                                                                // For stored procedures, we still need the SMO object for the ObjectRelations.Parse method
                                                                StoredProcedure storedProc = (StoredProcedure)srv.GetSmoObject(objectUrn);
                                                                SQLObject s = FetchLineageDep.SQLObjectFromUrn(objectUrn);
                                                                string rJson = ObjectRelations.Parse(ConnectionString, "StoredProcedure", s.ObjFullName, storedProc, "");
                                                                relationJson.Add(s.ObjFullName, rJson);
                                                            }
                                                            else
                                                            {
                                                                logger.LogInformation($"Skipping duplicate stored procedure {objectUrn}");
                                                            }
                                                            break;
                                                    }
                                                }
                                                else
                                                {
                                                    inValidObjectMK = inValidObjectMK + "," + ObjectMK;
                                                    logger.LogWarning($"Not found: {db}.[{Schema}].[{Object}] ({ObjectMK})");
                                                }
                                            }
                                        }

                                        //Tag Invalid and Valid Objects
                                        using (SqlCommand cmd = new SqlCommand("flw.UpdLineageObjectNotInUse", sqlFlowCon))
                                        {
                                            logger.LogInformation("Tag objects not in in use");
                                            cmd.CommandType = CommandType.StoredProcedure;
                                            cmd.Parameters.Add("@InvalidMKList", SqlDbType.VarChar).Value =
                                                inValidObjectMK;
                                            cmd.Parameters.Add("@ValidMKList", SqlDbType.VarChar).Value =
                                                ValidObjectMK;
                                            //string cmdTxt = SqlCommandText.GetCommandText(cmd);
                                            cmd.ExecuteNonQuery();
                                        }

                                        DependencyWalker dependencyWalker = new DependencyWalker(srv);
                                        List<DepObject> rootWithDep = new List<DepObject>();
                                        UrnCollection depObjects = new UrnCollection();

                                        using (logger.TrackOperation("Detecting base dependencies"))
                                        {
                                            foreach (Urn urn in baseObjects)
                                            {
                                                if (FetchLineageDep.IsDependencyObject(urn))
                                                {
                                                    DataRow dr = FetchLineageDep.GetDataRowFromUrn(tb, urn);
                                                    DepObject dO = new DepObject
                                                    {
                                                        RootDataRow = dr,
                                                        RootObject = FetchLineageDep.SQLObjectFromUrn(urn)
                                                    };
                                                    DependencyTree tree =
                                                        dependencyWalker.DiscoverDependencies(new Urn[] { urn },
                                                            DependencyType.Parents);
                                                    var depends = dependencyWalker.WalkDependencies(tree)
                                                        .Where(x => x.Urn != urn);

                                                    UrnCollection currentObjects = new UrnCollection();
                                                    foreach (var item in depends)
                                                    {
                                                        if (FetchLineageDep.IsLineageObject(item.Urn))
                                                        {
                                                            if (FetchLineageDep.DupeUrnInCollection(allObjects,
                                                                    item.Urn) == false)
                                                            {
                                                                allObjects.Add(item.Urn);
                                                                depObjects.Add(item.Urn);

                                                                SQLObject s = FetchLineageDep.SQLObjectFromUrn(item.Urn);
                                                                string rJson = ObjectRelations.Parse(ConnectionString, item.Urn.Type, s.ObjFullName, item, "");
                                                                relationJson.Add(s.ObjFullName, rJson);
                                                            }

                                                            currentObjects.Add(item.Urn);
                                                        }
                                                    }
                                                    dO.DependencyObjects = currentObjects;
                                                    rootWithDep.Add(dO);
                                                }
                                            }
                                        }

                                        //Increase total count with dependent objects
                                        _total = _total + depObjects.Count;

                                        StringCollection sc = new StringCollection();
                                        List<SQLObject> SQLObjectsWithScript = new List<SQLObject>();
                                        using (var operation = logger.TrackOperation("Script objects"))
                                        {
                                            sc = scripter.Script(allObjects);
                                            SQLObjectsWithScript = FetchLineageDep.BuildSQLObjectFromCollection(Database, sc, allObjects);
                                        }

                                        using (logger.TrackOperation("Parse base object dependencies"))
                                        {
                                            //loop base objects
                                            foreach (Urn u in baseObjects)
                                            {
                                                DataRow dr = FetchLineageDep.GetDataRowFromUrn(tb, u);

                                                string dmlSQL = FetchLineageDep.GetDDLForUrn(SQLObjectsWithScript, u);

                                                ObjectMK = dr["ObjectMK"]?.ToString() ?? string.Empty;
                                                ObjectName = dr["ObjectName"]?.ToString() ?? string.Empty;
                                                Schema = dr["Schema"]?.ToString() ?? string.Empty;
                                                Object = dr["Object"]?.ToString() ?? string.Empty;
                                                SysAlias = dr["SysAlias"]?.ToString() ?? string.Empty;
                                                IsDependencyObject = FetchLineageDep.IsDependencyObject(u);
                                                string ObjectType = FetchLineageDep.GetSQLFlowObjectType(u);
                                                string AfterDependency = "";
                                                string BeforeDependency = "";

                                                if (FetchLineageDep.IsDependencyObject(u))
                                                {
                                                    string dmlSQLCleaned = dmlSQL;
                                                    if (u.Type == "View")
                                                    {
                                                        dmlSQLCleaned = ViewHelper.ExtractAfterCreateViewAs(dmlSQL);
                                                        //RemoveCreateViewStatement(dmlSQL);
                                                    }
                                                    DependencyParser dp = new DependencyParser(Database, Schema, dmlSQLCleaned, ObjectName, IsDependencyObject);

                                                    BeforeDependency = dp.BeforeDependencyObjectsString;
                                                    AfterDependency = dp.AfterDependencyObjectsString;
                                                }

                                                // Find the indexes for the current table using the Urn
                                                string indexesString = "";
                                                if (u.Type == "Table")
                                                {
                                                    var tableIndex = tableIndexes.FirstOrDefault(ti => ti.Item1 == u);
                                                    if (tableIndex != null)
                                                    {
                                                        indexesString = tableIndex.Item2;
                                                    }
                                                }

                                                if (!string.IsNullOrEmpty(BeforeDependency))
                                                {
                                                    logger.LogInformation($"BeforeDependency for {ObjectName}: {BeforeDependency}");
                                                }

                                                if (!string.IsNullOrEmpty(AfterDependency))
                                                {
                                                    logger.LogInformation($"AfterDependency for {ObjectName}: {AfterDependency}");
                                                }
                                                Rows.Add(
                                                    $"1|||{ObjectMK}|||{SysAlias}|||{ObjectName}|||{ObjectType}|||{dmlSQL}|||{AfterDependency}|||{BeforeDependency}|||{indexesString}");
                                            }
                                        }

                                        using (logger.TrackOperation("Parse all dependencies "))
                                        {
                                            foreach (DepObject depObj in rootWithDep)
                                            {
                                                DataRow dr = depObj.RootDataRow;
                                                string xSysAlias = dr["SysAlias"]?.ToString() ?? string.Empty;

                                                foreach (Urn u in depObj.DependencyObjects)
                                                {
                                                    if (FetchLineageDep.DupeUrnInCollection(depObjects, u))
                                                    {
                                                        SQLObject sQLObjectFromUrn =
                                                            FetchLineageDep.SQLObjectFromUrn(u);
                                                        string xObjectType = FetchLineageDep.GetSQLFlowObjectType(u);
                                                        string xDmlSQL =
                                                            FetchLineageDep.GetDDLForUrn(SQLObjectsWithScript, u);
                                                        bool xIsDependencyObject =
                                                            FetchLineageDep.IsDependencyObject(u);
                                                        string AfterDependency = "";
                                                        string BeforeDependency = "";
                                                        if (FetchLineageDep.IsDependencyObject(u))
                                                        {
                                                            //FetchLineageCalc calc =
                                                            //    new FetchLineageCalc(sQLObjectFromUrn.ObjDatabase,
                                                            //        sQLObjectFromUrn.ObjSchema,
                                                            //        sQLObjectFromUrn.ObjName, xIsDependencyObject,
                                                            //        xDmlSQL);
                                                            //BeforeDependency = string.Join(",",
                                                            //    MergeBeforeDependency(u, calc.BeforeDependency,
                                                            //        calc.AfterDependency, rootWithDep));
                                                            //AfterDependency = string.Join(",", calc.AfterDependency);
                                                            SQLObject s = FetchLineageDep.SQLObjectFromUrn(u);

                                                            DependencyParser dp = new DependencyParser(Database, sQLObjectFromUrn.ObjSchema, xDmlSQL, s.ObjFullName, xIsDependencyObject);
                                                            BeforeDependency = dp.BeforeDependencyObjectsString;
                                                            AfterDependency = dp.AfterDependencyObjectsString;

                                                        }

                                                        // Find the indexes for the current table using the Urn
                                                        string indexesString = "";
                                                        if (u.Type == "Table")
                                                        {
                                                            var tableIndex = tableIndexes.FirstOrDefault(ti => ti.Item1 == u);
                                                            if (tableIndex != null)
                                                            {
                                                                indexesString = tableIndex.Item2;
                                                            }
                                                        }

                                                        if (!string.IsNullOrEmpty(BeforeDependency))
                                                        {
                                                            logger.LogInformation($"Extended BeforeDependency for {ObjectName}: {BeforeDependency}");
                                                        }

                                                        if (!string.IsNullOrEmpty(AfterDependency))
                                                        {
                                                            logger.LogInformation($"Extended AfterDependency for {ObjectName}: {AfterDependency}");
                                                        }

                                                        Rows.Add(
                                                            $"2|||0|||{xSysAlias}|||{sQLObjectFromUrn.ObjFullName}|||{xObjectType}|||{xDmlSQL}|||{AfterDependency}|||{BeforeDependency}|||{indexesString}");
                                                    }
                                                }
                                            }
                                        }

                                        

                                    }
                                }
                                catch (Exception e)
                                {
                                    string innerExceptionDetails = "";
                                    Exception innerEx = e.InnerException;
                                    int depth = 0;

                                    // Build a chain of inner exceptions with their details
                                    while (innerEx != null && depth < 5)  // Limit depth to avoid infinite loops
                                    {
                                        innerExceptionDetails += $"\n--- Inner Exception Level {depth + 1}: {innerEx.GetType().Name}\n" +
                                                                 $"    Message: {innerEx.Message}\n" +
                                                                 $"    Stack Trace: {innerEx.StackTrace}\n";
                                        innerEx = innerEx.InnerException;
                                        depth++;
                                    }

                                    logOutput.Write($"Error: {e.Message}\n" +
                                                    $"Exception Type: {e.GetType().Name}\n" +
                                                    $"Stack Trace: {e.StackTrace}\n" +
                                                    innerExceptionDetails);
                                    logOutput.Flush();
                                }


                            }
                            catch (Exception e)
                            {
                                string innerExceptionDetails = "";
                                Exception innerEx = e.InnerException;
                                int depth = 0;

                                while (innerEx != null && depth < 5)
                                {
                                    innerExceptionDetails += $"\n--- Inner Exception Level {depth + 1}: {innerEx.GetType().Name}\n" +
                                                             $"    Message: {innerEx.Message}\n" +
                                                             $"    Stack Trace: {innerEx.StackTrace}\n";
                                    innerEx = innerEx.InnerException;
                                    depth++;
                                }

                                logOutput.Write($"--- Connection Error: {e.Message}\n" +
                                                $"Exception Type: {e.GetType().Name}\n" +
                                                $"Stack Trace: {e.StackTrace}\n" +
                                                innerExceptionDetails + Environment.NewLine);
                                logOutput.Flush();

                                //Remove Objects for this database from total
                                _total = _total - tb.Rows.Count;
                            }
                            finally
                            {
                                srv.ConnectionContext.Disconnect();
                                srvCon.Disconnect();
                                sqlCon.Close();
                                sqlCon.Dispose();
                            }
                        }

                    }

                    foreach (DataRow dr in SubscriberTbl.Rows)
                    {
                        string ToObjectMK = dr["ToObjectMK"]?.ToString() ?? string.Empty;
                        string SubscriberName = dr["SubscriberName"]?.ToString() ?? string.Empty;
                        string sqlCmd = dr["SQLCmd"]?.ToString() ?? string.Empty;


                        string ObjectType = "sub";
                        string AfterDependency = "";
                        string BeforeDependency = "";

                        DependencyParser dp = new DependencyParser("DbNameMissing", "dbo", sqlCmd, SubscriberName, true);

                        BeforeDependency = dp.BeforeDependencyObjectsString;
                        AfterDependency = dp.AfterDependencyObjectsString;
                        Rows.Add(
                            $"3|||{ToObjectMK}||||||{SubscriberName}|||{ObjectType}|||{sqlCmd}|||{AfterDependency}|||{BeforeDependency}|||");
                    }

                    Dictionary<string, List<RelationReference>> subscriberRelations = new Dictionary<string, List<RelationReference>>();


                    foreach (DataRow dr in SubscriberRelationTbl.Rows)
                    {
                        string ToObjectMK = dr["ToObjectMK"]?.ToString() ?? string.Empty;
                        string SubscriberName = dr["SubscriberName"]?.ToString() ?? string.Empty;
                        string sqlCmd = dr["SQLCmd"]?.ToString() ?? string.Empty;

                        string ConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                        string srcTenantId = dr["srcTenantId"]?.ToString() ?? string.Empty;
                        string srcSubscriptionId = dr["srcSubscriptionId"]?.ToString() ?? string.Empty;
                        string srcApplicationId = dr["srcApplicationId"]?.ToString() ?? string.Empty;
                        string srcClientSecret = dr["srcClientSecret"]?.ToString() ?? string.Empty;
                        string srcKeyVaultName = dr["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        string srcSecretName = dr["srcSecretName"]?.ToString() ?? string.Empty;


                        if (srcSecretName.Length > 0)
                        {
                            //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(srcKeyVaultName);
                            AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                srcTenantId,
                                srcApplicationId,
                                srcClientSecret,
                                srcKeyVaultName);
                            ConnectionString = srcKeyVaultManager.GetSecret(srcSecretName);
                        }

                        ConStringParser objConStringParser = new ConStringParser(ConnectionString);
                        string conStrParsed = objConStringParser.ConBuilderMsSql.ConnectionString;

                        string rJson = ObjectRelations.Parse(conStrParsed, "Sql", SubscriberName, null, sqlCmd);

                        var relationReferences = JsonConvert.DeserializeObject<List<RelationReference>>(rJson);

                        if (relationReferences != null)
                        {
                            if (!subscriberRelations.ContainsKey(SubscriberName))
                            {
                                subscriberRelations[SubscriberName] = new List<RelationReference>();
                            }
                            subscriberRelations[SubscriberName].AddRange(relationReferences);
                        }
                    }

                    // Now serialize each list into a single JSON string for each SubscriberName
                    Dictionary<string, string> combinedJsonForSubscribers = new Dictionary<string, string>();
                    foreach (var kvp in subscriberRelations)
                    {
                        string combinedJson = JsonConvert.SerializeObject(kvp.Value, Formatting.Indented);
                        combinedJsonForSubscribers.Add(kvp.Key, combinedJson);
                    }

                    //Add the combined JSON strings to the _relationJson dictionary
                    foreach (var entry in combinedJsonForSubscribers)
                    {
                        _relationJson.Add(entry.Key, entry.Value);
                    }

                    foreach (var item in Rows)
                    {
                        string[] rowSplit = item.Split("|||");
                        string xObjectMK = rowSplit[1];
                        string xSysAlias = rowSplit[2];
                        string xObjectName = rowSplit[3];
                        string xObjectType = rowSplit[4];
                        string xdmlSQL = rowSplit[5];
                        string xAfterDependency = rowSplit[6];
                        string xBeforeDependency = rowSplit[7];
                        string xIndexesString = rowSplit[8];

                        using (SqlCommand cmd = new SqlCommand("[flw].[AddDepObject]", sqlFlowCon))
                        {

                            var tmpRelation = "";
                            if (_relationJson.ContainsKey(xObjectName))
                            {
                                tmpRelation = _relationJson[xObjectName];
                            }
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@ObjectMK", SqlDbType.Int).Value = xObjectMK;
                            cmd.Parameters.Add("@SysAlias", SqlDbType.NVarChar).Value = xSysAlias;
                            cmd.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = xObjectName;
                            cmd.Parameters.Add("@ObjectType", SqlDbType.NVarChar).Value = xObjectType;
                            cmd.Parameters.Add("@dmlSQL", SqlDbType.NVarChar).Value = xdmlSQL;
                            cmd.Parameters.Add("@BeforeDependency", SqlDbType.NVarChar).Value = xBeforeDependency.Replace("[].[].[]", "");
                            cmd.Parameters.Add("@AfterDependency", SqlDbType.NVarChar).Value = xAfterDependency.Replace("[].[].[]", "");
                            cmd.Parameters.Add("@relationJson", SqlDbType.NVarChar).Value = tmpRelation;
                            cmd.Parameters.Add("@CurrentIndexes", SqlDbType.NVarChar).Value = xIndexesString;
                            cmd.ExecuteNonQuery();
                            cmd.Dispose();
                        }
                    }

                    using (var operation = logger.TrackOperation("Executing LineagePost Step [flw].[CalcLineagePost]"))
                    {
                        using (SqlCommand cmd = new SqlCommand("[flw].[CalcLineagePost]", sqlFlowCon))
                        {
                            cmd.CommandTimeout = 600;
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.ExecuteNonQuery();
                            cmd.Dispose();
                        }
                    }
                    logger.Flush();

                    using (var operation = logger.TrackOperation("Build Pipeline Graph"))
                    {
                        DataTable lineageMapBase = CommonDB.GetData(sqlFlowCon, "[flw].[GetLineageMapObjects]", 300);
                        LineageParser lineage = new LineageParser(lineageMapBase);
                        DataTable lineageResult = lineage.GetResult();
                        logger.LogInformation("Lineage Map Calculated ({logDurationPre.ToString()} sec)");
                        logger.Flush();

                        Dictionary<string, string> linMap = CommonDB.BuildColumnMapping(lineageResult);
                        CommonDB.TruncateAndBulkInsert(sqlFlowCon, "[flw].[LineageMap]", true, lineageResult, linMap);

                        logger.LogInformation($"Lineage Map Saved To SQLFlow ({logDurationPre.ToString()} sec)  {Environment.NewLine}");
                        logger.Flush();
                    }

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
                catch (Exception e)
                {
                    logger.LogInformation(e.Message + Environment.NewLine + e.StackTrace + Environment.NewLine);
                    logger.Flush();
                }

                totalTime.Stop();
                logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                logger.LogInformation($"Total processing time {logDurationPre.ToString()} (sec)");
                logOutput.Flush();
           }
        }

        /// <summary>
        /// Handles the ScriptingProgress event of the Scripter object.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ProgressReportEventArgs"/> instance containing the event data.</param>
        /// <param name="sw">The StreamWriter instance to write the progress information.</param>
        /// <remarks>
        /// This method increments the object counter, calculates the status, and writes the progress information to the StreamWriter. 
        /// It also triggers the OnLineageCalculated event with the current lineage status.
        /// </remarks>
        private static void Scripter_ScriptingProgress(object sender, ProgressReportEventArgs e, RealTimeLogger logger)
        {
            Interlocked.Increment(ref _objectCounter);
            double Status = _objectCounter / (double)_total; //Adding One For the StatusBar

            string Var = e.Current.ToString();
            //Var = Var.Substring(Var.LastIndexOf("@Name="), Var.Length - Var.LastIndexOf("@Name=") - 1);       

            EventArgsLineage arg = new EventArgsLineage
            {
                ObjectUrn = Var,
                InTotal = _total,
                InQueue = _total - _objectCounter,
                Processed = _objectCounter,
                Status = Status
            };

            OnLineageCalculated?.Invoke(Thread.CurrentThread, arg);
            logger.LogInformation($"Scripted: {Var} {_objectCounter}/{_total}");
            logger.Flush();
        }

        /// <summary>
        /// Handles the OnLineageCalculated event.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsLineage instance that contains the event data.</param>
        /// <remarks>
        /// This method is invoked when the lineage calculation is completed. It triggers the OnLineageCalculated event, passing along the event data received.
        /// </remarks>
        private static void FetchLineageBase_OnLineageCalculated(object sender, EventArgsLineage e)
        {
            OnLineageCalculated?.Invoke(Thread.CurrentThread, e);
        }


        internal static bool IsSystemObject(Urn urn)
        {
            // Check if the schema is a system schema
            if (urn.GetAttribute("Schema") != null)
            {
                string schema = urn.GetAttribute("Schema").ToString();
                if (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
                    schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
                    schema.StartsWith("db_", StringComparison.OrdinalIgnoreCase) ||
                    schema.Equals("guest", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion ExecLineageMap
    }
}