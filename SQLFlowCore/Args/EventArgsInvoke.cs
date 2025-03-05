using System;

namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the arguments for an invocation event.
    /// </summary>
    public class EventArgsInvoke
    {
        /// <summary>
        /// Gets or sets the name of the invoked object.
        /// </summary>
        public string InvokedObjectName { get; set; }
        /// <summary>
        /// Gets or sets the time span of the invocation in seconds.
        /// </summary>
        public long TimeSpan { get; set; }
        /// <summary>
        /// Gets or sets the status of the invocation.
        /// </summary>
        public string InvokeStatus { get; set; }
        /// <summary>
        /// Gets or sets the date and time of the event.
        /// </summary>
        public DateTime EventDateTime { get; set; }
    }

}
