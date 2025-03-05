#nullable enable
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using SQLFlowApi.RequestType;
using Microsoft.AspNetCore.Http.Features;
using SQLFlowApi.Models;
using SQLFlowCore.ExecParams;
using Microsoft.AspNetCore.Identity;
using SQLFlowApi.Services;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.SqlServer.Types;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Pipeline;

namespace SQLFlowApi.Controllers
{
    [Route("/api/")]
    [ApiController]

    /// <summary>
    /// The SQLFlowController is a controller class in the SQLFlowApi. It provides endpoints for executing SQL flows, 
    /// processing batches, generating lineage maps, managing source control, performing health checks, executing assertions, 
    /// and generating system documentation. It also provides endpoints for handling GET and POST requests for each operation.
    /// </summary>
    /// <remarks>
    /// This controller is decorated with the ApiController attribute, which indicates that it responds to web API requests.
    /// It uses dependency injection to receive an instance of ILogger and IConfiguration. 
    /// The connection string for the SQLFlow service is retrieved from an environment variable named "SQLFlowConStr".
    /// </remarks>
    public class OperationsController : ControllerBase
    {
        private readonly ILogger<OperationsController> _logger;
        private string _rowString;
        private string _conStr = "";
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly TokenService _tokenService;

        private static Dictionary<string, List<CancellationTokenSource>> _tokens = new Dictionary<string, List<CancellationTokenSource>>();


        #region ExecLineageLookupJson
        /// <summary>
        /// Gets the lineage data in JSON format for D3 visualization
        /// </summary>
        /// <param name="flowID">The ID of the flow to lookup</param>
        /// <param name="allDep">Include all dependencies</param>
        /// <param name="allBatches">Include all batches</param>
        /// <param name="dir">Direction: "A" for ancestors, "D" for descendants</param>
        /// <returns>JSON object containing nodes and edges</returns>
        [HttpGet("json")]
        public IActionResult GetJson(string flowID = "0", bool allDep = false, bool allBatches = false, string dir = "A")
        {
            try
            {
                string result = ExecLineageLookup.ExecJson(_conStr, flowID, allDep, allBatches, dir);
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        #endregion


        #region MatchKey
        [HttpGet("ExecMatchKey")]
        public async Task<IActionResult> ExecMatchKeyGet([FromQuery] MatchKeyRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();
            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";
            // Ensure the response starts immediately without buffering
            await response.StartAsync();
            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();
                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await ExecMatchKey(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }
            // Flushing the final content to the response
            await Response.Body.FlushAsync();

            return new EmptyResult();
        }

        private async Task<string> ExecMatchKey(StreamWriter sw, string conStr, MatchKeyRequest request)
        {
            string result = "";

            try
            {
                Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                _totalTime.Start();

                Task t = Task.Run(() =>
                {
                    string _result = string.Empty;
                    var sqlFlowParam = new SQLFlowCore.Common.SqlFlowParam()
                    {
                        batch = request?.Batch ?? string.Empty,
                        flowId = int.TryParse(request?.FlowId, out int flowId) ? flowId : 0,
                        matchKeyId = int.TryParse(request?.MatchKeyId, out int matchKeyId) ? matchKeyId : 0,
                        dbg = int.TryParse(request?.Dbg, out int dbg) ? dbg : 0,
                        batchId = "0",
                        sqlFlowConString = conStr?.Trim() ?? string.Empty
                    };
                    _result = SQLFlowCore.Pipeline.ExecMatchKey.Exec(sqlFlowParam);
                    result = _result;
                });

                await t;

                await sw.WriteAsync(result);
                await sw.FlushAsync();

                _totalTime.Stop();
                await sw.WriteLineAsync($"Total processing time: {_totalTime.ElapsedMilliseconds / 1000} seconds");
                await sw.FlushAsync();
            }
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                sw.Close();
            }
            return result;
        }
        #endregion MatchKey
        

        /// <summary>
        /// Initializes a new instance of the <see cref="SQLFlowController"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration, injected by the ASP.NET Core IoC container.</param>
        /// <param name="logger">The logger service, injected by the ASP.NET Core IoC container.</param>
        /// <remarks>
        /// The constructor initializes the logger service and sets the connection string for the SQLFlow service.
        /// The connection string is retrieved from an environment variable named "SQLFlowConStr".
        /// </remarks>
        public OperationsController(IConfiguration configuration, SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, TokenService tokenService, ILogger<OperationsController> logger)
        {
            _logger = logger;
            _rowString = "";
            _signInManager = signInManager;
            _userManager = userManager;
            _tokenService = tokenService;
            _conStr = Environment.GetEnvironmentVariable("SQLFlowConStr") ??
                      string.Empty; //configuration.GetConnectionString("SQLFlowConStr") ?? string.Empty;
        }

        #region Batch
        /// <summary>
        /// Executes a POST request to process a batch of SQL flows.
        /// </summary>
        /// <param name="request">An object of type FlowBatchRequest containing the details of the batch to be processed.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method writes the results of the SQL flows directly to the Response.Body stream.
        /// </remarks>
        [HttpPost("ExecFlowBatch")]
        public async Task ExecFlowBatchPost([FromBody] FlowBatchRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";
            using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true)
            { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQlFlowBatch(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
                
                // Flushing the final content to the response
                await Response.Body.FlushAsync();
                if (!string.IsNullOrEmpty(request.CallBackUri))
                {
                    await RunCallBack(request.CallBackUri, FinalResult);
                }
                
            }
        }

        /// <summary>
        /// Executes a GET request to process a batch of SQL flows.
        /// </summary>
        /// <param name="request">An object of type FlowBatchRequest containing the details of the batch to be processed.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method writes the results of the SQL flows directly to the Response.Body stream.
        /// </remarks>
        [HttpGet("ExecFlowBatch")]
        public async Task ExecFlowBatchGet([FromQuery] FlowBatchRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true)
            { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQlFlowBatch(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
                
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();

            if (!string.IsNullOrEmpty(request.CallBackUri))
            {
                await RunCallBack(request.CallBackUri, FinalResult);
            }
        }

        /// <summary>
        /// Executes a SQLFlow batch process.
        /// </summary>
        /// <param name="sw">The StreamWriter used for writing the output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="batch">The batch of SQLFlow commands to be executed.</param>
        /// <param name="flowtype">The type of the flow to be executed.</param>
        /// <param name="sysalias">The system alias for the batch execution.</param>
        /// <param name="execMode">The execution mode for the batch process.</param>
        /// <param name="dbg">The debug mode flag.</param>
        /// <returns>A string representing the result of the batch execution.</returns>
        /// <remarks>
        /// This method will execute a batch of SQLFlow commands based on the provided parameters. 
        /// If the batch or sysalias parameters are not provided, the method will write an error message and close the StreamWriter.
        /// </remarks>
        private async Task<string> SQlFlowBatch(StreamWriter sw, string conStr, FlowBatchRequest request)
        {
            string rValue = "";
            await sw.WriteLineAsync("Executing SQLFlow batch process...");

            var cts = new CancellationTokenSource();

            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }

            try
            {
                if (request.Batch.Length > 0 || request.Sysalias.Length > 0)
                {
                    Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                    _totalTime.Start();

                    Task t = Task.Run(() =>
                    {
                        // This Task.Run should also respect cancellation
                        cts.Token.ThrowIfCancellationRequested(); // Throws OperationCanceledException if cancellation requested

                        // Replace this with actual call to SQLFlowCore.Engine.ExecFlowBatch.Exec
                        rValue = ExecFlowBatch.Exec(sw, conStr, request.Batch, request.FlowType, request.Sysalias, request.ExecMode, int.Parse(request.Dbg));
                    });

                    await KeepAliveWithCancel(t, sw, request.cancelTokenId);
                    await t.ContinueWith(_ => {
                        _totalTime.Stop();
                        writeProcTime(sw, (_totalTime.ElapsedMilliseconds / 1000).ToString());
                    });
                }
                else
                {
                    await sw.WriteAsync("Parameter batch or sysalias is required");
                    await sw.FlushAsync();
                    sw.Close();
                }
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                rValue = "Operation was cancelled"; // Or handle as needed
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }

            return rValue;
        }
        #endregion Batch

        #region Node
        /// <summary>
        /// Executes a specific node in the SQL flow.
        /// </summary>
        /// <param name="request">A request object containing the details of the node to be executed.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method receives a POST request and responds with text/plain content type.
        /// The response buffering is disabled for this method.
        /// </remarks>
        [HttpPost("ExecFlowNode")]
        public async Task ExecFlowNodePost([FromBody] FlowNodeRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";

            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {

                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQlFlowNode(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
                
                // Flushing the final content to the response
                await Response.Body.FlushAsync();

                if (!string.IsNullOrEmpty(request.CallBackUri))
                {
                    await RunCallBack(request.CallBackUri, FinalResult);
                }
            }
        }

        /// <summary>
        /// Executes a SQL Flow Node and returns the result as a HTTP GET response.
        /// </summary>
        /// <param name="request">A FlowNodeRequest object that contains the parameters for the SQL Flow Node execution.</param>
        /// <returns>An IActionResult that represents the result of the SQL Flow Node execution.</returns>
        /// <remarks>
        /// This method sets the Response ContentType to "text/plain" and writes the result of the SQL Flow Node execution to the Response Body.
        /// </remarks>
        [HttpGet("ExecFlowNode")]
        public async Task<IActionResult> ExecFlowNodeGet([FromQuery] FlowNodeRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            string FinalResult = "";
            
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQlFlowNode(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            
            if (!string.IsNullOrEmpty(request.CallBackUri))
            {
                await RunCallBack(request.CallBackUri, FinalResult);
            }
            
            return new EmptyResult();
        }

        /// <summary>
        /// Executes a SQL Flow Node operation.
        /// </summary>
        /// <param name="sw">The StreamWriter used for writing the output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="_node">The node to be executed.</param>
        /// <param name="_dir">The direction of the execution.</param>
        /// <param name="_batch">The batch to be executed.</param>
        /// <param name="exitOnError">Indicates whether the execution should stop on error.</param>
        /// <param name="fetchAllDep">Indicates whether all dependencies should be fetched.</param>
        /// <param name="_execMode">The execution mode.</param>
        /// <param name="_dbg">The debug level.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains the final result of the operation.</returns>
        public static async Task<string> SQlFlowNode(StreamWriter sw, String conStr, FlowNodeRequest request)
        {
            var _sw = sw;

            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }

            string result = "";

            try
            {
                if (request.Node.Length > 0)
                {
                    Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                    _totalTime.Start();
                    ExecFlowNode.InvokeIsRunning += (sender, e) => InvokeIsRunning(sender, e, sw);
                    Task t = Task.Run(() =>
                    {
                        // This Task.Run should also respect cancellation
                        cts.Token.ThrowIfCancellationRequested(); // Throws OperationCanceledException if cancellation requested
                        //SQLFlowCore.Engine.ExecFlowBatch.OnBatchDone += (sender, e) => ExecFlowBatch_OnBatchDone(sender, e, _stream, sw);
                        //SQLFlowCore.Engine.ExecFlowBatch.OnFlowProcessed += (sender, e) => ExecFlowBatch_OnFlowProcessed(sender, e, sw);
                        result = ExecFlowNode.Exec(_sw, conStr, request.Node, request.Dir, request.Batch, request.GetExitOnErrorBool(), request.GetFetchAllDepBool(), true, int.Parse(request.Dbg));
                    });

                    await KeepAliveWithCancel(t, _sw, request.cancelTokenId);
                    Task.WaitAll(t);
                    await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
                }
                else
                {
                    result = "Parameter node and dir is required";
                    _sw.Write(result);
                    await _sw.FlushAsync();
                    _sw.Close();
                    _sw.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                result = "Operation was cancelled"; // Or handle as needed
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }


            
            return result;
        }

        #endregion Node

        #region Flow
        /// <summary>
        /// Executes the flow process for a given request.
        /// </summary>
        /// <param name="request">The request containing the flow process parameters.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method receives a POST request and responds with plain text.
        /// It disables response buffering for performance optimization.
        /// </remarks>
        [HttpPost("ExecFlowProcess")]
        public async Task ExecFlowProcessPost([FromBody] FlowProcessRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await ExecFlowProcess(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }

                // Flushing the final content to the response
                await Response.Body.FlushAsync();
                if (!string.IsNullOrEmpty(request.CallBackUri))
                {
                    await RunCallBack(request.CallBackUri, FinalResult);
                }
            }
        }

        /// <summary>
        /// Executes the flow process based on the provided request parameters.
        /// </summary>
        /// <param name="request">An object of type FlowProcessRequest containing the parameters for the flow process.</param>
        /// <returns>An IActionResult representing the result of the flow process execution.</returns>
        /// <remarks>
        /// This method is accessible via HTTP GET requests.
        /// </remarks>
        [HttpGet("ExecFlowProcess")]
        public async Task<IActionResult> ExecFlowProcessGet([FromQuery] FlowProcessRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            string finalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();
                if (validateRequest.UserAuthenticated)
                {
                    await ExecFlowProcess(streamWriter, _conStr, request);
                }
                else
                {
                    finalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            if (!string.IsNullOrEmpty(request.CallBackUri))
            {
                await RunCallBack(request.CallBackUri, finalResult);
            }
            
            return new EmptyResult();
        }

        /// <summary>
        /// Executes a flow process.
        /// </summary>
        /// <param name="sw">The StreamWriter used to write the output of the process.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="flowid">The ID of the flow to be executed.</param>
        /// <param name="execMode">The execution mode of the flow process.</param>
        /// <param name="dbg">Debug mode flag. Set to "1" to enable debug mode.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the final result of the flow process.</returns>
        /// <exception cref="Exception">Thrown when an error occurs during the execution of the flow process.</exception>
        /// <remarks>
        /// This method is private and is used by the public methods ExecFlowProcessPost and ExecFlowProcessGet to execute a flow process.
        /// </remarks>
        private async Task<string> ExecFlowProcess(StreamWriter sw, string conStr, FlowProcessRequest request)
        {
            var _sw = sw;
            string result = "";
            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }
            
            try
            {
                if (request.FlowId != "0")
                {
                    Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                    _totalTime.Start();

                    SQLFlowCore.Pipeline.ExecFlowProcess.OnRowsCopied += OnRowsCopied;

                    Task t = Task.Run(() =>
                    {
                        // This Task.Run should also respect cancellation
                        cts.Token.ThrowIfCancellationRequested();
                        result = SQLFlowCore.Pipeline.ExecFlowProcess.Exec(_sw, conStr.Trim(), int.Parse(request.FlowId), request.ExecMode, int.Parse(request.Dbg), request.SrcFileWithPath);
                    });

                    await KeepAliveWithCancel(t, _sw, request.cancelTokenId);

                    Task.WaitAll(t);
                    await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
                }
                else
                {
                    await sw.WriteLineAsync("Parameter flowid is required");
                }

                await sw.FlushAsync();
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                await sw.WriteLineAsync($"Operation was cancelled");
            }
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                sw.Close();
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }

            return result;
        }
        #endregion Flow

        #region LineageMap
        /// <summary>
        /// Executes the lineage map operation with the provided request data.
        /// </summary>
        /// <param name="request">The request data for the lineage map operation. It includes parameters like 'All', 'Alias', 'ExecMode', 'Threads', 'Dbg', and 'CallBackUri'.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method receives a POST request and responds with 'text/plain' content type. It disables response buffering to allow for large data processing.
        /// </remarks>
        [HttpPost("ExecLineageMap")]
        public async Task ExecLineageMapPost([FromBody] LineageMapRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";

            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQLFlowLineage(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }

                // Flushing the final content to the response
                await Response.Body.FlushAsync();

                await RunCallBack(request.CallBackUri, FinalResult);

            }
        }

        /// <summary>
        /// Executes the Lineage Map operation with the provided parameters.
        /// </summary>
        /// <param name="request">An object of type LineageMapRequest containing the parameters for the Lineage Map operation.</param>
        /// <returns>An IActionResult that represents the result of the Lineage Map operation.</returns>
        /// <remarks>
        /// This method is a HTTP GET endpoint and it responds with a plain text content. 
        /// It uses a StreamWriter to write the response to the Response.Body stream.
        /// </remarks>
        [HttpGet("ExecLineageMap")]
        public async Task<IActionResult> ExecLineageMapGet([FromQuery] LineageMapRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            
            string FinalResult = "";
            
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    await SQLFlowLineage(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            if (!string.IsNullOrEmpty(request.CallBackUri))
            {
                await RunCallBack(request.CallBackUri, FinalResult);
            }
            return new EmptyResult();
        }

        /// <summary>
        /// Executes the SQL Flow Lineage process.
        /// </summary>
        /// <param name="sw">A StreamWriter object to write the output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="all">A string representing whether to execute all processes or not.</param>
        /// <param name="alias">The alias for the process.</param>
        /// <param name="execMode">The execution mode for the process.</param>
        /// <param name="noOfThreads">The number of threads to use for the process.</param>
        /// <param name="dbg">A string representing whether to debug the process or not.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains a string that represents the result of the process.</returns>
        private async Task<string> SQLFlowLineage(StreamWriter sw, string conStr, LineageMapRequest request)
        {
            string rValue = "";
            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }
            
            try
            {
                Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                _totalTime.Start();

                Task t = Task.Run(() =>
                {
                    // This Task.Run should also respect cancellation
                    cts.Token.ThrowIfCancellationRequested();
                    rValue = ExecLineageMap.Exec(sw, conStr, request.All, request.Alias, request.ExecMode, int.Parse(request.Threads), int.Parse(request.Dbg));
                });

                await KeepAliveWithCancel(t, sw, request.cancelTokenId);
                await t;
                await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                rValue = "Operation was cancelled"; // Or handle as needed
            }
            catch (Exception ex)
            {
                rValue = ex.Message;
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                sw.Close();
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }

            return rValue;

        }
        #endregion LineageMap




        #region SourceControl
        /// <summary>
        /// Executes the source control operation based on the provided request.
        /// </summary>
        /// <param name="request">An object of type SourceControlRequest that contains the parameters for the source control operation.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method responds to a POST request at the "ExecSourceControl" endpoint.
        /// It disables response buffering and writes the result of the source control operation to the response body.
        /// </remarks>
        [HttpPost("ExecSourceControl")] 
        public async Task ExecSourceControlPost([FromBody] SourceControlRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();

            string FinalResult = "";

            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();
                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQLFlowSourceControl(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }

                // Flushing the final content to the response
                await Response.Body.FlushAsync();

                if (!string.IsNullOrEmpty(request.CallBackUri))
                {
                    await RunCallBack(request.CallBackUri, FinalResult);
                }
            }

        }

        /// <summary>
        /// Handles the HTTP GET request for executing source control operations.
        /// </summary>
        /// <param name="request">An object of type SourceControlRequest containing the parameters for the source control operation.</param>
        /// <returns>An IActionResult that represents the result of the operation. The content of the response is plain text.</returns>
        [HttpGet("ExecSourceControl")]
        public async Task<IActionResult> ExecSourceControlGet([FromQuery] SourceControlRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            

            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await SQLFlowSourceControl(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
                
                
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            if (!string.IsNullOrEmpty(request.CallBackUri))
            {
                await RunCallBack(request.CallBackUri, FinalResult);
            }
            return new EmptyResult();
        }

        /// <summary>
        /// Executes the source control process for SQLFlow.
        /// </summary>
        /// <param name="sw">The StreamWriter used to write the output of the process.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="scalias">The alias for the source control.</param>
        /// <param name="batch">The batch identifier.</param>
        /// <returns>A string indicating the result of the process. If the process completes successfully, it returns "Process completed successfully". If an error occurs, it returns the error message.</returns>
        /// <exception cref="Exception">Throws an exception if an error occurs during the process.</exception>
        private async Task<string> SQLFlowSourceControl(StreamWriter sw, string conStr, SourceControlRequest request)
        {
            string rvalue = "";

            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }
            
            try
            {
                Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                _totalTime.Start();

                Task t = Task.Run(() =>
                {
                    // This Task.Run should also respect cancellation
                    cts.Token.ThrowIfCancellationRequested();
                    ExecSourceControl.Exec(sw, conStr, request.Scalias, request.Batch);
                    rvalue = "Process completed successfully.";
                });

                await KeepAliveWithCancel(t, sw, request.cancelTokenId);

                Task.WaitAll(t);
                await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                rvalue = "Operation was cancelled"; // Or handle as needed
            }
            catch (Exception ex)
            {
                rvalue = ex.Message;
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
                sw.Close();
            }

            return rvalue;
        }
        #endregion SourceControl   

        #region HealthCheck
        /// <summary>
        /// Executes a health check on the SQL Flow system.
        /// </summary>
        /// <param name="request">A <see cref="HealthCheckRequest"/> object containing the parameters for the health check.</param>
        /// <returns>An <see cref="IActionResult"/> that represents the result of the health check operation.</returns>
        /// <remarks>
        /// This method performs a health check by executing the `ExecHealthCheck` method with the parameters specified in the `HealthCheckRequest` object.
        /// The result of the health check is written to the response body.
        /// </remarks>
        [HttpGet("ExecHealthCheck")]
        public async Task<IActionResult> ExecHealthCheckGet([FromQuery] HealthCheckRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            
            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await ExecHealthCheck(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            
            return new EmptyResult();
        }

        /// <summary>
        /// Executes a health check on the SQLFlow process.
        /// </summary>
        /// <param name="sw">A StreamWriter object used for writing output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="flowid">The ID of the flow to check.</param>
        /// <param name="runModelSelection">The model selection to run.</param>
        /// <param name="dbg">Debug mode flag.</param>
        /// <returns>A Task that represents the asynchronous operation, containing the result of the health check.</returns>
        private async Task<string> ExecHealthCheck(StreamWriter sw, string conStr, HealthCheckRequest request)
        {
            var _sw = sw;
            string result = "";
            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }
            
            try
            {
                if (request.FlowId.Length > 0)
                {
                    Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                    _totalTime.Start();

                    SQLFlowCore.Pipeline.ExecFlowProcess.OnRowsCopied += OnRowsCopied;

                    var sqlFlowParam = new SQLFlowCore.Common.SqlFlowParam()
                    {
                        flowId = int.TryParse(request?.FlowId, out int flowId) ? flowId : 0,
                        dbg = 1,
                        sqlFlowConString = conStr?.Trim() ?? string.Empty
                    };
                    
                    Task t = Task.Run(() =>
                    {
                        // This Task.Run should also respect cancellation
                        cts.Token.ThrowIfCancellationRequested();
                        result = SQLFlowCore.Pipeline.ExecHealthCheck.Exec(_sw, conStr.Trim(), int.Parse(request.FlowId), int.Parse(request.RunModelSelection), int.Parse(request.dbg));
                    });

                    await KeepAliveWithCancel(t, _sw, request.cancelTokenId);

                    Task.WaitAll(t);
                    await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
                }
                else
                {
                    await sw.WriteLineAsync("Parameter flowid is required");
                }

                await sw.FlushAsync();
            }
            catch (OperationCanceledException)
            {
                await sw.WriteLineAsync("Operation was cancelled");
            }
            
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose();
                sw.Close();
            }

            return result;
        }
        #endregion HealthCheck

        #region Assertion
        /// <summary>
        /// Executes an assertion operation based on the provided request parameters.
        /// </summary>
        /// <param name="request">An object of type AssertionRequest which includes the FlowId and a debug flag.</param>
        /// <returns>An IActionResult that represents the result of the assertion operation.</returns>
        /// <remarks>
        /// This method is a HTTP GET endpoint in the SQLFlow API. It sets the response content type to 'text/plain' and 
        /// uses the ExecAssertionMethod to perform the assertion operation. The final result is flushed to the response body.
        /// </remarks>
        [HttpGet("ExecAssertion")]
        public async Task<IActionResult> ExecAssertionGet([FromQuery] AssertionRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    FinalResult = await ExecAssertionMethod(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            
            return new EmptyResult();
        }

        /// <summary>
        /// Executes the assertion method for the SQL Flow.
        /// </summary>
        /// <param name="sw">The StreamWriter used to write the output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="flowid">The ID of the flow to execute.</param>
        /// <param name="dbg">The debug parameter.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains the execution result as a string.</returns>
        private async Task<string> ExecAssertionMethod(StreamWriter sw, string conStr, AssertionRequest request)
        {
            var _sw = sw;
            string result = "";
            var cts = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }

            try
            {
                if (request.FlowId.Length > 0)
                {
                    Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                    _totalTime.Start();

                    SQLFlowCore.Pipeline.ExecFlowProcess.OnRowsCopied += OnRowsCopied;

                    Task t = Task.Run(() =>
                    {
                        // This Task.Run should also respect cancellation
                        cts.Token.ThrowIfCancellationRequested();
                        result = ExecAssertion.Exec(_sw, conStr.Trim(), int.Parse(request.FlowId), int.Parse(request.dbg));
                    });

                    await KeepAliveWithCancel(t, _sw, request.cancelTokenId);

                    Task.WaitAll(t);
                    await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
                }
                else
                {
                    await sw.WriteLineAsync("Parameter flowid is required");
                }

                await sw.FlushAsync();
            }
            catch (OperationCanceledException)
            {
                await sw.WriteLineAsync("Operation was cancelled");
            }
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Clean up
                }
                cts.Dispose();
                sw.Close();
            }

            return result;
        }
        #endregion Assertion

        #region ExecSysDocGen
        /// <summary>
        /// Handles the HTTP GET request for generating System Object Details
        /// </summary>
        /// <param name="request">The request parameters for System Object Detail Generation.</param>
        /// <returns>An IActionResult that produces a plain text response.</returns>
        [HttpGet("ExecSysDocGen")]
        public async Task<IActionResult> ExecSysDocGenGet([FromQuery] SysDocGenRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            
            string FinalResult = "";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    await ExecSysDocGen(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
                
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();

            return new EmptyResult();
        }

        /// <summary>
        /// Executes the system documentation generation process.
        /// </summary>
        /// <param name="sw">The StreamWriter used to write the output of the process.</param>
        /// <param name="conStr">The connection string used in the process.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains a string that represents the result of the process.</returns>
        /// <exception cref="Exception">Thrown when an error occurs during the execution of the process.</exception>
        private async Task<string> ExecSysDocGen(StreamWriter sw, string conStr, SysDocGenRequest request)
        {
            var _sw = sw;
            string result = "";

            try
            {
                Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                _totalTime.Start();

                SQLFlowCore.Pipeline.ExecFlowProcess.OnRowsCopied += OnRowsCopied;

                Task t = Task.Run(() =>
                {
                    SQLFlowCore.Pipeline.ExecSysDocGen.Exec(_sw, conStr.Trim(), request?.Objectname);
                });

                await KeepAlive(t, _sw);

                Task.WaitAll(t);
                await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });

                await sw.FlushAsync();
            }
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                sw.Close();
            }

            return result;
        }
        #endregion ExecSysDocGen 

        #region ExecSysDocPrompt
        /// <summary>
        /// Executes the system documentation prompt request.
        /// </summary>
        /// <param name="request">The system documentation prompt request parameters.</param>
        /// <returns>An IActionResult that represents an asynchronous operation.</returns>
        /// <remarks>
        /// This method handles HTTP POST requests at the path "/api/ExecSysDocPrompt". 
        /// It writes the result of the system documentation prompt execution to the response body.
        /// </remarks>
        [HttpPost("ExecSysDocPrompt")]
        public async Task<IActionResult> ExecSysDocPromptGet([FromBody] [FromQuery] SysDocPromptRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            string FinalResult = "";
            
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    await ExecSysDocPrompt(streamWriter, _conStr, request);
                }
                else
                {
                    FinalResult = validateRequest.ErrorMessage;
                    streamWriter.Write(validateRequest.ErrorMessage);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();

            return new EmptyResult();
        }
        
        /// <summary>
        /// Executes the system documentation prompt operation.
        /// </summary>
        /// <param name="sw">The StreamWriter object to write the output.</param>
        /// <param name="conStr">The connection string to the database.</param>
        /// <param name="request">The SysDocPromptRequest object containing the parameters for the system documentation prompt operation.</param>
        /// <returns>A Task that represents the asynchronous operation. The value of the TResult parameter contains a string that represents the result of the operation.</returns>
        private async Task<string> ExecSysDocPrompt(StreamWriter sw, string conStr, SysDocPromptRequest request)
        {
            var _sw = sw;
            string result = "";
            try
            {
                Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                _totalTime.Start();

                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    OpenAIPayLoad PayLoad = new OpenAIPayLoad
                    {
                        model = request.Model,
                        max_tokens = request.Max_tokens,
                        temperature = request.Temperature,
                        top_p = request.Top_p,
                        frequency_penalty = request.Frequency_penalty,
                        presence_penalty = request.Presence_penalty,
                        prompt = request.Prompt
                    };

                    Task t = Task.Run(() =>
                    {
                        OpenAIPayLoad _PayLoad = PayLoad;
                        SQLFlowCore.Pipeline.ExecSysDocPrompt.Exec(_sw, conStr.Trim(), request.Objectname, request.UseDbPayload, _PayLoad);
                    });

                    await KeepAlive(t, _sw);

                    Task.WaitAll(t);
                    await t.ContinueWith(_ => { _totalTime.Stop(); writeProcTime(_sw, (_totalTime.ElapsedMilliseconds / 1000).ToString()); });
                    await sw.FlushAsync();
                }
                else
                {
                    result = validateRequest.ErrorMessage;
                    sw.Write(validateRequest.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                await sw.WriteLineAsync($"An error occurred: {ex.Message}");
            }
            finally
            {
                sw.Close();
            }

            return result;
        }
        #endregion ExecSysDocPrompt

        #region ExecTrgTblSchema

        [HttpGet("ExecTrgTblSchema")]
        public async Task<IActionResult> ExecTrgTblSchema([FromQuery] TrgTblSchemaRequest request)
        {
            Response.ContentType = "text/plain";
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                var validateRequest = await AuthRequest();

                if (validateRequest.UserAuthenticated)
                {
                    string result = "";

                    try
                    {
                        Stopwatch _totalTime = new System.Diagnostics.Stopwatch();
                        _totalTime.Start();

                        Task t = Task.Run(() =>
                        {
                            SQLFlowCore.Pipeline.ExecTrgTblSchema.Exec(streamWriter, _conStr, int.Parse(request.FlowId));
                        });
                        Task.WaitAll(t);

                        // streamWriter back to the start of the MemoryStream for reading
                        using var memoryStream = new MemoryStream();
                        memoryStream.Seek(0, SeekOrigin.Begin);
                        // Read the contents of the memory stream.
                        using (var streamReader = new StreamReader(memoryStream, Encoding.UTF8))
                        {
                            result = streamReader.ReadToEnd();
                        }
                        return Ok(result);
                    }
                    catch (Exception ex)
                    {
                        return BadRequest($"An error occurred: {ex.Message}");
                    }
                    finally
                    {
                        streamWriter.Close();
                    }
                }
                else
                {
                    return Unauthorized(validateRequest.ErrorMessage);
                }
            }

           
        }

        #endregion ExecTrgTblSchema

        #region DetectUniqueKey

        [HttpPost("ExecDetectUniqueKey")]
        public async Task ExecDetectUniqueKeyPost([FromBody] DetectUniqueKeyRequest request)
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();
            
            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync();
            
            await using (var streamWriter = new StreamWriter(response.Body, Encoding.UTF8, 256, leaveOpen: true))
            {
                streamWriter.AutoFlush = true;

                var validateRequest = await AuthRequest();
                if (validateRequest.UserAuthenticated)
                {
                    await InternalExecDetectUniqueKey(streamWriter, _conStr, request);
                }
                else
                {
                    await streamWriter.WriteLineAsync(validateRequest.ErrorMessage);
                }
            } // using block ensures StreamWriter is flushed and closed properly
        }

        [HttpGet("ExecDetectUniqueKey")]
        public async Task ExecDetectUniqueKeyGet([FromQuery] DetectUniqueKeyRequest request)
        {
            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // No buffering for real-time streaming
            await response.BodyWriter.AsStream().FlushAsync(); // Ensures the response starts immediately
            // Ensure the response starts immediately without buffering
            await response.StartAsync();

            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            await using (var streamWriter = new StreamWriter(response.Body, Encoding.UTF8, 256, leaveOpen: true))
            {
                streamWriter.AutoFlush = true;

                var validateRequest = await AuthRequest();
                if (validateRequest.UserAuthenticated)
                {
                    await InternalExecDetectUniqueKey(streamWriter, _conStr, request);
                }
                else
                {
                    await streamWriter.WriteLineAsync(validateRequest.ErrorMessage);
                }
            } // using block ensures StreamWriter is flushed and closed properly
        }

        private async Task InternalExecDetectUniqueKey(StreamWriter sw, string conStr, DetectUniqueKeyRequest request)
        {
            await sw.WriteLineAsync("Info: Detecting Unique Keys...");

            var cts = new CancellationTokenSource();

            if (!string.IsNullOrEmpty(request.cancelTokenId))
            {
                AddTokenSource(request.cancelTokenId, cts);
            }

            try
            {
                if (!string.IsNullOrEmpty(request.FlowId.ToString()) && request.ColList.Length > 0)
                {
                    Stopwatch totalTime = new Stopwatch();
                    totalTime.Start();

                    var task = Task.Run(async () =>
                    {
                        // Respect cancellation
                        cts.Token.ThrowIfCancellationRequested();

                        ExecDetectUniqueKey.Exec(sw, _conStr,
                            request.FlowId, request.ColList, request.NumberOfRowsToSample,
                            request.TotalUniqueKeysSought, request.MaxKeyCombinationSize,
                            (decimal)request.RedundantColSimilarityThreshold,
                            (decimal)request.SelectRatioFromTopUniquenessScore, request.AnalysisMode, request.ExecuteProofQuery, request.EarlyExitOnFound);

                        // Simulate long-running operation (replace with your actual database call)
                        //await Task.Delay(30000, cts.Token); // Use actual async call here

                        // Example of writing to the stream within the Task
                        // Make sure any writes to the StreamWriter occur here if you want to stream back results as they are available
                        // await sw.WriteLineAsync("Result from database operation");

                    }, cts.Token);

                    await KeepAliveWithCancel(task, sw, request.cancelTokenId);
                    await task;

                    totalTime.Stop();
                    await sw.WriteLineAsync($"Process Time: {totalTime.ElapsedMilliseconds / 1000}s");
                }
                else
                {
                    await sw.WriteLineAsync("Error: FlowId and ColList are required.");
                }
            }
            catch (OperationCanceledException)
            {
                await sw.WriteLineAsync("Operation was cancelled");
            }
            finally
            {
                if (!string.IsNullOrEmpty(request.cancelTokenId))
                {
                    _tokens.Remove(request.cancelTokenId); // Assuming this is your way of tracking CancellationTokenSource instances
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }
        }

        #endregion DetectUniqueKey

        #region Login
        /// <summary>
        /// Performs user login operation.
        /// </summary>
        /// <param name="request">An object of type LoginRequest containing the username and password for authentication.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains the JWT token if the login is successful.</returns>
        /// <remarks>
        /// This method performs the following steps:
        /// - Attempts to sign in with the provided username and password.
        /// - If sign in fails, it responds with "Invalid user or password".
        /// - If sign in succeeds, it generates a JWT token and adds it to the response headers.
        /// - Finally, it signs in the user with the generated claims and responds with the token.
        /// </remarks>
        [HttpPost("Login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var signInResult = await _signInManager.PasswordSignInAsync(request.Username, request.Password, isPersistent: true, lockoutOnFailure: false);
            if (!signInResult.Succeeded)
            {
                return Unauthorized("Invalid user or password");
            }

            var user = await _userManager.FindByNameAsync(request.Username);
            if (user == null)
            {
                return Unauthorized("Invalid user or password");
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.NameIdentifier, user.Id),
            };

            var userRoles = await _userManager.GetRolesAsync(user);
            claims.AddRange(userRoles.Select(role => new Claim(ClaimTypes.Role, role)));

            // Generate and return JWT token
            var token = _tokenService.GenerateJwtToken(claims);

            Response.Headers.Add("Authorization", $"Bearer {token}");
            Response.Headers.Add("X-SQLFlowAuth-Header", "true");

            await HttpContext.SignInAsync(
                IdentityConstants.ApplicationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.Add(TimeSpan.FromHours(24))
                });
            
            // Return structured response with token
            return Ok(new
            {
                token = token,
                userName = user.UserName,
                roles = userRoles
            });
        }

        private async Task WriteResponseAsync(string content)
        {
            Response.ContentType = "text/plain";
            //var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            //bufferingFeature?.DisableBuffering();
            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true })
            {
                streamWriter.Write(content);
                await Response.Body.FlushAsync();
            }
        }

        #endregion Login

        #region CheckAuth
        /// <summary>
        /// Checks the authentication status of the current user.
        /// </summary>
        /// <returns>
        /// Returns an IActionResult that represents the result of the authentication check.
        /// If the user is authenticated, it returns an Ok result with a success message.
        /// If the user is not authenticated, it returns an Unauthorized result with an error message.
        /// </returns>
        [HttpGet("CheckAuth")]
        public async Task<IActionResult> CheckAuth()
        {
            // First check if the user is already authenticated via standard ASP.NET Core auth
            if (User.Identity.IsAuthenticated)
            {
                // User is authenticated through cookies/standard auth
                return Ok(new
                {
                    message = "Authenticated via standard authentication",
                    userId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                    userName = User.FindFirstValue(ClaimTypes.Name),
                    authMethod = "standard"
                });
            }

            // If not authenticated via standard auth, check for token in Authorization header
            string authHeader = Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authHeader.Substring("Bearer ".Length).Trim();

                try
                {
                    var principal = _tokenService.ValidateToken(token);
                    if (principal != null && principal.Identity.IsAuthenticated)
                    {
                        // Token is valid
                        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                        var userName = principal.FindFirstValue(ClaimTypes.Name);

                        return Ok(new
                        {
                            message = "Authenticated via token",
                            userId = userId,
                            userName = userName,
                            authMethod = "token"
                        });
                    }
                }
                catch (Exception ex)
                {
                    return Unauthorized($"Token validation failed: {ex.Message}");
                }
            }

            // Check for token as URL parameter (if you want to support this method)
            if (Request.Query.TryGetValue("token", out var tokenValues))
            {
                string token = tokenValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(token))
                {
                    try
                    {
                        var principal = _tokenService.ValidateToken(token);
                        if (principal != null && principal.Identity.IsAuthenticated)
                        {
                            // Token is valid
                            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                            var userName = principal.FindFirstValue(ClaimTypes.Name);

                            return Ok(new
                            {
                                message = "Authenticated via URL token",
                                userId = userId,
                                userName = userName,
                                authMethod = "url_token"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        return Unauthorized($"URL token validation failed: {ex.Message}");
                    }
                }
            }

            // No valid authentication found
            return Unauthorized("Not authenticated");
        }
        #endregion CheckAuth

        #region LongLivedSwtToken

        [HttpGet("GetLongLivedSwtToken")]
        public async Task<IActionResult> GetLongLivedSwtToken()
        {
            var validateRequest = await AuthRequest();

            if (validateRequest.UserAuthenticated)
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null)
                {
                    return BadRequest("User not found");
                }

                var claims = await _userManager.GetClaimsAsync(user);
                var token = _tokenService.GenerateLongLivedJwtToken(claims);
                return Ok(token);
            }
            else
            {
                return Unauthorized(validateRequest.ErrorMessage);
            }
        }
        #endregion LongLivedSwtToken

        #region Logout
        /// <summary>
        /// Logs out the currently authenticated user.
        /// </summary>
        /// <remarks>
        /// This method signs out the user from the application and clears all related cookies.
        /// </remarks>
        /// <returns>A message indicating successful logout.</returns>
        [HttpPost("Logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            // Clear the existing external cookie
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            // Clear the cookie used for application sign-in
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

            // Any additional cookies set by the application can be cleared by specifying their names
            //Response.Cookies.Delete("YourCookieName");

            // Return a message indicating successful logout
            return Ok(new { message = "User logged out successfully" });
        }
        #endregion

        #region ValidateToken
        /// <summary>
        /// Validates the provided authentication token.
        /// </summary>
        /// <param name="request">An object of type ValidateTokenRequest containing the token to be validated.</param>
        /// <returns>Returns an IActionResult that represents the result of the token validation process. If the token is valid, the method returns the claims of the authenticated user. If the token is invalid or expired, the method returns an Unauthorized result.</returns>
        /// <remarks>
        /// This method uses the TokenService to validate the token. If the token is null or whitespace, the method immediately returns a BadRequest result. If the token is valid, the method extracts the claims from the token and checks if the user is authenticated. If the user is authenticated, the method proceeds with the operation. If the user is not authenticated or if the token
        [HttpPost("ValidateToken")]
        public async Task<IActionResult> ValidateToken([FromBody] ValidateTokenReqest request)
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return BadRequest("Token is required");
            }

            try
            {
                var principal = _tokenService.ValidateToken(request.Token);
                if (principal != null)
                {
                    var identity = principal.Identity as ClaimsIdentity;
                    if (identity != null && identity.IsAuthenticated)
                    {
                        // Extract user information from claims, typically the user's ID or username
                        var userId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return Unauthorized("Invalid token claims: ID not found");
                        }

                        // Retrieve the user by their ID or username
                        var user = await _userManager.FindByIdAsync(userId);
                        if (user == null)
                        {
                            return Unauthorized("User does not exist");
                        }

                        // Get claims from the validated token
                        var claims = principal.Claims.ToList(); // Convert to List<Claim> if necessary

                        // Sign in the user with the specified claims
                        await _signInManager.SignInWithClaimsAsync(user, isPersistent: true, claims);
                        Response.Headers.Add("Authorization", $"Bearer {request.Token}");
                        Response.Headers.Add("X-SQLFlowAuth-Header", $"true");

                        return Ok("Token is valid and user signed in");
                    }
                    return Unauthorized("Invalid token claims");
                }
                return Unauthorized("Invalid token");
            }
            catch (SecurityTokenExpiredException)
            {
                return Unauthorized("Token has expired");
            }
            catch (Exception ex)
            {
                return Unauthorized($"Token validation failed: {ex.Message}");
            }
        }
        #endregion ValidateToken

        #region CancelProcess
        [HttpPost("CancelProcess")]
        public async Task<IActionResult> CancelProcess([FromBody] CancelProcessRequest request)
        {
            var validateRequest = await AuthRequest();
            string FinalResult = "NotAuthenticated";
            if (validateRequest.UserAuthenticated)
            {
                // Check if the cancellation token exists
                if (_tokens.TryGetValue(request.cancelTokenId, out var ctsList))
                {
                    foreach (var cts in ctsList) // Loop over all CancellationTokenSource instances
                    {
                        cts.Cancel(); // Cancel each associated process
                    }
                    return Ok(new { message = "Canceled" }); 
                }
                return BadRequest(new { message = "NotFound" }); 
            }
            else
            {
                return BadRequest(new { message = validateRequest.ErrorMessage });
            }

        }

        #endregion
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

        /// <summary>
        /// Keeps the stream writer active while a given task is running.
        /// </summary>
        /// <param name="t">The task to monitor.</param>
        /// <param name="sw">The stream writer to keep alive.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private static async Task KeepAlive(Task t, StreamWriter sw)
        {
            await Task.Run(async () =>
            {
                var watch = new System.Diagnostics.Stopwatch();
                watch.Start();

                while (!t.IsCompleted)
                {
                    if (watch.Elapsed.TotalSeconds > 10)
                    {
                        await sw.WriteAsync($"time-lapse ({((int)watch.Elapsed.TotalSeconds)} sec){Environment.NewLine}");
                        watch.Restart();
                    }
                }
            });
        }

        /// <summary>
        /// Keeps the task alive and checks for cancellation requests.
        /// </summary>
        /// <param name="t">The task to keep alive.</param>
        /// <param name="sw">The StreamWriter used for logging.</param>
        /// <param name="cancelTokenId">The cancellation token ID. If it's not null or empty, a new CancellationTokenSource is added to the tokens dictionary with this ID.</param>
        /// <remarks>
        /// This method starts a new task that keeps running until the provided task is completed. 
        /// Every 10 seconds, it writes a time-lapse message to the StreamWriter.
        /// If a cancellation is requested via the provided cancellation token ID, it throws an OperationCanceledException and removes the token from the tokens dictionary.
        /// </remarks>
        /// <returns>A Task representing the asynchronous operation.</returns>
        private static async Task KeepAliveWithCancel(Task t, StreamWriter sw, string cancelTokenId)
        {
            var cts = new CancellationTokenSource();

            if (!string.IsNullOrEmpty(cancelTokenId))
            {
                AddTokenSource(cancelTokenId, cts);
            }

            try
            {
                await Task.Run(async () =>
                {
                    cts.Token.ThrowIfCancellationRequested(); // Throws OperationCanceledException if cancellation requested
                    
                    var watch = new System.Diagnostics.Stopwatch();
                    watch.Start();

                    while (!t.IsCompleted)
                    {
                        if (watch.Elapsed.TotalSeconds > 10)
                        {
                            await sw.WriteAsync($"time-lapse ({((int)watch.Elapsed.TotalSeconds)} sec){Environment.NewLine}");
                            sw.FlushAsync();
                            watch.Restart();
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (e.g., log the cancellation, clean up, etc.)
                //rValue = "Operation was cancelled"; // Or handle as needed
            }
            finally
            {
                if (!string.IsNullOrEmpty(cancelTokenId))
                {
                    _tokens.Remove(cancelTokenId); // Clean up
                }
                cts.Dispose(); // Always dispose CancellationTokenSource when done
            }
            

            
        }

        /// <summary>
        /// Asynchronously sends the final result of a task to a specified callback URI.
        /// </summary>
        /// <param name="CallBackUri">The URI to which the final result should be sent.</param>
        /// <param name="FinalResult">The final result of the task to be sent to the callback URI.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task RunCallBack(string CallBackUri, string FinalResult) 
        {
            if (!string.IsNullOrEmpty(CallBackUri))
            {
                if (ErrorChecker.HasError(FinalResult))
                {
                    var callbackPayload = new
                    {
                        statusCode = 200,
                        output = new { message = FinalResult }
                    };

                    using (var httpClient = new HttpClient())
                    {
                        var content = new StringContent(JsonConvert.SerializeObject(callbackPayload), Encoding.UTF8, "application/json");
                        await httpClient.PostAsync(CallBackUri, content);
                    }

                }
                else
                {
                    var callbackPayload = new
                    {
                        statusCode = 200,
                        output = new { error = FinalResult }
                    };

                    using (var httpClient = new HttpClient())
                    {
                        var content = new StringContent(JsonConvert.SerializeObject(callbackPayload), Encoding.UTF8, "application/json");
                        await httpClient.PostAsync(CallBackUri, content);
                    }
                }
            }

        }

        /// <summary>
        /// Writes the processing time to the provided StreamWriter.
        /// </summary>
        /// <param name="sw">The StreamWriter to which the processing time will be written.</param>
        /// <param name="Duration">The duration of the processing time in seconds.</param>
        private static void writeProcTime(StreamWriter sw, string Duration)
        {
            sw.WriteAsync($"#### Processing time {Duration} (sec) {Environment.NewLine}");
            sw.FlushAsync();
        }

        /// <summary>
        /// Handles the OnRowsCopied event of the SQLFlowCore.Engine.ExecFlowProcess class.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="SQLFlowCore.Args.EventArgsRowsCopied"/> instance containing the event data.</param>
        /// <remarks>
        /// This method is triggered when rows are copied in the SQLFlowCore process. It updates the _rowString field with the number of rows processed.
        /// </remarks>
        private void OnRowsCopied(object? sender, EventArgsRowsCopied e)
        {
            //string ThreadID = "F" + Thread.CurrentThread.ManagedThreadId.ToString();
            _rowString = $"{e.RowsProcessed}";
        }

        /// <summary>
        /// Handles the InvokeIsRunning event of the ExecFlowNode class.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="SQLFlowCore.Args.EventArgsInvoke"/> instance containing the event data.</param>
        /// <param name="sw">The StreamWriter instance to write the output.</param>
        /// <remarks>
        /// This method writes the invoked object name and its processing time to the StreamWriter. 
        /// It is important to flush the StreamWriter after writing to ensure the data is not lost.
        /// </remarks>
        private static void InvokeIsRunning(object? sender, EventArgsInvoke e, StreamWriter sw)
        {
            string Invoked = $"##### {e.InvokedObjectName} processing time ({e.TimeSpan}) seconds";
            sw.WriteAsync(Invoked + Environment.NewLine);
            sw.FlushAsync();//otherwise you are risking empty stream
        }

        /// <summary>
        /// Adds a CancellationTokenSource to the static tokens dictionary for the provided key.
        /// </summary>
        /// <param name="key">The key in the dictionary to which the CancellationTokenSource should be added.</param>
        /// <param name="cts">The CancellationTokenSource to be added to the dictionary.</param>
        /// <remarks>
        /// If the key already exists in the dictionary, the CancellationTokenSource is added to the existing list.
        /// If the key does not exist, a new list is created and added to the dictionary.
        /// </remarks>
        public static void AddTokenSource(string key, CancellationTokenSource cts)
        {
            // Check if the key already exists in the dictionary
            if (_tokens.ContainsKey(key))
            {
                // If the key exists, add the CancellationTokenSource to the existing list
                _tokens[key].Add(cts);
            }
            else
            {
                // If the key does not exist, create a new list and add it to the dictionary
                _tokens[key] = new List<CancellationTokenSource> { cts };
            }
        }


        /// <summary>
        /// Gets information about all loaded assemblies in the current application domain.
        /// Only authenticated users can access this endpoint.
        /// </summary>
        /// <returns>An IActionResult containing the list of loaded assemblies and their details.</returns>
        [HttpGet("DumpAssemblies")]
        public async Task<IActionResult> DumpAssemblies()
        {
            // Disable response buffering
            var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";

            // Ensure the response starts immediately without buffering
            await response.StartAsync();
            await response.BodyWriter.AsStream().FlushAsync();

            await using (var streamWriter = new StreamWriter(Response.Body, Encoding.UTF8, 1024, leaveOpen: true)
            {
                AutoFlush = true
            })
            {
                var validateRequest = await AuthRequest();

                if (!validateRequest.UserAuthenticated)
                {
                    await streamWriter.WriteLineAsync(validateRequest.ErrorMessage);
                    return new EmptyResult();
                }

                try
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                    try
                    {
                        var point = SqlGeography.Point(47.6062, -122.3321, 4326);
                        Console.WriteLine($"SQL Server Geography Point: {point}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }

                    await streamWriter.WriteLineAsync("=== Loaded Assemblies ===");
                    await streamWriter.WriteLineAsync($"Total Count: {assemblies.Length}");
                    await streamWriter.WriteLineAsync();

                    foreach (var assembly in assemblies.OrderBy(a => a.FullName))
                    {
                        await streamWriter.WriteLineAsync($"Assembly: {assembly.FullName}");
                        await streamWriter.WriteLineAsync($"Location: {assembly.Location}");

                        var version = assembly.GetName().Version;
                        await streamWriter.WriteLineAsync($"Version: {version}");

                        // Get assembly attributes
                        var customAttributes = assembly.GetCustomAttributes(inherit: false);
                        if (customAttributes.Any())
                        {
                            await streamWriter.WriteLineAsync("Attributes:");
                            foreach (var attribute in customAttributes)
                            {
                                await streamWriter.WriteLineAsync($"  - {attribute}");
                            }
                        }

                        await streamWriter.WriteLineAsync(new string('-', 80));
                        await streamWriter.FlushAsync();
                    }
                }
                catch (Exception ex)
                {
                    await streamWriter.WriteLineAsync($"Error occurred while dumping assemblies: {ex.Message}");
                    await streamWriter.WriteLineAsync(ex.StackTrace);
                }
            }

            // Flushing the final content to the response
            await Response.Body.FlushAsync();
            return new EmptyResult();
        }


    }
}
