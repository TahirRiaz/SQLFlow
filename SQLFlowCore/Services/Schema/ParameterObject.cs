using MySql.Data.MySqlClient;
using System;
using Microsoft.Data.SqlClient;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents a parameter object used in SQL queries.
    /// </summary>
    internal class ParameterObject
    {
        /// <summary>
        /// Gets or sets the name of the parameter.
        /// </summary>
        internal string Name { get; set; }
        /// <summary>
        /// Gets or sets the value of the parameter.
        /// </summary>
        internal string Value { get; set; }
        /// <summary>
        /// Gets or sets the default value of the parameter.
        /// </summary>
        internal string DefaultValue { get; set; }
        /// <summary>
        /// Gets or sets the name of the data type of the parameter.
        /// </summary>
        internal string DataTypeName { get; set; }
        /// <summary>
        /// Gets or sets the data type of the parameter.
        /// </summary>
        internal Type DataType { get; set; }
        /// <summary>
        /// Gets or sets the type of the parameter (e.g., "MSSQL").
        /// </summary>
        internal string ParameterType { get; set; }
        /// <summary>
        /// Gets or sets the MySqlParameter associated with this parameter object.
        /// </summary>
        internal MySqlParameter mySqlParameter { get; set; }
        /// <summary>
        /// Gets or sets the SqlParameter associated with this parameter object.
        /// </summary>
        internal SqlParameter sqlParameter { get; set; }
    }
}

