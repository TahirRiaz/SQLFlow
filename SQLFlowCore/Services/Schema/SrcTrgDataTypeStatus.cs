using System.Data;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents the status of data type comparison between source and target in SQL Flow Core Engine.
    /// </summary>
    /// <remarks>
    /// This class is used in the process of comparing column data types between source and target SQL connections.
    /// </remarks>
    internal class SrcTrgDataTypeStatus
    {
        /// <summary>
        /// Gets or sets the comparison data as a DataTable.
        /// </summary>
        internal DataTable compData { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether there is a data type mismatch.
        /// </summary>
        internal bool MisMatchDataType { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether there is a critical mismatch.
        /// </summary>
        internal bool CriticalMismatch { get; set; }
        /// <summary>
        /// Gets or sets the SQL command to alter the data type.
        /// </summary>
        internal string AlterCmd { get; set; }
        /// <summary>
        /// Gets or sets the SQL command to add a new column.
        /// </summary>
        internal string AddCmd { get; set; }
        /// <summary>
        /// Gets or sets the XML string that contains warnings about data types.
        /// </summary>
        internal string DataTypeWarningXML { get; set; }
        /// <summary>
        /// Gets or sets the XML string that contains warnings about columns.
        /// </summary>
        internal string ColumnWarningXML { get; set; }
    }



}
