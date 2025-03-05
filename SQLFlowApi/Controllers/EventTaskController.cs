using System.Collections.Concurrent;
using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using SQLFlowApi.Models;
using SQLFlowApi.Services;
using SQLFlowCore.Lineage;
using SQLFlowCore.Pipeline;
using SQLFlowUi.Service;


namespace SQLFlowApi.Controllers
{
    [Route("/event/")]
    [ApiController]
    public class EventTaskController : ControllerBase
    {
        private readonly ConfigService _configService;
        private readonly ILogger<EventTaskController> _logger;
        private string _rowString;
        private string _conStr = "";
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly TokenService _tokenService;
        private DataTable _lineageMapTbl = new DataTable();
        private DataTable _nodeTbl = new DataTable();
        private DataTable _fileDetailsTbl = new DataTable();

        private DateTime _lastRefreshTime = DateTime.MinValue;
        private static Dictionary<string, List<CancellationTokenSource>> _tokens = new Dictionary<string, List<CancellationTokenSource>>();
        private static int _flowIdFromFile = 0;
        private PipelineBackgroundService _backgroundService;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventTaskController"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration, injected by the ASP.NET Core IoC container.</param>
        /// <param name="signInManager">The manager for user sign in operations, injected by the ASP.NET Core IoC container.</param>
        /// <param name="userManager">The manager for user identity operations, injected by the ASP.NET Core IoC container.</param>
        /// <param name="tokenService">The service for JWT token operations, injected by the ASP.NET Core IoC container.</param>
        /// <param name="backgroundService">The service for managing background tasks, injected by the ASP.NET Core IoC container.</param>
        /// <param name="configService">The service for managing application configuration, injected by the ASP.NET Core IoC container.</param>
        /// <param name="logger">The logger service, injected by the ASP.NET Core IoC container.</param>
        public EventTaskController(IConfiguration configuration, SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, TokenService tokenService, PipelineBackgroundService backgroundService, ConfigService configService , ILogger<EventTaskController> logger)
        {
            _backgroundService = backgroundService;
            _configService = configService;
            _logger = logger;
            _rowString = "";
            _signInManager = signInManager;
            _userManager = userManager;
            _tokenService = tokenService;
            _conStr = Environment.GetEnvironmentVariable("SQLFlowConStr") ??
                      string.Empty; //configuration.GetConnectionString("SQLFlowConStr") ?? string.Empty;
            _flowIdFromFile = 0;

            //_backgroundService.SetMaxParallel(_configService.configSettings.MaxParallelTasks,
                //_configService.configSettings.MaxParallelSteps);
        }


        /// <summary>
        /// Handles webhook events from EventGrid.
        /// </summary>
        /// <param name="events">An array of EventGridEvent objects representing the events received from EventGrid.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains the IActionResult that represents the result of the operation.</returns>
        /// <remarks>
        /// This method handles two types of events: "Microsoft.EventGrid.SubscriptionValidationEvent" and "Microsoft.Storage.BlobCreated".
        /// For "Microsoft.EventGrid.SubscriptionValidationEvent", it validates the subscription and returns the validation code.
        /// For "Microsoft.Storage.BlobCreated", it processes the blob creation event and enqueues a pipeline request for matching flows.
        /// </remarks>
        [HttpPost("webhook")]
        public async Task<IActionResult> webhook([FromBody] EventGridEvent[] events)
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                foreach (var eventGridEvent in events)
                {
                    _logger.LogInformation($"Received event: {eventGridEvent.EventType}");

                    switch (eventGridEvent.EventType)
                    {
                        case "Microsoft.EventGrid.SubscriptionValidationEvent":
                            try
                            {
                                var validationData = JsonConvert.DeserializeObject<SubscriptionValidationEventData>(eventGridEvent.Data.ToString());
                                _logger.LogInformation($"Validation code: {validationData.ValidationCode}");
                                return Ok(new { validationResponse = validationData.ValidationCode });
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogError($"Error deserializing SubscriptionValidationEvent data: {ex.Message}");
                                return BadRequest("Invalid data format for SubscriptionValidationEvent.");
                            }

                        case "Microsoft.Storage.BlobCreated":
                            try
                            {
                                //var blobData = JsonConvert.DeserializeObject<BlobCreatedEventData>(eventGridEvent.Data.ToString());
                                string containerName, filePath;
                                ExtractContainerAndFilePath(eventGridEvent.Subject, out containerName, out filePath);

                                string usequeue = "0";
                                string flowid = "0";
                                string execasnode = "0";

                                if (HttpContext.Request.Query.TryGetValue("usequeue", out var qp_usequeue))
                                {
                                    usequeue = qp_usequeue;
                                }

                                if (HttpContext.Request.Query.TryGetValue("flowid", out var qp_flowid))
                                {
                                    flowid = qp_flowid;
                                }
                                
                                if (HttpContext.Request.Query.TryGetValue("execasnode", out var qp_execasnode))
                                {
                                    execasnode = qp_execasnode;
                                }

                                if (usequeue != "0")
                                {
                                    if (flowid != "0" && execasnode == "0")
                                    {
                                        _flowIdFromFile = int.Parse(flowid);
                                        LogEventToSqlServer(_flowIdFromFile, filePath);
                                        MemoryStream memoryStream = new MemoryStream();
                                        StreamWriter writer = new StreamWriter(memoryStream);
                                        ExecFlowProcess.Exec(writer, Environment.GetEnvironmentVariable("SQLFlowConStr"), _flowIdFromFile, "Event", 1, filePath);
                                        _logger.LogInformation($"FlowID: {_flowIdFromFile} executed");
                                    }
                                    else
                                    {
                                        _flowIdFromFile = int.Parse(flowid);
                                        LogEventToSqlServer(_flowIdFromFile, filePath);
                                        MemoryStream memoryStream = new MemoryStream();
                                        StreamWriter writer = new StreamWriter(memoryStream);
                                        ExecFlowNode.Exec(writer,
                                            Environment.GetEnvironmentVariable("SQLFlowConStr"), flowid, "A", "Event",
                                            true, false, false, 1, filePath);
                                        _logger.LogInformation($"Node: {_flowIdFromFile} executed");
                                    }
                                }
                                else
                                {
                                    if (flowid != "0" && execasnode == "0")
                                    {
                                        _flowIdFromFile = int.Parse(flowid);
                                        LogEventToSqlServer(_flowIdFromFile, filePath);

                                        List<PipelineStep> Steps = new List<PipelineStep>();
                                        Guid pipelineId = Guid.NewGuid();
                                        PipelineStep ps = new PipelineStep();
                                        ps.FlowId = _flowIdFromFile;
                                        ps.Step = 100;
                                        ps.FileName = filePath;
                                        ps.PipelineId = pipelineId;
                                        ps.Status = "Queued";
                                        Steps.Add(ps);
                                        _logger.LogInformation($"FlowID: {_flowIdFromFile}, Step: {100}");

                                        PipelineRequest request = new PipelineRequest
                                        {
                                            Key = pipelineId,
                                            Steps = Steps
                                        };

                                        _backgroundService.EnqueuePipeline(request);
                                        _logger.LogInformation("Pipeline request {PipelineId} received and enqueued.", request.Key);
                                    }
                                    else
                                    {
                                        RefreshFileFlows();
                                        FindMatchingFlows(_fileDetailsTbl, filePath);
                                        LogEventToSqlServer(_flowIdFromFile, filePath);
                                        LineageDescendants dfs = new LineageDescendants(_lineageMapTbl, _flowIdFromFile, false, false);
                                        _nodeTbl = LineageHelper.GetMaxStepPerFlowID(dfs.GetResult());
                                        //_logger.LogInformation($"Blob created at: {blobData.Url}");
                                        if (_nodeTbl.Rows != null)
                                        {
                                            if (_nodeTbl.Rows.Count > 0)
                                            {
                                                _logger.LogInformation($"FlowID: {_nodeTbl.Rows[0]["FlowID"]}");
                                                List<PipelineStep> Steps = new List<PipelineStep>();
                                                Guid pipelineId = Guid.NewGuid();
                                                int stepItemCounter = 0;
                                                foreach (DataRow row in _nodeTbl.Rows)
                                                {
                                                    PipelineStep ps = new PipelineStep();
                                                    ps.FlowId = int.Parse(row["FlowID"].ToString());
                                                    ps.Step = int.Parse(row["Step"].ToString());
                                                    ps.FileName = (stepItemCounter == 0 ? filePath : "");
                                                    ps.PipelineId = pipelineId;
                                                    ps.Status = "Queued";
                                                    Steps.Add(ps);
                                                    _logger.LogInformation($"FlowID: {row["FlowID"]}, Step: {row["Step"]}");
                                                    stepItemCounter++;
                                                }

                                                PipelineRequest request = new PipelineRequest
                                                {
                                                    Key = pipelineId,
                                                    Steps = Steps
                                                };

                                                _backgroundService.EnqueuePipeline(request);
                                                _logger.LogInformation("Pipeline request {PipelineId} received and enqueued.", request.Key);
                                            }
                                            else
                                            {
                                                _logger.LogInformation("No matching flow found");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogError($"Error deserializing BlobCreatedEventData: {ex.Message}");
                            }
                            break;

                        default:
                            _logger.LogWarning("Event type not supported");
                            break;
                    }
                }
                return Ok();
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
            
            
        }

        /// <summary>
        /// Retrieves the list of queued pipelines.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="IActionResult"/> that contains the list of queued pipelines if the user is authenticated, otherwise an Unauthorized status code with an error message.
        /// </returns>
        /// <remarks>
        /// This method first authenticates the request using the <see cref="AuthRequest"/> method. If the user is authenticated, it retrieves the queued pipelines from the <see cref="PipelineBackgroundService"/> and returns them in the response.
        /// Each pipeline in the response includes the pipeline key and a list of steps. Each step includes the pipeline ID, flow ID, step number, file name, and status.
        /// If the user is not authenticated, it returns an Unauthorized status code with an error message.
        /// </remarks>
        [HttpGet("queued")]
        public async Task<IActionResult>  GetQueuedPipelines()
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                var queuedPipelines = _backgroundService.GetQueuedPipelines().Select(req => new
                {
                    req.Key,
                    Steps = req.Steps.Select(s => new { s.PipelineId, s.FlowId, s.Step, s.FileName, s.Status })
                });

                return Ok(queuedPipelines);
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
            
        }

        /// <summary>
        /// Retrieves the list of currently running pipelines.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="IActionResult"/> that contains the list of running pipelines if the user is authenticated, otherwise returns an Unauthorized result with an error message.
        /// </returns>
        /// <remarks>
        /// This method first authenticates the request using the <see cref="AuthRequest"/> method. If the user is authenticated, it retrieves the list of running pipelines from the <see cref="PipelineBackgroundService"/> and returns an Ok result with the list.
        /// If the user is not authenticated, it returns an Unauthorized result with an error message.
        /// </remarks>
        [HttpGet("running")]
        public async Task<IActionResult> GetRunningPipelines()
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {

                var runningPipelines = _backgroundService.GetRunningPipelines();
                return Ok(runningPipelines);
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
        }

        /// <summary>
        /// Gets the list of executed pipelines.
        /// </summary>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains the action result for the HTTP response.
        /// The action result may be either Ok with the list of executed pipelines or Unauthorized with an error message.
        /// </returns>
        /// <remarks>
        /// This method first authenticates the request. If the user is authenticated, it retrieves the status of the background service,
        /// selects the executed pipelines from the status, and returns them in the Ok result.
        /// If the user is not authenticated, it returns an Unauthorized result with an error message.
        /// </remarks>
        [HttpGet("executed")]
        public async Task<IActionResult> GetExecutedPipelines()
        {
               var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                var status = _backgroundService.GetStatus();
                var executedPipelines = status.Executed.Select(log => new
                {
                    log.PipelineId,
                    log.Result,
                    log.Timestamp,
                    Steps = log.Request.Steps.Select(s => new { s.FlowId, s.Step, s.FileName, s.Status })
                });

                return Ok(executedPipelines);
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
            
        }

        /// <summary>
        /// Retrieves the list of waiting pipelines.
        /// </summary>
        /// <returns>
        /// An IActionResult that contains either the list of waiting pipelines or an error message.
        /// </returns>
        /// <remarks>
        /// This method first validates the request using the AuthRequest method. If the user is authenticated, it retrieves the list of waiting pipelines from the background service and returns it.
        /// Each pipeline is represented by an anonymous object that includes the pipeline's key and a list of its steps. Each step is also represented by an anonymous object that includes the pipeline ID, flow ID, step number, file name, and status.
        /// If the user is not authenticated, it returns an Unauthorized result with an error message.
        /// </remarks>
        [HttpGet("waiting")]
        public async Task<IActionResult> GetWaitingPipelines()
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                var waitingPipelines = _backgroundService.GetWaitingPipelines().Select(req => new
                {
                    req.Key,
                    Steps = req.Steps.Select(s => new { s.PipelineId, s.FlowId, s.Step, s.FileName, s.Status })
                });

                return Ok(waitingPipelines);
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
        }

        /// <summary>
        /// Clears all pipelines in the background service.
        /// </summary>
        /// <returns>
        /// An IActionResult that represents the result of the operation. Returns Ok with a success message if the user is authenticated and the pipelines are cleared. Returns Unauthorized with an error message if the user is not authenticated.
        /// </returns>
        /// <remarks>
        /// This method first authenticates the request using the AuthRequest method. If the user is authenticated, it calls the ClearAllPipelines method of the PipelineBackgroundService to clear all pipelines, logs an information message, and returns Ok with a success message. If the user is not authenticated, it returns Unauthorized with an error message from the ValidateRequest.
        /// </remarks>
        [HttpPost("clear")]
        public async Task<IActionResult> ClearPipelines()
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                _backgroundService.ClearAllPipelines();
                _logger.LogInformation("All pipeline data has been cleared.");
                return Ok("All pipeline data has been cleared.");
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
        }


        private async Task LogEventToSqlServer(int flowId, string FileName_DW)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(_conStr))
                {
                    await connection.OpenAsync();
                    string query = @"[flw].[AddSysFileEventLog]";
                            
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@FlowID", flowId);
                        command.Parameters.AddWithValue("@FileName_DW", FileName_DW);
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging event to SQL Server: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Refreshes the file flows by querying the database for updated flow details and lineage map objects.
        /// This method is called when a new blob is created in the storage.
        /// </summary>
        /// <remarks>
        /// This method checks if the last refresh time is older than the configured refresh interval.
        /// If so, it opens a connection to the database and executes two queries:
        /// 1. To fetch the flow details from the [flw].[FlowPathFileDS] table.
        /// 2. To fetch the lineage map objects by executing the [flw].[GetLineageMapObjects] stored procedure.
        /// The results of these queries are stored in DataTables for further processing.
        /// In case of any exception during this process, it logs the error message.
        /// </remarks>
        private void RefreshFileFlows()
        {
            try
            {
                if (_lastRefreshTime < DateTime.Now.AddMinutes(_configService.configSettings.RefreshLineageAfterMinutes))
                {
                    string connectionString = _conStr;
                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        string query = "SELECT FlowID, SrcPath, SrcPathMask, SearchSubDirectories, SrcFile FROM [flw].[FlowPathFileDS]";
                        using (SqlCommand command = new SqlCommand(query, connection))
                        {
                            command.CommandType = CommandType.Text;
                            var da = new SqlDataAdapter(command) { FillLoadOption = LoadOption.Upsert };
                            da.Fill(_fileDetailsTbl);
                            da.Dispose();
                            command.Dispose();
                        }

                        query = "[flw].[GetLineageMapObjects]";
                        using (SqlCommand command = new SqlCommand(query, connection))
                        {
                            command.CommandType = CommandType.Text;
                            var da = new SqlDataAdapter(command) { FillLoadOption = LoadOption.Upsert };
                            da.Fill(_lineageMapTbl);
                            da.Dispose();
                            command.Dispose();
                        }

                        _lastRefreshTime = DateTime.Now;
                        //LineageDescendants dfs = new LineageDescendants(connection, int.Parse(node), allDep, allBatches);
                        //bDSTbl = LineageHelper.GetMaxStepPerFlowID(dfs.GetResult());
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing file flows: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Finds the matching flows for a given file path in a DataTable of flows.
        /// </summary>
        /// <param name="flows">A DataTable containing flow data.</param>
        /// <param name="filePath">The file path to match against the flows.</param>
        /// <remarks>
        /// This method iterates over each flow in the provided DataTable. For each flow, it checks if the file path contains the source path of the flow (case-insensitive). If it does, it then checks if the source path mask is either null or empty, or if it matches the file path using a regular expression. If either of these conditions are met, it then checks if the source file matches the file path using a regular expression. If this condition is met, it sets the static field _flowIdFromFile to the FlowID of the current flow.
        /// </remarks>
        public static void FindMatchingFlows(DataTable flows, string filePath)
        {
            foreach (DataRow flow in flows.Rows)
            {
                string srcPath = flow["SrcPath"].ToString();
                string srcPathMask = flow["SrcPathMask"].ToString();
                bool searchSubDirectories = (bool)flow["SearchSubDirectories"];
                string srcFile = flow["SrcFile"].ToString();

                if (filePath.Contains(srcPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(srcPathMask) || Regex.IsMatch(filePath, srcPathMask))
                    {
                        if (Regex.IsMatch(filePath, srcFile))
                        {
                            _flowIdFromFile = int.Parse(flow["FlowID"].ToString());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Extracts the container name and file path from the provided subject string.
        /// </summary>
        /// <param name="subject">The subject string from which to extract the container name and file path.</param>
        /// <param name="containerName">Output parameter that holds the extracted container name.</param>
        /// <param name="filePath">Output parameter that holds the extracted file path.</param>
        /// <remarks>
        /// The method uses a regular expression to parse the subject string and extract the container name and file path.
        /// The container name and file path are expected to be in the format "/containers/{containerName}/blobs/{filePath}".
        /// </remarks>
        public static void ExtractContainerAndFilePath(string subject, out string containerName, out string filePath)
        {
            // Define the regex pattern to capture the container name and file path
            string pattern = @"\/containers\/([^\/]+)\/blobs\/(.*)";

            // Initialize output variables
            containerName = string.Empty;
            filePath = string.Empty;

            // Create a Regex object with the defined pattern
            Regex regex = new Regex(pattern);
            Match match = regex.Match(subject);

            // Check if the pattern matches and extract the container name and file path
            if (match.Success)
            {
                containerName = match.Groups[1].Value;  // Container name is in the first capturing group
                filePath = match.Groups[2].Value;       // File path is in the second capturing group
            }
        }


        /// <summary>
        /// Authenticates the request by checking the user's identity and validating the JWT token.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="ValidateRequest"/> that contains the authentication status and message.
        /// </returns>
        /// <remarks>
        /// This method first checks if the user is authenticated. If the user is authenticated, it sets the UserAuthenticated property to true and the Message property to "Authenticated User".
        /// If the user is not authenticated, it checks the Authorization header. If the Authorization header starts with "Bearer", it extracts the JWT token and validates it.
        /// If the token is valid, it sets the UserAuthenticated property to true and the Message property to the validation message.
        /// If the token is not valid, it sets the UserAuthenticated property to false and the Message property to the error message.
        /// If no token is provided, it sets the UserAuthenticated property to false and the Message property to "No token provided.".
        /// If the Authorization header is null, it sets the UserAuthenticated property to false and the Message property to "Unauthorized".
        /// </remarks>
        private async Task<ValidateRequest> AuthRequest()
        {
            var valdateRequest = new ValidateRequest();

            string authorizationHeader = HttpContext.Request.Headers["Authorization"];
            string jwtToken = string.Empty;

            // First, check if the user is authenticated
            if (User.Identity.IsAuthenticated)
            {
                valdateRequest.UserAuthenticated = true;
                valdateRequest.Message = "Authenticated User";
            }
            else
            {
                // Check if the token is provided as a URL parameter
                if (HttpContext.Request.Query.TryGetValue("token", out var tokenValues))
                {
                    jwtToken = tokenValues.FirstOrDefault();
                    await loginWithToken(jwtToken, valdateRequest);
                }
                // If token is not found in the URL, check the Authorization header
                else if (authorizationHeader != null)
                {
                    // Check if the Authorization header is not null and starts with "Bearer"
                    if (!string.IsNullOrEmpty(authorizationHeader) && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        jwtToken = authorizationHeader.Substring("Bearer ".Length).Trim();
                    }
                    else
                    {
                        jwtToken = authorizationHeader;
                    }

                    // Continue with token validation
                    await loginWithToken(jwtToken, valdateRequest);
                }
                else
                {
                    valdateRequest.UserAuthenticated = false;
                    valdateRequest.Message = "Unauthorized";
                }
            }

            return valdateRequest;
        }

        private async Task loginWithToken(string jwtToken, ValidateRequest valdateRequest)
        {
            if (!string.IsNullOrEmpty(jwtToken))
            {
                var result = await ValidateTokenInternal(jwtToken);
                if (result.IsValid)
                {
                    valdateRequest.UserAuthenticated = true;
                    valdateRequest.Message = result.Message;
                }
                else
                {
                    valdateRequest.UserAuthenticated = false;
                    valdateRequest.Message = result.ErrorMessage;
                }
            }
            else
            {
                valdateRequest.UserAuthenticated = false;
                valdateRequest.Message = "No token provided.";
            }
        }

        /// <summary>
        /// Validates the provided JWT token.
        /// </summary>
        /// <param name="Token">The JWT token to validate.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation, with a <see cref="TokeValidationResult"/> result providing the validation result.</returns>
        /// <remarks>
        /// This method will validate the token and return a <see cref="TokeValidationResult"/> with the validation status and any relevant messages.
        /// If the token is null or whitespace, the method will return an invalid result with a message indicating that the token is required.
        /// If the token is invalid or expired, the method will return an invalid result with a message indicating the error.
        /// If the token is valid, the method will return a valid result with a message indicating that the token has been validated.
        /// </remarks>
        private async Task<TokeValidationResult> ValidateTokenInternal(string Token)
        {
            var tokeValidationResult = new TokeValidationResult();

            if (string.IsNullOrWhiteSpace(Token))
            {
                tokeValidationResult.IsValid = false;
                tokeValidationResult.ErrorMessage = "Token is required";
            }

            try
            {
                var principal = _tokenService.ValidateToken(Token);
                if (principal != null)
                {
                    var identity = principal.Identity as ClaimsIdentity;

                    tokeValidationResult.IsValid = true;
                    tokeValidationResult.Message = "Token Validated";
                }
                else
                {
                    tokeValidationResult.IsValid = false;
                    tokeValidationResult.ErrorMessage = "Invalid token claims";
                }
            }
            catch (SecurityTokenExpiredException)
            {
                tokeValidationResult.IsValid = false;
                tokeValidationResult.ErrorMessage = "Token has expired";
            }
            catch (Exception ex)
            {
                tokeValidationResult.IsValid = false;
                tokeValidationResult.ErrorMessage = $"Token validation failed: {ex.Message}";
            }

            return tokeValidationResult;
        }
    }

    public class EventGridEvent
    {
        public string Id { get; set; }
        public string EventType { get; set; }
        public DateTime EventTime { get; set; }
        public object Data { get; set; }
        public string Topic { get; set; }
        public string Subject { get; set; }
    }

    public class SubscriptionValidationEventData
    {
        [JsonProperty("validationCode")]
        public string ValidationCode { get; set; }
    }

    public class BlobCreatedEventData
    {
        public string Api { get; set; }
        public string ClientRequestId { get; set; }
        public string RequestId { get; set; }
        public string ETag { get; set; }
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public string BlobType { get; set; }
        public string Url { get; set; }
        public string Sequencer { get; set; }
        public StorageDiagnostics StorageDiagnostics { get; set; }
    }


    public class EventData
    {
        public string Api { get; set; }
        public string ClientRequestId { get; set; }
        public string RequestId { get; set; }
        public string ETag { get; set; }
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public string BlobType { get; set; }
        public string Url { get; set; }
        public string Sequencer { get; set; }
        public StorageDiagnostics StorageDiagnostics { get; set; }
    }

    public class StorageDiagnostics
    {
        public string BatchId { get; set; }
    }


    public class PipelineBackgroundService : BackgroundService
    {
        private readonly ILogger<PipelineBackgroundService> _logger;
        private readonly ConcurrentQueue<PipelineRequest> _pipelineQueue = new();
        private readonly ConcurrentDictionary<Guid, PipelineRequest> _runningPipelines = new();
        private readonly ConcurrentBag<PipelineRequest> _completedPipelines = new();
        private readonly ConcurrentBag<PipelineExecutionLog> _executionLog = new();
        private ConcurrentQueue<PipelineRequest> _waitingQueue = new ConcurrentQueue<PipelineRequest>();

        private  SemaphoreSlim _taskThrottle;
        private  SemaphoreSlim _parallelismThrottle;

        public PipelineBackgroundService(ILogger<PipelineBackgroundService> logger, int maxParallelTasks, int maxParallelSteps)
        {
            _logger = logger;
            _taskThrottle = new SemaphoreSlim(maxParallelTasks);
            _parallelismThrottle = new SemaphoreSlim(maxParallelSteps);
        }

        public void SetMaxParallel(int maxParallelTasks, int maxParallelSteps)
        {
            //_taskThrottle = new SemaphoreSlim(maxParallelTasks);
            //_parallelismThrottle = new SemaphoreSlim(maxParallelSteps);
        }

        public void EnqueuePipeline(PipelineRequest request)
        {

            try
            {
                // Check for queued pipelines with the same FlowId
                var waitingQueued = _waitingQueue.FirstOrDefault(q => q.Steps.Any(s => s.FlowId == request.Steps[0].FlowId));
                if (waitingQueued != null)
                {
                    // Modify the waiting pipeline to include the first step of the new request
                    waitingQueued.Steps.Insert(0, request.Steps[0]);
                    _logger.LogInformation($"Modified waiting pipeline {waitingQueued.Key} with new step.");
                    return;
                }


                var pipelineQueued = _pipelineQueue.FirstOrDefault(q => q.Steps.Any(s => s.FlowId == request.Steps[0].FlowId));
                if (pipelineQueued != null)
                {
                    // Modify the waiting pipeline to include the first step of the new request
                    pipelineQueued.Steps.Insert(0, request.Steps[0]);
                    _logger.LogInformation($"Modified Queued pipeline {pipelineQueued.Key} with new step.");
                    return;
                }


                // Check for running pipelines with the same FlowId, if yes add the request to the waiting queue
                var existingRunning = _runningPipelines.Values
                    .FirstOrDefault(r => r.Steps.Any(s => s.FlowId == request.Steps[0].FlowId));
                if (existingRunning != null)
                {
                    _waitingQueue.Enqueue(request);
                    _logger.LogInformation($"Pipeline Added to the waiting queue");
                    return;
                }

                // If no running or queued duplicates, enqueue normally
                _pipelineQueue.Enqueue(request);
                _logger.LogInformation("Successfully enqueued new pipeline request with key {PipelineKey}", request.Key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue pipeline request {PipelineKey}: {ErrorMessage}", request.Key, ex.Message);
                // Consider whether to rethrow the exception or handle it silently depending on the business logic
            }


        }

        private async Task ProcessWaitingQueue(CancellationToken stoppingToken)
        {
            // Use the updated activeFlowIds to check only non-completed flow IDs
            var activeFlowIds = new HashSet<int>(_runningPipelines.Values.SelectMany(p => p.Steps).Where(s => s.Status != "Done").Select(s => s.FlowId));
            var eligibleForProcessing = new List<PipelineRequest>();

            // First, identify eligible requests without modifying the queue
            foreach (var request in _waitingQueue)
            {
                // Check if the first step's FlowId is not in any active pipelines
                if (!activeFlowIds.Contains(request.Steps[0].FlowId))
                {
                    eligibleForProcessing.Add(request);
                }
            }

            // Then, process eligible requests
            foreach (var request in eligibleForProcessing)
            {
                bool removed = false;
                // Since ConcurrentQueue does not support removing arbitrary elements directly, simulate this by replacing the queue
                lock (_waitingQueue)
                {
                    var tempQueue = new ConcurrentQueue<PipelineRequest>();
                    PipelineRequest dequeuedRequest;
                    while (_waitingQueue.TryDequeue(out dequeuedRequest))
                    {
                        if (!eligibleForProcessing.Contains(dequeuedRequest) || removed)
                        {
                            tempQueue.Enqueue(dequeuedRequest);
                        }
                        else
                        {
                            // Log and enqueue only the first eligible instance to avoid duplicates
                            _pipelineQueue.Enqueue(dequeuedRequest);
                            _logger.LogInformation($"Moved pipeline from waiting to active queue for FlowId {dequeuedRequest.Steps[0].FlowId}");
                            removed = true; // Ensure only the first match is moved
                        }
                    }
                    _waitingQueue = tempQueue;
                }
            }

            // Optionally, delay to prevent tight loops or excessive CPU usage
            await Task.Delay(1000, stoppingToken);
        }

        public IEnumerable<PipelineRequest> GetWaitingPipelines()
        {
            return _waitingQueue.ToList();
        }

        public IEnumerable<PipelineRequest> GetQueuedPipelines()
        {
            _logger.LogInformation($"Fetching queued pipelines. Current queue size: {_pipelineQueue.Count}");
            var queueSnapshot = _pipelineQueue.ToList();
            if (!queueSnapshot.Any())
            {
                _logger.LogWarning("Queue is empty at the time of request.");
            }
            return queueSnapshot;
        }

        public IEnumerable<object> GetRunningPipelines()
        {
            _logger.LogInformation("Fetching running pipelines.");
            return _runningPipelines.Select(p => new
            {
                PipelineId = p.Key,
                Steps = p.Value.Steps.Select(s => new { s.PipelineId, s.FlowId, s.Step, s.FileName, s.Status })
            });
        }

        public (IEnumerable<PipelineRequest> Queued, IEnumerable<PipelineRequest> Running, IEnumerable<PipelineExecutionLog> Executed) GetStatus()
        {
            return (_pipelineQueue.ToList(), _runningPipelines.Values.ToList(), _executionLog.ToList());
        }


        public void ClearAllPipelines()
        {
            _pipelineQueue.Clear(); // Clear all items in the queue
            //_runningPipelines.Clear(); // Clear all items in the running pipelines
            _completedPipelines.Clear(); // Optionally clear completed pipelines, if needed
            _executionLog.Clear(); // Clear execution logs

            _logger.LogInformation("All pipelines and logs have been cleared.");
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_pipelineQueue.TryDequeue(out var pipelineRequest))
                    {
                        // Add a delay of 2-3 seconds before moving the pipeline to running state
                        //var delay = new Random().Next(10, 100);
                        //await Task.Delay(delay, stoppingToken);

                        await _taskThrottle.WaitAsync(stoppingToken);

                        _runningPipelines.TryAdd(pipelineRequest.Key, pipelineRequest);
                        _ = ProcessPipelineAsync(pipelineRequest.Key, pipelineRequest, stoppingToken)
                            .ContinueWith(task =>
                            {
                                var log = new PipelineExecutionLog
                                {
                                    PipelineId = pipelineRequest.Key,
                                    Request = pipelineRequest, // Save full request details
                                    Timestamp = DateTime.UtcNow,
                                    Result = task.Status == TaskStatus.RanToCompletion ? "Completed successfully" :
                                        task.IsFaulted ? $"Faulted: {task.Exception?.GetBaseException().Message}" :
                                        "Cancelled"
                                };

                                _executionLog.Add(log);
                                _runningPipelines.TryRemove(pipelineRequest.Key, out _);
                                _taskThrottle.Release();
                            });
                    }

                    await Task.Delay(10, stoppingToken); // Adjust as needed
                }
                catch (OperationCanceledException)
                {
                    // Handle cancellation request
                    _logger.LogInformation("Pipeline execution canceled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during pipeline execution: {Message}", ex.Message);
                    // Handle the exception or perform any necessary cleanup
                }
            }
        }

        private async Task ProcessPipelineAsync(Guid pipelineId, PipelineRequest pipelineRequest, CancellationToken stoppingToken)
        {
            try
            {
                // Group and order steps by FlowId and Step to manage dependencies within a flow
                var flows = pipelineRequest.Steps
                    .GroupBy(s => s.FlowId)
                    .Select(g => new { FlowId = g.Key, Steps = g.OrderBy(s => s.Step).ToList() })
                    .ToList();

                foreach (var flow in flows)
                {
                    for (int i = 0; i < flow.Steps.Count;)
                    {
                        var currentStepNumber = flow.Steps[i].Step;
                        var stepsToExecute = flow.Steps.Where(s => s.Step == currentStepNumber).ToList();

                        // Execute all steps with the same Step number in parallel
                        var tasks = stepsToExecute.Select(step => Task.Run(async () =>
                        {
                            await _parallelismThrottle.WaitAsync(stoppingToken);
                            try
                            {
                                step.Status = "Executing";
                                _logger.LogInformation($"Start processing Step: {step.Step}, FlowId: {step.FlowId}, FileName: {step.FileName}");
                                MemoryStream memoryStream = new MemoryStream();
                                StreamWriter writer = new StreamWriter(memoryStream);
                                ExecFlowProcess.Exec(writer, Environment.GetEnvironmentVariable("SQLFlowConStr"), step.FlowId, "Event", 1, step.FileName);
                                //await Task.Delay(9000, stoppingToken); // Simulate work
                                step.Status = "Done";
                                _logger.LogInformation($"Completed processing Step: {step.Step}, FileName: {step.FileName}");
                            }
                            catch (Exception ex)
                            {
                                step.Status = $"Failed: {ex.Message}";
                                _logger.LogError($"Error in Step: {step.Step}, FileName: {step.FileName}, Error: {ex.Message}");
                                throw;
                            }
                            finally
                            {
                                _parallelismThrottle.Release();
                            }
                        }, stoppingToken)).ToList();

                        try
                        {
                            await Task.WhenAll(tasks);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error in pipeline {pipelineId}: {ex.Message}. Aborting pipeline.");

                            // Update the status of remaining steps in the current pipeline as "Aborted"
                            foreach (var step in flow.Steps.Skip(i))
                            {
                                step.Status = "Aborted";
                            }

                            // Rethrow the exception to abort the current pipeline
                            throw;
                        }

                        i += stepsToExecute.Count;
                    }

                    await ProcessWaitingQueue(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Pipeline {pipelineId} aborted due to an error: {ex.Message}");

                // Update the status of the pipeline in the execution log
                var log = _executionLog.FirstOrDefault(l => l.PipelineId == pipelineId);
                if (log != null)
                {
                    log.Result = $"Aborted: {ex.Message}";
                }
            }
        }


    }

    public class PipelineStep
    {
        internal Guid PipelineId { get; set; }  // Parent Pipeline GUID
        public int FlowId { get; set; }
        public int Step { get; set; }
        public string FileName { get; set; }
        internal string Status { get; set; } = "Queued"; // Default status
    }

    public class PipelineRequest
    {
        internal Guid Key { get; set; }
        public List<PipelineStep> Steps { get; set; }

    }

    public class PipelineExecutionLog
    {
        public Guid PipelineId { get; set; }
        public PipelineRequest Request { get; set; } // Holds full request details
        public string Result { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    
    
}
