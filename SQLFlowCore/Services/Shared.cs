using Azure.Storage.Files.DataLake.Models;
using Azure.Storage.Files.DataLake;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Common;
using SQLFlowCore.Logger;
using SQLFlowCore.Services.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Services.Schema;

namespace SQLFlowCore.Services
{
    internal static  class Shared
    {
        private static readonly DateTime DefaultDate = new DateTime(1900, 1, 1);

        /// <summary>
        /// Writes a system log entry to [flw].[AddSysLog] stored procedure
        /// Matches the exact parameter set and behavior of the stored procedure
        /// </summary>
        internal static void WriteToSysLog(
            SqlConnection connection,
            int flowId,                              // Required
            string flowType,                         // Required
            string execMode,                         // Required
            string process = "",                     // Optional with default
            DateTime? startTime = null,              // Optional datetime
            DateTime? endTime = null,                // Optional datetime
            long durationFlow = 0,                   // Changed to long
            long durationPre = 0,                    // Changed to long
            long durationPost = 0,                   // Changed to long
            long fetched = 0,                        // Changed to long
            long inserted = 0,                       // Changed to long
            long updated = 0,                        // Changed to long
            long deleted = 0,                        // Changed to long
            int noOfThreads = 0,                     // Optional with default
            string selectCmd = "",                   // Optional with default
            string insertCmd = "",                   // Optional with default
            string updateCmd = "",                   // Optional with default
            string deleteCmd = "",                   // Optional with default
            string runtimeCmd = "",                  // Optional with default
            string createCmd = "",                   // Optional with default
            string errorInsert = "",                 // Optional with default
            string errorUpdate = "",                 // Optional with default
            string errorDelete = "",                 // Optional with default
            string errorRuntime = "",                // Optional with default
            string fileName = "",                    // Optional with default
            long fileSize = 0,                        // Optional with default
            string fileDate = "",                    // Optional with default
            string sysAlias = "",                    // Optional with default
            string batch = "",                       // Optional with default
            string batchId = "",                     // Optional with default
            string whereIncExp = "",                 // Optional with default
            string whereDateExp = "",                // Optional with default
            string whereXML = "",                    // Optional with default
            string dataTypeWarning = "",             // Optional with default
            string columnWarning = "",               // Optional with default
            string surrogateKeyCmd = "",             // Optional with default
            string nextExportDate = null,            // Optional nullable
            int nextExportValue = 0,           // Optional nullable
            int debug = 0,                           // Optional with default
            string traceLog = "",                    // Optional with default
            string inferDatatypeCmd = "",            // Optional with default
            ILogger logger = null)                   // Optional logger for diagnostics
        {
            try
            {
                using (SqlCommand cmd = new SqlCommand("[flw].[AddSysLog]", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    // Required parameters
                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowId;
                    cmd.Parameters.Add("@FlowType", SqlDbType.NVarChar, 25).Value = flowType;
                    cmd.Parameters.Add("@ExecMode", SqlDbType.NVarChar, 255).Value = execMode;

                    // Optional parameters with specified defaults
                    cmd.Parameters.Add("@Process", SqlDbType.NVarChar, 2000).Value = string.IsNullOrEmpty(process) ? DBNull.Value : (object)process;

                    // DateTime handling
                    cmd.Parameters.Add("@StartTime", SqlDbType.DateTime).Value =
                        startTime.HasValue && startTime.Value != DefaultDate ? startTime.Value : DBNull.Value;
                    cmd.Parameters.Add("@EndTime", SqlDbType.DateTime).Value =
                        endTime.HasValue && endTime.Value != DefaultDate ? endTime.Value : DBNull.Value;

                    // Numeric parameters - now properly typed as BigInt
                    cmd.Parameters.Add("@DurationFlow", SqlDbType.BigInt).Value = durationFlow;
                    cmd.Parameters.Add("@DurationPre", SqlDbType.BigInt).Value = durationPre;
                    cmd.Parameters.Add("@DurationPost", SqlDbType.BigInt).Value = durationPost;
                    cmd.Parameters.Add("@Fetched", SqlDbType.BigInt).Value = fetched;
                    cmd.Parameters.Add("@Inserted", SqlDbType.BigInt).Value = inserted;
                    cmd.Parameters.Add("@Updated", SqlDbType.BigInt).Value = updated;
                    cmd.Parameters.Add("@Deleted", SqlDbType.BigInt).Value = deleted;
                    cmd.Parameters.Add("@NoOfThreads", SqlDbType.Int).Value = noOfThreads;

                    // Command text parameters - MAX length
                    cmd.Parameters.Add("@SelectCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(selectCmd) ? DBNull.Value : (object)selectCmd;
                    cmd.Parameters.Add("@InsertCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(insertCmd) ? DBNull.Value : (object)insertCmd;
                    cmd.Parameters.Add("@UpdateCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(updateCmd) ? DBNull.Value : (object)updateCmd;
                    cmd.Parameters.Add("@DeleteCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(deleteCmd) ? DBNull.Value : (object)deleteCmd;
                    cmd.Parameters.Add("@RuntimeCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(runtimeCmd) ? DBNull.Value : (object)runtimeCmd;
                    cmd.Parameters.Add("@CreateCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(createCmd) ? DBNull.Value : (object)createCmd;

                    // Error text parameters - MAX length
                    cmd.Parameters.Add("@ErrorInsert", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(errorInsert) ? DBNull.Value : (object)errorInsert;
                    cmd.Parameters.Add("@ErrorUpdate", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(errorUpdate) ? DBNull.Value : (object)errorUpdate;
                    cmd.Parameters.Add("@ErrorDelete", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(errorDelete) ? DBNull.Value : (object)errorDelete;
                    cmd.Parameters.Add("@ErrorRuntime", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(errorRuntime) ? DBNull.Value : (object)errorRuntime;

                    // File related parameters
                    cmd.Parameters.Add("@FileName", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(fileName) ? DBNull.Value : (object)fileName;
                    cmd.Parameters.Add("@FileSize", SqlDbType.Int).Value = fileSize;
                    cmd.Parameters.Add("@FileDate", SqlDbType.VarChar, 15).Value = string.IsNullOrEmpty(fileDate) ? DBNull.Value : (object)fileDate;

                    // System and batch parameters
                    cmd.Parameters.Add("@SysAlias", SqlDbType.NVarChar, 250).Value = string.IsNullOrEmpty(sysAlias) ? DBNull.Value : (object)sysAlias;
                    cmd.Parameters.Add("@Batch", SqlDbType.NVarChar, 255).Value = string.IsNullOrEmpty(batch) ? DBNull.Value : (object)batch;
                    cmd.Parameters.Add("@BatchID", SqlDbType.NVarChar, 70).Value = string.IsNullOrEmpty(batchId) ? DBNull.Value : (object)batchId;

                    // Where expressions - MAX length
                    cmd.Parameters.Add("@WhereIncExp", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(whereIncExp) ? DBNull.Value : (object)whereIncExp;
                    cmd.Parameters.Add("@WhereDateExp", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(whereDateExp) ? DBNull.Value : (object)whereDateExp;
                    cmd.Parameters.Add("@WhereXML", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(whereXML) ? DBNull.Value : (object)whereXML;

                    // Warning and key parameters - MAX length
                    cmd.Parameters.Add("@DataTypeWarning", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(dataTypeWarning) ? DBNull.Value : (object)dataTypeWarning;
                    cmd.Parameters.Add("@ColumnWarning", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(columnWarning) ? DBNull.Value : (object)columnWarning;
                    cmd.Parameters.Add("@SurrogateKeyCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(surrogateKeyCmd) ? DBNull.Value : (object)surrogateKeyCmd;

                    // Export parameters
                    cmd.Parameters.Add("@NextExportDate", SqlDbType.NVarChar, 255).Value = string.IsNullOrEmpty(nextExportDate) ? DBNull.Value : (object)nextExportDate;
                    cmd.Parameters.Add("@NextExportValue", SqlDbType.Int, 255).Value = nextExportValue;

                    // Debug and trace parameters
                    cmd.Parameters.Add("@dbg", SqlDbType.Int).Value = debug;
                    cmd.Parameters.Add("@TraceLog", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(traceLog) ? DBNull.Value : (object)traceLog;
                    cmd.Parameters.Add("@InferDatatypeCmd", SqlDbType.NVarChar, -1).Value = string.IsNullOrEmpty(inferDatatypeCmd) ? DBNull.Value : (object)inferDatatypeCmd;

                    logger?.LogDebug("Executing [flw].[AddSysLog] for FlowID: {FlowId}", flowId);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error writing to system log for FlowID: {FlowId}", flowId);
                throw;
            }
        }


        internal static void RefreshSourceView(ObjectName oSrcDatabase, ObjectName oSrcSchema, ObjectName oSrcObject,
            RealTimeLogger logger, SqlConnection srcSqlCon, int bulkLoadTimeoutInSek)
        {
            string refreshSourceViewCmd = @$"DECLARE @Type NVARCHAR(255)
                                        SELECT
                                            @Type = type_desc
                                        FROM
                                        sys.objects
                                        WHERE
                                            object_id = OBJECT_ID('[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]');
                                        IF(@Type = 'VIEW')
                                        BEGIN
                                            EXEC sp_refreshview '[{oSrcDatabase.UnquotedName}].[{oSrcSchema.UnquotedName}].[{oSrcObject.UnquotedName}]';
                                        END";

            if (refreshSourceViewCmd.Length > 2)
            {
                using (var operation = logger.TrackOperation("Refresh source schema"))
                {
                    logger.LogCodeBlock("refreshSourceViewCmd", refreshSourceViewCmd);
                    try
                    {
                        using (var command = new SqlCommand(refreshSourceViewCmd, srcSqlCon))
                        {
                            command.CommandType = CommandType.Text;
                            command.CommandTimeout = bulkLoadTimeoutInSek;
                            command.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Unable to refresh schema: Access Denied", ex);
                        throw;
                    }
                }
            }
        }

        internal static void LogAndExecutePreProcess(string preProcessOnTrg, RealTimeLogger logger, SqlConnection trgSqlCon,
            int bulkLoadTimeoutInSek)
        {
            if (preProcessOnTrg.Length > 2)
            {
                // Optionally log the pre-process command as a code block at Debug level
                logger.LogCodeBlock("PreProcess Command", preProcessOnTrg);

                // Track the operation of executing the PreProcess command on target
                using (var operation = logger.TrackOperation("Executing PreProcess on target"))
                {
                    var cmdOnSrc = new ExecNonQuery(trgSqlCon, preProcessOnTrg, bulkLoadTimeoutInSek);
                    cmdOnSrc.Exec();
                }

            }
        }

        internal static DataTable FetchVirtualSchema(SqlFlowParam sqlFlowParam, RealTimeLogger logger, SqlConnection sqlFlowCon,
            int generalTimeoutInSek)
        {
            DataTable vsTbl;
            using (var operation = logger.TrackOperation("Fetching Virtual Schema"))
            {
                // Build the SQL command for fetching the virtual schema
                string virtualSchemaCmd =
                    $@"exec [flw].[GetVirtualSchemaDS] @FlowID={sqlFlowParam.flowId}, @mode=1, @dbg={sqlFlowParam.dbg}";

                // Log the command as a code block at Debug level
                logger.LogCodeBlock("GetVirtualSchemaDS", virtualSchemaCmd);

                // Create the GetData instance for fetching the virtual schema
                var virtualSchemaData = new GetData(sqlFlowCon, virtualSchemaCmd, generalTimeoutInSek);
                vsTbl = virtualSchemaData.Fetch();
            }

            return vsTbl;
        }

        //Shared.LogOuterError(sqlFlowParam, e, sp, logger, logOutput, sqlFlowCon);
        internal static string LogOuterError(SqlFlowParam sqlFlowParam, Exception e, ServiceParam sp ,  RealTimeLogger logger,
            StringBuilder logOutput, SqlConnection sqlFlowCon)
        {
            long logInserted;
            long logUpdated;
            
            //Error returned to client
            logInserted = 0;
            logUpdated = 0;

            // Build complete exception details including inner exceptions
            StringBuilder exceptionDetails = new StringBuilder();
            exceptionDetails.AppendLine(e.Message);
            exceptionDetails.AppendLine(e.StackTrace);

            // Add inner exception details if they exist
            Exception innerException = e.InnerException;
            while (innerException != null)
            {
                exceptionDetails.AppendLine("\nInner Exception:");
                exceptionDetails.AppendLine(innerException.Message);
                exceptionDetails.AppendLine(innerException.StackTrace);
                innerException = innerException.InnerException;
            }

            sp.logFetched = sp.srcRowCount;
            string execSuccess = "false";
            string rJson = $"\u274c flowid:{sqlFlowParam.flowId.ToString()},success:{execSuccess}, fetched:{sp.logFetched},inserted:{logInserted},updated:{logUpdated}";
            logger.LogInformation(rJson);
            sp.result = logOutput.ToString() + exceptionDetails + Environment.NewLine;

            if (sqlFlowCon.State == ConnectionState.Open)
            {
                Shared.WriteToSysLog(
                    connection: sqlFlowCon,
                    flowId: sqlFlowParam.flowId,
                    flowType: sqlFlowParam.flowType,
                    execMode: sqlFlowParam.execMode,
                    errorRuntime: exceptionDetails.ToString(),
                    debug: sqlFlowParam.dbg,  // Note: In original code this is @dbg
                    batch: sp.flowBatch,
                    sysAlias: sp.sysAlias,
                    batchId: sqlFlowParam.batchId,
                    traceLog: logOutput.ToString()
                );

                sqlFlowCon.Close();
                sqlFlowCon.Dispose();
            }
            if (sp.onErrorResume == false)
            {
                throw new Exception(sp.result);
            }

            return sp.result;
        }

        internal static string EvaluateExecutionSuccess(StringBuilder logOutput, string logErrorRuntime, string logErrorInsert,
            string logErrorUpdate, string logErrorDelete)
        {
            string currentTrace = logOutput.ToString();
            string ExecSuccess;
            ExecSuccess = ErrorChecker.HasError(currentTrace)  || logErrorRuntime.Length > 0 || logErrorInsert.Length > 0 || logErrorUpdate.Length > 0 || logErrorDelete.Length > 0 ? "false" : "true";
            return ExecSuccess;
        }

        internal static Encoding GetFileEncoding(ServiceParam sp)
        {
            Encoding encoding = Encoding.Default;
            switch (sp.srcEncoding)
            {
                case "ASCII":
                    encoding = System.Text.Encoding.ASCII;
                    break;
                case "Unicode":
                    encoding = System.Text.Encoding.Unicode;
                    break;
                case "UTF32":
                    encoding = System.Text.Encoding.UTF32;
                    break;
                case "UTF7":
                    encoding = System.Text.Encoding.UTF8; //UTF7 Is insecure
                    break;
                case "UTF8":
                    encoding = System.Text.Encoding.UTF8;
                    break;
                case "Latin1":
                    encoding = System.Text.Encoding.Latin1;
                    break;
                case "BigEndianUnicode":
                    encoding = System.Text.Encoding.BigEndianUnicode;
                    break;
            }

            return encoding;
        }
        internal static void BuildAndExecuteViewCommand(RealTimeLogger logger, ServiceParam sp, SqlConnection trgSqlCon)
        {
            using (var operation = logger.TrackOperation("Create transformation view"))
            {
                sp.logSelectCmd = sp.currentViewSelect.Length > 0 ? sp.currentViewSelect : sp.viewSelect;
                // Build the command to create the view by escaping single quotes in viewCmd
                sp.execViewCmd = "DECLARE @val nvarchar(max) " +
                                 " set @val = '" + sp.viewCmd.Replace("'", "''") + "'" +
                                 " exec  [" + sp.trgDatabase + "].sys.sp_executesql @val";

                // Log the original and generated commands at Debug level
                logger.LogCodeBlock("View CMD:", sp.viewCmd);
                logger.LogCodeBlock("Create View CMD:", sp.execViewCmd);

                CommonDB.ExecDDLScript(trgSqlCon, sp.execViewCmd, sp.generalTimeoutInSek, sp.trgIsSynapse);
            }
        }

        internal static string LogException(SqlFlowParam sqlFlowParam, Exception e, StringBuilder logOutput, RealTimeLogger logger, SqlConnection sqlFlowCon , ServiceParam sp)
        {
            string logRuntimeCmd;
            string logErrorRuntime;
            
            StringBuilder exceptionDetails = new StringBuilder();
            exceptionDetails.AppendLine(e.Message);
            exceptionDetails.AppendLine(e.StackTrace);

            // Add inner exception details if they exist
            Exception innerException = e.InnerException;
            while (innerException != null)
            {
                exceptionDetails.AppendLine("\nInner Exception:");
                exceptionDetails.AppendLine(innerException.Message);
                exceptionDetails.AppendLine(innerException.StackTrace);
                innerException = innerException.InnerException;
            }

            if (sp.logErrorInsert.Length > 0) { sp.logInserted = 0; }

            if (sp.logErrorDelete.Length > 0)
            {

            }

            if (sp.logErrorUpdate.Length > 0) { sp.logUpdated = 0; }


            logRuntimeCmd = logOutput.ToString();
            logErrorRuntime = exceptionDetails.ToString();

            //Error returned to client
            string execSuccess = "false";
            execSuccess = ErrorChecker.HasError(logOutput.ToString()) || logErrorRuntime.Length > 0 || sp.logErrorInsert.Length > 0 || sp.logErrorUpdate.Length > 0 || sp.logErrorDelete.Length > 0 ? "false" : "true";

            sp.logFetched = sp.srcRowCount;
            string rJson = $"\u274c flowid:{sqlFlowParam.flowId.ToString()},success:{execSuccess},fetched:{sp.logFetched},inserted:{sp.logInserted},updated:{sp.logUpdated}";
            logger.LogInformation(rJson);

            sp.result = logOutput.ToString() + exceptionDetails + Environment.NewLine;

            Shared.WriteToSysLog(
                connection: sqlFlowCon,
                flowId: sqlFlowParam.flowId,
                flowType: sqlFlowParam.flowType,
                execMode: sqlFlowParam.execMode,
                startTime: sp.logStartTime,
                endTime: DateTime.Now,
                durationFlow: sp.logDurationFlow,
                durationPre: sp.logDurationPre,
                durationPost: sp.logDurationPost,
                fetched: sp.logFetched,
                noOfThreads: sp.noOfThreads,
                selectCmd: sp.logSelectCmd,
                createCmd: sp.logCreateCmd,
                errorRuntime: logErrorRuntime,
                batch: sp.flowBatch,
                sysAlias: sp.sysAlias,
                batchId: sqlFlowParam.batchId,
                columnWarning: sp.ColumnWarning,
                dataTypeWarning: sp.DataTypeWarning,
                surrogateKeyCmd: sp.logSurrogateKeyCmd,
                inserted: sp.tokenize ? 0 : sp.logInserted,
                updated: sp.tokenize ? 0 : sp.logUpdated,
                insertCmd: sp.tokenize ? "" : sp.logInsertCmd,
                updateCmd: sp.tokenize ? "" : sp.logUpdateCmd,
                errorInsert: sp.tokenize ? "" : sp.logErrorInsert,
                errorUpdate: sp.tokenize ? "" : sp.logErrorUpdate,
                errorDelete: sp.tokenize ? "" : sp.logErrorDelete,
                traceLog: sp.result,
                debug: Convert.ToInt32(sqlFlowParam.dbg)
            );

            if (sp.onErrorResume == false)
            {
                throw new Exception(sp.result);
            }

            return sp.result;
        }

        internal static string LogFileException(SqlFlowParam sqlFlowParam, ServiceParam sp, RealTimeLogger logger,
            Exception e, SqlConnection sqlFlowCon, StringBuilder logOutput)
        {
            StringBuilder exceptionDetails = new StringBuilder();
            exceptionDetails.AppendLine(e.Message);
            exceptionDetails.AppendLine(e.StackTrace);

            // Add inner exception details if they exist
            Exception innerException = e.InnerException;
            while (innerException != null)
            {
                exceptionDetails.AppendLine("\nInner Exception:");
                exceptionDetails.AppendLine(innerException.Message);
                exceptionDetails.AppendLine(innerException.StackTrace);
                innerException = innerException.InnerException;
            }
            
            sp.logRuntimeCmd = logOutput.ToString();
            sp.logErrorRuntime = e.Message + Environment.NewLine + e.StackTrace;

            string execSuccess = "false";
            execSuccess = ErrorChecker.HasError(logOutput.ToString()) || sp.logErrorRuntime.Length > 0 || sp.logErrorInsert.Length > 0 ? "false" : "true";

            sp.logFetched = sp.srcRowCount;
            string rJson = $"flowid:{sqlFlowParam.flowId.ToString()},success:{execSuccess},fetched:{sp.logFetched},inserted:{sp.logInserted}";
            logger.LogInformation(rJson);
            
            //Error returned to client
            sp.result = logOutput.ToString() + exceptionDetails;

            Shared.WriteToSysLog(
                connection: sqlFlowCon,
                flowId: sqlFlowParam.flowId,
                flowType: sqlFlowParam.flowType,
                execMode: sqlFlowParam.execMode,
                startTime: sp.logStartTime,
                endTime: sp.logEndTime,
                durationFlow: sp.logDurationFlow,
                durationPre: sp.logDurationPre,
                durationPost: sp.logDurationPost,
                fetched: sp.logFetched > 0 ? sp.logFetched : 0,
                inserted: sp.logInserted,
                fileName: sp.processedFileList,
                fileSize: sp.logLength,
                selectCmd: sp.logSelectCmd,
                createCmd: sp.logCreateCmd,
                errorRuntime: sp.logErrorRuntime,
                debug: sqlFlowParam.dbg,
                noOfThreads: sp.noOfThreads,
                batch: sp.flowBatch,
                sysAlias: sp.sysAlias,
                batchId: sqlFlowParam.batchId,
                traceLog: logOutput.ToString(),
                inferDatatypeCmd: sp.InferDatatypeCmd.ToString()
            );

            return sp.result;
        }

        internal static void WriteToSysFileLog(
            SqlConnection connection,
            string batchId,                          // Required
            int flowId,                              // Required
            string fileDate_DW,                      // Required
            string dataSet_DW,                       // Required
            string fileName_DW,                      // Required
            DateTime fileRowDate_DW,                 // Required
            decimal fileSize_DW,                     // Required
            int fileColumnCount,                     // Required
            int expectedColumnCount,                 // Required
            ILogger logger = null)                   // Optional logger for diagnostics
        {
            try
            {
                using (SqlCommand cmd = new SqlCommand("[flw].[AddSysFileLog]", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    // Add parameters
                    cmd.Parameters.Add("@BatchID", SqlDbType.VarChar, 70).Value =
                        string.IsNullOrEmpty(batchId) ? DBNull.Value : (object)batchId;

                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowId;

                    cmd.Parameters.Add("@FileDate_DW", SqlDbType.VarChar, 255).Value =
                        string.IsNullOrEmpty(fileDate_DW) ? DBNull.Value : (object)fileDate_DW;

                    cmd.Parameters.Add("@DataSet_DW", SqlDbType.VarChar, 255).Value =
                        string.IsNullOrEmpty(dataSet_DW) ? DBNull.Value : (object)dataSet_DW;

                    cmd.Parameters.Add("@FileName_DW", SqlDbType.VarChar, -1).Value =
                        string.IsNullOrEmpty(fileName_DW) ? DBNull.Value : (object)fileName_DW;

                    cmd.Parameters.Add("@FileRowDate_DW", SqlDbType.DateTime).Value = fileRowDate_DW;

                    cmd.Parameters.Add("@FileSize_DW", SqlDbType.Decimal).Value = fileSize_DW;

                    cmd.Parameters.Add("@FileColumnCount", SqlDbType.Int).Value = fileColumnCount;

                    cmd.Parameters.Add("@ExpectedColumnCount", SqlDbType.Int).Value = expectedColumnCount;

                    logger?.LogDebug("Executing [flw].[AddSysFileLog] for FlowID: {FlowId}", flowId);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error writing to system file log for FlowID: {FlowId}", flowId);
                throw;
            }
        }


        internal static DataTable GetFilesFromTable(RealTimeLogger logger, ServiceParam sp, SqlConnection trgSqlCon)
        {
            DataTable tblPreFiles = new DataTable();
            using (logger.TrackOperation("Fetch existing files in target table"))
            {
                string cmdPreFiles = @$" IF(OBJECT_ID('{sp.trgDbSchTbl}') IS NOT NULL)
                                            BEGIN
                                                SELECT DISTINCT FileName_DW, FileSize_DW
                                                FROM {sp.trgDbSchTbl}
                                            END
                                                ELSE
                                            BEGIN
                                                SELECT 'NA' FileName_DW, '0' FileSize_DW
                                            END";

                var tblPreObj = new GetData(trgSqlCon, cmdPreFiles, sp.generalTimeoutInSek);
                tblPreFiles = tblPreObj.Fetch();
            }

            return tblPreFiles;
        }

        internal static string FileDateFromNextFlow(RealTimeLogger logger, DataTable incrTbl, ServiceParam sp)
        {
            string fileDateFromNextFlow = string.Empty;
            using (var operation = logger.TrackOperation("Fetch file date from next flow"))
            {
                fileDateFromNextFlow = CommonDB.GetFileDateFromNextFlow(incrTbl, sp.generalTimeoutInSek);

                if (!string.IsNullOrEmpty(fileDateFromNextFlow))
                {
                    long xVal = long.Parse(fileDateFromNextFlow);

                    // Change file date only if xVal is less than fileDate,
                    // ensuring xVal > 0 for archive tables that are empty and that
                    // fileDate is adjusted only when the difference is less than 60.
                    if (sp.fileDate > xVal && xVal > 0 && sp.fileDate - xVal < 60)
                    {
                        sp.fileDate = xVal;
                    }

                    // Log the successful fetch. Operation timing is automatically handled by TrackOperation.
                    logger.LogInformation("File date fetched from next flow.");
                }
            }

            return fileDateFromNextFlow;
        }

        internal static void CheckAndTruncateTargetTable(SqlFlowParam sqlFlowParam, RealTimeLogger logger,
            SqlConnection sqlFlowCon, ServiceParam sp, SqlConnection trgSqlCon, DataTable tblPreFiles)
        {
            using (var operation = logger.TrackOperation("Fetch next step status"))
            {
                string trgTruncCmd = "";
                string cmdNextStepOK = $"exec flw.WasNextStepSuccessfulOnLastRun @FlowID={sqlFlowParam.flowId.ToString()}";
                logger.LogCodeBlock("Was Next Step Successful On LastRun:", cmdNextStepOK);

                var cmdNextStep = new GetData(sqlFlowCon, cmdNextStepOK, 360);
                DataTable tblNextStepOK = cmdNextStep.Fetch();

                string NextStepStatus = tblNextStepOK.Rows[0]["NextStepStatus"]?.ToString() ?? string.Empty;

                if (NextStepStatus == "1" || sp.fileDate == 0)
                {
                    trgTruncCmd = $"TRUNCATE TABLE {sp.trgDbSchTbl}; ALTER TABLE {sp.trgDbSchTbl} REBUILD;";
                }

                if (trgTruncCmd.Length > 0)
                {
                    using (var command = new SqlCommand(trgTruncCmd, trgSqlCon))
                    {
                        command.ExecuteNonQuery();
                        command.Dispose();
                    }

                    logger.LogInformation($"Was Next Step Successful On LastRun ({NextStepStatus})? Target Table Truncated");

                    tblPreFiles.Rows.Clear(); //Remove All Rows as we want to re-read all files
                }

            }
        }

        
        

        internal static void FetchTargetTableRowCount(RealTimeLogger logger, ServiceParam sp, SqlConnection trgSqlCon)
        {
            using (var operation =
                   logger.TrackOperation("Get RowCount from target table"))
            {
                var cmdRowCount = $@"SELECT SUM(sPTN.Rows) AS [RowCount]
                                                 FROM      [{sp.trgDatabase}].sys.objects AS sOBJ with({sp.trgWithHint})
                                                 INNER JOIN [{sp.trgDatabase}].sys.partitions AS sPTN with({sp.trgWithHint})
                                                    ON sOBJ.object_id = sPTN.object_id
                                                 WHERE      sOBJ.type = 'U'
                                                   AND      sOBJ.is_ms_shipped = 0x0
                                                   AND      index_id < 2-- 0:Heap, 1:Clustered
                                                 AND      sOBJ.Object_id = OBJECT_ID('{sp.trgDbSchTbl}')
                                                 GROUP BY sOBJ.schema_id,
                                                          sOBJ.name;";

                logger.LogCodeBlock("Fetch Target Table Rowcount", cmdRowCount);
                using (SqlCommand commandRowCount = new SqlCommand(cmdRowCount, trgSqlCon))
                {
                    sp.logInserted = Convert.ToInt32(commandRowCount.ExecuteScalar());
                    commandRowCount.Dispose();
                }
            }
        }

        internal static void CheckAndLogTargetIndexes(SqlFlowParam sqlFlowParam, SqlConnection trgSqlCon, ServiceParam sp,
            SqlConnection sqlFlowCon)
        {
            CommonDB.CheckIfObjectExsists(trgSqlCon, sp.trgDbSchTbl, sp.bulkLoadTimeoutInSek);

            if (sp.targetExsits)
            {
                SQLObject sObj = CommonDB.SQLObjectFromDBSchobj(sp.trgDbSchTbl);
                sp.Indexes = Services.Schema.ObjectIndexes.GetObjectIndexes(
                    sp.trgConString,
                    sObj.ObjDatabase,
                    sObj.ObjSchema,
                    sObj.ObjName,
                    true
                );

                // Log indexes in case the process fails via a stored procedure call
                using (var cmd = new SqlCommand("[flw].[AddObjectIndexes]", sqlFlowCon))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = sqlFlowParam.flowId;
                    cmd.Parameters.Add("@TrgIndexes", SqlDbType.VarChar).Value = sp.Indexes;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        internal static IEnumerable<GenericFileItem> EnumerateMultipleLocalFiles(ServiceParam sp, RealTimeLogger logger,
            FileSystemInfoAdapter adapter2)
        {
            IEnumerable<GenericFileItem> genericFileList;
            string curPathMask = sp.srcPathMask.Length > 0 ? sp.srcPathMask : ".";

            logger.LogCodeBlock("curPathMask:", curPathMask);

            var rxCurPathMask = new Regex(curPathMask, RegexOptions.IgnoreCase);
            var rxSrcFile = new Regex(sp.srcFile, RegexOptions.IgnoreCase);

            string FileExtMask = "*" + Path.GetExtension(sp.srcFile);

            IEnumerable<System.IO.FileSystemInfo> filelist = null;
            if (sp.InitFromFileDate > 0)
            {
                filelist = new DirectoryInfo(sp.srcPath)
                    .EnumerateFileSystemInfos(FileExtMask, sp.srchOption)
                    .Where(file => (long.Parse(file.CreationTime.ToString("yyyyMMddHHmmss")) > sp.InitFromFileDate || long.Parse(file.LastWriteTime.ToString("yyyyMMddHHmmss")) > sp.InitFromFileDate) && (long.Parse(file.CreationTime.ToString("yyyyMMddHHmmss")) < sp.InitToFileDate || long.Parse(file.LastWriteTime.ToString("yyyyMMddHHmmss")) < sp.InitToFileDate) && rxCurPathMask.IsMatch(file.FullName) && rxSrcFile.IsMatch(file.Name))
                    .OrderBy(file => file.CreationTime)
                    .ThenBy(file => file.Name);
            }
            else
            {
                filelist = new DirectoryInfo(sp.srcPath)
                    .EnumerateFileSystemInfos(FileExtMask, sp.srchOption)
                    .Where(file => (long.Parse(file.CreationTime.ToString("yyyyMMddHHmmss")) > sp.fileDate || long.Parse(file.LastWriteTime.ToString("yyyyMMddHHmmss")) > sp.fileDate) && rxCurPathMask.IsMatch(file.FullName) && rxSrcFile.IsMatch(file.Name))
                    .OrderBy(file => file.CreationTime)
                    .ThenBy(file => file.Name);
            }

            genericFileList = filelist.Select(fileInfo => adapter2.ConvertToGenericFileItem(fileInfo));
            return genericFileList;
        }

        internal static IEnumerable<GenericFileItem> EnumerateLocalFile(SqlFlowParam sqlFlowParam, IEnumerable<GenericFileItem> genericFileList)
        {
            // Use the provided input file path
            string inputFilePath = sqlFlowParam.srcFileWithPath;

            // Create a FileInfo object for the input file
            FileInfo fileInfo = new FileInfo(inputFilePath);

            // Check if the file exists
            if (fileInfo.Exists)
            {
                // Create the GenericFileItem with the file's metadata
                GenericFileItem inputFile = new GenericFileItem
                {
                    Name = fileInfo.Name,
                    FullPath = fileInfo.FullName,
                    LastModified = fileInfo.LastWriteTime,
                    ContentLength = fileInfo.Length
                };

                // Add the input file to the generic file list
                genericFileList = new List<GenericFileItem> { inputFile };
            }

            return genericFileList;
        }

        internal static IEnumerable<GenericFileItem> EnumerateMultipleLakeFiles(ServiceParam sp, DataLakeFileSystemClient srcFileSystemClient,
            RealTimeLogger logger, PathItemAdapter adapter)
        {
            IEnumerable<GenericFileItem> genericFileList;
            string curPathMask = sp.srcPathMask.Length > 0 ? sp.srcPathMask : ".";
            var rxCurPathMask = new Regex(curPathMask, RegexOptions.IgnoreCase);
            var rxSrcFile = new Regex(sp.srcFile, RegexOptions.IgnoreCase);

            // First, enumerate all the folders in the container that match the rxCurPathMask pattern
            var folderPaths = srcFileSystemClient.GetPaths(sp.srcPath, false)
                .Where(path => path.IsDirectory.Value && rxCurPathMask.IsMatch(path.Name))
                .Select(path => path.Name)
                .ToList();

            // Create a list to store the enumerated files
            var fileList = new ConcurrentBag<GenericFileItem>();

            logger.LogCodeBlock("curPathMask:", curPathMask);

            if (sp.InitFromFileDate > 0)
            {
                // Fetch the files in each folder in parallel
                Parallel.ForEach(folderPaths, folderPath =>
                {
                    var folderFiles = srcFileSystemClient.GetPaths(folderPath, sp.searchSubDirectories)
                        .Where(file => !file.IsDirectory.Value && file.ContentLength > 0 &&
                                       long.Parse(file.LastModified.ToString("yyyyMMddHHmmss")) >= sp.InitFromFileDate &&
                                       long.Parse(file.LastModified.ToString("yyyyMMddHHmmss")) <= sp.InitToFileDate &&
                                       rxSrcFile.IsMatch(file.Name))
                        .Select(file => adapter.ConvertToGenericFileItem(file))
                        .ToList();

                    // Add the enumerated files to the fileList
                    foreach (var file in folderFiles)
                    {
                        fileList.Add(file);
                    }
                });
            }
            else
            {
                // Fetch the files in each folder in parallel
                Parallel.ForEach(folderPaths, folderPath =>
                {
                    var folderFiles = srcFileSystemClient.GetPaths(folderPath, sp.searchSubDirectories)
                        .Where(file => !file.IsDirectory.Value && file.ContentLength > 0 &&
                                       long.Parse(file.LastModified.ToString("yyyyMMddHHmmss")) > sp.fileDate &&
                                       rxSrcFile.IsMatch(file.Name))
                        .Select(file => adapter.ConvertToGenericFileItem(file))
                        .ToList();

                    // Add the enumerated files to the fileList
                    foreach (var file in folderFiles)
                    {
                        fileList.Add(file);
                    }
                });
            }

            // Order the files by LastModified and then by Name
            genericFileList = fileList.OrderBy(file => file.LastModified)
                .ThenBy(file => file.Name)
                .ToList();
            return genericFileList;
        }

        internal static IEnumerable<GenericFileItem> EnumerateSingelLakeFile(SqlFlowParam sqlFlowParam,
            DataLakeFileSystemClient srcFileSystemClient)
        {
            IEnumerable<GenericFileItem> genericFileList;
            // Use the provided input file path
            string inputFilePath = sqlFlowParam.srcFileWithPath;

            // Get the DataLakeFileClient for the specific file
            DataLakeFileClient fileClient = srcFileSystemClient.GetFileClient(inputFilePath);

            // Get the file's properties
            PathProperties fileProperties = fileClient.GetProperties();

            GenericFileItem inputFile = new GenericFileItem
            {
                Name = Path.GetFileName(inputFilePath),
                FullPath = inputFilePath,
                DirectoryName = Path.GetDirectoryName(inputFilePath),
                ContentLength = fileProperties.ContentLength,
                LastModified = fileProperties.LastModified.DateTime,
                LastWriteTime = fileProperties.LastModified.DateTime,
                CreationTime = fileProperties.LastModified.DateTime
            };

            // Add the input file to the generic file list
            genericFileList = new List<GenericFileItem> { inputFile };
            return genericFileList;
        }

        internal static DataTable GetFilePipelineMetadata(string SpName, SqlFlowParam sqlFlowParam, RealTimeLogger logger, SqlConnection sqlFlowCon,
            out DataTable paramTbl, out DataTable incrTbl, out DataTable DateTimeFormats, out DataTable procParamTbl)
        {
            DataSet ds;
            paramTbl = new DataTable();
            incrTbl = new DataTable();
            DateTimeFormats = new DataTable();
            procParamTbl = new DataTable();  
            
            using (var operation = logger.TrackOperation("Fetch pipeline meta data"))
            {
                DateTimeFormats = FlowDates.GetDateTimeFormats(sqlFlowCon);
                string flowParamCmd =
                    $@" exec {SpName} @FlowID = {sqlFlowParam.flowId.ToString()}, @ExecMode = '{sqlFlowParam.execMode}', @dbg = {sqlFlowParam.dbg.ToString()}";
                logger.LogCodeBlock("Runtime values:", flowParamCmd);
                ds = CommonDB.GetDataSetFromSP(sqlFlowCon, flowParamCmd, 360);

                // Check if dataset or first table is empty and throw an error
                if (ds == null || ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0)
                {
                    throw new InvalidOperationException("No metadata returned from stored procedure. Please verify SysLog and defined metadata.");
                }

                paramTbl = ds.Tables[0];
                if (ds.Tables.Count == 2)
                {
                    incrTbl = ds.Tables[1];
                }
                if (ds.Tables.Count == 3 && sqlFlowParam.flowType == "prc")
                {
                    procParamTbl = ds.Tables[2];
                }
                

            }

            // Return paramTbl which contains the metadata instead of DateTimeFormats
            return paramTbl;
        }


        internal static void WriteToTarget(SqlFlowParam sqlFlowParam, ServiceParam sp, RealTimeLogger logger,
            DataTable dataTable, GenericFileItem cFile, Dictionary<string, string> columnMappings, Hashtable retryErrorCodes)
        {
            if (sp.noOfThreads > 1)
            {
                var batchSize = sp.srcRowCount / sp.noOfThreads;
                batchSize = batchSize == 0 ? sp.srcRowCount : batchSize;

                IEnumerable<DataTable> batches;
                using (var operation = logger.TrackOperation("DataTable Splitting"))
                {
                    // List<DataTable> Batches = createBatchTable(srcTBL, BatchSize); //A bit slow
                    // var batches = dataTable.AsEnumerable().ToChunks(batchSize).Select(rows => rows.CopyToDataTable());
                    batches = DataTableSplitter.SplitDataTable(dataTable, batchSize);
                }

                var finalBatchSize = sp.bulkLoadBatchSize == -1 ? batchSize : sp.bulkLoadBatchSize;
                Parallel.ForEach(batches, table =>
                {
                    var currentRowCount = table.Rows.Count;

                    var bulk = new PushToSql(sp.trgConString,
                        $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", cFile.Name, columnMappings,
                        sp.bulkLoadTimeoutInSek, finalBatchSize, logger, sp.maxRetry, sp.retryDelayMs,
                        retryErrorCodes, sqlFlowParam.dbg, ref currentRowCount); // BatchSize
                    bulk.WriteWithRetries(table);
                    table.Clear();
                    table.Dispose();
                });
            }
            else
            {
                var bulk = new PushToSql(sp.trgConString,
                    $"[{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]", cFile.Name, columnMappings,
                    sp.bulkLoadTimeoutInSek, 0, logger, sp.maxRetry, sp.retryDelayMs,
                    retryErrorCodes, sqlFlowParam.dbg, ref sp.srcRowCount); // BatchSize
                bulk.WriteWithRetries(dataTable);
            }
        }

        internal static void FetchDataTypes(SqlFlowParam sqlFlowParam, RealTimeLogger logger, ServiceParam sp,
            SqlConnection sqlFlowCon, SqlConnection trgSqlCon)
        {
            using (var operation = logger.TrackOperation("Fetch DataTypes"))
            {
                logger.LogCodeBlock("Infer DataType Code:", sp.InferDatatypeCmd);
                GetData dat = new GetData(trgSqlCon, sp.InferDatatypeCmd, sp.bulkLoadTimeoutInSek);
                DataTable dataTable = dat.Fetch();
                sp.viewCmd = string.Empty;

                foreach (DataRow row in dataTable.Rows)
                {
                    string cmdFlowTransExp = $"exec flw.UpdPreIngTransfromExp @FlowID={sqlFlowParam.flowId}, @FlowType='{sqlFlowParam.flowType}', @Virtual=0, @ColName='{row["ColName"]}', @SelectExp='{(row["SelectExp"]?.ToString() ?? string.Empty).Replace("'", "''")}', @ColAlias='{row["ColAlias"]}', @SortOrder=NULL, @ExcludeColFromView = 0";
                    CommonDB.ExecNonQuery(sqlFlowCon, cmdFlowTransExp, sp.bulkLoadTimeoutInSek);
                }

                string vCMD = $"SELECT * FROM [flw].[GetPreViewCmd]({sqlFlowParam.flowId})";
                GetData addGetView = new GetData(sqlFlowCon, vCMD, sp.bulkLoadTimeoutInSek);
                DataTable resTable = addGetView.Fetch();

                if (resTable.Rows != null && resTable.Rows.Count > 0)
                {
                    sp.viewCmd = resTable.Rows[0]["ViewCMD"]?.ToString() ?? string.Empty;
                }
            }

            if (sp.viewCmd.Length > 10)
            {
                Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
            }
        }

        internal static void HandelNoFilesFound(SqlFlowParam sqlFlowParam, ServiceParam sp, RealTimeLogger logger,
    SqlConnection trgSqlCon, Stopwatch execTime, SqlConnection sqlFlowCon, StringBuilder logOutput)
        {
            try
            {
                // Safe logging with ToString() to prevent formatting issues
                if (sp != null)
                {
                    if (sp.fileDate > 0)
                    {
                        logger?.LogInformation($"No files found after create/modified date: {sp.fileDate.ToString()}");
                    }
                    else
                    {
                        logger?.LogInformation($"No files found, please verify folder and filename create/modified {sp.fileDate.ToString()}");
                    }

                    // Requested by Ahmad. View should get updated even if there is no new files. Adding new transformations
                    if (sp.targetExsits && !string.IsNullOrEmpty(sp.viewCmd) && sp.viewCmd.Length > 10 && trgSqlCon != null)
                    {
                        Shared.BuildAndExecuteViewCommand(logger, sp, trgSqlCon);
                    }
                }

                // Stop execution timer
                if (execTime != null && execTime.IsRunning)
                {
                    execTime.Stop();
                }

                // Set duration and end time if sp is not null
                if (sp != null)
                {
                    sp.logDurationFlow = execTime?.ElapsedMilliseconds / 1000 ?? 0;
                    sp.logEndTime = DateTime.Now;
                }

                // Write to system log
                if (sqlFlowCon != null && sqlFlowCon.State == ConnectionState.Open && sqlFlowParam != null && sp != null)
                {
                    // Use the parameter pattern that matches the WriteToSysLog method in the codebase
                    Shared.WriteToSysLog(
                        connection: sqlFlowCon,
                        flowId: sqlFlowParam.flowId,
                        flowType: sqlFlowParam.flowType ?? string.Empty,
                        execMode: sqlFlowParam.execMode ?? string.Empty,
                        startTime: sp.logStartTime,
                        endTime: sp.logEndTime,
                        durationFlow: sp.logDurationFlow,
                        runtimeCmd: sp.viewCmd ?? string.Empty,
                        errorRuntime: string.Empty,
                        debug: sqlFlowParam.dbg,
                        noOfThreads: sp.noOfThreads,
                        batch: sp.flowBatch ?? string.Empty,
                        sysAlias: sp.sysAlias ?? string.Empty,
                        batchId: sqlFlowParam.batchId ?? string.Empty,
                        traceLog: logOutput?.ToString() ?? string.Empty,
                        logger: logger  // Pass logger for diagnostics
                    );
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that might occur
                logger?.LogError(ex, "Error in HandelNoFilesFound: {0}", ex.Message);

                try
                {
                    if (sqlFlowCon != null && sqlFlowCon.State == ConnectionState.Open && sqlFlowParam != null)
                    {
                        // Log the error to the system log
                        Shared.WriteToSysLog(
                            connection: sqlFlowCon,
                            flowId: sqlFlowParam.flowId,
                            flowType: sqlFlowParam.flowType ?? string.Empty,
                            execMode: sqlFlowParam.execMode ?? string.Empty,
                            errorRuntime: ex.ToString(),
                            debug: sqlFlowParam.dbg,
                            batchId: sqlFlowParam.batchId ?? string.Empty,
                            traceLog: (logOutput?.ToString() ?? string.Empty) + Environment.NewLine + "Exception: " + ex.ToString(),
                            logger: logger  // Pass logger for diagnostics
                        );
                    }
                }
                catch (Exception innerEx)
                {
                    // Last resort error handling
                    logger?.LogError(innerEx, "Fatal error in HandelNoFilesFound error handling: {0}", innerEx.Message);
                }
            }
        }

        internal static void EvaluateAndLogExecution(SqlConnection sqlFlowCon, SqlFlowParam sqlFlowParam, StringBuilder logOutput, ServiceParam sp,
    RealTimeLogger logger)
        {
            string execSuccess = "false";
            execSuccess = Shared.EvaluateExecutionSuccess(logOutput, sp.logErrorRuntime, sp.logErrorInsert, sp.logErrorUpdate, sp.logErrorDelete);
            if (sp.logErrorRuntime.Length > 0)
            {
                logger.LogError($"Runtime Error: {sp.logErrorRuntime}");
            }
            else if (sp.logErrorInsert.Length > 0)
            {
                logger.LogError($"Insert Error: {sp.logErrorInsert}");
            }
            else if (sp.logErrorUpdate.Length > 0)
            {
                logger.LogError($"Update Error: {sp.logErrorUpdate}");
            }
            else if (sp.logErrorDelete.Length > 0)
            {
                logger.LogError($"Delete Error: {sp.logErrorDelete}");
            }
            if (execSuccess == "true")
            {
                sp.Success = true;
            }

            string rJson = $"\u2705 flowid:{sqlFlowParam.flowId.ToString()},success:{execSuccess},fetched:{sp.logFetched},inserted:{sp.logInserted},updated:{sp.logUpdated}";
            logger.LogInformation(rJson);
            sp.result = logOutput.ToString() + Environment.NewLine;
            Shared.WriteToSysLog(
                connection: sqlFlowCon,
                flowId: sqlFlowParam.flowId,
                flowType: sqlFlowParam.flowType,
                execMode: sqlFlowParam.execMode,
                startTime: sp.logStartTime,
                endTime: sp.logEndTime,
                durationFlow: sp.logDurationFlow,
                durationPre: sp.logDurationPre,
                durationPost: sp.logDurationPost,
                fetched: sp.logFetched > 0 ? sp.logFetched : 0,
                inserted: sp.logInserted,
                fileName: sp.processedFileList,
                fileSize: sp.logLength,
                fileDate: sp.InitFromFileDate == 0
                    ? (sp.createDateTime > sp.modifiedDateTime
                        ? sp.createDateTime.ToString("yyyyMMddHHmmss")
                        : sp.modifiedDateTime.ToString("yyyyMMddHHmmss"))
                    : "",
                selectCmd: sp.logSelectCmd,
                createCmd: sp.logCreateCmd,
                errorRuntime: sp.logErrorRuntime,
                debug: sqlFlowParam.dbg,
                noOfThreads: sp.noOfThreads,
                batch: sp.flowBatch,
                sysAlias: sp.sysAlias,
                batchId: sqlFlowParam.batchId,
                traceLog: logOutput.ToString(),
                inferDatatypeCmd: sp.InferDatatypeCmd.ToString(),
                updated: 0,
                whereIncExp: sp.MinXML.IncColCMD,
                whereDateExp: sp.MinXML.DateColCMD,
                whereXML: sp.whereXML,
                nextExportDate: execSuccess == "true" ?
                    ((sp.logNextExportDate - DateTime.Now.Date).TotalDays < 1 ?
                        sp.logNextExportDate.AddDays(-1) : sp.logNextExportDate).ToString("yyyy-MM-dd") : null,
                nextExportValue: execSuccess == "true" ? sp.logNextExportValue : 0,
                logger: logger
            );
        }

        internal static void BuildSchemaAndTransformations(SqlFlowParam sqlFlowParam, string FileDate_DW, DataTable dataTable, ServiceParam sp,
            GenericFileItem cFile, DateTime FileRowDate_DW, long FileSize_DW, string DataSet_DW, RealTimeLogger logger,
            SqlConnection sqlFlowCon,  SqlConnection trgSqlCon, Dictionary<string, string> columnMappings,
            Hashtable retryErrorCodes, DataLakeFileSystemClient srcFileSystemClient)
        {
            using (logger.TrackOperation("Build schema and transformations"))
            {
                DataColumn dc = new DataColumn();
                dc.ColumnName = "FileDate_DW";
                dc.DataType = typeof(string);
                dc.DefaultValue = FileDate_DW;
                dataTable.Columns.Add(dc);

                DataColumn dc2 = new DataColumn();
                dc2.ColumnName = "FileName_DW";
                dc2.DataType = typeof(string);
                dc2.DefaultValue = sp.showPathWithFileName ? cFile.Name : Path.GetFileName(cFile.Name);
                dataTable.Columns.Add(dc2);

                DataColumn dc3 = new DataColumn();
                dc3.ColumnName = "FileRowDate_DW";
                dc3.DataType = typeof(string);
                dc3.DefaultValue = FileRowDate_DW.ToString("yyyy-MM-dd HH:mm:ss");
                dataTable.Columns.Add(dc3);

                DataColumn dc4 = new DataColumn();
                dc4.ColumnName = "FileSize_DW";
                dc4.DataType = typeof(string);
                dc4.DefaultValue = FileSize_DW.ToString();
                dataTable.Columns.Add(dc4);

                DataColumn dc5 = new DataColumn();
                dc5.ColumnName = "DataSet_DW";
                dc5.DataType = typeof(string);
                dc5.DefaultValue = DataSet_DW;
                dataTable.Columns.Add(dc5);

                if (sqlFlowParam.flowType.Equals("csv",StringComparison.InvariantCulture))
                {
                    DataColumn fNum = dataTable.Columns["FileLineNumber"];
                    if (fNum != null)
                    {
                        dataTable.Columns["FileLineNumber"].ColumnName = "FileLineNumber_DW";
                        //dataTable.Columns["FileLineNumber"].DataType = typeof(int);
                    }
                }

                if (sqlFlowParam.flowType == "xls" && sp.IncludeFileLineNumber)
                {
                    DataColumn dc6 = new DataColumn();
                    dc6.ColumnName = "FileLineNumber_DW";
                    dc6.DataType = typeof(string);
                    dataTable.Columns.Add(dc6);
                    int counter = 1; // Starting counter value
                    foreach (DataRow row in dataTable.Rows)
                    {
                        row["FileLineNumber_DW"] = counter.ToString(); // Update the column with the counter
                        counter++; // Increment the counter
                    }
                }

                logger.LogInformation($"Source file read into memory {cFile.Name}");

                string cmdColumns = "";
                string columnList = "";
                string columnMappingList = "";

                string[] fileColNames = dataTable.Columns.Cast<DataColumn>()
                    .Select(x => x.ColumnName)
                    .ToArray();
                List<string> list = fileColNames.ToList();

                Dictionary<string, string> colDic = new Dictionary<string, string>();

                //Column Name Cleanup
                foreach (DataColumn col in dataTable.Columns)
                {
                    var columnName = col.ColumnName;

                    if (sp.firstRowHasHeader)
                    {
                        columnName = Regex.Replace(col.ColumnName, sp.colCleanupSqlRegExp, "_");
                        if (colDic.ContainsKeyIgnoreCase(columnName))
                        {
                            columnName = columnName + col.Ordinal;
                            colDic.Add(columnName, columnName);
                            dataTable.Columns[col.Ordinal].ColumnName = columnName;
                        }
                        else
                        {
                            colDic.Add(columnName, columnName);
                            dataTable.Columns[col.Ordinal].ColumnName = columnName;
                        }
                    }

                    SyncInput si = new SyncInput();
                    List<string> SysColNames = si.SysColNames;

                    if (dataTable.Columns.Count == col.Ordinal + 1)
                    {
                        cmdColumns = cmdColumns + $"[{columnName}] {(SysColNames.Contains(columnName, StringComparer.InvariantCultureIgnoreCase) ? "varchar(255)" : sp.defaultColDataType)} NULL";
                        columnList = columnList + $"[{columnName}]";
                    }
                    else
                    {
                        cmdColumns = cmdColumns + $"[{columnName}] {(SysColNames.Contains(columnName, StringComparer.InvariantCultureIgnoreCase) ? "varchar(255)" : sp.defaultColDataType)} NULL,";
                        columnList = columnList + $"[{columnName}],";
                    }
                    //dataTable.AcceptChanges();
                }
                logger.LogCodeBlock("Cmd Columns:", cmdColumns);

                string cmdCreateTransformations =
                    $"exec flw.AddPreIngTransfrom @FlowID={sqlFlowParam.flowId.ToString()}, @FlowType='{sqlFlowParam.flowType}', @ColList='{columnList}'";
                logger.LogCodeBlock("Add column transformations:", cmdCreateTransformations);

                var cmdAlter = new GetData(sqlFlowCon, cmdCreateTransformations, 360);
                DataTable cmdTbl = cmdAlter.Fetch();
                sp.cmdAlterSQL = cmdTbl.Rows[0]["alterCmd"]?.ToString() ?? string.Empty;
                sp.cmdCreate = cmdTbl.Rows[0]["cmdCreate"]?.ToString() ?? string.Empty;
                sp.tfColList = cmdTbl.Rows[0]["tfColList"]?.ToString() ?? string.Empty;
                sp.currentViewCMD = cmdTbl.Rows[0]["viewCMD"]?.ToString() ?? string.Empty;
                sp.currentViewSelect = cmdTbl.Rows[0]["viewSelect"]?.ToString() ?? string.Empty;

                logger.LogCodeBlock("alterCmd", sp.cmdAlterSQL);
                logger.LogCodeBlock("cmdCreate", sp.cmdCreate);
                logger.LogCodeBlock("tfColList", sp.tfColList);

                var tfColumnDic = sp.tfColList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select((str, idx) => new { str, idx })
                    .ToDictionary(x => x.str, x => x.idx);

                logger.LogInformation($"Number of Pre Ingestion Transform Columns: {tfColumnDic.Count()}");

                string trgTblCmd = "";

                if (sp.syncSchema)
                {
                    if (sp.FileCounter == 0)
                    {
                        sp.targetExsits = true;
                        trgTblCmd = sp.cmdCreate;
                    }
                    else
                    {
                        trgTblCmd = sp.cmdAlterSQL;
                    }
                }

                sp.logCreateCmd = trgTblCmd;
                logger.LogCodeBlock("Target table prepare command:", trgTblCmd);

                if (trgTblCmd.Length > 0)
                {
                    CommonDB.ExecDDLScript(trgSqlCon, trgTblCmd, sp.bulkLoadTimeoutInSek, sp.trgIsSynapse);
                    sp.targetExsits = true;
                    logger.LogInformation("Prepare command executed on target table");

                }

                sp.logFetched =+ dataTable.Rows.Count;
                
                //Remove Target Table Indexes
                if (sp.FileCounter == 0)
                {
                    SQLObject sObj = CommonDB.SQLObjectFromDBSchobj(sp.trgDbSchTbl);
                    using (var operation = logger.TrackOperation("Drop Target Table Indexes"))
                    {
                        Services.Schema.ObjectIndexes.DropObjectIndexes(sp.trgConString, sObj.ObjDatabase, sObj.ObjSchema, sObj.ObjName);
                    }
                }

                //Create mappings for sqlbulk
                foreach (DataColumn col in dataTable.Columns)
                {
                    var columnName = "[" + col.ColumnName + "]";

                    if (columnMappings.ContainsKeyIgnoreCase(columnName) == false && tfColumnDic.ContainsKeyIgnoreCase(columnName) == true)
                    {
                        string trgColName =
                            Functions.FindColumnName(sp.tfColList, col.ColumnName);
                        columnMappings.Add(columnName, trgColName);
                    }
                }

                columnMappingList = string.Join(", ", columnMappings.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                logger.LogCodeBlock("Column Mapping List:", columnMappingList);

                sp.srcRowCount =+ dataTable.Rows.Count;

                //Ingestion 
                Shared.WriteToTarget(sqlFlowParam, sp, logger, dataTable, cFile, columnMappings, retryErrorCodes);

                dataTable.Clear();
                dataTable.Dispose();

                if (sp.srcDeleteIngested)
                {
                    if (sqlFlowParam.sourceIsAzCont)
                    {
                        DataLakeFileClient delClient = srcFileSystemClient.GetFileClient(cFile.FullPath);
                        delClient.DeleteIfExists();
                        logger.LogInformation($"Deleting Source File");
                    }
                    else
                    {
                        if (File.Exists(cFile.FullPath))
                        {
                            File.Delete(cFile.FullPath);
                            logger.LogInformation($"Deleting Source File");
                        }
                    }
                }

                sp.FileCounter += 1;
            }
        }

        internal static void TruncateTargetTable(ServiceParam sp, RealTimeLogger logger, SqlConnection trgSqlCon)
        {
            if (sp.truncateTrg == true)
            {
                var cmdTruncTrg = sp.truncateTrg ? $"TRUNCATE TABLE [{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}]; ALTER TABLE [{sp.trgDatabase}].[{sp.trgSchema}].[{sp.trgObject}] REBUILD;  " : "";

                // Track the operation of truncating the target table
                using (var operation = logger.TrackOperation("TruncateTargetTable"))
                {
                    logger.LogCodeBlock("Truncate target table", cmdTruncTrg);
                    var turncTrgCmd = new ExecNonQuery(trgSqlCon, cmdTruncTrg, sp.bulkLoadTimeoutInSek);

                    try
                    {
                        turncTrgCmd.Exec();
                    }
                    catch (SqlException ex)
                    {
                        logger.LogError($"Error truncating target table: {ex.Message}");
                    }

                    // Log completion of the target table truncation
                    logger.LogInformation($"Target table truncated due to flag");
                }
            }
        }
    }
}
