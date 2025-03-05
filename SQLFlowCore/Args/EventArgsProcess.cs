using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the arguments for a process event.
    /// </summary>
    public class EventArgsProcess
    {
        /// <summary>
        /// Gets or sets a value indicating whether the process is completed.
        /// </summary>
        public bool Completed { get; set; }
        /// <summary>
        /// Gets or sets the result of the process.
        /// </summary>
        public string Result { get; set; }
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
        /// Gets or sets the number of processed rows.
        /// </summary>
        public long RowsProcessed { get; set; }
        /// <summary>
        /// Gets or sets the time span of the process.
        /// </summary>
        public long TimeSpan { get; set; }
        /// <summary>
        /// Gets or sets the status of the process.
        /// </summary>
        public double Status { get; set; }
        /// <summary>
        /// Gets or sets the date and time of the event.
        /// </summary>
        public DateTime EventDateTime { get; set; }
    }
}

