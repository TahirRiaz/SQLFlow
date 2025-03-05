using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Trigger = Microsoft.SqlServer.Management.Smo.Trigger;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// The ExecSysDocGen class is responsible for executing system documentation generation.
    /// It provides methods for executing SQL scripts, inserting scripts into the database, 
    /// filtering scripts, and generating column scripts.
    /// </summary>
    /// <remarks>
    /// This class also contains an event handler for when an object is scripted.
    /// </remarks>
    public class ExecSysDocGen : EventArgs
    {
        /// <summary>
        /// Occurs when an object has been scripted.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of the `Exec` method, after an object has been scripted and its script has been inserted into the database.
        /// The event handler receives an argument of type `EventArgsSourceControl` containing data related to the scripted object, such as its URN, the total number of objects to be processed, the number of objects already processed, the number of objects remaining in the queue, and the processing status.
        /// </remarks>
        public static event EventHandler<EventArgsSourceControl> OnObjectScripted;
        private static string _lineageLog = "";
        private static int _objectCounter = 0;
        private static int _total = 1;

        #region ExecSysDocGen
        /// <summary>
        /// Executes the system documentation generation process.
        /// </summary>
        /// <param name="logWriter">The StreamWriter object to write logs.</param>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <remarks>
        /// This method initializes the connection to the SQLFlow database, starts the stopwatch to track the total processing time, 
        /// and handles any exceptions that occur during the execution. It also logs the total processing time at the end of the execution.
        /// </remarks>
        public static void Exec(StreamWriter logWriter, string sqlFlowConString, string ObjectName)
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

            string DBName = conStringParser.ConBuilderMsSql.InitialCatalog;

            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            int commandTimeOutInSek = 180;

            long logDurationPre = 0;
            var totalTime = new Stopwatch();
            new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();

                    string ObjectCountCMD = @$"[flw].[GetRVSysDoc] '{ObjectName}'";

                    DataTable DataTbls = CommonDB.FetchData(sqlFlowCon, ObjectCountCMD, commandTimeOutInSek);
                    _total = DataTbls.Rows.Count;

                    logWriter.Write($"## Total number of objects {_total.ToString()} {Environment.NewLine}");
                    logWriter.Flush();

                    SqlConnection sqlCon =
                        new SqlConnection(sqlFlowConString);
                    ServerConnection srvCon = new ServerConnection(sqlCon);
                    Server srv = new Server(srvCon);
                    try
                    {
                        Database db = srv.Databases[DBName];

                        if (db != null)
                        {
                            ScriptingOptions sOpt = SmoHelper.SmoScriptingOptions();
                            sOpt.ExtendedProperties = false;
                            sOpt.Indexes = false;
                            sOpt.PrimaryObject = true;
                            sOpt.NoCollation = true;
                            sOpt.Triggers = false;
                            sOpt.DriAllConstraints = false;

                            db.PrefetchObjects(typeof(Table), sOpt);
                            db.PrefetchObjects(typeof(View), sOpt);
                            db.PrefetchObjects(typeof(StoredProcedure), sOpt);
                            db.PrefetchObjects(typeof(UserDefinedFunction), sOpt);
                            db.PrefetchObjects(typeof(Schema), sOpt);

                            SmoHelper.ScriptFolders();

                            Scripter scripter = new Scripter(srv);
                            DependencyWalker walker = new DependencyWalker(srv);

                            string[] RemoveParts = { "SET ANSI_NULLS ON", "SET QUOTED_IDENTIFIER ON", "SET ANSI_NULLS OFF", "WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF)" };
                            string scriptGenId = DateTime.Now.ToString("yyyyMMddHHmmss");

                            if (ObjectName.Length > 0)
                            {
                                scriptGenId = "0";
                            }

                            foreach (View obj in db.Views)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "View";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;



                                if (obj.IsSystemObject == false && 1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    string relationJson = ObjectRelations.Parse(sqlFlowConString, scriptType, scriptName, obj, "");

                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";
                                    List<ObjDependency> dependsOnList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, true);
                                    List<ObjDependency> dependsOnByList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, false);

                                    if (dependsOnList.Count > 0)
                                    {
                                        dependsOnStr = JsonConvert.SerializeObject(dependsOnList, Formatting.Indented);
                                    }

                                    if (dependsOnByList.Count > 0)
                                    {
                                        dependsOnByStr = JsonConvert.SerializeObject(dependsOnByList, Formatting.Indented);
                                    }

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, relationJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

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
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "StoredProcedure";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (obj.IsSystemObject == false && 1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    string relationJson = ObjectRelations.Parse(sqlFlowConString, scriptType,
                                        scriptName, obj, "");

                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";
                                    List<ObjDependency> dependsOnList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, true);
                                    List<ObjDependency> dependsOnByList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, false);

                                    if (dependsOnList.Count > 0)
                                    {
                                        dependsOnStr = JsonConvert.SerializeObject(dependsOnList, Formatting.Indented);
                                    }

                                    if (dependsOnByList.Count > 0)
                                    {
                                        dependsOnByStr = JsonConvert.SerializeObject(dependsOnByList, Formatting.Indented);
                                    }

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, relationJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

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

                            foreach (UserDefinedFunction obj in db.UserDefinedFunctions)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "UserDefinedFunction";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (obj.IsSystemObject == false && 1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    var paramCol = obj.Parameters;

                                    string relationJson = ObjectRelations.Parse(sqlFlowConString, scriptType,
                                        scriptName, obj, "");

                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";
                                    List<ObjDependency> dependsOnList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, true);
                                    List<ObjDependency> dependsOnByList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, false);


                                    if (dependsOnList.Count > 0)
                                    {
                                        dependsOnStr = JsonConvert.SerializeObject(dependsOnList, Formatting.Indented);
                                    }

                                    if (dependsOnByList.Count > 0)
                                    {
                                        dependsOnByStr = JsonConvert.SerializeObject(dependsOnByList, Formatting.Indented);
                                    }

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, relationJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

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

                            foreach (Table obj in db.Tables)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "Table";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (obj.IsSystemObject == false && 1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts).Trim();
                                    //filteredScripts = RemoveMultiLineBlockComments(filteredScripts);

                                    string relationJson = "";

                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";
                                    List<ObjDependency> dependsOnList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, true);
                                    List<ObjDependency> dependsOnByList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, false);


                                    if (dependsOnList.Count > 0)
                                    {
                                        dependsOnStr = JsonConvert.SerializeObject(dependsOnList, Formatting.Indented);
                                    }

                                    if (dependsOnByList.Count > 0)
                                    {
                                        dependsOnByStr = JsonConvert.SerializeObject(dependsOnByList, Formatting.Indented);
                                    }

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, relationJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

                                    _lineageLog += $"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}";
                                    logWriter.Write($"## Scripted Object ({obj.Urn.Value}) {Environment.NewLine}");
                                    logWriter.Flush();
                                    _objectCounter++;

                                    // Now loop through each column in the table
                                    foreach (Column column in obj.Columns)
                                    {
                                        string fullColNane = $"[{obj.Schema}].[{obj.Name}].[{column.Name}]";

                                        string colScritp = GenerateColumnScript(column);

                                        InsertTableColumnData(scriptGenId, fullColNane, "Column", colScritp, sqlFlowCon);
                                    }

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

                            foreach (Table obj in db.Tables)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (obj.IsSystemObject == false && 1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts).Trim();
                                    //filteredScripts = RemoveMultiLineBlockComments(filteredScripts);

                                    foreach (Trigger objs in obj.Triggers)
                                    {
                                        if (objs.IsSystemObject == false)
                                        {
                                            var scripts2 = objs.Script(sOpt).Cast<string>();
                                            var filteredScripts2 = FilterScript(scripts2, RemoveParts);
                                            filteredScripts2 = RemoveMultiLineBlockComments(filteredScripts2).Trim();
                                            string scriptName2 = $"[{obj.Schema}].[{objs.Name}]";
                                            string scriptType2 = "Trigger";

                                            string JoinsJson2 = "";

                                            string dependsOnStr2 = "";
                                            string dependsOnByStr2 = "";

                                            List<ObjDependency> dependsOnList2 = DiscoverDependencies(scriptName2, objs.Urn, scripter, walker, true);
                                            List<ObjDependency> dependsOnByList2 = DiscoverDependencies(scriptName2, objs.Urn, scripter, walker, false);


                                            if (dependsOnList2.Count > 0)
                                            {
                                                dependsOnStr2 = JsonConvert.SerializeObject(dependsOnList2, Formatting.Indented);
                                            }

                                            if (dependsOnByList2.Count > 0)
                                            {
                                                dependsOnByStr2 = JsonConvert.SerializeObject(dependsOnByList2, Formatting.Indented);
                                            }

                                            //List<ObjDependency> dependsOnList2 = new List<ObjDependency>();

                                            //string uDb = objs.Urn.XPathExpression.GetAttribute("Name", "Database");
                                            //string uSch = objs.Urn.XPathExpression.GetAttribute("Schema", objs.Urn.Type);
                                            //string uObj = objs.Urn.XPathExpression.GetAttribute("Name", objs.Urn.Type);

                                            //ObjDependency ab = new ObjDependency();
                                            //ab.RootObject = scriptName;
                                            //ab.Database = uDb;
                                            //ab.Schema = uSch;
                                            //ab.Name = uObj;
                                            //ab.Type = objs.Urn.Type;

                                            //dependsOnList2.add(ab);

                                            //if (dependsOnList2.Count > 0)
                                            //{
                                            //    dependsOnStr2 = JsonConvert.SerializeObject(dependsOnList2, Newtonsoft.Json.Formatting.Indented);
                                            //}

                                            InsertScriptIntoDatabase(scriptGenId, scriptName2, scriptType2, filteredScripts2, JoinsJson2, dependsOnStr2, dependsOnByStr2, sqlFlowCon);
                                            _lineageLog += $"## Scripted Object ({objs.Urn.Value}) {Environment.NewLine}";
                                            logWriter.Write($"## Scripted Object ({objs.Urn.Value}) {Environment.NewLine}");
                                            logWriter.Flush();
                                            _objectCounter++;

                                            EventArgsSourceControl arg2 = new EventArgsSourceControl
                                            {
                                                Total = _total,
                                                Processed = _objectCounter,
                                                ObjectUrn = obj.Urn.Value,
                                                InQueue = _total - _objectCounter,
                                                Status = _objectCounter / (double)_total
                                            };
                                            OnObjectScripted?.Invoke(Thread.CurrentThread, arg2);
                                        }
                                    }
                                }
                            }

                            foreach (Sequence obj in db.Sequences)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "Sequence";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    string JoinsJson = "";
                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, JoinsJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

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

                            foreach (Synonym obj in db.Synonyms)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "Synonym";

                                if (1 == (ObjectName.Length > 0
                                        ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                            ? 1
                                            : 0
                                        : 1))
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    string JoinsJson = "";
                                    string dependsOnStr = "";
                                    string dependsOnByStr = "";

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, JoinsJson, dependsOnStr, dependsOnByStr, sqlFlowCon);

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

                            foreach (UserDefinedTableType obj in db.UserDefinedTableTypes)
                            {
                                string scriptName = $"[{obj.Schema}].[{obj.Name}]";
                                string scriptType = "UserDefinedTableType";

                                int mode = ObjectName.Length > 0
                                    ? scriptName.Equals(ObjectName, StringComparison.InvariantCultureIgnoreCase)
                                        ? 1
                                        : 0
                                    : 1;

                                if (1 == mode)
                                {
                                    var scripts = obj.Script(sOpt).Cast<string>();
                                    var filteredScripts = FilterScript(scripts, RemoveParts);
                                    filteredScripts = RemoveMultiLineBlockComments(filteredScripts).Trim();

                                    string JoinsJson = "";

                                    List<ObjDependency> dependsOnList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, true);
                                    string dependsOnStr = "";

                                    List<ObjDependency> dependsOnByList = DiscoverDependencies(scriptName, obj.Urn, scripter, walker, false);
                                    string dependsOnByStr = "";

                                    if (dependsOnList.Count > 0)
                                    {
                                        dependsOnStr = JsonConvert.SerializeObject(dependsOnList, Formatting.Indented);
                                    }

                                    if (dependsOnByList.Count > 0)
                                    {
                                        dependsOnByStr = JsonConvert.SerializeObject(dependsOnByList, Formatting.Indented);
                                    }

                                    InsertScriptIntoDatabase(scriptGenId, scriptName, scriptType, filteredScripts, JoinsJson, dependsOnStr, dependsOnByStr, sqlFlowCon);



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

                            string cmdSQL = @"[flw].[AddSysDocRelations]";
                            using (var command = new SqlCommand(cmdSQL, sqlFlowCon))
                            {
                                command.CommandType = CommandType.StoredProcedure;
                                command.ExecuteNonQuery();
                            }
                            _lineageLog += $"## Executed [flw].[AddSysDocRelations] {Environment.NewLine}";
                            logWriter.Write($"## Executed [flw].[AddSysDocRelations] {Environment.NewLine}");
                            logWriter.Flush();
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
                _lineageLog += Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}",
                    logDurationPre.ToString(), Environment.NewLine);
                logWriter.Write(Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}",
                    logDurationPre.ToString(), Environment.NewLine));
                logWriter.Flush();
            }

            //return result;
        }
        #endregion ExecSysDocGen

        /// <summary>
        /// Discovers the dependencies of a given root object.
        /// </summary>
        /// <param name="rootObject">The name of the root object for which dependencies are to be discovered.</param>
        /// <param name="objUrn">The URN (Uniform Resource Name) of the root object.</param>
        /// <param name="scripter">The scripter used to discover dependencies.</param>
        /// <param name="walker">The dependency walker used to walk through the dependencies.</param>
        /// <param name="isDependencyBy">A boolean value indicating whether the dependencies are by the root object or on the root object.</param>
        /// <returns>A list of object dependencies for the given root object.</returns>
        private static List<ObjDependency> DiscoverDependencies(string rootObject, Urn objUrn, Scripter scripter, DependencyWalker walker, bool isDependencyBy)
        {
            var dependencyTree = scripter.DiscoverDependencies(new Urn[] { objUrn }, isDependencyBy);
            var dependencyNodes = walker.WalkDependencies(dependencyTree);
            var dependencies = new List<ObjDependency>();
            foreach (var node in dependencyNodes)
            {
                if (objUrn != node.Urn)
                {
                    string uDb = node.Urn.XPathExpression.GetAttribute("Name", "Database");
                    string uSch = node.Urn.XPathExpression.GetAttribute("Schema", node.Urn.Type);
                    string uObj = node.Urn.XPathExpression.GetAttribute("Name", node.Urn.Type);

                    var dependency = new ObjDependency
                    {
                        RootObject = rootObject,
                        Database = uDb,
                        Schema = uSch,
                        Name = uObj,
                        Type = node.Urn.Type
                    };
                    dependencies.Add(dependency);
                }
            }
            return dependencies;
        }

        /// <summary>
        /// Removes multi-line block comments from the provided code string.
        /// </summary>
        /// <param name="code">The code string from which to remove multi-line block comments.</param>
        /// <returns>The code string with multi-line block comments removed.</returns>
        /// <exception cref="ArgumentException">Thrown when a block comment is not closed.</exception>
        internal static string RemoveMultiLineBlockComments(string code)
        {
            int commentStartIndex = 0;
            while ((commentStartIndex = code.IndexOf("/*", commentStartIndex, StringComparison.Ordinal)) != -1)
            {
                var commentEndIndex = code.IndexOf("*/", commentStartIndex + 2, StringComparison.Ordinal);
                if (commentEndIndex == -1)
                {
                    throw new ArgumentException("Block comment is not closed", nameof(code));
                }
                var comment = code.Substring(commentStartIndex, commentEndIndex - commentStartIndex + 2);
                if (comment.Contains(Environment.NewLine))
                {
                    code = code.Remove(commentStartIndex, commentEndIndex - commentStartIndex + 2);
                }
                else
                {
                    commentStartIndex = commentEndIndex + 2;
                }
            }
            return code;
        }

        /// <summary>
        /// Inserts a script into the database using the provided parameters.
        /// </summary>
        /// <param name="scriptGenId">The identifier for the script generation process.</param>
        /// <param name="scriptName">The name of the script.</param>
        /// <param name="scriptType">The type of the script.</param>
        /// <param name="scriptContent">The content of the script.</param>
        /// <param name="relation">The relation JSON string.</param>
        /// <param name="dependsOn">The JSON string representing the dependencies of the script.</param>
        /// <param name="dependsOnBy">The JSON string representing the dependencies on the script.</param>
        /// <param name="sqlConnection">The SQL connection to use for the operation.</param>
        internal static void InsertScriptIntoDatabase(string scriptGenId, string scriptName, string scriptType, string scriptContent, string relation, string dependsOn, string dependsOnBy, SqlConnection sqlConnection)
        {
            string cmdSQL = @"[flw].[AddSysDoc]";
            using (var command = new SqlCommand(cmdSQL, sqlConnection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = scriptName;
                command.Parameters.Add("@ObjectType", SqlDbType.NVarChar).Value = scriptType;
                command.Parameters.Add("@ObjectDef", SqlDbType.NVarChar).Value = scriptContent;
                command.Parameters.Add("@RelationJson", SqlDbType.NVarChar).Value = relation;
                command.Parameters.Add("@DependsOnJson", SqlDbType.NVarChar).Value = dependsOn;
                command.Parameters.Add("@DependsOnByJson", SqlDbType.NVarChar).Value = dependsOnBy;
                command.Parameters.Add("@ScriptGenID", SqlDbType.BigInt).Value = long.Parse(scriptGenId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Inserts the script content of a table column into the database.
        /// </summary>
        /// <param name="scriptGenId">The identifier for the script generation process.</param>
        /// <param name="scriptName">The name of the script, typically the fully qualified name of the column.</param>
        /// <param name="scriptType">The type of the script, in this case, "Column".</param>
        /// <param name="scriptContent">The content of the script, which is the SQL definition of the column.</param>
        /// <param name="sqlConnection">The SQL connection to the database where the script content will be inserted.</param>
        /// <remarks>
        /// This method uses the stored procedure "[flw].[AddSysDoc]" to insert the script content into the database.
        /// </remarks>
        internal static void InsertTableColumnData(string scriptGenId, string scriptName, string scriptType, string scriptContent, SqlConnection sqlConnection)
        {
            string cmdSQL = @"[flw].[AddSysDoc]";
            using (var command = new SqlCommand(cmdSQL, sqlConnection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = scriptName;
                command.Parameters.Add("@ObjectType", SqlDbType.NVarChar).Value = scriptType;
                command.Parameters.Add("@ObjectDef", SqlDbType.NVarChar).Value = scriptContent;
                command.Parameters.Add("@ScriptGenID", SqlDbType.BigInt).Value = long.Parse(scriptGenId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Filters the given scripts by removing the specified filters.
        /// </summary>
        /// <param name="scripts">An IEnumerable of scripts to be filtered.</param>
        /// <param name="filters">An array of strings to be removed from the scripts.</param>
        /// <returns>A single string that represents the combined scripts after the filters have been removed.</returns>
        internal static string FilterScript(IEnumerable<string> scripts, params string[] filters)
        {
            var combinedScript = string.Join(Environment.NewLine, scripts);
            foreach (var filter in filters)
            {
                combinedScript = combinedScript.Replace(filter, string.Empty, StringComparison.InvariantCultureIgnoreCase);
            }
            return combinedScript;
        }

        /// <summary>
        /// Generates a script for a given SQL column.
        /// </summary>
        /// <param name="column">The SQL column for which the script is to be generated.</param>
        /// <returns>A string representing the SQL script for the given column.</returns>
        /// <remarks>
        /// This method handles different data types, identity columns, nullability, default values, and computed columns.
        /// Additional handling for SPARSE Columns, Collation, etc., can be added in the future.
        /// </remarks>
        internal static string GenerateColumnScript(Column column)
        {
            string columnScript = $"[{column.Name}] ";

            // Handling different data types
            switch (column.DataType.SqlDataType)
            {
                case SqlDataType.Decimal:
                case SqlDataType.Numeric:
                    columnScript +=
                        $"{column.DataType.Name}({column.DataType.NumericPrecision}, {column.DataType.NumericScale})";
                    break;
                case SqlDataType.VarChar:
                case SqlDataType.NVarChar:
                case SqlDataType.VarBinary:
                    string maxLength = column.DataType.MaximumLength == -1
                        ? "MAX"
                        : column.DataType.MaximumLength.ToString();
                    columnScript += $"{column.DataType.Name}({maxLength})";
                    break;
                case SqlDataType.Timestamp:
                    columnScript += "ROWVERSION";
                    break;
                default:
                    columnScript += column.DataType.Name;
                    break;
            }

            // Handling identity columns
            if (column.Identity)
            {
                columnScript += $" IDENTITY({column.IdentitySeed},{column.IdentityIncrement})";
            }

            // Adding NOT NULL or NULL
            columnScript += column.Nullable ? " NULL" : " NOT NULL";

            // Handling default values
            if (column.DefaultConstraint != null && !string.IsNullOrEmpty(column.DefaultConstraint.Text))
            {
                columnScript += $" DEFAULT {column.DefaultConstraint.Text}";
            }

            // Handling computed columns
            if (column.Computed)
            {
                columnScript += $" AS {column.ComputedText}";
            }

            // Additional handling for SPARSE Columns, Collation, etc., can be added here

            return columnScript;
        }

        private static void Scripter_ScriptingProgress1(object sender, ProgressReportEventArgs e)
        {
            Console.WriteLine($"{e.Current.Value} {e.TotalCount}/{e.Total}");
        }

        private static void ScripterX_ScriptingProgress(object sender, ProgressReportEventArgs e)
        {
            Console.WriteLine($"{e.Current.Value} {e.TotalCount}/{e.Total}");

        }
    }

    /// <summary>
    /// Represents a dependency object in a SQL database.
    /// </summary>
    public class ObjDependency
    {
        /// <summary>
        /// Gets or sets the root object of the dependency.
        /// </summary>
        public string RootObject { get; set; }
        /// <summary>
        /// Gets or sets the database where the dependency object is located.
        /// </summary>
        public string Database { get; set; }
        /// <summary>
        /// Gets or sets the schema of the dependency object.
        /// </summary>
        public string Schema { get; set; }
        /// <summary>
        /// Gets or sets the name of the dependency object.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Gets or sets the type of the dependency object.
        /// </summary>
        public string Type { get; set; }
    }


}