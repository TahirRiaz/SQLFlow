using Azure.Identity;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.DataFactory;
using Azure.ResourceManager.DataFactory.Models;

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Automation.Models;
using Azure.ResourceManager;
using Azure.Storage.Files.DataLake;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Logger;
using System.Collections;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Octokit;

namespace SQLFlowCore.Services
{
    /// <summary>
    /// Provides a set of static methods and classes to execute SQLFlow processes.
    /// </summary>
    /// <remarks>
    /// This class contains methods and classes to manage and execute SQLFlow processes, including the execution of SQLFlow items and handling of events related to the running of these processes.
    /// </remarks>
    /// <example>
    /// This sample shows how to call the <see cref="Exec"/> method.
    /// <code>
    /// string result = ProcessInvoke.Exec(sqlFlowConString, flowId, execMode, BatchID, dbg, sqlFlowItem);
    /// </code>
    /// </example>
    internal static class ProcessInvoke
    {
        /// <summary>
        /// Occurs when the Invoke process is running.
        /// </summary>
        /// <remarks>
        /// This event is triggered in the ExecFlowBatch class during the execution of the batch process.
        /// </remarks>
        internal static event EventHandler<EventArgsInvoke> InvokeIsRunning;

        #region ProcessInvoke
        /// <summary>
        /// Executes a SQL Flow process.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQL Flow database.</param>
        /// <param name="flowId">The identifier of the flow to be executed.</param>
        /// <param name="execMode">The execution mode of the flow.</param>
        /// <param name="batchId">The identifier of the batch.</param>
        /// <param name="dbg">The debug level.</param>
        /// <param name="sqlFlowParam">The SQL Flow item to be processed.</param>
        /// <returns>A string representing the result of the execution.</returns>
        internal static string Exec(SqlFlowParam sqlFlowParam)
        {
           
            ConStringParser conStringParser = new ConStringParser(sqlFlowParam.sqlFlowConString) { ConBuilderMsSql = { ApplicationName = "SQLFlow App" } };

            var execTime = new Stopwatch();
            execTime.Start();

            var logOutput = new StringBuilder();
            Action<string> outputAction = s => logOutput.Append(s);
            var logger = new RealTimeLogger("PreIngestionCsv", outputAction, LogLevel.Information, debugLevel: sqlFlowParam.dbg);

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
                        Shared.GetFilePipelineMetadata("[flw].[GetRVInvoke]", sqlFlowParam, logger, sqlFlowCon, out paramTbl, out incrTbl, out DateTimeFormats, out procParamTbl);
                    }

                    if (paramTbl.Rows.Count > 0)
                    {
                        // Initialize the parameters
                        sp.Initialize(paramTbl);
                        
                        if (sp.invokeAlias.Length > 0)
                        {
                            try
                            {
                                if (sp.invokeType == "ps")
                                {
                                    using (var operation = logger.TrackOperation("Invoke powershell"))
                                    {
                                        logger.LogInformation($"Executing {sp.invokeAlias}");
                                        var idArquivo = Guid.NewGuid();
                                        var scriptFileWithPath = $@"{sp.invokePath}\{idArquivo}.ps1";

                                        if (sp.invokeFile.Length > 0)
                                        {
                                            scriptFileWithPath = sp.invokeFile;
                                            logger.LogInformation($"Current Script file {sp.invokeFile}");
                                        }
                                        else
                                        {
                                            using (var fileStream = new FileStream(scriptFileWithPath, System.IO.FileMode.Create))
                                            {
                                                using (var sw = new StreamWriter(fileStream, Encoding.GetEncoding("UTF-8")) { NewLine = "\r\n" })
                                                {
                                                    sw.Write(sp.code);
                                                    logger.LogInformation($"New Script file created {scriptFileWithPath}");
                                                }
                                            }
                                        }

                                        logger.LogCodeBlock("scriptFileWithPath:", scriptFileWithPath);

                                        string args;
                                        if (string.IsNullOrEmpty(sp.arguments))
                                            args = @"-ExecutionPolicy ByPass -File """ + scriptFileWithPath + @""""; // -ExecutionPolicy Unrestricted
                                        else
                                            // Para funcionar, precisa executar antes no powershell da máquina destino: Enable-PSRemoting –Force
                                            args = @"-ExecutionPolicy ByPass -Command { Invoke-Command -ComputerName " + sp.arguments +
                                                         @" -FilePath """ + scriptFileWithPath + @""" }";

                                        logger.LogCodeBlock("Script Args:", args);
                                        logger.LogInformation($"Script Args {args}");

                                        using (var scriptProc = new Process
                                        {
                                            StartInfo = {
                                                FileName = "powershell", Arguments = args, UseShellExecute = false,
                                                RedirectStandardOutput = true, RedirectStandardError = true,
                                                StandardOutputEncoding = Encoding.GetEncoding(850), CreateNoWindow = true
                                            }
                                        }
                                        )
                                        {
                                            sp.logRuntimeCmd = scriptProc.StandardOutput.ReadToEnd();
                                            sp.logErrorRuntime = scriptProc.StandardError.ReadToEnd();

                                            logger.LogCodeBlock("logRuntimeCmd:", sp.logRuntimeCmd);
                                            logger.LogCodeBlock("logErrorRuntime:", sp.logErrorRuntime);

                                            sp.logErrorRuntime = scriptProc.StandardError.ReadToEnd();

                                            logger.LogInformation($"{sp.invokeType} script executed ");
                                            if (sp.invokeFile.Length > 0)
                                            {
                                                File.Delete(scriptFileWithPath);
                                            }
                                        }
                                    }
                                
                                
                                }

                                if (sp.invokeType == "adf")
                                {
                                    using (var operation = logger.TrackOperation("Invoke Azure Data Factory"))
                                    {
                                        logger.LogInformation($"Executing ADF Pipeline {sp.trgPipelineName}");

                                        // Deserialize pipeline parameters if provided.
                                        Dictionary<string, object> adfParam = new Dictionary<string, object>();
                                        if (!string.IsNullOrEmpty(sp.trgParameterJSON))
                                        {
                                            adfParam = JsonConvert.DeserializeObject<Dictionary<string, object>>(sp.trgParameterJSON);
                                        }

                                        // Convert parameters into IDictionary<string, BinaryData>
                                        IDictionary<string, BinaryData> binaryParams = new Dictionary<string, BinaryData>();
                                        foreach (var kv in adfParam)
                                        {
                                            // Using BinaryData.FromObjectAsJson for proper JSON conversion.
                                            binaryParams[kv.Key] = BinaryData.FromObjectAsJson(kv.Value);
                                        }

                                        // Initialize credential and ArmClient.
                                        var clientSecretCredential = new ClientSecretCredential(sp.trgTenantId, sp.trgApplicationId, sp.trgClientSecret);
                                        var armClient = new ArmClient(clientSecretCredential);

                                        // Get the Data Factory resource.
                                        var dataFactoryId = DataFactoryResource.CreateResourceIdentifier(sp.trgSubscriptionId, sp.trgResourceGroup, sp.trgDataFactoryName);
                                        DataFactoryResource dataFactory = armClient.GetDataFactoryResource(dataFactoryId);

                                        // Retrieve the pipeline resource (unwrap the response using .Value).
                                        var pipelineResponse = dataFactory.GetDataFactoryPipelineAsync(sp.trgPipelineName).Result;
                                        DataFactoryPipelineResource pipeline = pipelineResponse.Value;

                                        // Trigger the pipeline run.
                                        PipelineCreateRunResult runResult = pipeline
                                            .CreateRunAsync(parameterValueSpecification: binaryParams, cancellationToken: CancellationToken.None)
                                            .Result.Value;

                                        logger.LogCodeBlock("Job RunID:", runResult.RunId.ToString());
                                        logger.LogInformation($"Pipeline RunID {runResult.RunId}");

                                        // Poll for pipeline run status.
                                        using (logger.TrackOperation("Azure Data Factory Running"))
                                        {
                                            var watch = new Stopwatch();
                                            watch.Start();
                                            DataFactoryPipelineRunInfo pipelineRunInfo;

                                            while (true)
                                            {
                                                // Get the latest run status.
                                                pipelineRunInfo = dataFactory
                                                    .GetPipelineRunAsync(runResult.RunId.ToString(), CancellationToken.None)
                                                    .Result.Value;

                                                EventArgsInvoke arg = new EventArgsInvoke
                                                {
                                                    TimeSpan = watch.ElapsedMilliseconds / 1000,
                                                    EventDateTime = DateTime.Now,
                                                    InvokedObjectName = "Data Factory pipeline " + sp.trgPipelineName,
                                                    InvokeStatus = pipelineRunInfo.Status
                                                };
                                                InvokeIsRunning?.Invoke(Thread.CurrentThread, arg);

                                                logger.LogInformation($"Status {pipelineRunInfo.Status} ({watch.ElapsedMilliseconds / 1000} sec)");

                                                if (pipelineRunInfo.Status == "Running" ||
                                                    pipelineRunInfo.Status == "New" ||
                                                    pipelineRunInfo.Status == "InProgress" ||
                                                    pipelineRunInfo.Status == "Queued" ||
                                                    pipelineRunInfo.Status == "Activating" ||
                                                    pipelineRunInfo.Status == "Starting")
                                                {
                                                    Thread.Sleep(8000);
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }



                                if (sp.invokeType == "aut")
                                {
                                    using (var operation = logger.TrackOperation("Invoke Azure Automation"))
                                    {
                                        logger.LogInformation($"Executing Automation Pipeline {sp.trgRunbookName}");

                                        //var autContext = new AuthenticationContext("https://login.windows.net/" + trgTenantId);
                                        //var autClientCredential = new ClientCredential(trgApplicationId, trgClientSecret);
                                        //var autResult = autContext.AcquireTokenAsync("https://management.azure.com/", autClientCredential).Result;
                                        //TokenCloudCredentials autCred = new TokenCloudCredentials(trgSubscriptionId, autResult.AccessToken);

                                        var credential = new ClientSecretCredential(sp.trgTenantId, sp.trgApplicationId, sp.trgClientSecret);

                                        // First, create an instance of ArmClient with your credentials - this is your entry point to managing Azure resources
                                        var armClient = new ArmClient(credential);

                                        ResourceIdentifier resourceId = new ResourceIdentifier($"/subscriptions/{sp.trgSubscriptionId}/resourceGroups/{sp.trgResourceGroup}/providers/Microsoft.Automation/automationAccounts/{sp.trgAutomationAccountName}");

                                        // Retrieve the Automation Account resource or any other resource you wish to manage
                                        var automationAccountResource = armClient.GetAutomationAccountResource(resourceId);

                                        Dictionary<string, string> autParam = new Dictionary<string, string>();
                                        if (sp.trgParameterJSON.Length > 0)
                                        {
                                            autParam = JsonConvert.DeserializeObject<Dictionary<string, string>>(sp.trgParameterJSON);
                                        }

                                        AutomationJobCreateOrUpdateContent job = new AutomationJobCreateOrUpdateContent();
                                        job.RunbookName = sp.trgRunbookName;
                                        //job.Parameters = autParam;
                                        string guid = Guid.NewGuid().ToString();

                                        var jobRun = automationAccountResource.GetAutomationJobs().CreateOrUpdate(WaitUntil.Completed, $"{sp.trgRunbookName}_SQLFLW_{guid}", job);

                                        var token = credential.GetToken(new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

                                        using (logger.TrackOperation("Azure Automation Running"))
                                        {
                                            var watch = new Stopwatch();
                                            watch.Start();
                                            while (true)
                                            {
                                                string status = RetrieveAzRunbookStatus(token.Token, sp.trgSubscriptionId, sp.trgResourceGroup, sp.trgAutomationAccountName, jobRun.Value.Data.JobId.ToString());
                                                EventArgsInvoke arg = new EventArgsInvoke
                                                {
                                                    TimeSpan = watch.ElapsedMilliseconds / 1000,
                                                    EventDateTime = DateTime.Now,
                                                    InvokedObjectName = "Automation " + sp.trgRunbookName,
                                                    InvokeStatus = status
                                                };
                                                InvokeIsRunning?.Invoke(Thread.CurrentThread, arg);

                                                logger.LogInformation($"Status {sp.trgRunbookName} {status} ({watch.ElapsedMilliseconds / 1000} sec) {Environment.NewLine}");
                                                if (status == "Running" || status == "New" || status == "InProgress" || status == "Queued" ||
                                                    status == "Activating" || status == "Starting")
                                                    Thread.Sleep(9000);
                                                else
                                                    break;
                                            }
                                        }

                                    }
                                }

                                if (sp.invokeType == "cs")
                                {
                                    using (var operation = logger.TrackOperation("Invoke C# Code"))
                                    {
                                        logger.LogInformation($"Executing Dynamic CSharp Code {sp.trgPipelineName}");
                                        logger.LogCodeBlock("Dynamic CSharp Code:", sp.code);

                                        string fileSystemName = "<your-file-system-name>";
                                        Uri serviceUri = new Uri($"https://accountName.dfs.core.windows.net/{fileSystemName}");

                                        // Use DefaultAzureCredential or another credential from Azure.Identity
                                        DefaultAzureCredential defaultAzureCredential = new DefaultAzureCredential();

                                        // Instantiate the DataLakeFileSystemClient with the URI and token credential
                                        DataLakeFileSystemClient trgFileSystemClient = new DataLakeFileSystemClient(serviceUri, defaultAzureCredential);
                                        DataLakeFileSystemClient srcFileSystemClient = new DataLakeFileSystemClient(serviceUri, defaultAzureCredential);
                                        string keyVaultUrl = "https://<your-key-vault-name>.vault.azure.net/";
                                        AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(keyVaultUrl);

                                        if (sp.trgTenantId.Length > 0)
                                        {
                                            trgFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                                sp.trgTenantId,
                                                sp.trgApplicationId,
                                                sp.trgClientSecret,
                                                sp.trgKeyVaultName,
                                                sp.trgSecretName,
                                                sp.trgStorageAccountName,
                                                sp.trgBlobContainer);

                                            logger.LogInformation($"trgFileSystemClient Created {sp.trgPipelineName}");

                                            trgKeyVaultManager = new AzureKeyVaultManager(
                                            sp.trgTenantId,
                                            sp.trgApplicationId,
                                            sp.trgClientSecret,
                                            sp.trgKeyVaultName);

                                            logger.LogInformation($"AzureKeyVaultManager Created {sp.trgPipelineName}");
                                        }

                                        if (sp.srcTenantId.Length > 0)
                                        {
                                            srcFileSystemClient = DataLakeHelper.GetDataLakeFileSystemClient(
                                                sp.srcTenantId,
                                                sp.srcApplicationId,
                                                sp.srcClientSecret,
                                                sp.srcKeyVaultName,
                                                sp.srcSecretName,
                                                sp.srcStorageAccountName,
                                                sp.srcBlobContainer);

                                            logger.LogInformation($"srcFileSystemClient Created {sp.trgPipelineName}");
                                        }

                                        try
                                        {
                                            var val = InvokeCs.DynamicCode(logger, sp.code, trgKeyVaultManager, srcFileSystemClient, trgFileSystemClient).Result;
                                        }
                                        catch (Exception ex)
                                        {
                                            sp.logErrorRuntime = $"Error executing Dynamic CSharp Code {ex.Message}";
                                            throw;
                                        }
                                    }
                                }
                                

                                execTime.Stop();
                                sp.logDurationFlow = execTime.ElapsedMilliseconds / 1000;
                                sp.logEndTime = DateTime.Now;
                                sp.logRuntimeCmd = logOutput.ToString();

                                logger.LogInformation($"Total processing time ({sp.logDurationFlow} sec)");
                                Shared.EvaluateAndLogExecution(sqlFlowCon, sqlFlowParam, logOutput, sp, logger);
                                return sp.result;
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
                                sqlFlowCon.Close();
                            }

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
                    //SqlConnection.ClearPool(sqlFlowCon);
                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
            }

            return sp.result;
        }

        /// <summary>
        /// Retrieves the status of an Azure Runbook job.
        /// </summary>
        /// <param name="token">The access token for Azure management API.</param>
        /// <param name="subscriptionId">The subscription ID of the Azure account.</param>
        /// <param name="resourceGroup">The resource group where the automation account is located.</param>
        /// <param name="automationaccount">The name of the automation account.</param>
        /// <param name="jobid">The ID of the job to retrieve the status for.</param>
        /// <returns>The status of the specified Azure Runbook job.</returns>
        private static string RetrieveAzRunbookStatus(string token, string subscriptionId, string resourceGroup, string automationaccount, string jobid)
        {
            string URL = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Automation/automationAccounts/{automationaccount}/jobs/{jobid}?api-version=2017-05-15-preview";
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            HttpResponseMessage httpResponseMessage = client.GetAsync(URL).Result;

            ResponseClass resObj = new ResponseClass();
            string result = "";

            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                result = httpResponseMessage.Content.ReadAsStringAsync().Result;
            }
            else
            {
                var response = httpResponseMessage.Content.ReadAsStringAsync().Result;
                resObj = JsonConvert.DeserializeObject<ResponseClass>(response);
                result = resObj.properties.status;
            }

            return result;
        }

        [JsonObject]
        /// <summary>
        /// Represents the response from an Azure Runbook job.
        /// </summary>
        /// <remarks>
        /// This class is used to deserialize the JSON response from an Azure Runbook job.
        /// It includes properties such as the job's ID, name, type, and other job-related properties.
        /// </remarks>
        internal class ResponseClass
        {
            [JsonProperty("id")]
            internal string id { get; set; }

            [JsonProperty("name")]
            internal string name { get; set; }

            [JsonProperty("type")]
            internal string type { get; set; }

            [JsonProperty("properties")]
            internal JobProps properties { get; set; }
        }

        [JsonObject]
        /// <summary>
        /// Represents the properties of a job in the context of a ProcessInvoke class.
        /// </summary>
        /// <remarks>
        /// This class includes properties such as jobId, creationTime, provisioningState, status, 
        /// statusDetails, startedBy, startTime, endTime, lastModifiedTime, lastStatusModifiedTime, 
        /// exception, parameters, runOn, and runbook.
        /// </remarks>
        internal class JobProps
        {
            [JsonProperty("jobId")]
            internal string jobId { get; set; }

            [JsonProperty("creationTime")]
            internal string creationTime { get; set; }

            [JsonProperty("provisioningState")]
            internal string provisioningState { get; set; }

            [JsonProperty("status")]
            internal string status { get; set; }

            [JsonProperty("statusDetails")]
            internal string statusDetails { get; set; }

            [JsonProperty("startedBy")]
            internal string startedBy { get; set; }

            [JsonProperty("startTime")]
            internal string startTime { get; set; }

            [JsonProperty("endTime")]
            internal string endTime { get; set; }

            [JsonProperty("lastModifiedTime")]
            internal string lastModifiedTime { get; set; }

            [JsonProperty("lastStatusModifiedTime")]
            internal string lastStatusModifiedTime { get; set; }

            [JsonProperty("exception")]
            internal string exception { get; set; }

            [JsonProperty("parameters")]
            internal Dictionary<string, string> parameters { get; set; }

            [JsonProperty("runOn")]
            internal string runOn { get; set; }

            [JsonProperty("runbook")]
            internal Runbooks runbook { get; set; }
        }

        /// <summary>
        /// Represents a runbook in the context of a job in the ProcessInvoke class.
        /// </summary>
        internal class Runbooks
        {
            /// <summary>
            /// Gets or sets the name of the runbook.
            /// </summary>
            [JsonProperty("name")]
            internal string name { get; set; }
        }

        /// <summary>
        /// Represents the parameters used in the ProcessInvoke class.
        /// </summary>
        internal class Parameters
        {
            /// <summary>
            /// Gets or sets the name of the parameter.
            /// </summary>
            [JsonProperty("name")]
            internal string name { get; set; }

            /// <summary>
            /// Gets or sets the value of the parameter.
            /// </summary>
            [JsonProperty("value")]
            internal string value { get; set; }
        }

        private static void OnSqlRowsCopied(object sender, SqlRowsCopiedEventArgs e)
        {
            //Interlocked.Add(ref _curRowCount, _notify); 

        }
        #endregion ProcessInvoke

    }
}