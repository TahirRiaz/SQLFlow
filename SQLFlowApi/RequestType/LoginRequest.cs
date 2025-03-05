using System.ComponentModel;

namespace SQLFlowApi.RequestType
{

    public class LoginRequest
    {
        /// <summary>
        /// Required field.
        /// </summary>
        [DefaultValue("")]
        public string Username { get; set; } = "";

        /// <summary>
        /// Required field.
        /// </summary>
        [DefaultValue("")]
        public string Password { get; set; } = "";
        
    }
}
