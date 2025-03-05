using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using System.Threading;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Logger;
using Microsoft.Extensions.Logging;
using System.Collections;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Services.TsqlParser;
using static TorchSharp.torch;

namespace SQLFlowCore.Services
{

    /// <summary>
    /// Provides functionality to process stored procedures.
    /// </summary>
    internal static class ProcessStoredProcedure
    {

        #region ExecStoredProcedure
        /// <summary>
        /// Executes a stored procedure in the SQLFlow engine.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="flowId">The ID of the flow to execute.</param>
        /// <param name="execMode">The execution mode.</param>
        /// <param name="batchId">The ID of the batch.</param>
        /// <param name="dbg">Debug level indicator.</param>
        /// <param name="sqlFlowParam">The SQLFlow item to process.</param>
        /// <returns>A string representing the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionSp", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

            ServiceParam sp = ServiceParam.Current;
            var trgSqlCon = new SqlConnection();

            using (var sqlFlowCon = new SqlConnection(sqlFlowParam.sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();

                    var retryErrorCodes = new Hashtable(StringComparer.InvariantCultureIgnoreCase);
                    RetryCodes rCodes = new RetryCodes();
                    retryErrorCodes = rCodes.HashTbl;

                    DataTable paramTbl = new DataTable();
                    DataTable incrTbl = new DataTable();
                    DataTable DateTimeFormats = new DataTable();
                    DataTable procParamTbl = new DataTable();
                    
                    using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
                    {
                        Shared.GetFilePipelineMetadata("[flw].[GetRVStoredProcedure]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                    }
                    DataTable spParam = incrTbl;

                    
                    
                    //Fetch param values for current SP 
                    List<SqlParameter> paramList = new List<SqlParameter>();
                    using (logger.TrackOperation("Populate parameter values"))
                    {
                        List<ParameterObject> prefetchParams = new List<ParameterObject>();
                        foreach (DataRow dr in spParam.Rows)
                        {
                            sp.ParamName = dr["ParamName"]?.ToString() ?? string.Empty;
                            sp.ParamSelectExp = dr["SelectExp"]?.ToString() ?? string.Empty;
                            sp.ParamConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                            sp.trgTenantId = paramTbl.Rows[0]["trgTenantId"]?.ToString() ?? string.Empty;
                            sp.trgApplicationId = paramTbl.Rows[0]["trgApplicationId"]?.ToString() ?? string.Empty;
                            sp.trgClientSecret = paramTbl.Rows[0]["trgClientSecret"]?.ToString() ?? string.Empty;
                            sp.trgKeyVaultName = paramTbl.Rows[0]["trgKeyVaultName"]?.ToString() ?? string.Empty;
                            sp.trgSecretName = paramTbl.Rows[0]["trgSecretName"]?.ToString() ?? string.Empty;
                            sp.trgStorageAccountName = paramTbl.Rows[0]["trgStorageAccountName"]?.ToString() ?? string.Empty;
                            sp.trgBlobContainer = paramTbl.Rows[0]["trgBlobContainer"]?.ToString() ?? string.Empty;
                            bool pIsSynapse = (dr["IsSynapse"]?.ToString() ?? string.Empty).Equals("True");

                            if (sp.trgSecretName.Length > 0)
                            {
                                AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                    sp.trgTenantId,
                                    sp.trgApplicationId,
                                    sp.trgClientSecret,
                                    sp.trgKeyVaultName);
                                sp.ParamConnectionString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                            }

                            sp.SourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            sp.Defaultvalue = dr["Defaultvalue"]?.ToString() ?? string.Empty;
                            sp.PreFetch = (dr["PreFetch"]?.ToString() ?? string.Empty).Equals("True");

                            TypeInfo vInfo = TypeDeterminer.GetValueType(sp.Defaultvalue, DateTimeFormats);

                            //Fetch Param Value
                            conStringParser = new ConStringParser(sp.ParamConnectionString) { ConBuilderMsSql = { ApplicationName = "SQLFlow SP Param" } };

                            string paramConString = conStringParser.ConBuilderMsSql.ConnectionString;
                            if (sp.PreFetch)
                            {
                                //Fetch Param Value
                                logger.LogCodeBlock("SelectExp for PreFetch Parameter", sp.ParamSelectExp);

                                object rValue = null;
                                DbType dbType = DbType.String;

                                rValue = CommonDB.ExecuteScalar(paramConString, sp.ParamSelectExp, sp.SourceType, 360);
                                dbType = CommonDB.GetSqlDbTypeFromObject(rValue);

                                if (ParserParameters.DupeParameter(prefetchParams, sp.ParamName) == false)
                                {
                                    SqlParameter p = new SqlParameter
                                    {
                                        ParameterName = sp.ParamName,
                                        Value = rValue,
                                        DbType = dbType
                                    };

                                    //logger.LogInformation($"Prefetched {sp.ParamName} value {rValue}");

                                    ParameterObject pinfo = new ParameterObject();
                                    pinfo.Name = sp.ParamName;
                                    pinfo.Value = rValue.ToString();
                                    pinfo.sqlParameter = p;
                                    pinfo.ParameterType = sp.SourceType;
                                    prefetchParams.Add(pinfo);
                                }
                            }
                        }


                        foreach (DataRow dr in spParam.Rows)
                        {
                            sp.ParamName = dr["ParamName"]?.ToString() ?? string.Empty;
                            sp.ParamSelectExp = dr["SelectExp"]?.ToString() ?? string.Empty;
                            sp.ParamConnectionString = dr["ConnectionString"]?.ToString() ?? string.Empty;
                            sp.trgTenantId = paramTbl.Rows[0]["trgTenantId"]?.ToString() ?? string.Empty;
                            sp.trgApplicationId = paramTbl.Rows[0]["trgApplicationId"]?.ToString() ?? string.Empty;
                            sp.trgClientSecret = paramTbl.Rows[0]["trgClientSecret"]?.ToString() ?? string.Empty;
                            sp.trgKeyVaultName = paramTbl.Rows[0]["trgKeyVaultName"]?.ToString() ?? string.Empty;
                            sp.trgSecretName = paramTbl.Rows[0]["trgSecretName"]?.ToString() ?? string.Empty;
                            sp.trgStorageAccountName = paramTbl.Rows[0]["trgStorageAccountName"]?.ToString() ?? string.Empty;
                            sp.trgBlobContainer = paramTbl.Rows[0]["trgBlobContainer"]?.ToString() ?? string.Empty;
                            sp.SourceType = dr["SourceType"]?.ToString() ?? string.Empty;
                            sp.Defaultvalue = dr["Defaultvalue"]?.ToString() ?? string.Empty;
                            sp.PreFetch = (dr["PreFetch"]?.ToString() ?? string.Empty).Equals("True");

                            TypeInfo vInfo = TypeDeterminer.GetValueType(sp.Defaultvalue, DateTimeFormats);
                            
                            if (sp.trgSecretName.Length > 0)
                            {
                                AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                    sp.trgTenantId,
                                    sp.trgApplicationId,
                                    sp.trgClientSecret,
                                    sp.trgKeyVaultName);
                                sp.ParamConnectionString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                            }

                            conStringParser = new ConStringParser(sp.ParamConnectionString) { ConBuilderMsSql = { ApplicationName = "SQLFlow SP Param" } };
                            string paramConString = conStringParser.ConBuilderMsSql.ConnectionString;

                            if (sp.PreFetch == false)
                            {
                                List<ParameterObject> paramLookup = ParserParameters.GetParametersFromSql(sp.ParamSelectExp);
                                List<ParameterObject> cmdParam = new List<ParameterObject>();

                                //CheckForError if Param SQL contains a Prefetch Reference 
                                foreach (ParameterObject pa in paramLookup)
                                {
                                    foreach (ParameterObject pa2 in prefetchParams)
                                    {
                                        if (pa.Name == pa2.Name)
                                        {
                                            cmdParam.Add(pa2);
                                            logger.LogInformation($"Prefetched value used for {pa2.Name}={pa2.Value}");
                                        }
                                        else
                                        {
                                           
                                            pa.sqlParameter.Value = vInfo.ParsedValue;
                                            pa.sqlParameter.DbType = vInfo.DbType;
                                            cmdParam.Add(pa);
                                            logger.LogInformation($"Parameter added with default value {pa.Name}={pa.DefaultValue}");
                                        }
                                    }
                                }

                                //Fetch Param Value
                                logger.LogCodeBlock("SelectCmd for ParameterObject", sp.ParamSelectExp);
                                object rValue = CommonDB.ExecuteScalarWithParam(paramConString, sp.ParamSelectExp, "MSSQL", 360, cmdParam);
                                
                                SqlParameter p = new SqlParameter
                                {
                                    ParameterName = sp.ParamName,
                                    Value = rValue,
                                    Direction = ParameterDirection.Input,
                                    DbType = CommonDB.GetSqlDbTypeFromObject(rValue)
                                };
                                paramList.Add(p);

                                logger.LogInformation($"Fetched parameter {sp.ParamName} with value {rValue}");
                            }
                        }
                    }
                    
                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);

                        //Init LogStack
                        logger.LogInformation($"Init execution of stored procedure {sp.trgDbSchTbl}");
                        
                        if (sp.trgSecretName.Length > 0)
                        {
                            AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                sp.trgTenantId,
                                sp.trgApplicationId,
                                sp.trgClientSecret,
                                sp.trgKeyVaultName);
                            sp.trgConString = trgKeyVaultManager.GetSecret(sp.trgSecretName);
                        }

                        conStringParser = new ConStringParser(sp.trgConString) {ConBuilderMsSql = { ApplicationName = "SQLFlow Target" }};
                        sp.trgConString = conStringParser.ConBuilderMsSql.ConnectionString;

                        trgSqlCon = new SqlConnection(sp.trgConString);
                        trgSqlCon.Open();
                        
                        if (sp.trgDBSchSP.Length > 0)
                        {
                            try
                            {
                                using (logger.TrackOperation("Fetch sp parameters"))
                                {
                                    SqlConnection sqlCon = new SqlConnection(sp.trgConString);
                                    ServerConnection srvCon = new ServerConnection(sqlCon);
                                    Server srv = new Server(srvCon);
                                    
                                    var smoObject = SmoHelper.GetSmoObjectFromUrn(srv, sp, "sp");
                                    if (smoObject is StoredProcedure)
                                    {
                                        StoredProcedure proc = smoObject as StoredProcedure;
                                        List<string> statsParam = Functions.GetStatsParams();
                                        foreach (Parameter p in proc.Parameters)
                                        {
                                            if (statsParam.Contains(p.Name.ToLower()))
                                            {
                                                SqlParameter p2 = new SqlParameter
                                                {
                                                    ParameterName = p.Name,
                                                    Direction = ParameterDirection.Output,
                                                    DbType = DbType.Int32
                                                };
                                                paramList.Add(p2);
                                            }
                                        }
                                    }
                                    
                                    srv.ConnectionContext.Disconnect();
                                    srvCon.Disconnect();
                                    sqlCon.Close();
                                    sqlCon.Dispose();
                                }

                                using (logger.TrackOperation("Execute stored procedure"))
                                {
                                    SqlCommand cmdOntrg = CommonDB.BuildCmdForSPWithParam(trgSqlCon, sp.trgDBSchSP, paramList, sp.bulkLoadTimeoutInSek);
                                    sp.logSelectCmd = SqlCommandText.GetCommandText(cmdOntrg);
                                    cmdOntrg.ExecuteNonQuery();

                                    foreach (SqlParameter p in cmdOntrg.Parameters)
                                    {
                                        string rValue = Convert.ToString(p.Value);
                                        if (rValue.Length > 0)
                                        {
                                            if (p.ParameterName.ToLower().Replace("@", "") == Functions.StatsParameter.Fetched.ToString().ToLower())
                                            {
                                                sp.logFetched = long.Parse(rValue);
                                            }

                                            if (p.ParameterName.ToLower().Replace("@", "") == Functions.StatsParameter.Deleted.ToString().ToLower())
                                            {
                                                sp.logDeleted = long.Parse(rValue);
                                            }

                                            if (p.ParameterName.ToLower().Replace("@", "") == Functions.StatsParameter.Inserted.ToString().ToLower())
                                            {
                                                sp.logInserted = long.Parse(rValue);
                                            }

                                            if (p.ParameterName.ToLower().Replace("@", "") == Functions.StatsParameter.Updated.ToString().ToLower())
                                            {
                                                sp.logUpdated = long.Parse(rValue);
                                            }
                                        }
                                    }
                                }

                                execTime.Stop();
                                sp.logEndTime = DateTime.Now;
                                var totDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                logger.LogInformation($"Total processing time ({totDurationFlow} sec)");

                                Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);

                                trgSqlCon.Close();
                                trgSqlCon.Dispose();
                            }
                            catch (Exception e)
                            {
                                Shared.LogFileException(sqlFlowParam, sp, logger, e, sqlFlowCon, logOutput);
                                if (trgSqlCon.State == ConnectionState.Open)
                                {
                                    trgSqlCon.Close();
                                    trgSqlCon.Dispose();
                                }

                                if (sqlFlowCon.State == ConnectionState.Open)
                                {
                                    sqlFlowCon.Close();
                                }
                            }
                            finally
                            {
                                trgSqlCon.Close();
                                trgSqlCon.Dispose();
                                sqlFlowCon.Close();
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException("Unable to parse meta data");
                        }
                    }

                    return sp.result;
                }
                catch (Exception e)
                {
                    sp.result = Shared.LogOuterError(sqlFlowParam, e, sp, logger, logOutput, sqlFlowCon);
                }
                finally
                {
                    sqlFlowCon.Close();
                }
            }

            return sp.result;
        }

        

        #endregion ExecStoredProcedure
    }
}