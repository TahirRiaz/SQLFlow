using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SQLFlowCore.Common
{
    public abstract class IntervalTask : IWorkerProcess, IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _source;
        private Task _internalTask;

        public IntervalTask(TimeSpan interval)
        {
            _interval = interval;
            _source = new CancellationTokenSource();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Start()
        {
            if (_internalTask != null)
                throw new IntervalTaskException("Task is already running");

            _internalTask = Task.Run(() =>
            {
                while (!_source.IsCancellationRequested)
                    TryExecute();

            }, _source.Token);
        }

        public void Cancel()
        {
            _source.Cancel();
            _internalTask = null;
        }

        private void TryExecute()
        {
            try
            {
                Task.Delay(_interval)
                    .ContinueWith(_ => Execute())
                    .Wait();
            }
            catch (AggregateException ex)
            {
                Report(ex.ToString());
                if (Debugger.IsAttached)
                    Trace.TraceError(ex.ToString());
            }
        }

        protected abstract void Execute();
        protected abstract void Report(string message);

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _internalTask.Dispose();
                _source.Dispose();
            }

            _internalTask = null;
        }
    }
}