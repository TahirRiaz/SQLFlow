using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services;
using SQLFlowCore.Lineage;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a node in the execution flow of SQL operations.
    /// </summary>
    /// <remarks>
    /// This class provides functionality to execute SQL operations, handle events related to the execution flow, and manage data related to the execution flow.
    /// </remarks>
    /// <example>
    /// An example of using this class might look like this:
    /// <code>
    /// SQLFlowCore.Engine.ExecFlowNode.OnFlowProcessed += OnNodeEvent;
    /// SQLFlowCore.Engine.ExecFlowNode.OnRowsCopied += OnRowsCopied;
    /// SQLFlowCore.Engine.ExecFlowNode.InvokeIsRunning += InvokeIsRunning;
    /// string path = Directory.GetCurrentDirectory() + $"\\Node_{_node}_{_dir}.txt";
    /// StreamWriter writer = new StreamWriter(path);
    /// _result = SQLFlowCore.Engine.ExecFlowNode.Exec(writer, _conStr.Trim(), _node, _dir, _execMode, _exitOnError, _alldep, _allbatches, _dbg);
    /// </code>
    /// </example>
    public class ExecFlowNode
    {
        private static string _execNodeLog = "";
        private static int _sharedCounter = 0;

        /// <summary>
        /// Occurs when a node in the execution flow of SQL operations has been processed.
        /// </summary>
        /// <remarks>
        /// This event is triggered after a node has been processed in the SQL execution flow. 
        /// It provides information about the processed node, including its name, direction, flow ID, flow type, and status.
        /// </remarks>
        /// <example>
        /// An example of subscribing to this event might look like this:
        /// <code>
        /// SQLFlowCore.Engine.ExecFlowNode.OnFlowProcessed += OnNodeEvent;
        /// </code>
        /// </example>
        public static event EventHandler<EventArgsNode> OnFlowProcessed;

        /// <summary>
        /// Occurs when rows are copied during the execution of SQL operations.
        /// </summary>
        /// <remarks>
        /// This event is triggered each time a set of rows is copied as part of the SQL operation execution flow. 
        /// It provides information about the copied rows through the EventArgsRowsCopied event data.
        /// </remarks>
        /// <example>
        /// An example of subscribing to this event might look like this:
        /// <code>
        /// SQLFlowCore.Engine.ExecFlowNode.OnRowsCopied += OnRowsCopied;
        /// </code>
        /// </example>
        public static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        /// <summary>
        /// Occurs when the execution of a flow node is in progress.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of a flow node in the SQLFlow engine. 
        /// It can be used to track the running status of a flow node execution.
        /// </remarks>
        public static event EventHandler<EventArgsInvoke> InvokeIsRunning;

        #region ExecFlowNode
        /// <summary>
        /// Executes a flow node in SQLFlow.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to log execution details.</param>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="node">The node to be executed.</param>
        /// <param name="dir">The direction of execution.</param>
        /// <param name="execMode">The execution mode. Default is "node".</param>
        /// <param name="exitOnError">Indicates whether to exit on error. Default is true.</param>
        /// <param name="allDep">Indicates whether to execute all dependencies. Default is false.</param>
        /// <param name="allBatches">Indicates whether to execute all batches. Default is false.</param>
        /// <param name="dbg">Debug level. Default is 0.</param>
        /// <returns>A string representing the result of the execution.</returns>
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, string node, string dir, string execMode = "node", bool exitOnError = true, bool allDep = false, bool allBatches = false, int dbg = 0, string srcFileWithPath = "")
        {
            var result = "false";
            var codeStack = "";
            _execNodeLog = "";

            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            long logDurationPre = 0;
            var totalTime = new Stopwatch();

            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    int flowId;
                    string FlowType;
                    bool SourceIsAzCont = true;
                    bool DeactivateFromBatch = true;

                    DataTable bDSTbl = new DataTable();
                    if (dir == "A")
                    {
                        LineageDescendants dfs = new LineageDescendants(sqlFlowCon, int.Parse(node), allDep, allBatches);
                        bDSTbl = LineageHelper.GetMaxStepPerFlowID(dfs.GetResult());
                    }
                    else
                    {
                        LineageAncestors dfs = new LineageAncestors(sqlFlowCon, int.Parse(node), allDep, allBatches);
                        bDSTbl = LineageHelper.GetMaxStepPerFlowID(dfs.GetResult());
                    }

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                    totalTime.Start();

                    // Use LINQ to sort the DataTable
                    var orderedRows = from row in bDSTbl.AsEnumerable()
                                      orderby row.Field<int>("Step") ascending
                                      select row;

                    int NodeElements = orderedRows.Count();

                    foreach (DataRow dr in orderedRows)
                    {
                        flowId = int.Parse(dr["FlowID"]?.ToString() ?? string.Empty);
                        FlowType = dr["FlowType"]?.ToString() ?? string.Empty;
                        SourceIsAzCont = (dr["SourceIsAzCont"]?.ToString() ?? string.Empty).Equals("True");
                        DeactivateFromBatch = (dr["DeactivateFromBatch"]?.ToString() ?? string.Empty).Equals("True");

                        SqlFlowParam sqlFlowParam = new SqlFlowParam(SourceIsAzCont, FlowType);
                        sqlFlowParam.sqlFlowConString = sqlFlowConString;
                        sqlFlowParam.flowId = flowId;
                        sqlFlowParam.execMode = execMode;
                        sqlFlowParam.dbg = dbg;
                        sqlFlowParam.batchId = "0";
                        sqlFlowParam.srcFileWithPath = srcFileWithPath;

                        var execTime = new Stopwatch();
                        execTime.Start();

                        string stepLog = "";

                        if (DeactivateFromBatch == false)
                        {
                            try
                            {
                                InvokeNodeEvent(NodeElements, node, dir, flowId, FlowType);
                                switch (FlowType)
                                {
                                    case "ado":
                                        stepLog = ProcessADO.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "ing":
                                        ProcessIngestion.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessIngestion.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "csv":
                                        ProcessCsv.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessCsv.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "xls":
                                        ProcessXls.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessXls.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "xml":
                                        ProcessXml.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessXml.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "jsn":
                                        ProcessJsn.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessJsn.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "prq":
                                        ProcessPrq.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessPrq.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "prc":
                                        ProcessPrc.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ProcessPrc.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "exp":
                                        //ExecExport.OnFileExported += HandlerOnRowsCopied;
                                        //ProcessPrq.OnRowsCopied += HandlerOnRowsCopied;
                                        stepLog = ExecExport.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "inv":
                                    case "adf":
                                    case "aut":
                                    case "ps":
                                    case "cs":
                                        ProcessInvoke.InvokeIsRunning += ExecInvoke_InvokeIsRunning;
                                        stepLog = ProcessInvoke.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                    case "sp":
                                        stepLog = ProcessStoredProcedure.Exec(sqlFlowParam);
                                        _execNodeLog += stepLog;
                                        logWriter.Write(stepLog);
                                        logWriter.Flush();
                                        break;
                                }
                            }
                            catch (Exception e)
                            {
                                //If one of the nodes have failed stop execution
                                if (exitOnError)
                                {
                                    if (_execNodeLog.Contains("success:false"))
                                    {
                                        //Error returned to client
                                        result = _execNodeLog + e.StackTrace + Environment.NewLine + codeStack;
                                        logWriter.Write(e.StackTrace + Environment.NewLine + codeStack);
                                        logWriter.Flush();
                                        throw;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string cLog = $"Flow {dr["FlowID"].ToString()} is deactivated from batch and node execution";
                            _execNodeLog += cLog;
                            logWriter.Write(cLog);
                            logWriter.Flush();
                        }

                    }

                    totalTime.Stop();
                    logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                    _execNodeLog += string.Format("Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine);
                    logWriter.Write(string.Format("Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine));
                    logWriter.Flush();
                    result = _execNodeLog;
                }
                catch (Exception e)
                {
                    //Error returned to client
                    result = _execNodeLog + e.StackTrace + Environment.NewLine + codeStack;
                    logWriter.Write(e.StackTrace + Environment.NewLine + codeStack);
                    logWriter.Flush();

                    throw;
                }
                result = _execNodeLog;
            }
            return result;
        }



        /// <summary>
        /// Handles the invocation of the IsRunning event.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsInvoke that contains the event data.</param>
        private static void ExecInvoke_InvokeIsRunning(object sender, EventArgsInvoke e)
        {
            InvokeIsRunning?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the event when rows are copied.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsRowsCopied instance that contains the event data.</param>
        private static void HandlerOnRowsCopied(object sender, EventArgsRowsCopied e)
        {
            OnRowsCopied?.Invoke(sender, e);
        }

        /// <summary>
        /// Invokes the event for a node in the execution flow.
        /// </summary>
        /// <param name="nodeElements">The total number of elements in the node.</param>
        /// <param name="node">The name of the node.</param>
        /// <param name="dir">The direction of the node.</param>
        /// <param name="flowId">The identifier of the flow.</param>
        /// <param name="flowType">The type of the flow.</param>
        /// <remarks>
        /// This method increments a shared counter and calculates the status as a ratio of the shared counter to the total number of node elements. 
        /// It then creates an instance of EventArgsNode with the provided parameters and the calculated status, and invokes the OnFlowProcessed event.
        /// </remarks>
        static void InvokeNodeEvent(int nodeElements, string node, string dir, int flowId, string flowType)
        {
            //_execNodeLog += log + Environment.NewLine;
            Interlocked.Increment(ref _sharedCounter);
            double status = _sharedCounter / (double)nodeElements; //Adding One For the StatusBar

            EventArgsNode arg = new EventArgsNode()

            {
                Node = node,
                Direction = dir,
                FlowID = flowId,
                FlowType = flowType,
                InTotal = nodeElements,
                Processed = _sharedCounter,
                InQueue = nodeElements - _sharedCounter,
                Status = status
            };

            OnFlowProcessed?.Invoke(Thread.CurrentThread, arg);
        }

        #endregion ExecFlowNode
    }
}