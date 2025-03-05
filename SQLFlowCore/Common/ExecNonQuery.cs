using System.Data;
using Microsoft.Data.SqlClient;

namespace SQLFlowCore.Common
{
    internal class ExecNonQuery
    {
        private readonly int _commandTimeout = 240;
        private readonly string _query = "";
        private readonly SqlConnection _sqlConnection;
        private DataTable _dataTable = new();

        internal ExecNonQuery(SqlConnection conObj, string cmd, int commandTimeout)
        {
            _sqlConnection = conObj;
            _query = cmd;
            _commandTimeout = commandTimeout;
        }
        internal int Exec()
        {
            using (var command = new SqlCommand(_query, _sqlConnection))
            {
                command.CommandType = CommandType.Text;
                command.CommandTimeout = _commandTimeout;
                return command.ExecuteNonQuery();
            }

        }
    }
}