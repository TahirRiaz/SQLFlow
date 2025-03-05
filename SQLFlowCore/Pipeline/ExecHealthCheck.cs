using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using SQLFlowCore.Common;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// The ExecHealthCheck class is responsible for executing health checks on SQL flows.
    /// </summary>
    /// <remarks>
    /// This class provides a static method, Exec, which performs the health check operation.
    /// The method takes a connection string, flow id, and optional parameters for model selection and debugging.
    /// It returns a string representing the execution process log.
    /// </remarks>
    public class ExecHealthCheck
    {
        private static string _execProcessLog = "";

        #region ExecHealthCheck
        /// <summary>
        /// Executes a health check on a SQL flow.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to which the execution process log is written.</param>
        /// <param name="sqlFlowConString">The connection string for the SQL flow.</param>
        /// <param name="flowid">The ID of the SQL flow to check.</param>
        /// <param name="runModelSelection">Optional parameter to indicate whether model selection should be run (default is 1).</param>
        /// <param name="dbg">Optional parameter for debugging (default is 0).</param>
        /// <returns>A string representing the execution process log.</returns>
        /// <exception cref="Exception">Throws an exception if the health check fails.</exception>
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, int flowid, int runModelSelection = 1, int dbg = 0)
        {
            var result = "false";
            _execProcessLog = "";

            ConStringParser conStringParser = new ConStringParser(sqlFlowConString) {ConBuilderMsSql = { ApplicationName = "SQLFlow App" }};
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            var totalTime = new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    SqlFlowParam srcIsAzure = new SqlFlowParam(sqlFlowCon, flowid);

                    var execTime = new Stopwatch();
                    execTime.Start();
                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();

                    _execProcessLog = ExecMLHealthCheck.Exec(sqlFlowConString, flowid, runModelSelection, dbg);
                    logWriter.Write(_execProcessLog);
                    logWriter.Flush();
                }
                catch (Exception e)
                {
                    //Error returned to client
                    result = _execProcessLog + Environment.NewLine + e.StackTrace;
                    logWriter.Write(_execProcessLog);
                    logWriter.Flush();

                    throw;
                }
            }

            return _execProcessLog;
        }
        #endregion ExecHealthCheck
    }
}