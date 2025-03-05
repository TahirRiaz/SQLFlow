namespace SQLFlowUi.Controllers
{
    public class HealthCheckHeader
    {
        public string MLModelSelection { get; set; }
        public string MLModelName { get; set; }
        public DateTime? MLModelDate { get; set; }
        public DateTime? ResultDate { get; set; }

        public string trgObject { get; set; }

        public IEnumerable<ValidationModelData> MLModelSelectionParsed { get; set; }
    }
}
