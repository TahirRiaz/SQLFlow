#nullable enable
using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for batch processing of SQL flows.
    /// </summary>
    /// <remarks>
    /// This class is used to encapsulate the details of a batch of SQL flows that need to be processed. 
    /// It is used in both GET and POST requests in the SQLFlowController.
    /// </remarks>
    public class FlowBatchRequest
    {
        /// <summary>
        /// Required field.
        /// </summary>
        public string Batch { get; set; } = "";

        /// <summary>
        /// Sysalias is an optional field and limits the batch to a certain system.
        /// </summary>
        [DefaultValue("")]
        public string? Sysalias { get; set; } = "";

        /// <summary>
        /// FlowType is an optional field and limits the batch to a certain type of flow.
        /// </summary>
        [DefaultValue("")]
        public string? FlowType { get; set; } = "";

        /// <summary>
        /// ExecMode is an optional field and defines the executing interface of the SQLFlow process.
        /// </summary>
        [DefaultValue("api")]
        public string ExecMode { get; set; } = "api";
        /// <summary>
        /// Dbg is an optional field defines the detail level (1-2) of the SQLFlow process log.
        /// </summary>
        [DefaultValue("1")]
        public string Dbg { get; set; } = "1";

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";

        /// <summary>
        /// Gets or sets the cancellation token identifier.
        /// </summary>
        /// <value>
        /// The identifier of the cancellation token used to signal the request to cancel the operation.
        /// </value>
        /// <remarks>
        /// This property is used to associate a cancellation token with the batch request. 
        /// If the operation needs to be cancelled, the cancellation token corresponding to this identifier is triggered.
        /// </remarks>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

    }
}
