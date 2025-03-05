using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the event arguments for rows copied in SQLFlowCore.
    /// </summary>
    public class EventArgsRowsCopied
    {
        /// <summary>
        /// Gets or sets the name of the target object.
        /// </summary>
        public string TrgObjectName { get; set; }
        /// <summary>
        /// Gets or sets the name of the source object.
        /// </summary>
        public string SrcObjectName { get; set; }
        /// <summary>
        /// Gets or sets the total number of rows.
        /// </summary>
        public long RowsInTotal { get; set; }
        /// <summary>
        /// Gets or sets the number of rows in the queue.
        /// </summary>
        public long RowsInQueue { get; set; }
        /// <summary>
        /// Gets or sets the number of rows processed.
        /// </summary>
        public long RowsProcessed { get; set; }
        /// <summary>
        /// Gets or sets the time span.
        /// </summary>
        public long TimeSpan { get; set; }
        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        public double Status { get; set; }
        /// <summary>
        /// Gets or sets the event date and time.
        /// </summary>
        public DateTime EventDateTime { get; set; }
    }

}
