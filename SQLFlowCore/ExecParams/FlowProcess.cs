#nullable enable
using System.ComponentModel;

namespace SQLFlowCore.ExecParams
{
    public class FlowProcess
    {
        public string FlowId { get; set; } = "0";
        public string ExecMode { get; set; } = "api";
        public string Dbg { get; set; } = "1";

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";
    }
}
