using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;


namespace SQLFlowCore.Common
{
    internal class ExecSqlInParallel
    {
        private static string _execSqlLog = "";

        /// <summary>
        ///     Author: Tahir Riaz
        ///     Date: 11.04.2020
        ///     CLR Function to execute a SQL Statements in parallel on a remote server
        ///      
        /// </summary>
        /// 
        #region ExecSqlInParallel
        internal static string Exec(
            string trgConString,
            string commandList,
            int commandTimeout,
            int maxConcurrency,
            int dbg
        )
        {
            var result = "false";
            var codeStack = "";
            _execSqlLog = "";

            try
            {
                //BulkLog = BulkLog + commandList;

                var commands = commandList.Split('§');

                using (var concurrencySemaphore = new SemaphoreSlim(maxConcurrency))
                {
                    var tasks = new List<Task>();
                    foreach (var msg in commands)
                    {
                        concurrencySemaphore.Wait();
                        var t = Task.Factory.StartNew(() =>
                        {
                            try
                            {
                                //BulkLog = BulkLog + DateTime.Now.ToString();
                                ConnectDbAndExecuteQuery(msg, trgConString, commandTimeout);
                            }
                            finally
                            {
                                concurrencySemaphore.Release();
                            }
                        }); //.ContinueWith(task => Console.WriteLine("- Done with " + msg));
                        tasks.Add(t);
                    }

                    Task.WaitAll(tasks.ToArray());
                    //string StartTime2 = DateTime.Now.ToString();
                    //BulkLog = BulkLog + String.Format("-### Execution Started at {0} ###- {1}", StartTime2, Environment.NewLine);
                }

            }
            catch (Exception e)
            {
                //Error returned to client
                result = e.StackTrace + Environment.NewLine + codeStack;
            }

            result = _execSqlLog;

            return result;
        }

        #endregion ExecSqlInParallel

        #region ConnectAndExecute
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
                _execSqlLog += e.Message;
            }
        }

        private static void connection_InfoMessage(object sender, SqlInfoMessageEventArgs e, Stopwatch watch)
        {
            watch.Stop();
            _execSqlLog +=
                $"--### Execution Time ({(watch.ElapsedMilliseconds / 1000).ToString()} sec) ###-- {Environment.NewLine}";

            _execSqlLog = _execSqlLog + e.Message;
        }


        #endregion

    }
}