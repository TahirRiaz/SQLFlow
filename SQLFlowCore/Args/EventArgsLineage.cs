using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the event arguments for lineage events.
    /// </summary>
    public class EventArgsLineage : EventArgs
    {
        /// <summary>
        /// Gets or sets the URN of the object.
        /// </summary>
        public string ObjectUrn { get; set; }
        /// <summary>
        /// Gets or sets the total number of items.
        /// </summary>
        public int InTotal { get; set; }
        /// <summary>
        /// Gets or sets the number of items in the queue.
        /// </summary>
        public int InQueue { get; set; }
        /// <summary>
        /// Gets or sets the number of processed items.
        /// </summary>
        public int Processed { get; set; }
        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        public double Status { get; set; }
        /// <summary>
        /// Gets or sets the initialization date and time.
        /// </summary>
        public DateTime InitDateTime { get; set; }
        /// <summary>
        /// Gets or sets the fetch exception.
        /// </summary>
        public string fetchException { get; set; }
    }

}
