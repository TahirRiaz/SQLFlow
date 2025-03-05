using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.SqlServer.Types;
using SQLFlowCore.Args;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Logger;
//using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
namespace SQLFlowCore.Common
{
    internal class StreamToSql
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
        private static int _dbg = 0;

        internal static long RowCount = 0;

        private long _srcRowCount = 0;
        private static int _notify = 0;

        private const int BaseNotifyThreshold = 500; // Base threshold for switching calculation method
        private const int MinNotifyAfter = 100; // Minimum notifications for very small datasets
        private const int MaxNotifyAfter = 10000; // Cap the maximum notification interval

        internal StreamToSql(string trgConString, string tableName, string srcName,
            Dictionary<string, string> tableMap, int timeoutInSek, int batchSize, RealTimeLogger logStack,
            int maxRetry, int retryDelayMs, Hashtable retryErrorCodes, int dbg, ref int srcRowCount)
        {
            _tableName = tableName;
            _srcName = srcName;
            _tableMap = tableMap;
            _connString = trgConString;
            _timeoutInSek = timeoutInSek;
            _batchSize = batchSize;
            _logger = logStack;
            _maxRetry = maxRetry;
            _retryDelayMs = retryDelayMs;
            _retryErrorCodes = retryErrorCodes;
            _dbg = dbg;
            _srcRowCount = srcRowCount;
            _notify = CalculateNotifyAfter(srcRowCount);
        }

        /// <summary>
        /// Streams data from reader to SQL database with retry mechanism in case of SqlException. 
        /// </summary>
        /// <param name="reader">IDataReader containing data to be streamed.</param>
        internal void StreamWithRetries(IDataReader reader)
        {
            var exceptions = new List<Exception>();

            // Retry loop
            while (true)
            {
                try
                {
                    using (var connection = new SqlConnection(_connString))
                    {
                        connection.Open();
                        using (var bulkCopy = MakeSqlBulkCopy(connection))
                        {
                            // Attach callback for bulk copy completion
                            bulkCopy.SqlRowsCopied += (sender, e) => OnSqlRowsCopied(sender, e, _tableName, _srcName, _srcRowCount);
                            bulkCopy.NotifyAfter = _notify;

                            // Track the bulk copy operation
                            using (_logger.TrackOperation("Bulk Copy WriteToServer"))
                            {
                                bulkCopy.WriteToServer(reader);
                            }
                        }
                        connection.Close();
                    }
                    break;
                }
                catch (SqlException sqlExn)
                {
                    // Retry if the error code is in the list of retryable errors
                    if (_retryErrorCodes.ContainsKey(sqlExn.Number))
                    {
                        exceptions.Add(sqlExn);

                        // If maximum retries reached, log the error details and throw
                        if (exceptions.Count == _maxRetry)
                        {
                            _logger.LogError("Too many attempts during bulk copy. Exceptions: {@Exceptions}", exceptions);
                            throw new AggregateException("Too many attempts.", exceptions);
                        }

                        // Wait before retrying
                        Thread.Sleep(_retryDelayMs);
                        continue;
                    }

                    _logger.LogError(sqlExn, "SQL CRITICAL ERROR during bulk copy.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CRITICAL ERROR during bulk copy.");
                    throw;
                }
                finally
                {
                    reader.Close();
                    reader.Dispose();
                }
            }
        }



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


        /// <summary>
        /// Function called every time data is copied to destination
        /// </summary>
        /// <param name="sender">object that raised the event</param>
        /// <param name="e">event arguments</param>
        /// <param name="TableName">name of the table in the database</param>
        /// <param name="srcName">name of data source</param>
        /// <param name="srcRowCount">count of rows in the source dataset</param>
        private static void OnSqlRowsCopied(object sender, SqlRowsCopiedEventArgs e, string TableName, string srcName, long srcRowCount)
        {
            //Calculating status of data copy in percentage
            double Status = e.RowsCopied / (double)srcRowCount * 100;
            //Creating custom EventArgs to report the progress
            EventArgsRowsCopied arg = new EventArgsRowsCopied
            {
                RowsInTotal = srcRowCount,
                RowsInQueue = srcRowCount - e.RowsCopied,
                RowsProcessed = e.RowsCopied,
                Status = Status,
                TrgObjectName = TableName,
                SrcObjectName = srcName
            };
            //Invokes the event with event-handler function providing thread and argument to the form
            OnRowsCopied?.Invoke(Thread.CurrentThread, arg);
        }

        /// <summary>
        /// Creates a new instance of SqlBulkCopy and sets its properties such as DestinationTableName, ColumnMappings, BulkCopyTimeout, EnableStreaming, and BatchSize.
        /// </summary>
        /// <param name="connection">The SQL connection to use.</param>
        /// <returns>A new instance of SqlBulkCopy with properties already set.</returns>
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

                SqlGeography.Null.ToString();

                // Maps the columns of the DataTable/DataReader being copied to the corresponding columns of the destination table based on the column mappings specified in _tableMap.
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
                tempBulkCopy?.Close();
            }

            return bulkCopy;
        }
    }
}