namespace SQLFlowCore.Common
{
    internal interface IWorkerProcess
    {
        void Start();
        void Cancel();
    }
}