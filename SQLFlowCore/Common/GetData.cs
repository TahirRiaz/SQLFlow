using System;
using System.Data;
using Microsoft.Data.SqlClient;
namespace SQLFlowCore.Common
{
    internal class GetData
    {
        private readonly int _commandTimeoutInSek = 3600;
        private readonly DataTable _dataTable = new();
        private readonly string _query = "";
        private readonly SqlConnection _sqlCon;

        internal GetData(SqlConnection sqlConnection, string query, int commandTimeOutInSek)
        {
            //ConnString = conStr;
            _sqlCon = sqlConnection;
            _query = query;
            _commandTimeoutInSek = commandTimeOutInSek;
        }

        internal DataTable Fetch()
        {
            var cmd = new SqlCommand(_query, _sqlCon) { CommandTimeout = _commandTimeoutInSek };
            // create data adapter
            var da = new SqlDataAdapter(cmd) { FillLoadOption = LoadOption.Upsert };

            da.Fill(_dataTable);
            da.Dispose();
            cmd.Dispose();

            return _dataTable;
        }


        internal DataSet FetchDS()
        {
            DataSet ds = new DataSet();
            try
            {
                // Create a SqlCommand
                using (SqlCommand command = new SqlCommand(_query, _sqlCon) { CommandTimeout = _commandTimeoutInSek })
                {
                    // Create a SqlDataAdapter
                    using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                    {
                        // Create and fill the DataSet
                        adapter.Fill(ds);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred: " + ex.Message);
            }

            return ds;
        }


    }
}