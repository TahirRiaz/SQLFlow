#nullable enable
using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for a flow node in SQLFlow.
    /// </summary>
    public class FlowNodeRequest
    {
        /// <summary>
        /// Gets or sets the node. Default value is "0".
        /// </summary>
        [DefaultValue("0")]
        public string Node { get; set; } = "0";
        /// <summary>
        /// Gets or sets the direction. Default value is "A".
        /// </summary>
        [DefaultValue("A")]
        public string Dir { get; set; } = "A";
        /// <summary>
        /// Gets or sets the batch. Default value is an empty string.
        /// </summary>
        [DefaultValue("")]
        public string Batch { get; set; } = "";
        /// <summary>
        /// Gets or sets the exit on error flag. Default value is "1".
        /// </summary>
        [DefaultValue("1")]
        public string exitOnError { get; set; } = "1";
        /// <summary>
        /// Gets or sets the fetch all dependencies flag. Default value is "0".
        /// </summary>
        [DefaultValue("0")]
        public string fetchAllDep { get; set; } = "0";
        /// <summary>
        /// Gets or sets the execution mode. Default value is "api".
        /// </summary>
        [DefaultValue("api")]
        public string ExecMode { get; set; } = "api";
        /// <summary>
        /// Gets or sets the debug level. Default value is "1".
        /// </summary>
        [DefaultValue("1")]
        public string Dbg { get; set; } = "1";
        /// <summary>
        /// Gets or sets the callback URI. Default value is an empty string.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";
        /// <summary>
        /// Returns a boolean value indicating whether to exit on error.
        /// </summary>
        /// <returns>True if exitOnError is "1", otherwise false.</returns>


        /// <summary>
        /// Gets or sets the cancellation token identifier. This identifier is used to cancel a running flow node request.
        /// </summary>
        /// <value>
        /// The cancellation token identifier. The default value is an empty string.
        /// </value>

        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

        public bool GetExitOnErrorBool()
        {
            bool rValue = false;
            if (exitOnError == "1")
                rValue = true;
            return rValue;
        }
        /// <summary>
        /// Returns a boolean value indicating whether to fetch all dependencies.
        /// </summary>
        /// <returns>True if fetchAllDep is "1", otherwise false.</returns>
        public bool GetFetchAllDepBool()
        {
            bool rValue = false;
            if (fetchAllDep == "1")
                rValue = true;
            return rValue;
        }
    }
}

