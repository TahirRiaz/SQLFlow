using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Common;
using SQLFlowCore.Logger;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.Schema;
using Octokit;


namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides functionality for executing SQL Flow assertions.
    /// </summary>
    /// <remarks>
    /// This class is responsible for executing SQL Flow assertions and logging the execution process.
    /// It uses events to notify about the progress of the execution.
    /// </remarks>
    internal static class ProcessAssertion
    {
        #region ProcessAssertion
        /// <summary>
        /// Executes the SQL Flow assertion process.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQL Flow database.</param>
        /// <param name="flowId">The ID of the flow to be processed.</param>
        /// <param name="dbg">The debug level for the process.</param>
        /// <returns>A string representing the result of the assertion process.</returns>
        /// <remarks>
        /// This method initiates a connection to the SQL Flow database using the provided connection string, 
        /// executes the SQL Flow assertion process for the specified flow ID, and returns a string that 
        /// represents the result of the process. The debug level parameter determines the amount of 
        /// information logged during the process.
        /// </remarks>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();
            
            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionAssertion", outputAction, LogLevel.Information, 1);

            ServiceParam sp = ServiceParam.Current;
            var trgSqlCon = new SqlConnection();
            
            using (var sqlFlowCon = new SqlConnection(sqlFlowParam.sqlFlowConString))
            {
                sqlFlowCon.Open();

                DataTable healthCheckTbl = new DataTable();

                using (var operation = logger.TrackOperation("Execute assertions"))
                {
                    string flowParamCmd = $" exec [flw].[GetRVAssertion] @FlowID = {sqlFlowParam.flowId}";
                    logger.LogCodeBlock("GetRVHealthCheck Runtime Values HealthCheck:", flowParamCmd);
                    DataSet ds = CommonDB.GetDataSetFromSP(sqlFlowCon, flowParamCmd, 360);
                    healthCheckTbl = ds.Tables[0];

                    foreach (DataRow row in healthCheckTbl.Rows)
                    {
                        ProcessRow(row, logger, sqlFlowCon);
                    }
                }

                sp.result = logOutput.ToString();
                sqlFlowCon.Close();
            }
            return sp.result;
        }

        private static void ProcessRow(DataRow row, RealTimeLogger logger, SqlConnection sqlFlowCon)
        {
            int flowID = Convert.ToInt32(row["FlowID"]);
            int assertionID = Convert.ToInt32(row["AssertionID"]);
            string assertionName = row["AssertionName"]?.ToString() ?? string.Empty;
            string assertionSqlCmd = row["AssertionSqlCmd"]?.ToString() ?? string.Empty;
            string trgDBSchTbl = row["trgDBSchTbl"]?.ToString() ?? string.Empty;
            string trgConString = row["trgConString"]?.ToString() ?? string.Empty;

            string trgTenantId = row["trgTenantId"]?.ToString() ?? string.Empty;
            string trgSubscriptionId = row["trgSubscriptionId"]?.ToString() ?? string.Empty;
            string trgApplicationId = row["trgApplicationId"]?.ToString() ?? string.Empty;
            string trgClientSecret = row["trgClientSecret"]?.ToString() ?? string.Empty;
            string trgKeyVaultName = row["trgKeyVaultName"]?.ToString() ?? string.Empty;
            string trgSecretName = row["trgSecretName"]?.ToString() ?? string.Empty;
            string trgResourceGroup = row["trgResourceGroup"]?.ToString() ?? string.Empty;
            string trgDataFactoryName = row["trgDataFactoryName"]?.ToString() ?? string.Empty;
            string trgAutomationAccountName = row["trgAutomationAccountName"]?.ToString() ?? string.Empty;
            string trgStorageAccountName = row["trgStorageAccountName"]?.ToString() ?? string.Empty;
            string trgBlobContainer = row["trgBlobContainer"]?.ToString() ?? string.Empty;

            if (assertionSqlCmd.Length > 0)
            {
                var watch = new Stopwatch();

                if (trgSecretName.Length > 0)
                {
                    AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                        trgTenantId,
                        trgApplicationId,
                        trgClientSecret,
                        trgKeyVaultName);
                    trgConString = trgKeyVaultManager.GetSecret(trgSecretName);
                }
                ConStringParser conStringParser = new ConStringParser(trgConString)
                {
                    ConBuilderMsSql = { ApplicationName = "SQLFlow Target" }
                };
                trgConString = conStringParser.ConBuilderMsSql.ConnectionString;
                try
                {
                    using (logger.TrackOperation($"{assertionName} evaluation on {trgDBSchTbl}"))
                    {
                        watch.Start();
                        var resultValue = CommonDB.RunQuery(trgConString, assertionSqlCmd, "MSSQL", 3600, new List<ParameterObject>());
                        string Value1 = "";
                        string Value2 = "";
                        if (resultValue != null)
                        {
                            if (resultValue.Rows.Count > 0)
                            {
                                if (resultValue.Columns.Count >= 1)
                                {
                                    Value1 = resultValue.Rows[0][0].ToString();
                                }
                                if (resultValue.Columns.Count >= 2)
                                {
                                    Value2 = resultValue.Rows[0][1].ToString();
                                }
                            }
                        }
                        watch.Stop();
                        long duration = watch.ElapsedMilliseconds / 1000;
                        string stepLog = $"{assertionName} evaluated ({duration.ToString()} sec)";
                        
                        using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogAssertion]", sqlFlowCon))
                        {
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowID;
                            cmd.Parameters.Add("@AssertionID", SqlDbType.Int).Value = assertionID;
                            cmd.Parameters.Add("@AssertionSqlCmd", SqlDbType.NVarChar).Value = assertionSqlCmd;
                            cmd.Parameters.Add("@Result", SqlDbType.NVarChar).Value = Value1;
                            cmd.Parameters.Add("@AssertedValue", SqlDbType.NVarChar).Value = Value2;
                            cmd.Parameters.Add("@TraceLog", SqlDbType.NVarChar).Value = stepLog;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    
                    
                }
                catch (Exception e)
                {
                    string error = $"Error: {assertionName} on flow {flowID} evaluation failed {Environment.NewLine} {e.Message}";

                    logger.LogInformation(error);
                    
                    using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogAssertion]", sqlFlowCon))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowID;
                        cmd.Parameters.Add("@AssertionID", SqlDbType.Int).Value = assertionID;
                        cmd.Parameters.Add("@AssertionSqlCmd", SqlDbType.NVarChar).Value = assertionSqlCmd;
                        cmd.Parameters.Add("@Result", SqlDbType.NVarChar).Value = "0";
                        cmd.Parameters.Add("@AssertedValue", SqlDbType.NVarChar).Value = "";
                        cmd.Parameters.Add("@TraceLog", SqlDbType.NVarChar).Value = error.ToString();
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        #endregion ProcessAssertion
    }
}


