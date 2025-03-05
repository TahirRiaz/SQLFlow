namespace SQLFlowApi.Models
{
    public class ValidateRequest
    {
        public bool UserAuthenticated { get; set; } = false;
        public string ErrorMessage { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
