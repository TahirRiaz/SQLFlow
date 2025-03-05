using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a batch execution flow in SQLFlowCore engine.
    /// </summary>
    /// <remarks>
    /// This class provides functionality to execute a batch of SQL commands and 
    /// raises several events during the execution process. It includes events for 
    /// when the flow is processed, when rows are copied, when an invoke operation 
    /// is running, and when a batch is done.
    /// </remarks>
    /// <example>
    /// This sample shows how to use the `ExecFlowBatch` class.
    /// <code>
    /// SQLFlowCore.Engine.ExecFlowBatch.OnFlowProcessed += OnBatchEvent;
    /// string path = Directory.GetCurrentDirectory() + $"\\Batch_{_batch}_{_flowType}.txt";
    /// StreamWriter writer = new StreamWriter(path);
    /// _result = SQLFlowCore.Engine.ExecFlowBatch.Exec(writer, _conStr.Trim(), _batch, _flowType, _sysAlias, _execMode, _dbg);
    /// </code>
    /// </example>
    public class ExecFlowBatch
    {
        private static string _execFlowBatchLog = "";
        private static readonly object BatchObjLock = new();
        private static int _sharedCounter = 0;

        /// <summary>
        /// Occurs when a flow in the batch has been processed.
        /// </summary>
        /// <remarks>
        /// This event is triggered after each flow in the batch is processed, providing information about the current status of the batch execution.
        /// </remarks>
        public static event EventHandler<EventArgsBatch> OnFlowProcessed;

        /// <summary>
        /// Occurs when rows are copied during the batch execution process.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the batch execution process when rows are copied. 
        /// It provides information about the copied rows through the EventArgsRowsCopied object.
        /// </remarks>
        /// <example>
        /// This sample shows how to use the `OnRowsCopied` event.
        /// <code>
        /// SQLFlowCore.Engine.ExecFlowBatch.OnRowsCopied += OnRowsCopiedEvent;
        /// </code>
        /// </example>
        public static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        /// <summary>
        /// Occurs when an invoke operation is running in the execution flow.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of a batch of SQL commands when an invoke operation is running. 
        /// It provides real-time tracking of the invoke operations within the execution flow.
        /// </remarks>
        /// <example>
        /// This sample shows how to use the `InvokeIsRunning` event.
        /// <code>
        /// SQLFlowCore.Engine.ExecFlowBatch.InvokeIsRunning += OnInvokeEvent;
        /// </code>
        /// </example>
        public static event EventHandler<EventArgsInvoke> InvokeIsRunning;

        /// <summary>
        /// Occurs when the batch execution is completed.
        /// </summary>
        /// <remarks>
        /// This event is triggered at the end of the batch execution process, whether it completes successfully or encounters an error.
        /// It provides an opportunity to perform cleanup operations or log the completion status.
        /// </remarks>
        /// <example>
        /// This sample shows how to use the `OnBatchDone` event.
        /// <code>
        /// SQLFlowCore.Engine.ExecFlowBatch.OnBatchDone += OnBatchDoneEvent;
        /// </code>
        /// </example>
        public static event EventHandler<EventArgsBatch> OnBatchDone;

        #region ExecFlowBatch
        /// <summary>
        /// Executes a SQL Flow batch.
        /// </summary>
        /// <param name="logWriter">The StreamWriter object to write logs.</param>
        /// <param name="sqlFlowConString">The SQL Flow connection string.</param>
        /// <param name="batchList">The list of batches to be executed. Default is an empty string.</param>
        /// <param name="flowtype">The type of flow to be executed. Default is an empty string.</param>
        /// <param name="sysAlias">The system alias. Default is an empty string.</param>
        /// <param name="execMode">The execution mode. Default is "Batch".</param>
        /// <param name="dbg">Debug mode flag. Default is 0.</param>
        /// <returns>A string representing the result of the execution.</returns>
        public static string Exec(StreamWriter logWriter,
                                  string sqlFlowConString,
                                  string batchList = "",
                                  string flowtype = "",
                                  string sysAlias = "",
                                  string execMode = "Batch",
                                  int dbg = 0)
        {
            ///ExecFlowBatch a = new ExecFlowBatch();
            var result = "false";
            var codeStack = "";
            _execFlowBatchLog = "";

            //this is used to control the flow of the batch
            bool ProcessNextBatch = true;

            //Parses the orginal connection string and adds the Application Name
            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            string batchDsCmd = $@"exec [flw].[GetRVFlowBatch] @BatchList = '{batchList}', @flowType = '{flowtype}', @sysAlias = '{sysAlias}', @dbg = {dbg.ToString()}";

            lock (BatchObjLock)
            {
                _execFlowBatchLog += "## " + batchDsCmd + Environment.NewLine;
            }

            lock (logWriter)
            {
                logWriter.Write("## " + batchDsCmd + Environment.NewLine);
            }

            long logDurationPre = 0;
            var totalTime = new Stopwatch();
            totalTime.Start();

            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    int maxConcurrency = 1;
                    var flowData = new GetData(sqlFlowCon, batchDsCmd, 720);
                    DataTable bDSTbl = flowData.Fetch();

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();

                    string _Log = "";

                    if (bDSTbl.Rows.Count > 0)
                    {
                        maxConcurrency = int.Parse(bDSTbl.Rows[0]["maxConcurrency"]?.ToString() ?? string.Empty);
                    }

                    int _Total = bDSTbl.Rows.Count;

                    List<DataTable> execInParallel = bDSTbl.AsEnumerable()
                        .AsParallel().AsOrdered()
                        .OrderBy(row => row.Field<int>("Step"))
                        .GroupBy(row => row.Field<int>("Step"))
                        .Select(g => g.CopyToDataTable())
                        .ToList();

                    lock (BatchObjLock)
                    {
                        _execFlowBatchLog += $"##### Batch: Total Flow Count: {_Total.ToString()} {Environment.NewLine}";
                    }
                    lock (logWriter)
                    {
                        logWriter.Write($"##### Batch: Total Flow Count: {_Total.ToString()} {Environment.NewLine}");
                    }

                    // Subscribe to rows-copied and invoke events
                    ProcessIngestion.OnRowsCopied += HandlerOnRowsCopied;
                    ProcessCsv.OnRowsCopied += HandlerOnRowsCopied;
                    ProcessXls.OnRowsCopied += HandlerOnRowsCopied;
                    ProcessXml.OnRowsCopied += HandlerOnRowsCopied;
                    ProcessInvoke.InvokeIsRunning += ExecInvoke_InvokeIsRunning;
                    ProcessPrq.OnRowsCopied += HandlerOnRowsCopied;
                    ProcessPrc.OnRowsCopied += HandlerOnRowsCopied;

                    // Process each DataTable step-by-step
                    foreach (DataTable tb in execInParallel)
                    {
                        if (ProcessNextBatch)
                        {
                            // Removed using(...) because Semaphore does not implement IDisposable
                            var concurrencySemaphore = new Semaphore(maxConcurrency, maxConcurrency);

                            try
                            {
                                var tasks = new List<Task>();
                                string BatchID = "";
                                string Batch = "";
                                int FlowID = 0;
                                string FlowType = "";
                                string SysAlias = "";
                                int Step = 0;
                                int StepCount = 0;
                                bool OnErrorResume = true;
                                int cCounter = 0;
                                bool SourceIsAzCont = true;
                                int rowIndex = 0;

                                if (tb.Rows.Count > 0)
                                {
                                    lock (BatchObjLock)
                                    {
                                        _execFlowBatchLog += $"########## Batch: Flow Count In Step {tb.Rows[0]["Step"]}: {tb.Rows.Count.ToString()} ##############################################{Environment.NewLine}";
                                    }
                                }

                                StepCount = tb.Rows.Count;

                                foreach (DataRow dr in tb.Rows)
                                {
                                    BatchID = dr["BatchID"]?.ToString() ?? string.Empty;
                                    Batch = dr["Batch"]?.ToString() ?? string.Empty;
                                    FlowID = int.Parse(dr["FlowID"]?.ToString() ?? string.Empty);
                                    FlowType = dr["FlowType"]?.ToString() ?? string.Empty;
                                    SysAlias = dr["SysAlias"]?.ToString() ?? string.Empty;
                                    Step = int.Parse(dr["Step"]?.ToString() ?? string.Empty);
                                    rowIndex = tb.Rows.IndexOf(dr) + 1;

                                    OnErrorResume = (dr["OnErrorResume"]?.ToString() ?? string.Empty).Equals("True");
                                    dbg = int.Parse(dr["dbg"]?.ToString() ?? string.Empty);
                                    SourceIsAzCont = (dr["SourceIsAzCont"]?.ToString() ?? string.Empty).Equals("True");

                                    string _BatchID = BatchID;
                                    string _Batch = Batch;
                                    int _FlowID = FlowID;
                                    string _FlowType = FlowType;
                                    string _SysAlias = SysAlias;
                                    int _Step = Step;
                                    int _StepCount = StepCount;
                                    bool _OnErrorResume = OnErrorResume;
                                    bool _sourceIsAzCont = SourceIsAzCont;
                                    StreamWriter _tw = logWriter;
                                    int _rowIndex = rowIndex;

                                    SqlFlowParam sqlFlowParam = new SqlFlowParam(_sourceIsAzCont, _FlowType)
                                    {
                                        sqlFlowConString = sqlFlowConString,
                                        flowId = _FlowID,
                                        execMode = execMode,
                                        dbg = dbg,
                                        batchId = _BatchID
                                    };

                                    var t = Task.Factory.StartNew(() =>
                                    {
                                        try
                                        {
                                            concurrencySemaphore.WaitOne();

                                            string NewFlow = "---------------------------------------------------------------------------------------------" + Environment.NewLine;
                                            var execTime = new Stopwatch();
                                            execTime.Start();

                                            switch (_FlowType)
                                            {
                                                case "ado":
                                                    _Log = ProcessADO.Exec(sqlFlowParam);
                                                    break;
                                                case "ing":
                                                    _Log = ProcessIngestion.Exec(sqlFlowParam);
                                                    break;
                                                case "csv":
                                                    _Log = ProcessCsv.Exec(sqlFlowParam);
                                                    break;
                                                case "xls":
                                                    _Log = ProcessXls.Exec(sqlFlowParam);
                                                    break;
                                                case "xml":
                                                    _Log = ProcessXml.Exec(sqlFlowParam);
                                                    break;
                                                case "jsn":
                                                    _Log = ProcessJsn.Exec(sqlFlowParam);
                                                    break;
                                                case "exp":
                                                    _Log = ExecExport.Exec(sqlFlowParam);
                                                    break;
                                                case "prq":
                                                    _Log = ProcessPrq.Exec(sqlFlowParam);
                                                    break;
                                                case "prc":
                                                    _Log = ProcessPrc.Exec(sqlFlowParam);
                                                    break;
                                                case "inv":
                                                case "adf":
                                                case "aut":
                                                case "ps":
                                                case "cs":
                                                    _Log = ProcessInvoke.Exec(sqlFlowParam);
                                                    break;
                                                case "sp":
                                                    _Log = ProcessStoredProcedure.Exec(sqlFlowParam);
                                                    break;
                                            }

                                            // Thread-safe concatenation to _execFlowBatchLog
                                            lock (BatchObjLock)
                                            {
                                                _execFlowBatchLog += NewFlow + _Log;
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            // If OnErrorResume is false, propagate exception
                                            concurrencySemaphore.Release();
                                            if (_OnErrorResume == false)
                                            {
                                                result = _execFlowBatchLog + e.StackTrace + Environment.NewLine + codeStack;
                                                lock (_tw)
                                                {
                                                    _tw.Write(e.StackTrace + Environment.NewLine + codeStack);
                                                }
                                                throw;
                                            }
                                        }
                                        finally
                                        {
                                            // Always release the semaphore
                                            concurrencySemaphore.Release();
                                        }
                                    }, TaskCreationOptions.LongRunning);

                                    t.ContinueWith(_ =>
                                    {
                                        batchStepCompleted(_tw, _Log, _Total, _Batch, _FlowID, _FlowType, _SysAlias, _OnErrorResume, _Step, _StepCount, _rowIndex);
                                    }, TaskContinuationOptions.OnlyOnRanToCompletion);

                                    tasks.Add(t);
                                    cCounter = cCounter + 1;
                                }

                                Task.WaitAll(tasks.ToArray());
                            }
                            finally
                            {
                                // Properly close the semaphore to release OS resources
                                concurrencySemaphore.Close();
                            }

                            if (ProcessNextBatch == false)
                            {
                                break;
                            }
                        }
                    }

                    EventArgsBatch arg = new EventArgsBatch
                    {
                        Done = true
                    };
                    OnBatchDone?.Invoke(Thread.CurrentThread, arg);
                }
                catch (Exception e)
                {
                    EventArgsBatch arg = new EventArgsBatch
                    {
                        Done = true
                    };
                    OnBatchDone?.Invoke(Thread.CurrentThread, arg);

                    //Error returned to client
                    result = _execFlowBatchLog + e.StackTrace + Environment.NewLine + codeStack;
                    lock (logWriter)
                    {
                        logWriter.Write(e.StackTrace + Environment.NewLine + codeStack);
                    }
                }
                finally
                {
                    // Unsubscribe to prevent potential memory leaks if Exec is called repeatedly
                    ProcessIngestion.OnRowsCopied -= HandlerOnRowsCopied;
                    ProcessCsv.OnRowsCopied -= HandlerOnRowsCopied;
                    ProcessXls.OnRowsCopied -= HandlerOnRowsCopied;
                    ProcessXml.OnRowsCopied -= HandlerOnRowsCopied;
                    ProcessInvoke.InvokeIsRunning -= ExecInvoke_InvokeIsRunning;
                    ProcessPrq.OnRowsCopied -= HandlerOnRowsCopied;
                    ProcessPrc.OnRowsCopied -= HandlerOnRowsCopied;
                }

                totalTime.Stop();
                logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                lock (BatchObjLock)
                {
                    _execFlowBatchLog += Environment.NewLine + string.Format("Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine);
                }

                result = _execFlowBatchLog;
            }

            return result;
        }

        /// <summary>
        /// Handles the InvokeIsRunning event raised by the ProcessInvoke class.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsInvoke that contains the event data.</param>
        /// <remarks>
        /// This method is called when the InvokeIsRunning event is raised. It then raises the InvokeIsRunning event of the ExecFlowBatch class, forwarding the event data.
        /// </remarks>
        private static void ExecInvoke_InvokeIsRunning(object sender, EventArgsInvoke e)
        {
            InvokeIsRunning?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the event when rows are copied during the execution of a batch.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An <see cref="EventArgsRowsCopied"/> instance that contains the event data.</param>
        /// <remarks>
        /// This method is invoked when the `OnRowsCopied` event is raised. It passes the event along with its data to any objects that have subscribed to the `OnRowsCopied` event.
        /// </remarks>
        private static void HandlerOnRowsCopied(object sender, EventArgsRowsCopied e)
        {
            OnRowsCopied?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the completion of a batch step in the execution flow.
        /// </summary>
        /// <param name="_sw">The StreamWriter object used for logging.</param>
        /// <param name="_log">The log message for the completed step.</param>
        /// <param name="_Total">The total number of steps in the batch.</param>
        /// <param name="_Batch">The batch identifier.</param>
        /// <param name="_FlowID">The flow identifier.</param>
        /// <param name="_FlowType">The type of the flow.</param>
        /// <param name="_SysAlias">The system alias.</param>
        /// <param name="_OnErrorResume">Indicates whether to resume on error.</param>
        /// <param name="_Step">The current step number.</param>
        /// <param name="_StepCount">The total number of steps.</param>
        /// <param name="_rowIndex">The row index.</param>
        /// <remarks>
        /// This method writes a log message for the completed step, updates the shared counter, 
        /// calculates the status, and raises the OnFlowProcessed event.
        /// </remarks>
        static void batchStepCompleted(StreamWriter _sw,
                                       string _log,
                                       int _Total,
                                       string _Batch,
                                       int _FlowID,
                                       string _FlowType,
                                       string _SysAlias,
                                       bool _OnErrorResume,
                                       int _Step,
                                       int _StepCount,
                                       int _rowIndex)
        {
            lock (_sw)
            {
                _sw.Write($"----- Step {_Step} in batch {_Batch} flow {_rowIndex} of {_StepCount} ------------------------------------------------------------------------------------{Environment.NewLine}" + _log);
            }

            Interlocked.Increment(ref _sharedCounter);
            double Status = _sharedCounter / (double)_Total;

            EventArgsBatch arg = new EventArgsBatch
            {
                Done = false,
                ResultLog = _log,
                Step = _Step,
                Batch = _Batch,
                FlowID = _FlowID,
                FlowType = _FlowType,
                SysAlias = _SysAlias,
                OnErrorResume = _OnErrorResume,
                InTotal = _Total,
                InQueue = _Total - _sharedCounter,
                Processed = _sharedCounter,
                Status = Status
            };
            OnFlowProcessed?.Invoke(Thread.CurrentThread, arg);
        }
        #endregion ExecFlowBatch
    }
}
