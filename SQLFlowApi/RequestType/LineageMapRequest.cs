using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for a lineage map operation in SQLFlow API.
    /// </summary>
    public class LineageMapRequest
    {
        /// <summary>
        /// Gets or sets the 'All' parameter for the lineage map operation. Default value is "1".
        /// </summary>
        public string All { get; set; } = "1";
        /// <summary>
        /// Gets or sets the 'Alias' parameter for the lineage map operation. Default value is an empty string.
        /// </summary>
        public string? Alias { get; set; } = "";
        /// <summary>
        /// Gets or sets the 'Threads' parameter for the lineage map operation. Default value is "1".
        /// </summary>
        public string Threads { get; set; } = "1";
        /// <summary>
        /// Gets or sets the 'ExecMode' parameter for the lineage map operation. 
        /// This is an optional field and defines the executing interface of the SQLFlow process. Default value is "api".
        /// </summary>
        [DefaultValue("api")]
        public string ExecMode { get; set; } = "api";
        /// <summary>
        /// Gets or sets the 'Dbg' parameter for the lineage map operation. 
        /// This is an optional field and defines the detail level (1-2) of the SQLFlow process log. Default value is "1".
        /// </summary>
        [DefaultValue("1")]
        public string Dbg { get; set; } = "1";
        /// <summary>
        /// Gets or sets the 'CallBackUri' parameter for the lineage map operation. 
        /// This is an optional field and is automatically populated by Azure Data Factory. Default value is an empty string.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";

        /// <summary>
        /// Gets or sets the cancellation token identifier.
        /// </summary>
        /// <value>
        /// The identifier of the cancellation token used to cancel the lineage map request operation.
        /// </value>
        /// <remarks>
        /// This property is used to associate a cancellation token with the lineage map request. 
        /// If the operation needs to be cancelled, this token can be used to signal the cancellation request.
        /// </remarks>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

    }

}
