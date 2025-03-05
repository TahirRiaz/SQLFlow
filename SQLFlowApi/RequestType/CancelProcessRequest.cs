using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    public class CancelProcessRequest
    {
        /// <summary>
        /// Required field.
        /// </summary>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";

    }
}
