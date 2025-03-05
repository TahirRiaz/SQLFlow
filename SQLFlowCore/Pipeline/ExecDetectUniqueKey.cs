using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.UniqueKeyDetector;

namespace SQLFlowCore.Pipeline
{
    public class ExecDetectUniqueKey
    {
        private static string _execProcessLog = "";
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, int flowid, string ColList, int NumberOfRowsToSample, int TotalUniqueKeysSought, int MaxKeyCombinationSize, decimal RedundantColSimilarityThreshold, decimal SelectRatioFromTopUniquenessScore, AnalysisMode Mode, bool ExecuteProofQuery, bool EarlyExitOnFound, int dbg = 0)
        {
            _execProcessLog = "";
            sqlFlowConString = UpdateConnectionString(sqlFlowConString);
            try
            {
                using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
                {
                    SqlFlowParam srcIsAzure = new SqlFlowParam(sqlFlowCon, flowid);

                    string trgDBSchObj = "";
                    string trgConString = "";
                    string trgTenantId = string.Empty;
                    string trgSubscriptionId = string.Empty;
                    string trgApplicationId = string.Empty;
                    string trgClientSecret = string.Empty;
                    string trgKeyVaultName = string.Empty;
                    string trgSecretName = string.Empty;
                    string trgStorageAccountName = string.Empty;
                    string trgBlobContainer = string.Empty;

                    sqlFlowCon.Open();
                    string TblSchemaCmd = $@" exec [flw].[GetRVTrgTblSchema] @FlowID = {flowid.ToString()}";

                    DataSet ds = new DataSet();
                    ds = CommonDB.GetDataSetFromSP(sqlFlowCon, TblSchemaCmd, 360);
                    DataTable TrgTblSchema = ds.Tables[0];

                    foreach (DataRow row in TrgTblSchema.Rows)
                    {
                        trgDBSchObj = row["trgDBSchObj"]?.ToString() ?? string.Empty;
                        trgConString = row["trgConString"]?.ToString() ?? string.Empty;

                        trgTenantId = row["trgTenantId"]?.ToString() ?? string.Empty;
                        trgApplicationId = row["trgApplicationId"]?.ToString() ?? string.Empty;
                        trgClientSecret = row["trgClientSecret"]?.ToString() ?? string.Empty;
                        trgKeyVaultName = row["trgKeyVaultName"]?.ToString() ?? string.Empty;
                        trgSecretName = row["trgSecretName"]?.ToString() ?? string.Empty;
                        trgStorageAccountName = row["trgStorageAccountName"]?.ToString() ?? string.Empty;
                        trgBlobContainer = row["trgBlobContainer"]?.ToString() ?? string.Empty;

                        if (trgSecretName.Length > 0)
                        {
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                trgTenantId,
                                trgApplicationId,
                                trgClientSecret,
                                trgKeyVaultName);
                            trgConString = trgKeyVaultManager.GetSecret(trgSecretName);
                        }
                        ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
                        {
                            ConBuilderMsSql =
                            {
                                ApplicationName = "SQLFlow App"
                            }
                        };
                        conStringParser = new ConStringParser(trgConString)
                        {
                            ConBuilderMsSql =
                            {
                                ApplicationName = "SQLFlow Target"
                            }
                        };
                        trgConString = conStringParser.ConBuilderMsSql.ConnectionString;

                        var trgSqlCon = new SqlConnection(trgConString);

                        try
                        {
                            trgSqlCon.Open();

                            // SQL query to get the metadata
                            string query = @$"SELECT TOP ({NumberOfRowsToSample}) {ColList} FROM {trgDBSchObj}";

                            // Create a new list to hold the result
                            List<ObjectColumns> columns = new List<ObjectColumns>();

                            DataTable TblSampleData = CommonDB.GetData(trgSqlCon, query, 3600);


                            //// Step 1: Calculate entropy for all columns.
                            logWriter.WriteLine("#### Executing entropy calculation for all columns.");
                            var entropyResults = EntropyCalculator.CalculateEntropyForColumns(TblSampleData);
                            logWriter.WriteLine(JsonConvert.SerializeObject(entropyResults, Formatting.Indented));
                            logWriter.Flush();

                            //// Step 2: Check pairwise column correlations to eliminate redundant columns.
                            logWriter.WriteLine("#### Check pairwise column correlations to find non Redundant Columns.");
                            var nonRedundantColumns = RedundancyRemover.EliminateRedundantColumns(TblSampleData, entropyResults, (double)RedundantColSimilarityThreshold);
                            logWriter.WriteLine(JsonConvert.SerializeObject(nonRedundantColumns, Formatting.Indented));
                            logWriter.Flush();

                            foreach (var column in nonRedundantColumns.ToList())
                            {
                                if (!TblSampleData.Columns.Contains(column))
                                {
                                    nonRedundantColumns.Remove(column);
                                }
                            }

                            //// Step 3: Estimate column uniqueness.
                            logWriter.WriteLine("#### Estimating column uniqueness");
                            var sampledUniquenessResults = UniquenessEstimator.EstimateUniqueness(TblSampleData, nonRedundantColumns);
                            logWriter.WriteLine(JsonConvert.SerializeObject(sampledUniquenessResults, Formatting.Indented));
                            logWriter.Flush();

                            foreach (var column in sampledUniquenessResults)
                            {
                                if (column.Value == 0)
                                {
                                    TblSampleData.Columns.Remove(column.Key);
                                    nonRedundantColumns.Remove(column.Key);
                                }
                            }

                            //// Step 6: Progressive refinement to identify potential unique key combinations.
                            var potentialUniqueKeys = ProgressiveRefinementWithHash.FindUniqueCombinations(logWriter, TblSampleData, sampledUniquenessResults, nonRedundantColumns, TotalUniqueKeysSought,
                                MaxKeyCombinationSize,

                                (double)SelectRatioFromTopUniquenessScore
                                , Mode
                                , EarlyExitOnFound
                                );

                            foreach (var key in potentialUniqueKeys)
                            {
                                string proofQueryResult = "-1";
                                string ProofQueryCount = "SELECT IsNull(COUNT(1),0) FROM " + trgDBSchObj + " GROUP BY " + key.DetectedKey + " HAVING COUNT(*) > 1";

                                if (ExecuteProofQuery)
                                {
                                    proofQueryResult = CommonDB.ExecuteScalarMSSQL(trgSqlCon, ProofQueryCount, 3600, "0");
                                }

                                key.FlowId = flowid.ToString();
                                key.ObjectName = trgDBSchObj;
                                key.ProofQuery = "SELECT TOP 100 " + key.DetectedKey +
                                                 ", COUNT(1) [Rowcount] FROM " + trgDBSchObj + " GROUP BY " +
                                                 key.DetectedKey +
                                                 " HAVING COUNT(*) > 1 Order by [Rowcount] Desc";
                                key.DuplicateRowCount = proofQueryResult;
                                key.ProofQueryExecuted = ExecuteProofQuery;

                            }

                            // Convert the list to JSON
                            string jsonResult = JsonConvert.SerializeObject(potentialUniqueKeys, Formatting.Indented);
                            WriteLog(logWriter, "#### Final result");
                            WriteLog(logWriter, "|||||");
                            WriteLog(logWriter, jsonResult);
                            WriteLog(logWriter, "|-|-|-|-|");
                            logWriter.Flush();
                        }
                        catch (Exception e)
                        {
                            _execProcessLog += "Error: " + e.Message;
                            WriteLog(logWriter, _execProcessLog);
                            return _execProcessLog;
                        }
                        finally
                        {
                            trgSqlCon.Close();
                            trgSqlCon.Dispose();
                        }

                    }
                }
                WriteLog(logWriter, _execProcessLog);
            }
            catch (Exception e)
            {
                AppendErrorToLog(e);
                WriteLog(logWriter, _execProcessLog);
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
        private static void WriteLog(StreamWriter logWriter, string log)
        {
            logWriter.Write(log);
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