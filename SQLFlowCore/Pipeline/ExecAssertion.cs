using System;
using Microsoft.Data.SqlClient;
using System.IO;
using SQLFlowCore.Common;
using SQLFlowCore.Services;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// The ExecAssertion class is responsible for executing assertions.
    /// </summary>
    public class ExecAssertion
    {
        private static string _execProcessLog = "";

        /// <summary>
        /// Executes the assertion with the provided parameters.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to log the execution process.</param>
        /// <param name="sqlFlowConString">The connection string for the SQL Flow.</param>
        /// <param name="flowid">The ID of the flow to be executed.</param>
        /// <param name="dbg">Optional parameter for debugging. Default is 0.</param>
        /// <returns>The log of the execution process.</returns>
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, int flowid, int dbg = 0)
        {
            _execProcessLog = "";
            sqlFlowConString = UpdateConnectionString(sqlFlowConString);
            try
            {
                using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
                {
                    SqlFlowParam sqlFlowParam = new SqlFlowParam(sqlFlowCon, flowid);
                    sqlFlowParam.flowType = "ast";
                    sqlFlowParam.sqlFlowConString = sqlFlowConString;
                    sqlFlowParam.flowId = flowid;
                    sqlFlowParam.dbg = dbg;

                    _execProcessLog = ProcessAssertion.Exec(sqlFlowParam);
                }

                WriteLog(logWriter);
            }
            catch (Exception e)
            {
                AppendErrorToLog(e);
                WriteLog(logWriter);
                throw;
            }

            return _execProcessLog;
        }

        /// <summary>
        /// Updates the connection string for the SQL Flow application.
        /// </summary>
        /// <param name="sqlFlowConString">The original connection string for the SQL Flow.</param>
        /// <returns>The updated connection string with the application name set to "SQLFlow App".</returns>
        private static string UpdateConnectionString(string sqlFlowConString)
        {
            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            return conStringParser.ConBuilderMsSql.ConnectionString;
        }

        /// <summary>
        /// Writes the execution process log to the provided StreamWriter and flushes the stream.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to which the log is written.</param>
        private static void WriteLog(StreamWriter logWriter)
        {
            logWriter.Write(_execProcessLog);
            logWriter.Flush();
        }

        /// <summary>
        /// Appends the stack trace of the provided exception to the execution process log.
        /// </summary>
        /// <param name="e">The exception whose stack trace is to be appended to the log.</param>
        private static void AppendErrorToLog(Exception e)
        {
            _execProcessLog += Environment.NewLine + e.StackTrace;
        }
    }

}