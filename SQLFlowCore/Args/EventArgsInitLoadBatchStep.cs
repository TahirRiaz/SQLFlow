namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the arguments for the initialization of a load batch step.
    /// </summary>
    public class EventArgsInitLoadBatchStep
    {
        /// <summary>
        /// Gets or sets the source select range.
        /// </summary>
        public string srcSelectRange { get; set; }
        /// <summary>
        /// Gets or sets the total task counter.
        /// </summary>
        public int totalTaskCounter { get; set; }
        /// <summary>
        /// Gets or sets the task status counter.
        /// </summary>
        public string taskStatusCounter { get; set; }
        /// <summary>
        /// Gets or sets the range time span.
        /// </summary>
        public string RangeTimeSpan { get; set; }
    }
}
