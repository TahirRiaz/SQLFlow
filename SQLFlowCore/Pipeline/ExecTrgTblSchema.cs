using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Services.UniqueKeyDetector;

namespace SQLFlowCore.Pipeline
{
    public class ExecTrgTblSchema
    {
        private static string _execProcessLog = "";
        public static string Exec(StreamWriter logWriter, string sqlFlowConString, int flowid, int dbg = 0)
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
                            string query = $@"SELECT QUOTENAME(TABLE_CATALOG) + '.' + QUOTENAME(TABLE_SCHEMA) + '.' + QUOTENAME(COLUMN_NAME) AS ObjectName,
                                                       QUOTENAME(COLUMN_NAME) AS ColumnName,
                                                       CAST(CASE
                                                                WHEN COLUMN_NAME LIKE '%_DW'
                                                                     OR COLUMN_NAME LIKE '%PK' THEN
                                                                    0
                                                                ELSE
                                                                    1
                                                            END AS BIT) Selected
                                                FROM INFORMATION_SCHEMA.COLUMNS
                                                WHERE OBJECT_ID(TABLE_CATALOG + '.' + TABLE_SCHEMA + '.' + TABLE_NAME) = OBJECT_ID('{trgDBSchObj}')
                                                ORDER BY ORDINAL_POSITION;";

                            // Create a new list to hold the result
                            List<ObjectColumns> columns = new List<ObjectColumns>();

                            DataTable TblSchema = CommonDB.GetData(trgSqlCon, query, 360);

                            // Read the data and fill the list
                            int i = 0;
                            foreach (DataRow dr in TblSchema.Rows)
                            {
                                ObjectColumns column = new ObjectColumns
                                {
                                    ObjectName = dr["ObjectName"].ToString(),
                                    ColumnName = dr["ColumnName"].ToString(),
                                    Ordinal = i,
                                    Selected = Convert.ToBoolean(dr["Selected"])
                                };
                                columns.Add(column);
                                i++;
                            }

                            // Convert the list to JSON
                            string jsonResult = JsonConvert.SerializeObject(columns, Formatting.Indented);

                            WriteLog(logWriter, jsonResult);
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