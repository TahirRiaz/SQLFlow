using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents a batch of event arguments in the SQLFlowCore engine.
    /// </summary>
    public class EventArgsBatch
    {
        /// <summary>
        /// Gets or sets the batch.
        /// </summary>
        public string Batch { get; set; }
        /// <summary>
        /// Gets or sets the result log.
        /// </summary>
        public string ResultLog { get; set; }
        /// <summary>
        /// Gets or sets the flow ID.
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
        /// Gets or sets a value indicating whether to resume on error.
        /// </summary>
        public bool OnErrorResume { get; set; }
        /// <summary>
        /// Gets or sets the step.
        /// </summary>
        public int Step { get; set; }
        /// <summary>
        /// Gets or sets the number of items in the queue.
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
        /// Gets or sets a value indicating whether the processing is done.
        /// </summary>
        public bool Done { get; set; }
        /// <summary>
        /// Gets or sets the initialization date and time.
        /// </summary>
        public DateTime InitDateTime { get; set; }
    }

}
