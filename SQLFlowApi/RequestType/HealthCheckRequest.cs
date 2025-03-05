using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for a health check operation in the SQL Flow system.
    /// </summary>
    /// <remarks>
    /// This class is used to pass parameters to the `ExecHealthCheck` method in the `SQLFlowController` class.
    /// </remarks>
    public class HealthCheckRequest
    {
        /// <summary>
        /// Gets or sets the ID of the flow to check.
        /// </summary>
        /// <value>
        /// The ID of the flow to check. The default value is "638".
        /// </value>
        /// <remarks>
        /// This property is used to specify the flow that should be checked in the health check operation. 
        /// It is used as a parameter in the `ExecHealthCheck` method of the `SQLFlowController` class.
        /// </remarks>
        [DefaultValue("638")]
        public string FlowId { get; set; } = "638";
        /// <summary>
        /// RunModelSelection is an optional field and defines the executing interface of the SQLFlow process.
        /// </summary>
        [DefaultValue("1")]
        public string RunModelSelection { get; set; } = "1";
        /// <summary>
        /// dbg level is an optional field and defines the level of debugging information returned by the SQLFlow process.
        /// </summary>
        [DefaultValue("1")]
        public string dbg { get; set; } = "1";

        /// <summary>
        /// Gets or sets the cancellation token identifier for the health check request.
        /// </summary>
        /// <value>
        /// The cancellation token identifier. If the operation needs to be cancelled, this token will be used.
        /// </value>
        /// <remarks>
        /// This property is optional. If not provided, a new cancellation token will be generated for the operation.
        /// </remarks>

        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

    }
}
