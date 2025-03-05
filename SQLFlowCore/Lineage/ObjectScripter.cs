using Microsoft.SqlServer.Management.Smo;

namespace SQLFlowCore.Lineage
{
    /// <summary>
    /// Represents a scripter that provides functionality for scripting SQL Server objects.
    /// This class also includes a custom event for tracking scripting progress.
    /// </summary>
    /// <remarks>
    /// This class inherits from the <see cref="Scripter"/> class and extends its functionality.
    /// </remarks>
    internal class ObjectScripter : Scripter
    {
        // Define a delegate for the custom event
        internal delegate void CustomScriptingProgressHandler(object sender, ProgressReportEventArgs e);

        // Define the custom event using the delegate
        internal event CustomScriptingProgressHandler CustomScriptingProgress;

        internal ObjectScripter(Server server) : base(server)
        {
            // Subscribe to the ScriptingProgress event
            ScriptingProgress += ObjectScripter_ScriptingProgress;
        }

        private void ObjectScripter_ScriptingProgress(object sender, ProgressReportEventArgs e)
        {
            // Raise the custom event when the ScriptingProgress event is triggered
            OnCustomScriptingProgress(e);
        }

        // Method to manually raise the custom event (this might not be needed now, but it's here if you want it)
        internal void RaiseCustomScriptingProgress(ProgressReportEventArgs e)
        {
            OnCustomScriptingProgress(e);
        }

        // Protected method that invokes the custom event
        protected virtual void OnCustomScriptingProgress(ProgressReportEventArgs e)
        {
            CustomScriptingProgress?.Invoke(this, e);
        }
    }

}
