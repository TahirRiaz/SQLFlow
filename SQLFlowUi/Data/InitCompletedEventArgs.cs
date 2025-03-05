namespace SQLFlowUi.Data
{
    public class InitCompletedEventArgs
    {
        public int FlowId { get; set; }

        public int SurrogateKeyID { get; set; }

        public int MatchKeyID { get; set; }

        public string NavigateToUrl { get; set; }
    }
}
