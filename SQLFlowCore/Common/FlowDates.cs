using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace SQLFlowCore.Common
{
    internal static class FlowDates
    {
        internal static DateTime Default = new(1900, 01, 01);

        internal static DataTable GetDateTimeFormats(SqlConnection sqlFlowCon)
        {
            DataTable dt = new DataTable();
            string sqlCMD = "exec [flw].[GetDateTimeFormat]";
            dt = CommonDB.FetchData(sqlFlowCon, sqlCMD, 360);

            return dt;
        }
    }
}
