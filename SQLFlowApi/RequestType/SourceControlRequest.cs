#nullable enable
using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for a source control operation.
    /// </summary>
    public class SourceControlRequest
    {
        /// <summary>
        /// Gets or sets the source control alias.
        /// </summary>
        public string Scalias { get; set; } = "";
        /// <summary>
        /// Gets or sets the batch identifier.
        /// </summary>
        public string Batch { get; set; } = "";
        /// <summary>
        /// Gets or sets the callback URI. This is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        /// <value>The callback URI or null.</value>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";

        /// <summary>
        /// Gets or sets the cancellation token identifier.
        /// </summary>
        /// <value>
        /// The identifier for the cancellation token used to signal the request to cancel the operation.
        /// </value>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";
    }

}
