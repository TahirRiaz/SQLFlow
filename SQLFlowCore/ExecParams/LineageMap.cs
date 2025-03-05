#nullable enable
using System.ComponentModel;

namespace SQLFlowCore.ExecParams
{
    public class LineageMap
    {
        public string All { get; set; } = "1";
        public string Alias { get; set; } = "";
        public string Threads { get; set; } = "1";
        public string ExecMode { get; set; } = "api";
        public string Dbg { get; set; } = "1";

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";
    }
}
