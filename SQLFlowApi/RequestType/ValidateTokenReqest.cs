using System.ComponentModel;

namespace SQLFlowApi.RequestType
{

    public class ValidateTokenReqest
    {
        /// <summary>
        /// Required field.
        /// </summary>
        [DefaultValue("")]
        public string Token { get; set; } = "";

    }
}
