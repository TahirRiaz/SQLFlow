using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using SQLFlowCore.Args;
using SQLFlowCore.Logger;
using Microsoft.Extensions.Logging;

namespace SQLFlowCore.Common
{
    /// <summary>
    ///     Details:
    /// </summary>
    internal class PushToSql
    {
        internal static event EventHandler<EventArgsRowsCopied> OnRowsCopied;
        private static int _maxRetry = 2;
        private static int _retryDelayMs = 100;
        private readonly int _batchSize;
        private readonly string _connString;
        private readonly RealTimeLogger _logger;
        private readonly Hashtable _retryErrorCodes;
        private readonly Dictionary<string, string> _tableMap;

        private readonly string _tableName;
        private readonly string _srcName;

        private readonly int _timeoutInSek;
        internal static long RowCount = 0;
        private static int _dbg = 0;

        private long _srcRowCount = 0;
        private static int _notify = 0;

        private const int BaseNotifyThreshold = 500; // Base threshold for switching calculation method
        private const int MinNotifyAfter = 100; // Minimum notifications for very small datasets
        private const int MaxNotifyAfter = 10000; // Cap the maximum notification interval

        internal PushToSql(string trgConString, string tableName, string srcName,
            Dictionary<string, string> tableMap, int timeoutInSek, int batchSize, RealTimeLogger logger,
            int maxRetry, int retryDelayMs, Hashtable retryErrorCodes, int dbg, ref int srcRowCount)
        {
            _tableName = tableName;
            _srcName = srcName;
            _tableMap = tableMap;
            _connString = trgConString;
            _timeoutInSek = timeoutInSek;
            _batchSize = batchSize;
            _logger = logger;
            _maxRetry = maxRetry;
            _retryDelayMs = retryDelayMs;
            _retryErrorCodes = retryErrorCodes;
            _dbg = dbg;
            _srcRowCount = srcRowCount;
            _notify = CalculateNotifyAfter(srcRowCount);
        }

        internal void WriteWithRetries(DataTable dataTable)
        {
            Write(dataTable);
        }

        private void Write(DataTable dataTable)
        {
            // Declare exceptions list outside the retry loop
            var exceptions = new List<Exception>();

            // Track the SQL Bulk Copy operation (operation name is context-specific)
            using (var operation = _logger.TrackOperation($"SQL Bulk Copy to {_tableName}"))
            {
                while (true)
                {
                    try
                    {
                        using (var connection = new SqlConnection(_connString))
                        {
                            connection.Open();
                            using (var bulkCopy = MakeSqlBulkCopy(connection))
                            {
                                using (var dataTableReader = new DataTableReader(dataTable))
                                {
                                    // Attach the row-copied event handler
                                    bulkCopy.SqlRowsCopied += (sender, e) => OnSqlRowsCopied(sender, e, _tableName, _srcName, _srcRowCount);
                                    bulkCopy.NotifyAfter = _notify;
                                    bulkCopy.WriteToServer(dataTableReader);
                                }
                            }
                        }
                        // If successful, exit the retry loop.
                        break;
                    }
                    catch (SqlException sqlExn)
                    {
                        if (_retryErrorCodes.ContainsKey(sqlExn.Number))
                        {
                            exceptions.Add(sqlExn);
                            if (exceptions.Count == _maxRetry)
                            {
                                // Log error for too many retry attempts and throw an aggregate exception.
                                _logger.LogError("Too many attempts encountered during SQL Bulk Copy. Exceptions: {Exceptions}", exceptions);
                                throw new AggregateException("Too many attempts.", exceptions);
                            }
                            // Wait before retrying.
                            Thread.Sleep(_retryDelayMs);
                            continue;
                        }
                        // Log a SQL-critical error and rethrow.
                        _logger.LogError("SQL Critical Error during Bulk Copy. Exception: {Exception}", sqlExn);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // Log a general critical error and rethrow.
                        _logger.LogError("Critical Error during Bulk Copy. Exception: {Exception}", exception);
                        throw;
                    }
                }
            }
        }



        //private static RetryPolicy<SqlDatabaseTransientErrorDetectionStrategy> MakeRetryPolicy()
        //{
        //    var fromMilliseconds = TimeSpan.FromMilliseconds(maxRetry);
        //    var policy = new RetryPolicy<SqlDatabaseTransientErrorDetectionStrategy>
        //        (maxRetry, fromMilliseconds);
        //    return policy;
        //}

        private static int CalculateNotifyAfter(int totalRowCount)
        {
            if (totalRowCount <= 0)
            {
                // No rows to copy, so no need to set up notifications
                //Console.WriteLine("No rows to copy. Skipping notification setup.");
                return 1; // Or return 1 to avoid division by zero errors elsewhere, if necessary
            }
            else if (totalRowCount <= MinNotifyAfter)
            {
                return Math.Max(1, totalRowCount / 10); // Small datasets: more frequent updates
            }
            else if (totalRowCount <= BaseNotifyThreshold)
            {
                // Intermediate datasets: linear scaling
                return Math.Max(MinNotifyAfter, totalRowCount / 50);
            }
            else
            {
                // Large datasets: logarithmic scaling with dynamic adjustment
                double scaleFactor = 2.0; // Adjust this to control how quickly the notify interval grows
                double logValue = Math.Log(totalRowCount - BaseNotifyThreshold + 1, scaleFactor);
                int notifyAfter = (int)(MinNotifyAfter + logValue * MinNotifyAfter);

                return Math.Min(notifyAfter, MaxNotifyAfter); // Ensure notifyAfter does not exceed max limit
            }
        }


        private static void OnSqlRowsCopied(object sender, SqlRowsCopiedEventArgs e, string TableName, string srcName, long srcRowCount)
        {
            //Interlocked.Add(ref _curRowCount, _notify);

            double Status = e.RowsCopied / (double)e.RowsCopied; //Adding One For the StatusBar
            EventArgsRowsCopied arg = new EventArgsRowsCopied
            {
                RowsInTotal = srcRowCount,
                RowsInQueue = srcRowCount - e.RowsCopied,
                RowsProcessed = e.RowsCopied,
                Status = Status,
                TrgObjectName = TableName,
                SrcObjectName = srcName
            };
            OnRowsCopied?.Invoke(Thread.CurrentThread, arg);
        }

        private SqlBulkCopy MakeSqlBulkCopy(SqlConnection connection)
        {
            SqlBulkCopy bulkCopy;
            SqlBulkCopy tempBulkCopy = null;
            try
            {
                tempBulkCopy = new SqlBulkCopy
                (
                    connection,
                    SqlBulkCopyOptions.TableLock |
                    SqlBulkCopyOptions.UseInternalTransaction,
                    null
                )
                {
                    EnableStreaming = true,
                    DestinationTableName = _tableName
                };
                _tableMap
                    .ToList()
                    .ForEach(kp => tempBulkCopy
                        .ColumnMappings
                        .Add(kp.Key, kp.Value));
                bulkCopy = tempBulkCopy;
                bulkCopy.BulkCopyTimeout = _timeoutInSek;
                bulkCopy.EnableStreaming = true;
                bulkCopy.BatchSize = 0; //Gives least log
                if (_batchSize != 0) bulkCopy.BatchSize = _batchSize;

                tempBulkCopy = null;
            }
            finally
            {
                if (tempBulkCopy != null)
                    tempBulkCopy.Close();
            }

            return bulkCopy;
        }
    }
}