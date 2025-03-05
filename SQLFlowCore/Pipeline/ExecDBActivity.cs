using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a class that provides functionality to execute database activities.
    /// </summary>
    /// <remarks>
    /// This class is part of the SQLFlowCore.Engine namespace and is used to execute SQL queries and stored procedures.
    /// It uses the SqlConnection and SqlCommand classes from the Microsoft.Data.SqlClient namespace to interact with the database.
    /// </remarks>
    public class ExecDBActivity
    {
        #region ExecDBActivity 
        /// <summary>
        /// Executes a stored procedure in the database and returns the result as a DataSet.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="flowid">The ID of the flow. Can be null.</param>
        /// <param name="node">The node number. Can be null.</param>
        /// <param name="batch">The batch string. Can be null.</param>
        /// <returns>A DataSet containing the result of the executed stored procedure.</returns>
        /// <exception cref="Exception">Throws an exception if an error occurs during the execution of the stored procedure.</exception>
        /// <remarks>
        /// This method uses the SqlConnection and SqlCommand classes from the Microsoft.Data.SqlClient namespace to interact with the database.
        /// It also uses the ConStringParser class to parse the connection string.
        /// </remarks>
        public static DataSet Exec(string sqlFlowConString, int? flowid, int? node, string batch)
        {
            DataSet whatsUp = new DataSet();

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
                    sqlFlowCon.Open();

                    var _dataTable = new DataTable();
                    using (SqlCommand cmd = new SqlCommand("[flw].[GetRVDBActivity]", sqlFlowCon))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;

                        if (flowid != null)
                        {
                            cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowid;
                        }

                        if (node != null)
                        {
                            cmd.Parameters.Add("@node", SqlDbType.VarChar).Value = node;
                        }

                        if (batch != null)
                        {
                            cmd.Parameters.Add("@Batch", SqlDbType.VarChar).Value = batch;
                        }

                        var da = new SqlDataAdapter(cmd) { FillLoadOption = LoadOption.Upsert };

                        da.Fill(_dataTable);
                        da.Dispose();
                        cmd.Dispose();
                    }

                    foreach (DataRow dr in _dataTable.Rows)
                    {
                        string SourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                        string DataSource = dr["DatabaseName"]?.ToString() ?? string.Empty;
                        string Alias = dr["Alias"]?.ToString() ?? string.Empty;
                        string ConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                        string srcTenantId = dr["srcTenantId"]?.ToString() ?? string.Empty;
                        string srcSubscriptionId = dr["srcSubscriptionId"]?.ToString() ?? string.Empty;
                        string srcApplicationId = dr["srcApplicationId"]?.ToString() ?? string.Empty;
                        string srcClientSecret = dr["srcClientSecret"]?.ToString() ?? string.Empty;
                        string srcKeyVaultName = dr["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        string srcSecretName = dr["srcSecretName"]?.ToString() ?? string.Empty;
                        string srcResourceGroup = dr["srcResourceGroup"]?.ToString() ?? string.Empty;
                        string srcDataFactoryName = dr["srcDataFactoryName"]?.ToString() ?? string.Empty;
                        string srcAutomationAccountName = dr["srcAutomationAccountName"]?.ToString() ?? string.Empty;
                        string srcStorageAccountName = dr["srcStorageAccountName"]?.ToString() ?? string.Empty;
                        string srcBlobContainer = dr["srcBlobContainer"]?.ToString() ?? string.Empty;
                        string IsSynapse = dr["IsSynapse"]?.ToString() ?? string.Empty;


                        if (SourceType.Equals("MSSQL", StringComparison.InvariantCultureIgnoreCase) ||
                            SourceType.Equals("AZDB", StringComparison.CurrentCultureIgnoreCase))
                        {
                            string cmdSQL = @$"
SELECT DB_NAME(SP.dbid) AS DataSetName,
       spid,
       DB_NAME(SP.dbid) AS dbname,
       ER.percent_complete,
       CAST(((DATEDIFF(s, start_time, GETDATE())) / 3600) AS VARCHAR) + ' hour(s), '
       + CAST((DATEDIFF(s, start_time, GETDATE()) % 3600) / 60 AS VARCHAR) + 'min, '
       + CAST((DATEDIFF(s, start_time, GETDATE()) % 60) AS VARCHAR) + ' sec' AS running_time,
       ER.blocking_session_id,
       lastwaittype,
       SP.status,
       cpu,
       hostname,
       login_time,
       loginame,
       program_name,
       '<pre>' + EST.text + '</pre>' as text
FROM sysprocesses SP
    INNER JOIN sys.dm_exec_requests ER
        ON SP.spid = ER.session_id
    CROSS APPLY sys.dm_exec_sql_text(ER.sql_handle) EST

;
";

                            if (srcSecretName.Length > 0)
                            {
                                AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                    srcTenantId,
                                    srcApplicationId,
                                    srcClientSecret,
                                    srcKeyVaultName);
                                ConnectionString = srcKeyVaultManager.GetSecret(srcSecretName);
                            }

                            //WHERE program_name<> 'SQLFlow Activity Fetch'
                            ConStringParser conParser = new ConStringParser(ConnectionString)
                            {
                                ConBuilderMsSql =
                               {
                                   ApplicationName = "SQLFlow Activity Fetch"
                               }
                            };
                            string sqlConString = conParser.ConBuilderMsSql.ConnectionString;

                            using (var sqlCon = new SqlConnection(sqlConString))
                            {
                                try
                                {
                                    sqlCon.Open();

                                    var activity = new DataTable();
                                    //activity.TableName = Alias;
                                    using (SqlCommand cmd = new SqlCommand(cmdSQL, sqlCon))
                                    {
                                        var da = new SqlDataAdapter(cmd) { FillLoadOption = LoadOption.Upsert };

                                        da.Fill(activity);
                                        da.Dispose();
                                        cmd.Dispose();

                                        whatsUp.Tables.Add(activity);
                                    }

                                    sqlCon.Close();
                                    sqlCon.Dispose();
                                }
                                catch (Exception)
                                {
                                    throw;
                                }
                            }
                        }
                    }
                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();

                }
                catch (Exception)
                {
                    throw;
                }
            }

            return whatsUp;
        }
        #endregion ExecDBActivity  
    }
}
