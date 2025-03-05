using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using MySql.Data.MySqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DataType = Microsoft.SqlServer.Management.Smo.DataType;
using Index = Microsoft.SqlServer.Management.Smo.Index;
using Table = Microsoft.SqlServer.Management.Smo.Table;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
namespace SQLFlowCore.Pipeline
{
    public static class ExecMatchKey
    {
        private static EventArgsSchema schArgs = new();

        public static string Exec(SqlFlowParam sqlFlowParam)
        {
            StringBuilder logStack = new StringBuilder("");
            var codeStack = new StringBuilder("");

            string srcWithHint = "";
            string trgWithHint = "";


            // Source Connection Strings
            string srcTenantId = string.Empty;
            string srcSubscriptionId = string.Empty;
            string srcApplicationId = string.Empty;
            string srcClientSecret = string.Empty;
            string srcKeyVaultName = string.Empty;
            string srcSecretName = string.Empty;
            string srcResourceGroup = string.Empty;
            string srcDataFactoryName = string.Empty;
            string srcAutomationAccountName = string.Empty;
            string srcStorageAccountName = string.Empty;
            string srcBlobContainer = string.Empty;

            // Target Connection Strings
            string trgTenantId = string.Empty;
            string trgSubscriptionId = string.Empty;
            string trgApplicationId = string.Empty;
            string trgClientSecret = string.Empty;
            string trgKeyVaultName = string.Empty;
            string trgSecretName = string.Empty;
            string trgResourceGroup = string.Empty;
            string trgDataFactoryName = string.Empty;
            string trgAutomationAccountName = string.Empty;
            string trgStorageAccountName = string.Empty;
            string trgBlobContainer = string.Empty;

            // Other Parameters
            string srcConString = string.Empty;
            string trgConString = string.Empty;
            string srcDatabase = string.Empty;
            string srcSchema = string.Empty;
            string srcObject = string.Empty;
            string stgSchema = string.Empty;
            string trgDatabase = string.Empty;
            string trgSchema = string.Empty;
            string trgObject = string.Empty;
            string dateColumn = string.Empty;
            int ignoreDeletedRowsAfter = 0;
            int bulkLoadTimeoutInSek = 0;
            int generalTimeoutInSek = 0;


            string flowBatch = "";
            bool onErrorResume = true;

            string preProcessOnTrg = string.Empty;
            string postProcessOnTrg = string.Empty;

            string srcFilter = string.Empty;
            string trgFilter = string.Empty;

            string keyColumns = string.Empty;
            string flowId = string.Empty;
            string matchKeyID = string.Empty;
            string sysAlias = string.Empty;
            bool srcIsSynapse = false;
            bool trgIsSynapse = false;
            string batch = string.Empty;

            string srcDSType = string.Empty;
            string delSchema = string.Empty;


            string actionType = string.Empty;


            bool dbg = false;

            DateTime InitLoadFromDate = DateTime.Now;
            DateTime InitLoadToDate = DateTime.Now;

            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            //sqlFlowParam.sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            int srcRowCount = 0;
            int srcDelRowCount = 0;
            int trgRowCount = 0;
            int trgDelRowCount = 0;
            int taggedRowCount = 0;
            var result = "false";
            int actionThresholdPercent = 20;
            using (var sqlFlowCon = new SqlConnection(sqlFlowParam.sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();

                    string flowParamCmd =
                        $@" exec [flw].[GetRVMatchKey] @FlowID = {sqlFlowParam.flowId.ToString()}, @MatchKeyID = {sqlFlowParam.matchKeyId.ToString()}, @ExecMode = '{sqlFlowParam.execMode}', @dbg = {sqlFlowParam.dbg.ToString()} {Environment.NewLine}";
                    codeStack.AppendIf(sqlFlowParam.dbg > 1, Functions.CodeStackSection("Runtime values:", flowParamCmd));

                    DataTable DateTimeFormats = FlowDates.GetDateTimeFormats(sqlFlowCon);

                    DataSet ds = CommonDB.GetDataSetFromSP(sqlFlowCon, flowParamCmd, 360);
                    DataTable paramTbl = ds.Tables[0];

                    foreach (DataRow dr in paramTbl.Rows)
                    {
                        // Flow Parameters
                        flowId = paramTbl.Rows[0]["FlowID"]?.ToString() ?? string.Empty;
                        matchKeyID = paramTbl.Rows[0]["MatchKeyID"]?.ToString() ?? string.Empty;

                        // Source Connection Strings
                        srcTenantId = paramTbl.Rows[0]["srcTenantId"]?.ToString() ?? string.Empty;
                        srcSubscriptionId = paramTbl.Rows[0]["srcSubscriptionId"]?.ToString() ?? string.Empty;
                        srcApplicationId = paramTbl.Rows[0]["srcApplicationId"]?.ToString() ?? string.Empty;
                        srcClientSecret = paramTbl.Rows[0]["srcClientSecret"]?.ToString() ?? string.Empty;
                        srcKeyVaultName = paramTbl.Rows[0]["srcKeyVaultName"]?.ToString() ?? string.Empty;
                        srcSecretName = paramTbl.Rows[0]["srcSecretName"]?.ToString() ?? string.Empty;
                        srcResourceGroup = paramTbl.Rows[0]["srcResourceGroup"]?.ToString() ?? string.Empty;
                        srcDataFactoryName = paramTbl.Rows[0]["srcDataFactoryName"]?.ToString() ?? string.Empty;
                        srcAutomationAccountName = paramTbl.Rows[0]["srcAutomationAccountName"]?.ToString() ?? string.Empty;
                        srcStorageAccountName = paramTbl.Rows[0]["srcStorageAccountName"]?.ToString() ?? string.Empty;
                        srcBlobContainer = paramTbl.Rows[0]["srcBlobContainer"]?.ToString() ?? string.Empty;

                        // Target Connection Strings
                        trgTenantId = paramTbl.Rows[0]["trgTenantId"]?.ToString() ?? string.Empty;
                        trgSubscriptionId = paramTbl.Rows[0]["trgSubscriptionId"]?.ToString() ?? string.Empty;
                        trgApplicationId = paramTbl.Rows[0]["trgApplicationId"]?.ToString() ?? string.Empty;
                        trgClientSecret = paramTbl.Rows[0]["trgClientSecret"]?.ToString() ?? string.Empty;
                        trgKeyVaultName = paramTbl.Rows[0]["trgKeyVaultName"]?.ToString() ?? string.Empty;
                        trgSecretName = paramTbl.Rows[0]["trgSecretName"]?.ToString() ?? string.Empty;
                        trgResourceGroup = paramTbl.Rows[0]["trgResourceGroup"]?.ToString() ?? string.Empty;
                        trgDataFactoryName = paramTbl.Rows[0]["trgDataFactoryName"]?.ToString() ?? string.Empty;
                        trgAutomationAccountName = paramTbl.Rows[0]["trgAutomationAccountName"]?.ToString() ?? string.Empty;
                        trgStorageAccountName = paramTbl.Rows[0]["trgStorageAccountName"]?.ToString() ?? string.Empty;
                        trgBlobContainer = paramTbl.Rows[0]["trgBlobContainer"]?.ToString() ?? string.Empty;

                        // Other Parameters
                        srcConString = paramTbl.Rows[0]["SrcConString"]?.ToString() ?? string.Empty;
                        trgConString = paramTbl.Rows[0]["TrgConString"]?.ToString() ?? string.Empty;
                        srcDatabase = paramTbl.Rows[0]["srcDatabase"]?.ToString() ?? string.Empty;
                        srcSchema = paramTbl.Rows[0]["srcSchema"]?.ToString() ?? string.Empty;
                        srcObject = paramTbl.Rows[0]["srcObject"]?.ToString() ?? string.Empty;
                        stgSchema = paramTbl.Rows[0]["stgSchema"]?.ToString() ?? string.Empty;
                        trgDatabase = paramTbl.Rows[0]["trgDatabase"]?.ToString() ?? string.Empty;
                        trgSchema = paramTbl.Rows[0]["trgSchema"]?.ToString() ?? string.Empty;
                        trgObject = paramTbl.Rows[0]["trgObject"]?.ToString() ?? string.Empty;
                        dateColumn = paramTbl.Rows[0]["DateColumn"]?.ToString() ?? string.Empty;
                        ignoreDeletedRowsAfter = int.Parse(paramTbl.Rows[0]["IgnoreDeletedRowsAfter"]?.ToString() ?? string.Empty);
                        bulkLoadTimeoutInSek = int.Parse(paramTbl.Rows[0]["BulkLoadTimeoutInSek"]?.ToString() ?? string.Empty);
                        generalTimeoutInSek = int.Parse(paramTbl.Rows[0]["GeneralTimeoutInSek"]?.ToString() ?? string.Empty);

                        preProcessOnTrg = paramTbl.Rows[0]["PreProcessOnTrg"]?.ToString() ?? string.Empty;
                        postProcessOnTrg = paramTbl.Rows[0]["PostProcessOnTrg"]?.ToString() ?? string.Empty;

                        srcFilter = paramTbl.Rows[0]["srcFilter"]?.ToString() ?? string.Empty;
                        trgFilter = paramTbl.Rows[0]["trgFilter"]?.ToString() ?? string.Empty;
                        keyColumns = paramTbl.Rows[0]["KeyColumns"]?.ToString() ?? string.Empty;

                        sysAlias = paramTbl.Rows[0]["SysAlias"]?.ToString() ?? string.Empty;

                        srcIsSynapse = paramTbl.Rows[0]["srcIsSynapse"].ToString().Equals("True");
                        trgIsSynapse = paramTbl.Rows[0]["trgIsSynapse"].ToString().Equals("True");

                        batch = paramTbl.Rows[0]["Batch"]?.ToString() ?? string.Empty;
                        onErrorResume = paramTbl.Rows[0]["onErrorResume"].ToString().Equals("True");

                        actionType = paramTbl.Rows[0]["ActionType"]?.ToString() ?? string.Empty;

                        srcDSType = paramTbl.Rows[0]["srcDSType"]?.ToString() ?? string.Empty;
                        delSchema = paramTbl.Rows[0]["delSchema"]?.ToString() ?? string.Empty;
                        dbg = paramTbl.Rows[0]["dbg"].ToString().Equals("True");

                        srcWithHint = srcIsSynapse ? "nolock" : "readpast";
                        trgWithHint = trgIsSynapse ? "nolock" : "readpast";

                        EnhancedObjectNameList oKeyColumns = new EnhancedObjectNameList(CommonDB.ParseObjectNames(keyColumns));

                        keyColumns = Functions.CleanupColumns(keyColumns);
                        dateColumn = Functions.CleanupColumns(dateColumn);

                        srcDatabase = Functions.CleanupColumns(srcDatabase);
                        srcSchema = Functions.CleanupColumns(srcSchema);
                        srcObject = Functions.CleanupColumns(srcObject);
                        stgSchema = Functions.CleanupColumns(stgSchema);
                        trgDatabase = Functions.CleanupColumns(trgDatabase);
                        trgSchema = Functions.CleanupColumns(trgSchema);
                        trgObject = Functions.CleanupColumns(trgObject);



                        //SchemaADO.SchemaSyncLog += (sender, e) => SchemaADO_SchemaSyncLog(sender, e, logStack);

                        if (trgDatabase.Length > 0)
                        {
                            var execTime = new Stopwatch();
                            execTime.Start();
                            var watch = new Stopwatch();

                            string logStartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                            string logEndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                            long logDurationFlow = 0;
                            long logDurationPre = 0;
                            long logDurationPost = 0;
                            string logRuntimeCmd = "";
                            string logErrorMessage = "";

                            if (srcSecretName.Length > 0)
                            {
                                AzureKeyVaultManager srcKeyVaultManager = new AzureKeyVaultManager(
                                    srcTenantId,
                                    srcApplicationId,
                                    srcClientSecret,
                                    srcKeyVaultName);
                                srcConString = srcKeyVaultManager.GetSecret(srcSecretName);
                            }

                            //logStack.Append($"CurrentLocalDirectory {System.IO.Directory.GetCurrentDirectory()} {Environment.NewLine}");

                            conStringParser = new ConStringParser(srcConString);
                            //conStringParser.ConBuilder.ApplicationName = "SQLFlow Source";
                            if (srcDSType.Equals("MySql", StringComparison.InvariantCultureIgnoreCase))
                            {
                                srcConString = conStringParser.ConBuilderMySql.ConnectionString;
                            }
                            else
                            {
                                srcConString = conStringParser.ConBuilderMsSql.ConnectionString;
                            }

                            if (trgSecretName.Length > 0)
                            {
                                AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                    trgTenantId,
                                    trgApplicationId,
                                    trgClientSecret,
                                    trgKeyVaultName);
                                trgConString = trgKeyVaultManager.GetSecret(trgSecretName);
                            }

                            conStringParser = new ConStringParser(trgConString)
                            {
                                ConBuilderMsSql =
                                {
                                    ApplicationName = "SQLFlow Target"
                                }
                            };
                            trgConString = conStringParser.ConBuilderMsSql.ConnectionString;

                            var trgSqlCon = new SqlConnection(trgConString);

                            DbConnection srcCon = null;
                            srcRowCount = 0;

                            try
                            {
                                trgSqlCon.Open();

                                //Execute preprosess on target 
                                if (preProcessOnTrg.Length > 2)
                                {
                                    var cmdOnSrc = new ExecNonQuery(trgSqlCon, preProcessOnTrg, bulkLoadTimeoutInSek);
                                    watch.Restart();
                                    cmdOnSrc.Exec();
                                    watch.Stop();
                                    logDurationPre = watch.ElapsedMilliseconds / 1000;
                                    logStack.Append(
                                        $"Info: PreProcess executed on target ({logDurationPre.ToString()} sec) {Environment.NewLine}");
                                }

                                //NonCritical Retry Error Codes from target Database
                                var retryErrorCodes = new Hashtable(StringComparer.InvariantCultureIgnoreCase);
                                RetryCodes rCodes = new RetryCodes();
                                retryErrorCodes = rCodes.HashTbl;

                                // Add key matching functionality here
                                var result2 = KeyMatcher.PerformKeyMatching(
                                    srcConString, trgConString,
                                    srcDatabase, srcSchema, srcObject,
                                    trgDatabase, trgSchema, trgObject,
                                    oKeyColumns, dateColumn, ignoreDeletedRowsAfter,
                                    delSchema, DateTimeFormats, srcFilter, trgFilter, matchKeyID, actionType, actionThresholdPercent).Result;


                                srcRowCount = result2.SourceRowsProcessed;
                                srcDelRowCount = result2.SrcDelRowCount;
                                trgRowCount = result2.TargetRowsProcessed;
                                trgDelRowCount = result2.TrgDelRowCount;
                                taggedRowCount = result2.RowsTaggedAsDeleted;
                                #region postProcessOnTrg
                                if (postProcessOnTrg.Length > 2)
                                {
                                    var cmdOnTrg = new ExecNonQuery(trgSqlCon, postProcessOnTrg, bulkLoadTimeoutInSek);
                                    watch.Restart();
                                    cmdOnTrg.Exec();
                                    watch.Stop();
                                    logDurationPost = watch.ElapsedMilliseconds / 1000;
                                    logStack.Append(
                                        $"Info: Post-process executed on target ({logDurationPost.ToString()} sec) {Environment.NewLine}");
                                }
                                #endregion postProcessOnTrg

                                logStack.Append(result2.logStack.ToString());

                                execTime.Stop();
                                logDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                logEndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                logStack.Append(
                                    $"Info: Total processing time ({logDurationFlow.ToString()} sec) {(srcRowCount / (logDurationFlow > 0 ? logDurationFlow : 1)).ToString()} (rows/sec) {Environment.NewLine}");

                                logRuntimeCmd = logStack.ToString();
                                using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogMatchKey]", sqlFlowCon))
                                {
                                    cmd.CommandType = CommandType.StoredProcedure;
                                    if (!string.IsNullOrEmpty(matchKeyID))
                                    {
                                        cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = int.Parse(matchKeyID);
                                    }
                                    else
                                    {
                                        cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = DBNull.Value;
                                    }

                                    if (!string.IsNullOrEmpty(flowId))
                                    {
                                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = int.Parse(flowId);
                                    }
                                    else
                                    {
                                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = DBNull.Value;
                                    }
                                    cmd.Parameters.Add("@BatchID", SqlDbType.VarChar).Value = sqlFlowParam.batchId;
                                    cmd.Parameters.Add("@SysAlias", SqlDbType.VarChar, 70).Value = (object)sysAlias ?? DBNull.Value;
                                    cmd.Parameters.Add("@Batch", SqlDbType.VarChar, 250).Value = (object)batch ?? DBNull.Value;
                                    cmd.Parameters.Add("@EndTime", SqlDbType.DateTime).Value = logEndTime;
                                    cmd.Parameters.Add("@StartTime", SqlDbType.DateTime).Value = logStartTime;
                                    cmd.Parameters.Add("@DurationMatch", SqlDbType.VarChar).Value = logDurationFlow;
                                    cmd.Parameters.Add("@DurationPre", SqlDbType.VarChar).Value = logDurationPre;
                                    cmd.Parameters.Add("@DurationPost", SqlDbType.VarChar).Value = logDurationPost;
                                    cmd.Parameters.Add("@SrcRowCount", SqlDbType.Int).Value = (object)srcRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@SrcDelRowCount", SqlDbType.Int).Value = (object)srcDelRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TrgRowCount", SqlDbType.Int).Value = (object)trgRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TrgDelRowCount", SqlDbType.Int).Value = (object)trgDelRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TaggedRowCount", SqlDbType.Int).Value = (object)taggedRowCount ?? DBNull.Value;
                                    cmd.ExecuteNonQuery();
                                    cmd.Dispose();
                                }

                                string ExecSuccess = "false";
                                ExecSuccess = ErrorChecker.HasError(logStack.ToString()) || logErrorMessage.Length > 0 ? "false" : "true";

                                //Add Error message to log stack

                                if (logErrorMessage.Length > 0)
                                {
                                    logStack.Append($"Error: Runtime {logErrorMessage} {Environment.NewLine}");
                                }


                                string rJson = $"Info: MatchId:{matchKeyID},success:{ExecSuccess},src:{srcRowCount},srcDel:{srcDelRowCount},trg:{trgRowCount},trgDel:{trgDelRowCount},TagDeleted: {taggedRowCount}" + Environment.NewLine;

                                result = rJson + (sqlFlowParam.dbg >= 1 ? logStack + (logStack.Length > 2 ? Environment.NewLine : "") : "") +
                                         (sqlFlowParam.dbg > 1 ? codeStack + Environment.NewLine : "");

                            }
                            catch (Exception e)
                            {
                                logRuntimeCmd = logStack.ToString();
                                logErrorMessage = e.Message + Environment.NewLine + e.StackTrace;

                                //Error returned to client
                                string ExecSuccess = "false";
                                ExecSuccess = ErrorChecker.HasError(logStack.ToString()) || logErrorMessage.Length > 0 ? "false" : "true";

                                string rJson = "{" + $"MatchedId:{sqlFlowParam.matchKeyId},success:{ExecSuccess},fetched:{srcRowCount}" + "}" + Environment.NewLine;

                                result = rJson + logStack + Environment.NewLine + e.Message +
                                         Environment.NewLine + e.StackTrace +
                                         Environment.NewLine + codeStack;

                                using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogMatchKey]", sqlFlowCon))
                                {
                                    cmd.CommandType = CommandType.StoredProcedure;
                                    if (!string.IsNullOrEmpty(matchKeyID))
                                    {
                                        cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = int.Parse(matchKeyID);
                                    }
                                    else
                                    {
                                        cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = DBNull.Value;
                                    }

                                    if (!string.IsNullOrEmpty(flowId))
                                    {
                                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = int.Parse(flowId);
                                    }
                                    else
                                    {
                                        cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = DBNull.Value;
                                    }
                                    cmd.Parameters.Add("@EndTime", SqlDbType.DateTime).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                    cmd.Parameters.Add("@StartTime", SqlDbType.DateTime).Value = logStartTime;
                                    cmd.Parameters.Add("@DurationMatch", SqlDbType.VarChar).Value = logDurationFlow;
                                    cmd.Parameters.Add("@DurationPre", SqlDbType.VarChar).Value = logDurationPre;
                                    cmd.Parameters.Add("@DurationPost", SqlDbType.VarChar).Value = logDurationPost;
                                    cmd.Parameters.Add("@SrcRowCount", SqlDbType.Int).Value = (object)srcRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@SrcDelRowCount", SqlDbType.Int).Value = (object)srcDelRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TrgRowCount", SqlDbType.Int).Value = (object)trgRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TrgDelRowCount", SqlDbType.Int).Value = (object)trgDelRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@TaggedRowCount", SqlDbType.Int).Value = (object)taggedRowCount ?? DBNull.Value;
                                    cmd.Parameters.Add("@ErrorMessage", SqlDbType.VarChar).Value = logErrorMessage;
                                    cmd.Parameters.Add("@batch", SqlDbType.VarChar).Value = batch;
                                    cmd.Parameters.Add("@SysAlias", SqlDbType.VarChar).Value = sysAlias;
                                    cmd.Parameters.Add("@BatchID", SqlDbType.VarChar).Value = sqlFlowParam.batchId;
                                    cmd.Parameters.Add("@TraceLog", SqlDbType.VarChar).Value = logStack.ToString();

                                    cmd.ExecuteNonQuery();
                                }

                                if (trgSqlCon.State == ConnectionState.Open)
                                {
                                    trgSqlCon.Close();
                                    trgSqlCon.Dispose();
                                }

                                if (sqlFlowCon.State == ConnectionState.Open)
                                {
                                    sqlFlowCon.Close();
                                    sqlFlowCon.Dispose();
                                }

                                if (onErrorResume == false)
                                {
                                    throw new Exception(result);
                                }
                            }
                            finally
                            {
                                
                                //SqlConnection.ClearPool(sqlFlowCon);
                                if (srcCon != null)
                                {
                                    srcCon.Close();
                                    srcCon.Dispose();
                                }

                                trgSqlCon.Close();
                                trgSqlCon.Dispose();

                                sqlFlowCon.Close();
                                sqlFlowCon.Dispose();
                            }

                        }
                    }

                    return result;
                }
                catch (Exception e)
                {
                    //Error returned to client

                    string ExecSuccess = "false";
                    string rJson = "{" + $"MatchedId:{sqlFlowParam.matchKeyId},success:{ExecSuccess}, fetched:{srcRowCount}" + "}" + Environment.NewLine;

                    result = rJson + e.Message +
                             Environment.NewLine + e.StackTrace +
                             Environment.NewLine;

                    if (sqlFlowCon.State == ConnectionState.Open)
                    {
                        if (!string.IsNullOrEmpty(matchKeyID))
                        {
                            using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLogMatchKey]", sqlFlowCon))
                            {
                                cmd.CommandType = CommandType.StoredProcedure;
                                if (!string.IsNullOrEmpty(matchKeyID))
                                {
                                    cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = int.Parse(matchKeyID);
                                }
                                else
                                {
                                    cmd.Parameters.Add("@MatchKeyID", SqlDbType.Int).Value = DBNull.Value;
                                }

                                if (!string.IsNullOrEmpty(flowId))
                                {
                                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = int.Parse(flowId);
                                }
                                else
                                {
                                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = DBNull.Value;
                                }

                                cmd.Parameters.Add("@ErrorMessage", SqlDbType.VarChar).Value = e.Message;
                                cmd.Parameters.Add("@batch", SqlDbType.VarChar).Value = flowBatch;
                                cmd.Parameters.Add("@SysAlias", SqlDbType.VarChar).Value = sysAlias;
                                cmd.Parameters.Add("@BatchID", SqlDbType.VarChar).Value = sqlFlowParam.batchId;
                                cmd.Parameters.Add("@TraceLog", SqlDbType.VarChar).Value = logStack.ToString();
                                cmd.ExecuteNonQuery();
                                cmd.Dispose();
                            }
                        }


                        sqlFlowCon.Close();
                        sqlFlowCon.Dispose();
                    }

                    if (onErrorResume == false)
                    {
                        throw new Exception(result);
                    }
                }
                finally
                {
                    //SqlConnection.ClearPool(sqlFlowCon);
                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
            }

            return result;
        }


        public static class KeyMatcher
        {
            public class KeyRecord : IComparable<KeyRecord>
            {
                public int HashKey { get; set; }
                public string Key { get; set; }
                public DateTime? Date { get; set; }
                public string DifferenceType { get; set; }

                public int CompareTo(KeyRecord other)
                {
                    if (other == null) return 1;

                    int hashComparison = HashKey.CompareTo(other.HashKey);
                    if (hashComparison != 0) return hashComparison;

                    // If hash codes are equal, compare the actual keys
                    return string.Compare(Key, other.Key, StringComparison.Ordinal);
                }

                public override bool Equals(object obj)
                {
                    return obj is KeyRecord record &&
                           HashKey == record.HashKey &&
                           Key == record.Key;
                }

                public override int GetHashCode()
                {
                    return HashCode.Combine(HashKey, Key);
                }
            }

            public class KeyMatchingResult
            {
                public int SourceRowsProcessed { get; set; } = 0;
                public int TargetRowsProcessed { get; set; } = 0;

                public int SrcDelRowCount { get; set; } = 0;
                public int TrgDelRowCount { get; set; } = 0;

                public int DifferencesFound { get; set; } = 0;
                public int RowsTaggedAsDeleted { get; set; } = 0;

                public StringBuilder logStack { get; set; } = new();
            }

            public static DataTable DateTimeFormats = new();


            internal static async Task<KeyMatchingResult> PerformKeyMatching(
                string srcConString, string trgConString,
                string srcDatabase, string srcSchema, string srcObject,
                string trgDatabase, string trgSchema, string trgObject,
                EnhancedObjectNameList keyColumns, string dateColumn, int ignoreDeletedRowsAfter,
                string delSchema, DataTable DateTimeFormats, string srcFilter, string trgFilter, string matchKeyId, string actionType, decimal thresholdPercent)
            {
                var result = new KeyMatchingResult();

                try
                {
                    StringBuilder logStack = new StringBuilder();
                    logStack.AppendLine("Starting dual queue stream-based key matching.");

                    var srcQueue = new SortedSet<KeyRecord>();
                    var trgQueue = new SortedSet<KeyRecord>();
                    var diffQueue = new SortedSet<KeyRecord>();

                    string delTableName = $"{trgObject}{matchKeyId}";

                    using (var srcReader = await GetOrderedDataReaderAsync(srcConString, BuildOrderedSelectQuery(srcDatabase, srcSchema, srcObject, keyColumns, dateColumn, srcFilter)))
                    using (var trgReader = await GetOrderedDataReaderAsync(trgConString, BuildOrderedSelectQuery(trgDatabase, trgSchema, trgObject, keyColumns, dateColumn, trgFilter)))
                    {
                        // Start tasks to fill queues concurrently
                        //var srcFillTask = Task.Run(() => FillQueue(srcReader, srcQueue, keyColumns, dateColumn, logStack));
                        //var trgFillTask = Task.Run(() => FillQueue(trgReader, trgQueue, keyColumns, dateColumn, logStack));

                        await ProcessStreams(srcReader, trgReader, srcQueue, trgQueue, diffQueue, keyColumns, dateColumn, result, logStack);

                        // Wait for filling tasks to complete
                        //await Task.WhenAll(srcFillTask, trgFillTask);

                    }


                    // Set DifferencesFound to diffQueue.Count
                    result.DifferencesFound = diffQueue.Count;


                    foreach (var queue in diffQueue)
                    {

                        if (queue.DifferenceType == "Missing in Target")
                        {
                            result.TrgDelRowCount++;
                        }
                        else if (queue.DifferenceType == "Missing in Source")
                        {
                            result.SrcDelRowCount++;
                        }
                    }

                    // Process remaining unmatched keys
                    await PushKeysToTargetDelTable(diffQueue, trgConString, trgDatabase, trgSchema, trgObject, delSchema, keyColumns,
                        dateColumn, result, delTableName, actionType);

                    logStack.AppendLine($"Source rows processed: {result.SourceRowsProcessed}");
                    logStack.AppendLine($"Target rows processed: {result.TargetRowsProcessed}");
                    logStack.AppendLine($"Total differences found: {result.DifferencesFound}");

                    // Tag deleted rows
                    result.RowsTaggedAsDeleted = await TagDeletedRowsAsync(trgConString, trgSchema, trgObject, keyColumns, dateColumn, ignoreDeletedRowsAfter, delSchema, trgFilter, delTableName, actionType, thresholdPercent);
                    logStack.AppendLine($"Rows tagged as deleted in target: {result.RowsTaggedAsDeleted}");

                    result.logStack = logStack;

                }
                catch (Exception ex)
                {
                    result.logStack.AppendLine($"Error: An exception occurred during key matching: {ex.Message}");
                    result.logStack.AppendLine($"StackTrace: {ex.StackTrace}");
                    throw;
                }

                return result;
            }

            private static async Task ProcessStreams(IDataReader srcReader, IDataReader trgReader,
    SortedSet<KeyRecord> srcQueue, SortedSet<KeyRecord> trgQueue, SortedSet<KeyRecord> diffQueue,
    EnhancedObjectNameList keyColumns, string dateColumn,
    KeyMatchingResult result, StringBuilder logStack)
            {
                bool srcExhausted = false, trgExhausted = false;
                int loopCount = 0;
                const int MaxLoops = 10000000; // Safety mechanism to prevent truly infinite loops

                while (loopCount < MaxLoops)
                {
                    loopCount++;

                    bool srcFilled = false, trgFilled = false;

                    // Fill queues if they're not full and not exhausted
                    if (srcQueue.Count < 1000 && !srcExhausted)
                    {
                        srcExhausted = !await FillQueue(srcReader, srcQueue, keyColumns, dateColumn, logStack);
                        srcFilled = true;
                    }

                    if (trgQueue.Count < 1000 && !trgExhausted)
                    {
                        trgExhausted = !await FillQueue(trgReader, trgQueue, keyColumns, dateColumn, logStack);
                        trgFilled = true;
                    }

                    // Process queues
                    int processedCount = ProcessQueues(srcQueue, trgQueue, diffQueue, result);

                    // Log progress
                    //if (loopCount % 100 == 0 || processedCount > 0)
                    //{
                    //    logStack.AppendLine($"Loop {loopCount}, Processed {processedCount} records. " +
                    //                        $"Total: Src {result.SourceRowsProcessed}, Trg {result.TargetRowsProcessed}. " +
                    //                        $"Queue sizes: Src {srcQueue.Count}, Trg {trgQueue.Count}. " +
                    //                        $"Exhausted: Src {srcExhausted}, Trg {trgExhausted}");
                    //}

                    // Check termination condition
                    if (srcExhausted && trgExhausted)
                    {
                        //logStack.AppendLine("Both readers exhausted. Exiting loop.");
                        break;
                    }

                    // If no progress is made and no queues were filled, wait a bit
                    if (processedCount == 0 && !srcFilled && !trgFilled)
                    {
                        await Task.Delay(100);
                    }
                }

                if (loopCount >= MaxLoops)
                {
                    //logStack.AppendLine($"Warning: Reached maximum loop count of {MaxLoops}. Forcibly exiting.");
                }

                logStack.AppendLine($"Stream processing completed. Processed {result.SourceRowsProcessed} source rows and {result.TargetRowsProcessed} target rows.");
            }

            private static int ProcessQueues(
                SortedSet<KeyRecord> srcQueue, SortedSet<KeyRecord> trgQueue,
                SortedSet<KeyRecord> diffQueue, KeyMatchingResult result)
            {
                int processedCount = 0;
                var processedDifferences = new HashSet<string>(); // Track unique differences

                while (srcQueue.Count > 0 && trgQueue.Count > 0)
                {
                    int comparison = srcQueue.Min.CompareTo(trgQueue.Min);

                    if (comparison == 0)
                    {
                        srcQueue.Remove(srcQueue.Min);
                        trgQueue.Remove(trgQueue.Min);
                        result.SourceRowsProcessed++;
                        result.TargetRowsProcessed++;
                    }
                    else if (comparison < 0)
                    {
                        ProcessUnmatchedRecord(srcQueue, diffQueue, result, "Missing in Target", isSource: true, processedDifferences);
                    }
                    else
                    {
                        ProcessUnmatchedRecord(trgQueue, diffQueue, result, "Missing in Source", isSource: false, processedDifferences);
                    }

                    processedCount++;
                }

                // Process remaining records
                while (srcQueue.Count > 0)
                {
                    ProcessUnmatchedRecord(srcQueue, diffQueue, result, "Missing in Target", isSource: true, processedDifferences);
                    processedCount++;
                }

                while (trgQueue.Count > 0)
                {
                    ProcessUnmatchedRecord(trgQueue, diffQueue, result, "Missing in Source", isSource: false, processedDifferences);
                    processedCount++;
                }

                return processedCount;
            }

            private static void ProcessUnmatchedRecord(
                SortedSet<KeyRecord> queue, SortedSet<KeyRecord> diffQueue,
                KeyMatchingResult result, string differenceType, bool isSource,
                HashSet<string> processedDifferences)
            {
                var record = queue.Min;
                record.DifferenceType = differenceType;

                // Only add to diffQueue and increment DifferencesFound if it's a new difference
                if (processedDifferences.Add(record.Key))
                {
                    diffQueue.Add(record);
                }

                queue.Remove(record);

                if (isSource)
                    result.SourceRowsProcessed++;
                else
                    result.TargetRowsProcessed++;
            }

            private static async Task<bool> FillQueue(IDataReader reader, SortedSet<KeyRecord> queue, EnhancedObjectNameList keyColumns, string dateColumn, StringBuilder logStack)
            {
                if (reader == null || queue == null)
                {
                    logStack.AppendLine("Error: Reader or Queue is null. Cannot add records.");
                    return false;
                }

                const int batchSize = 10000;
                const int maxQueueSize = 100000;
                bool hasMoreRecords = false;

                await Task.Run(() =>
                {
                    for (int i = 0; i < batchSize && queue.Count < maxQueueSize && reader.Read(); i++)
                    {
                        try
                        {
                            var keyRecord = CreateKeyRecord(reader, keyColumns, dateColumn);
                            if (keyRecord != null)
                            {
                                if (!queue.Add(keyRecord))
                                {
                                    //logStack.AppendLine($"Warning: Duplicate KeyRecord at row {i + 1}");
                                }
                            }
                            else
                            {
                                //logStack.AppendLine($"Warning: Null KeyRecord created at row {i + 1}");
                            }
                        }
                        catch (Exception)
                        {
                            //logStack.AppendLine($"Error processing record at row {i + 1}: {ex.Message}");
                            //logStack.AppendLine($"Stack Trace: {ex.StackTrace}");
                        }
                        hasMoreRecords = true;
                    }
                });

                return hasMoreRecords;
            }

            private static async Task<int> PushKeysToTargetDelTable(SortedSet<KeyRecord> diffQueue, string trgConString, string trgDatabase, string trgSchema, string trgObject, string delSchema, EnhancedObjectNameList keyColumns, string dateColumn, KeyMatchingResult result, string delTableName, string actionType)
            {
                int diffCount = 0;
                using (var connection = new SqlConnection(trgConString))
                {
                    await connection.OpenAsync();

                    EnsureTargetTableExists(trgConString, trgDatabase, trgSchema, trgObject, delSchema, keyColumns,
                        dateColumn, delTableName, actionType);

                    string createTableQuery = $"SELECT TOP 0 * FROM [{delSchema}].[{delTableName}]";
                    var dataTable = new DataTable();

                    using (var command = new SqlCommand(createTableQuery, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        dataTable.Load(reader);
                    }


                    // Insert the difference keys into the DataTable
                    foreach (var queue in diffQueue)
                    {
                        var row = dataTable.NewRow();

                        // Set the values for each key column
                        var keyValues = queue.Key.Split('|');
                        var keyColumnNames = keyColumns.GetUnquotedNamesList().ToArray();
                        for (int i = 0; i < keyColumnNames.Length; i++)
                        {
                            row[keyColumnNames[i]] = keyValues[i];
                        }
                        row[dateColumn] = queue.Date.HasValue ? queue.Date.Value : DBNull.Value;
                        row["DifferenceType"] = queue.DifferenceType;

                        row["MatchDate"] = DateTime.Now;
                        dataTable.Rows.Add(row);

                        diffCount += 1;
                    }

                    // Bulk load the difference keys from the DataTable into the temporary table
                    using (var bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = $"[{delSchema}].[{delTableName}]";
                        foreach (DataColumn column in dataTable.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }
                        await bulkCopy.WriteToServerAsync(dataTable);
                    }


                }
                return diffCount;
            }

            private static KeyRecord CreateKeyRecord(IDataReader reader, EnhancedObjectNameList keyColumns, string dateColumn)
            {
                try
                {
                    string key = GetKeyFromReader(reader, keyColumns);
                    if (string.IsNullOrEmpty(key))
                    {
                        Console.WriteLine("Warning: Empty key generated from reader.");
                        return null;
                    }

                    var hashKey = key.GetHashCode();
                    var date = GetDateFromReader(reader, dateColumn);

                    var keyRecord = new KeyRecord
                    {
                        HashKey = hashKey,
                        Key = key,
                        Date = date
                    };

                    //Console.WriteLine($"Created KeyRecord: HashKey={keyRecord.HashKey}, Key={keyRecord.Key}, Date={keyRecord.Date}");

                    return keyRecord;
                }
                catch (Exception)
                {
                    //Console.WriteLine($"Error creating KeyRecord: {ex.Message}");
                    //Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                    return null;
                }
            }

            private static async Task<IDataReader> GetOrderedDataReaderAsync(string connectionString, string query)
            {
                if (connectionString.ToLower().Contains("mysql"))
                {
                    var connection = new MySqlConnection(connectionString);
                    await connection.OpenAsync();
                    return await new MySqlCommand(query, connection).ExecuteReaderAsync(CommandBehavior.CloseConnection);
                }
                else // Assume SQL Server
                {
                    var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync();
                    return await new SqlCommand(query, connection).ExecuteReaderAsync(CommandBehavior.CloseConnection);
                }
            }

            private static string BuildOrderedSelectQuery(string database, string schema, string objectName, EnhancedObjectNameList keyColumns, string dateColumn, string Filter)
            {
                string columns = keyColumns.GetQuotedNames();
                string groupBy = columns;
                string orderBy = keyColumns.GetQuotedNames();

                if (!string.IsNullOrEmpty(dateColumn))
                {
                    columns += $", MIN([{dateColumn}]) AS [{dateColumn}]";
                }

                return $"SELECT {columns} FROM [{database}].[{schema}].[{objectName}] WHERE 1=1 {Filter} GROUP BY {groupBy} ORDER BY {orderBy}";
            }

            private static string GetKeyFromReader(IDataReader reader, EnhancedObjectNameList keyColumns)
            {
                return string.Join("|", keyColumns.Select(k => reader[k.UnquotedName]?.ToString() ?? ""));
            }

            private static DateTime? GetDateFromReader(IDataReader reader, string dateColumn)
            {
                if (string.IsNullOrEmpty(dateColumn))
                {
                    return null;
                }

                try
                {
                    // Check if the column exists in the reader
                    if (!HasColumn(reader, dateColumn))
                    {
                        Console.WriteLine($"Column {dateColumn} not found in the reader.");
                        return null;
                    }

                    // Now we can safely check for DBNull
                    if (reader[dateColumn] == DBNull.Value)
                    {
                        return null;
                    }
                    else if (reader[dateColumn] is DateTime dateTime)
                    {
                        return dateTime;
                    }
                    else if (reader[dateColumn] is string dateString)
                    {
                        DateTime parsedDate = Functions.ParseToDateTime(dateString, DateTimeFormats);

                        return parsedDate;
                    }

                    return Convert.ToDateTime(reader[dateColumn]);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error converting date for column {dateColumn}: {ex.Message}");
                    return null;
                }
            }

            // Helper method to check if a column exists in the reader
            private static bool HasColumn(IDataReader reader, string columnName)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }


            private static async Task<int> TagDeletedRowsAsync(
                                                                                string trgConString,
                                                                                string trgSchema,
                                                                                string trgObject,
                                                                                EnhancedObjectNameList keyColumns,
                                                                                string dateColumn,
                                                                                int? ignoreDeletedRowsAfter,
                                                                                string delSchema,
                                                                                string trgFilter,
                                                                                string delTableName,
                                                                                string actionType,
                                                                                decimal thresholdPercent)
            {
                string joinCondition = string.Join(" AND ", keyColumns.Select(k => $"t.{k.QuotedName} = kd.{k.QuotedName}"));

                // SQL to get total row count from DMVs and count of rows to be affected
                string countSql = $@"
    DECLARE @TotalRows BIGINT;

    SELECT @TotalRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.[object_id] = t.[object_id]
    JOIN sys.schemas s ON t.[schema_id] = s.[schema_id]
    WHERE p.index_id IN (0,1) -- 0 for heaps, 1 for clustered indexes
      AND s.name = '{trgSchema}'
      AND t.name = '{trgObject}';

    SELECT @TotalRows AS TotalRows,
           COUNT(*) AS RowsToBeAffected
    FROM [{trgSchema}].[{trgObject}] t
    INNER JOIN [{delSchema}].[{delTableName}] kd ON {joinCondition}
    WHERE 1=1 {trgFilter}";

                if (actionType == "Tag")
                {
                    countSql += " AND t.DeletedDate_DW IS NULL";
                    if (!string.IsNullOrEmpty(dateColumn) && ignoreDeletedRowsAfter > 0)
                    {
                        countSql += $@" AND t.[{dateColumn}] >= DateAdd(MONTH, -{ignoreDeletedRowsAfter}, GETDATE())";
                    }
                }

                using (var connection = new SqlConnection(trgConString))
                {
                    await connection.OpenAsync();

                    // Execute the count query
                    using (var command = new SqlCommand(countSql, connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                long totalRows = reader.GetInt64(0);
                                int rowsToBeAffected = reader.GetInt32(1);
                                decimal percentageToBeAffected = (decimal)rowsToBeAffected / totalRows * 100;

                                // Check if the percentage exceeds the threshold
                                if (percentageToBeAffected > thresholdPercent)
                                {
                                    // If it exceeds, return 0 to indicate no action was taken
                                    return 0;
                                }
                            }
                        }
                    }

                    // If we're here, it means we're below the threshold. Proceed with the action.
                    string actionSql = "";
                    switch (actionType)
                    {
                        case "Delete":
                            actionSql = $@"
                DELETE t
                FROM [{trgSchema}].[{trgObject}] t
                INNER JOIN [{delSchema}].[{delTableName}] kd ON {joinCondition}
                WHERE 1=1 {trgFilter}";
                            break;
                        case "Tag":
                            actionSql = $@"
                UPDATE t
                SET t.DeletedDate_DW = GETDATE()
                FROM [{trgSchema}].[{trgObject}] t
                INNER JOIN [{delSchema}].[{delTableName}] kd ON {joinCondition}
                WHERE t.DeletedDate_DW IS NULL {trgFilter}";
                            if (!string.IsNullOrEmpty(dateColumn) && ignoreDeletedRowsAfter > 0)
                            {
                                actionSql += $@" AND t.[{dateColumn}] >= DateAdd(MONTH, -{ignoreDeletedRowsAfter}, GETDATE())";
                            }
                            break;
                        default:
                            throw new ArgumentException("Invalid ActionType specified.");
                    }
                    actionSql += @"
        SELECT @@ROWCOUNT";

                    // Execute the action query
                    using (var command = new SqlCommand(actionSql, connection))
                    {
                        return (int)await command.ExecuteScalarAsync();
                    }
                }
            }

            private static void EnsureTargetTableExists(string trgConString, string trgDatabase, string trgSchema, string trgObject, string delSchema, EnhancedObjectNameList keyColumns, string dateColumn, string delTableName, string actionType)
            {
                SqlConnection smoSqlCon =
                    new SqlConnection(trgConString);

                ServerConnection smoSrvCon = new ServerConnection(smoSqlCon);
                Server smoSrv = new Server(smoSrvCon);
                Database database = smoSrv.Databases[trgDatabase];



                // Drop the table if it exists
                if (database.Tables.Contains(delTableName, delSchema))
                {
                    database.Tables[delTableName, delSchema].Drop();
                }

                // Get the schema of the target table
                Table targetTable = database.Tables[trgObject, trgSchema];
                if (targetTable == null)
                {
                    throw new Exception($"Target table [{trgSchema}].[{trgObject}] not found in database {trgDatabase}");
                }

                // Create new KeyDifferences table
                Table diffTable = new Table(database, delTableName, delSchema);

                // Add key columns with matching data types
                foreach (var keyColumn in keyColumns)
                {
                    Column targetColumn = targetTable.Columns[keyColumn.UnquotedName];
                    if (targetColumn == null)
                    {
                        throw new Exception($"Key column {keyColumn} not found in target table");
                    }
                    Column diffColumn = new Column(diffTable, keyColumn.UnquotedName, targetColumn.DataType);
                    diffColumn.Nullable = targetColumn.Nullable;
                    diffTable.Columns.Add(diffColumn);
                }

                // Add DateValue column if specified
                if (!string.IsNullOrEmpty(dateColumn))
                {
                    Column targetDateColumn = targetTable.Columns[dateColumn];
                    if (targetDateColumn == null)
                    {
                        throw new Exception($"Date column {dateColumn} not found in target table");
                    }
                    // Add MatchKeyId column
                    Column diffColumn = new Column(diffTable, dateColumn, DataType.VarChar(50));
                    diffColumn.Nullable = targetDateColumn.Nullable;
                    diffTable.Columns.Add(diffColumn);
                }

                // Add DifferenceType column
                diffTable.Columns.Add(new Column(diffTable, "DifferenceType", DataType.VarChar(50)));

                // Add MatchDate column
                diffTable.Columns.Add(new Column(diffTable, "MatchDate", DataType.DateTime));

                // Create the table
                diffTable.Create();

                if (actionType.Equals("Tag", StringComparison.InvariantCultureIgnoreCase))
                {
                    EnsureDeletedDateDWIndex(targetTable);
                }
            }

            private static void EnsureDeletedDateDWIndex(Table table)
            {
                // Check if the "DeletedDate_DW" column exists
                if (!table.Columns.Contains("DeletedDate_DW"))
                {
                    // Create the "DeletedDate_DW" column
                    Column deletedDateColumn = new Column(table, "DeletedDate_DW", DataType.DateTime);
                    deletedDateColumn.Nullable = true;
                    table.Columns.Add(deletedDateColumn);
                    table.Alter();
                }

                // Check if an index on "DeletedDate_DW" exists
                bool indexExists = table.Indexes.Cast<Index>().Any(index =>
                    index.IndexedColumns.Cast<IndexedColumn>().Any(col => col.Name == "DeletedDate_DW"));

                // If the index doesn't exist, create it
                if (!indexExists)
                {
                    Index deletedDateIndex = new Index(table, "NCI_DeletedDate_DW");
                    deletedDateIndex.IndexedColumns.Add(new IndexedColumn(deletedDateIndex, "DeletedDate_DW"));
                    deletedDateIndex.Create();
                }
            }
        }
    }
}


