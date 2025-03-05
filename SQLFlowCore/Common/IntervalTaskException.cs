using System;

namespace SQLFlowCore.Common
{
    [Serializable]
    internal class IntervalTaskException : Exception
    {
        internal IntervalTaskException()
        {
            // Add any type-specific logic, and supply the default message.
        }

        internal IntervalTaskException(string message)
            : base(message)
        {
            // Add any type-specific logic.
        }

        internal IntervalTaskException(string message, Exception innerException)
            : base(message, innerException)
        {
            // Add any type-specific logic for inner exceptions.
        }
    }
}