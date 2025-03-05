using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;


namespace SQLFlowCore.Common
{
    internal class ExecSql
    {
        private static string _execSqlLog = "";

        /// <summary>
        ///     Author: Tahir Riaz
        ///     Date: 11.04.2020
        ///     CLR Function to execute a SQL Statement on a remote server
        ///      
        /// </summary>
        /// 

        #region CLRFunExec
        internal static string Exec(
            string trgConString,
            string command,
            int commandTimeout,
            int dbg
        )
        {
            var result = "false";
            var codeStack = "";
            _execSqlLog = "";

            try
            {
                ConnectDbAndExecuteQuery(command, trgConString, commandTimeout);
            }
            catch (Exception e)
            {
                //Error returned to client
                result = e.StackTrace + Environment.NewLine + codeStack;
            }

            result = _execSqlLog;

            return result;
        }

        #endregion CLRFunExec

        #region  ConnectExecute
        internal static void ConnectDbAndExecuteQuery(string cmd, string trgConnectionStr, int commandTimeout)
        {
            try
            {
                var watch = new Stopwatch();
                watch.Start();

                using (var connection = new SqlConnection(trgConnectionStr))
                {
                    connection.InfoMessage += (sender, e) => connection_InfoMessage(sender, e, watch);
                    connection.Open();
                    using (var command = new SqlCommand(cmd, connection))
                    {
                        command.CommandTimeout = commandTimeout;
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }

                    connection.Close();
                    connection.Dispose();
                }
            }
            catch (Exception e)
            {
                _execSqlLog = _execSqlLog + e.Message;
            }
        }

        private static void connection_InfoMessage(object sender, SqlInfoMessageEventArgs e, Stopwatch watch)
        {
            watch.Stop();
            _execSqlLog = _execSqlLog +
                          $"--### Execution Time ({(watch.ElapsedMilliseconds / 1000).ToString()} sec) ###-- {Environment.NewLine}";
            _execSqlLog = _execSqlLog + e.Message;
        }


        #endregion

    }
}