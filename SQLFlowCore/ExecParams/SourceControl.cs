#nullable enable
using System.ComponentModel;

namespace SQLFlowCore.ExecParams
{

    public class SourceControl
    {
        public string Scalias { get; set; } = "";
        public string Batch { get; set; } = "";

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";
    }
}
