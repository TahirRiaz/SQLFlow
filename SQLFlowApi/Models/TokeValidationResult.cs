namespace SQLFlowApi.Models
{
    public class TokeValidationResult
    {
        public bool IsValid { get; set; } = false;
        public string ErrorMessage { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
