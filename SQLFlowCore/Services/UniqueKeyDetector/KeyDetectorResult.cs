namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public class KeyDetectorResult
    {
        public string FlowId { get; set; }

        public string ObjectName { get; set; }

        public string DetectedKey { get; set; }

        public string ProofQuery { get; set; }

        public string DuplicateRowCount { get; set; }

        public int ColumnCountInKey { get; set; }

        public int NumericColumnCountInKey { get; set; }

        public bool ProofQueryExecuted { get; set; }
    }
}

