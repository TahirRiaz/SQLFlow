using System;
using System.Data.SqlTypes;
using System.IO;
using System.Text;

namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Provides methods for file input/output operations.
    /// </summary>
    internal class FileIo
    {
        #region CreateFile
        /// <summary>
        /// Creates a file with the specified name and writes the provided text into it.
        /// </summary>
        /// <param name="trgFile">The name of the file to be created.</param>
        /// <param name="text">The text to be written into the file.</param>
        /// <returns>A string indicating the success of the operation. If an exception occurs, the exception message is returned.</returns>
        internal static SqlString CreateFile(SqlString trgFile, SqlChars text)
        {
            SqlString str = "true";
            try
            {
                StreamWriter writer = null;
                File.Delete(trgFile.ToString());
                FileStream stream = File.Open(trgFile.ToString(), FileMode.OpenOrCreate, FileAccess.Write);
                writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(text.ToSqlString());
                writer.Close();
                writer = null;
                stream.Close();
                stream = null;
            }
            catch (Exception exception)
            {
                str = exception.Message;
            }
            return str;
        }
        #endregion CreateFile
    }
}

