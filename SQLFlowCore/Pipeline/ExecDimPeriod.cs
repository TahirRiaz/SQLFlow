using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Text;
using SQLFlowCore.Common;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents the execution of a specific period in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class is responsible for executing a specific period in SQLFlow. It logs the execution process and handles the connection to the SQLFlow database.
    /// </remarks>
    public class ExecDimPeriod : EventArgs
    {
        private static StringBuilder _lineageLog = new();

        /// <summary>
        /// Executes a specific period in SQLFlow.
        /// </summary>
        /// <param name="logWriter">The StreamWriter used to log the execution process.</param>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="scAlias">The alias of the source control (optional).</param>
        /// <param name="batch">The batch to be executed (optional).</param>
        /// <remarks>
        /// This method is responsible for executing a specific period in SQLFlow. It logs the execution process, handles the connection to the SQLFlow database, and handles any exceptions that occur during execution.
        /// </remarks>
        public static void Exec(StreamWriter logWriter, string sqlFlowConString, string scAlias = "", string batch = "")
        {
            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            string scDsCmd = $"exec [flw].[GetSourceControl] @SCAlias = '{scAlias}', @batch = '{batch}'";
            Log(logWriter, scDsCmd);
            Stopwatch totalTime = new Stopwatch();
            totalTime.Start();
            using (SqlConnection sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();
                    // Database operations go here
                }
                catch (Exception e)
                {
                    Log(logWriter, e.Message);
                    _lineageLog.AppendLine(e.StackTrace);
                }
                totalTime.Stop();
                long logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                Log(logWriter, $"## Info: Total processing time {logDurationPre} (sec)");
            }
        }

        /// <summary>
        /// Logs a given message to a specified StreamWriter and to the lineage log.
        /// </summary>
        /// <param name="logWriter">The StreamWriter used to log the execution process.</param>
        /// <param name="message">The message to be logged.</param>
        /// <remarks>
        /// This method appends the provided message to the lineage log and writes it to the provided StreamWriter. After writing the message, it flushes the StreamWriter to ensure that the message is immediately written to the underlying stream.
        /// </remarks>
        private static void Log(StreamWriter logWriter, string message)
        {
            _lineageLog.AppendLine("## " + message);
            logWriter.WriteLine("## " + message);
            logWriter.Flush();
        }
    }
}