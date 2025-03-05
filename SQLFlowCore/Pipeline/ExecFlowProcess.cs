using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents the execution flow process in SQLFlowCore. This class cannot be inherited.
    /// </summary>
    /// <remarks>
    /// The ExecFlowProcess class provides a series of static events that are triggered during the execution of a SQL flow process.
    /// These events include OnRowsCopied, InvokeIsRunning, OnProcessCompleted, OnFileExported, and OnPrcExecuted.
    /// The class also provides a static method, Exec, which executes the flow process and returns a string result.
    /// </remarks>
    public class ExecFlowProcess
    {

        /// <summary>
        /// Occurs when rows are copied in the execution flow process.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of the flow process when rows are copied. 
        /// It allows for additional actions or logging to be performed when this specific event occurs.
        /// </remarks>
        public static event EventHandler<EventArgsRowsCopied> OnRowsCopied;

        /// <summary>
        /// Occurs when an invocation process is running.
        /// </summary>
        /// <remarks>
        /// This event is triggered during the execution of the SQL flow process when an invocation is running.
        /// The EventArgsInvoke object passed to the event handler contains information about the invoked object and the duration of the invocation.
        /// </remarks>
        public static event EventHandler<EventArgsInvoke> InvokeIsRunning;

        /// <summary>
        /// Occurs when the execution flow process is completed.
        /// </summary>
        /// <remarks>
        /// This event provides data through an <see cref="EventArgsProcess"/> instance.
        /// </remarks>
        public static event EventHandler<EventArgsProcess> OnProcessCompleted;

        /// <summary>
        /// Occurs when a file has been exported in the SQL flow process.
        /// </summary>
        /// <remarks>
        /// This event is triggered after a file export operation is completed in the SQL flow process.
        /// The event handler receives an argument of type <see cref="EventArgsExport"/> containing data related to this event.
        /// </remarks>
        public static event EventHandler<EventArgsExport> OnFileExported;

        /// <summary>
        /// Occurs when a stored procedure execution is completed in the SQL flow process.
        /// </summary>
        /// <remarks>
        /// This event is triggered after the successful execution of a stored procedure within the SQL flow process.
        /// The event handler receives an argument of type <see cref="EventArgsPrc"/> containing data related to this event.
        /// </remarks>
        public static event EventHandler<EventArgsPrc> OnPrcExecuted;
        private static string _execProcessLog = "";

        #region ExecFlowProcess
        /// <summary>
        /// Executes the SQL flow process.
        /// </summary>
        /// <param name="logWriter">The StreamWriter object to write logs.</param>
        /// <param name="sqlFlowConString">The connection string for the SQL flow.</param>
        /// <param name="flowid">The ID of the flow to be executed.</param>
        /// <param name="execMode">The execution mode. Default is "Man".</param>
        /// <param name="dbg">The debug level. Default is 0.</param>
        /// <returns>A string containing the execution process log.</returns>
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, int flowid, string execMode = "Man", int dbg = 0, string srcFileWithPath = "")
        {
            var result = "false";
            _execProcessLog = "";

            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            var totalTime = new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    SqlFlowParam sqlFlowParam = new SqlFlowParam(sqlFlowCon, flowid);
                    sqlFlowParam.sqlFlowConString = sqlFlowConString;
                    sqlFlowParam.flowId = flowid;
                    sqlFlowParam.execMode = execMode;
                    sqlFlowParam.dbg = dbg;
                    sqlFlowParam.srcFileWithPath = srcFileWithPath;

                    var execTime = new Stopwatch();
                    execTime.Start();
                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();

                    switch (sqlFlowParam.flowType)
                    {
                        case "ado":
                            ProcessADO.OnInitLoadBatchStepOnDone += (sender, e) => ExecIngestionADO_OnInitLoadBatchStepOnDone(sender, e, logWriter);
                            ProcessADO.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessADO.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "ing":
                            ProcessIngestion.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessIngestion.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "csv":
                            ProcessCsv.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessCsv.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "xls":
                            //ExecIngestionAzXls.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            ProcessXls.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessXls.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "prq":
                            ProcessPrq.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessPrq.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "prc":
                            ProcessPrc.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            ProcessPrc.OnPrcExecuted += (sender, e) => OnPrcExecuted_Handler(sender, e, logWriter);
                            _execProcessLog = ProcessPrc.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "xml":
                            ProcessXml.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessXml.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "jsn":
                            ProcessJsn.OnRowsCopied += (sender, e) => HandlerOnRowsCopied(sender, e, logWriter);
                            _execProcessLog = ProcessJsn.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "inv":
                        case "adf":
                        case "aut":
                        case "ps":
                        case "cs":
                            ProcessInvoke.InvokeIsRunning += (sender, e) => ExecInvoke_InvokeRunning(sender, e, logWriter);
                            _execProcessLog = ProcessInvoke.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "sp":
                            _execProcessLog = ProcessStoredProcedure.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;
                        case "exp":
                            ExecExport.OnFileExported += (sender, e) => OnFileExported_Handler(sender, e, logWriter);
                            _execProcessLog = ExecExport.Exec(sqlFlowParam);
                            logWriter.Write(_execProcessLog);
                            logWriter.Flush();
                            break;

                    }

                    EventArgsProcess arg = new EventArgsProcess
                    {
                        Completed = true,
                        Result = _execProcessLog
                    };
                    OnProcessCompleted?.Invoke(Thread.CurrentThread, arg);
                }
                catch (Exception e)
                {
                    //Error returned to client
                    result = _execProcessLog + Environment.NewLine + e.StackTrace;
                    logWriter.Write(_execProcessLog);
                    logWriter.Flush();
                    EventArgsProcess arg = new EventArgsProcess
                    {
                        Completed = true,
                        Result = result
                    };
                    OnProcessCompleted?.Invoke(Thread.CurrentThread, arg);
                    throw;
                }

                //totalTime.Stop();
                //logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                //_execNodeLog += string.Format("{1}Info: Total processing time {0} (sec) {1}", logDurationPre.ToString(), Environment.NewLine);
            }

            return _execProcessLog;
        }

        /// <summary>
        /// Handles the OnInitLoadBatchStepOnDone event of the ExecIngestionADO process.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The EventArgsInitLoadBatchStep instance containing the event data.</param>
        /// <param name="sw">The StreamWriter instance to write the output.</param>
        /// <remarks>
        /// This method writes the time-lapse information of the processed task to the provided StreamWriter.
        /// </remarks>
        private static void ExecIngestionADO_OnInitLoadBatchStepOnDone(object sender, EventArgsInitLoadBatchStep e, StreamWriter sw)
        {
            try
            {
                string Invoked = $"time-lapse (range {e.taskStatusCounter} processed in {e.RangeTimeSpan} sec)";
                sw.WriteAsync(Invoked + Environment.NewLine);
                sw.Flush();
            }
            catch { }
        }

        /// <summary>
        /// Handles the event when a file has been exported.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsExport instance that contains the event data.</param>
        /// <param name="sw">A StreamWriter instance to write the output.</param>
        /// <remarks>
        /// This method writes the number of rows exported, the full file name, and the progress to the StreamWriter.
        /// It also invokes the OnFileExported event if the total number of rows have been processed.
        /// </remarks>
        private static void OnFileExported_Handler(object sender, EventArgsExport e, StreamWriter sw)
        {
            //try
            //{
            //    string Invoked = $" {e.Rowcount} rows exported to {e.FullFileName} ({e.Processed}/{e.InTotal})";
            //    sw.WriteAsync(Invoked + Environment.NewLine);
            //    if (e.Processed == e.InTotal)
            //    {
            //        sw.WriteAsync(Invoked + Environment.NewLine);
            //    }

            //    sw.Flush();
            //}
            //catch { }

            OnFileExported?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the event when a process has been executed.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The EventArgsPrc instance containing the event data.</param>
        /// <param name="sw">The StreamWriter instance to write the output.</param>
        private static void OnPrcExecuted_Handler(object sender, EventArgsPrc e, StreamWriter sw)
        {
            try
            {
                sw.WriteAsync(e.Description + Environment.NewLine);
                if (e.Processed == e.InTotal)
                {
                    sw.WriteAsync(e.Description + Environment.NewLine);
                }

                sw.Flush();
            }
            catch { }

            OnPrcExecuted?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the execution of the invoked object and logs the processing time.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgsInvoke that contains the event data.</param>
        /// <param name="sw">The StreamWriter used to write the processing time to a stream.</param>
        private static void ExecInvoke_InvokeRunning(object sender, EventArgsInvoke e, StreamWriter sw)
        {
            try
            {
                string Invoked = $"{e.InvokedObjectName} processing time ({e.TimeSpan}) seconds";
                sw.WriteAsync(Invoked + Environment.NewLine);



                sw.Flush();
            }
            catch { }

            InvokeIsRunning?.Invoke(sender, e);
        }

        /// <summary>
        /// Handles the event when rows are copied.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The EventArgsRowsCopied instance containing the event data.</param>
        /// <param name="sw">The StreamWriter instance to write the status.</param>
        private static void HandlerOnRowsCopied(object sender, EventArgsRowsCopied e, StreamWriter sw)
        {
            try
            {
                //string IngStatus = $"Ingesting row {e.RowsProcessed} of {e.RowsInTotal}";
                //sw.WriteAsync(IngStatus + Environment.NewLine);
                //sw.Flush();
            }
            catch { }

            OnRowsCopied?.Invoke(sender, e);
        }

        #endregion ExecFlowProcess
    }
}