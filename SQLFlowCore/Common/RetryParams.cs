namespace SQLFlowCore.Common
{
    internal class RetryParams
    {
        internal RetryParams(int maxRetries,
            int minBackOffDelayInMilliseconds,
            int maxBackOffDelayInMilliseconds,
            int deltaBackOffInMilliseconds)
        {
            MaxRetries = maxRetries;
            MinBackOffDelayInMilliseconds = minBackOffDelayInMilliseconds;
            MaxBackOffDelayInMilliseconds = maxBackOffDelayInMilliseconds;
            DeltaBackOffInMilliseconds = deltaBackOffInMilliseconds;
        }

        internal static RetryParams Default { get; } = new(10, 20, 8000, 20);

        internal int MaxRetries { get; }

        internal int MinBackOffDelayInMilliseconds { get; }

        internal int MaxBackOffDelayInMilliseconds { get; }

        internal int DeltaBackOffInMilliseconds { get; }
    }
}