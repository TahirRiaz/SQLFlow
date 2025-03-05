using System.Data;
using Microsoft.Data.SqlClient;
using System.Data.SqlTypes;
using System.IO;

namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Provides functionality to export assemblies from the database.
    /// </summary>
    internal class AssemblyExporter
    {
        /// <summary>
        /// Saves the specified assembly to the given destination path.
        /// </summary>
        /// <param name="assemblyName">The name of the assembly to save.</param>
        /// <param name="destinationPath">The path where the assembly will be saved.</param>
        internal static void SaveAssembly(string assemblyName, string destinationPath)
        {
            string sql = @"SELECT af.name, af.content FROM sys.assemblies a INNER JOIN sys.assembly_files af ON a.assembly_id = af.assembly_id WHERE a.name = @assemblyname";
            using (SqlConnection conn = new SqlConnection("context connection=true"))   //Create current context connection
            {
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    SqlParameter param = new SqlParameter("@assemblyname", SqlDbType.VarChar)
                    {
                        Value = assemblyName
                    };
                    cmd.Parameters.Add(param);
                    cmd.Connection.Open();  //Open the context connetion
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read()) //Iterate through assembly files
                        {
                            string assemblyFileName = reader.GetString(0);  //get assembly file name from the name (first) column
                            SqlBytes bytes = reader.GetSqlBytes(1);         //get assembly binary data from the content (second) column
                            string outputFile = Path.Combine(destinationPath, assemblyFileName);
                            //TODO: LogSqlContext.Pipe.Send(string.Format("Exporting assembly file [{0}] to [{1}]", assemblyFileName, outputFile)); //Send information about exported file back to the calling session
                            using (FileStream byteStream = new FileStream(outputFile, FileMode.CreateNew))
                            {
                                byteStream.Write(bytes.Value, 0, (int)bytes.Length);
                                byteStream.Close();
                            }
                        }
                    }
                    cmd.Dispose();
                }
                conn.Close();
            }
        }
    }
}
