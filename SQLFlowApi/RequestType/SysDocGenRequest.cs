using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request for generating object details in SQLFlow API.
    /// </summary>
    /// <remarks>
    /// This class is used to pass parameters to the object details generation endpoint of the SQLFlow API.
    /// </remarks>
    public class SysDocGenRequest
    {
        private const string DefaultObjectName = "";

        /// <summary>
        /// Gets or sets the object name for which we should generate details.
        /// </summary>
        [DefaultValue(DefaultObjectName)]
        public string Objectname { get; set; } = DefaultObjectName;
    }
}
