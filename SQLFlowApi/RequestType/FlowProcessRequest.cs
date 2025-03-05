#nullable enable
using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for a flow process in the SQLFlow API.
    /// </summary>
    /// <remarks>
    /// This class is used to carry the parameters for a flow process request in both HTTP GET and POST methods in the SQLFlowController.
    /// </remarks>
    public class FlowProcessRequest
    {
        /// <summary>
        /// Gets or sets the identifier for the flow process.
        /// </summary>
        /// <value>
        /// The string that represents the unique identifier for the flow process. The default value is "1036".
        /// </value>
        /// <remarks>
        /// This property is used as a parameter in the SQLFlowController's ExecFlowProcess methods for both HTTP GET and POST requests.
        /// </remarks>
        public string FlowId { get; set; } = "1036";
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
        /// Gets or sets the source file with its full path.
        /// </summary>
        /// <value>
        /// The source file with its full path.
        /// </value>
        /// <remarks>
        /// This property is used to specify the location of the source file that is to be processed in the flow process request. 
        /// It should contain the full path of the file. If the file is not found at the specified location, an error will be returned.
        /// </remarks>
        [DefaultValue("")]
        public string? SrcFileWithPath { get; set; } = "";


        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";


        /// <summary>
        /// Gets or sets the cancellation token ID for the flow process request.
        /// </summary>
        /// <value>
        /// The cancellation token ID as a string. This is used to cancel a running flow process request.
        /// </value>
        /// <remarks>
        /// If a cancellation token ID is provided, it can be used to cancel the flow process request before it completes.
        /// </remarks>

        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

    }
}
