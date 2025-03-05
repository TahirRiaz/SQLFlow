namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the arguments for a process event.
    /// </summary>
    public class EventArgsPrc
    {
        /// <summary>
        /// Gets or sets the flow identifier.
        /// </summary>
        public int FlowID { get; set; }
        /// <summary>
        /// Gets or sets the type of the flow.
        /// </summary>
        public string FlowType { get; set; }
        /// <summary>
        /// Gets or sets the system alias.
        /// </summary>
        public string SysAlias { get; set; }
        /// <summary>
        /// Gets or sets the batch.
        /// </summary>
        public string Batch { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to resume on error.
        /// </summary>
        public bool OnErrorResume { get; set; }
        /// <summary>
        /// Gets or sets the result log.
        /// </summary>
        public string ResultLog { get; set; }
        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        public string Description { get; set; }
        /// <summary>
        /// Gets or sets the parameter values.
        /// </summary>
        public string ParamValues { get; set; }
        /// <summary>
        /// Gets or sets the step.
        /// </summary>
        public string Step { get; set; }
        /// <summary>
        /// Gets or sets the number of items in queue.
        /// </summary>
        public int InQueue { get; set; }
        /// <summary>
        /// Gets or sets the total number of items.
        /// </summary>
        public int InTotal { get; set; }
        /// <summary>
        /// Gets or sets the number of processed items.
        /// </summary>
        public int Processed { get; set; }
        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        public double Status { get; set; }
        /// <summary>
        /// Gets or sets the row count.
        /// </summary>
        public int Rowcount { get; set; }
    }
}