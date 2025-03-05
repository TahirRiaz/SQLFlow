using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the event arguments for source control.
    /// </summary>
    public class EventArgsSourceControl : EventArgs
    {
        /// <summary>
        /// Gets or sets the URN of the object.
        /// </summary>
        public string ObjectUrn { get; set; }
        /// <summary>
        /// Gets or sets the log.
        /// </summary>
        public string Log { get; set; }
        /// <summary>
        /// Gets or sets the total number of objects.
        /// </summary>
        public int Total { get; set; }
        /// <summary>
        /// Gets or sets the number of objects in the queue.
        /// </summary>
        public int InQueue { get; set; }
        /// <summary>
        /// Gets or sets the number of processed objects.
        /// </summary>
        public int Processed { get; set; }
        /// <summary>
        /// Gets or sets the status of the processing.
        /// </summary>
        public double Status { get; set; }
        /// <summary>
        /// Gets or sets the initialization date and time.
        /// </summary>
        public DateTime InitDateTime { get; set; }
    }

}
