#nullable enable
using System.ComponentModel;

namespace SQLFlowCore.ExecParams
{
    public class FlowNode
    {
        public string Node { get; set; } = "0";
        public string Dir { get; set; } = "A";
        public string Batch { get; set; } = "";
        public string ExecMode { get; set; } = "api";
        private string _exitOnErrorStr = "1";
        private string _fetchAllDepStr = "0";
        public string Dbg { get; set; } = "1";

        public bool ExitOnError
        {
            get => _exitOnErrorStr == "1";
            set => _exitOnErrorStr = value ? "1" : "0";
        }

        public bool FetchAllDep
        {
            get => _fetchAllDepStr == "1";
            set => _fetchAllDepStr = value ? "1" : "0";
        }

        // If you need to keep the string representation accessible
        public string ExitOnErrorStr
        {
            get => _exitOnErrorStr;
            set => _exitOnErrorStr = value;
        }

        public string FetchAllDepStr
        {
            get => _fetchAllDepStr;
            set => _fetchAllDepStr = value;
        }

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";

    }
}
