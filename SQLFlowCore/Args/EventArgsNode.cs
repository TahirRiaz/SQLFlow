using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents a node in the event argument flow.
    /// </summary>
    public class EventArgsNode
    {
        /// <summary>
        /// Gets or sets the node.
        /// </summary>
        public string Node { get; set; }
        /// <summary>
        /// Gets or sets the direction.
        /// </summary>
        public string Direction { get; set; }
        /// <summary>
        /// Gets or sets the flow ID.
        /// </summary>
        public int FlowID { get; set; }
        /// <summary>
        /// Gets or sets the type of the flow.
        /// </summary>
        public string FlowType { get; set; }
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
        /// Gets or sets the initialization date and time.
        /// </summary>
        public DateTime InitDateTime { get; set; }
    }

}
