using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for an assertion operation in the SQLFlow API.
    /// </summary>
    /// <remarks>
    /// This class is used to carry the parameters for an assertion operation. 
    /// The parameters include a FlowId and a debug flag.
    /// </remarks>
    public class AssertionRequest
    {
        /// <summary>
        /// Gets or sets the FlowId for the assertion operation. The default value is "638".
        /// </summary>
        [DefaultValue("638")]
        public string FlowId { get; set; } = "638";
        /// <summary>
        /// Gets or sets the debug level for the assertion operation. 
        /// This is an optional field and defines the level of debugging information returned by the SQLFlow process. The default value is "1".
        /// </summary>
        [DefaultValue("1")]
        public string dbg { get; set; } = "1";

        /// <summary>
        /// Gets or sets the cancellation token identifier for the assertion request.
        /// </summary>
        /// <value>
        /// The cancellation token identifier.
        /// </value>
        /// <remarks>
        /// This property is used to identify a cancellation request for an ongoing assertion operation.
        /// </remarks>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";



    }

}
