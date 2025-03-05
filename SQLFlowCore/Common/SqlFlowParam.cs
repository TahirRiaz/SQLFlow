using System.Data;
using Microsoft.Data.SqlClient;

namespace SQLFlowCore.Common
{
    public class SqlFlowParam
    {
        public int flowId { get; set; }
        public string flowType { get; set; }
        public bool sourceIsAzCont { get; set; }
        public int matchKeyId { get; set; }
        public string execMode { get; set; }
        public string batch { get; set; } = "";
        public string batchId { get; set; } = "0";
        public string srcFileWithPath { get; set; }
        public string sqlFlowConString { get; set; }
        public bool TruncateIsSetOnNextFlow { get; set; } = false;
        public int dbg { get; set; }
        //string sqlFlowConString,int flowId,string execMode,string batchId,int dbg,

        internal SqlFlowParam(bool sourceIsAzCont, string flowType)
        {
            this.sourceIsAzCont = sourceIsAzCont;
            this.flowType = flowType;
        }

        public SqlFlowParam()
        {
            flowType = string.Empty;
            sourceIsAzCont = false;
            flowId = 0;
            matchKeyId = 0;
            execMode = string.Empty;
            batch = string.Empty;
            batchId = "0";
            dbg = 0;
            srcFileWithPath = string.Empty;
            sqlFlowConString = string.Empty;
            TruncateIsSetOnNextFlow = false;
        }

        internal SqlFlowParam(SqlConnection sqlFlowCon, int flowid)
        {
            sourceIsAzCont = false;
            string batchDsCmd =
                $@" SELECT FlowType, SourceIsAzCont FROM [flw].[GetFlowTypeTBL]({flowid}) ";
            var flowData = new GetData(sqlFlowCon, batchDsCmd, 5);
            DataTable bDSTbl = flowData.Fetch();

            flowType = bDSTbl.Rows[0]["FlowType"]?.ToString() ?? string.Empty;
            sourceIsAzCont = (bDSTbl.Rows[0]["SourceIsAzCont"]?.ToString() ?? string.Empty).Equals("True");
        }
    }
}
