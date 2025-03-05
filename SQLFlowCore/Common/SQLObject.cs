using Microsoft.SqlServer.Management.Sdk.Sfc;

namespace SQLFlowCore.Common
{
    /// <summary>
    /// The SQLObject class represents a SQL object.
    /// </summary>
    public class SQLObject
    {
        /// <summary>
        /// Gets or sets the database name of the SQL object.
        /// </summary>
        internal string ObjDatabase { get; set; }

        /// <summary>
        /// Gets or sets the schema name of the SQL object.
        /// </summary>
        internal string ObjSchema { get; set; }

        /// <summary>
        /// Gets or sets the name of the SQL object.
        /// </summary>
        internal string ObjName { get; set; }

        /// <summary>
        /// Gets or sets the full name of the SQL object.
        /// </summary>
        internal string ObjFullName { get; set; }

        /// <summary>
        /// Gets or sets the type of the SQL object.
        /// </summary>
        internal string SQLFlowObjectType { get; set; }

        /// <summary>
        /// Gets or sets the URN of the SQL object.
        /// </summary>
        internal Urn ObjUrn { get; set; }

        /// <summary>
        /// Gets or sets the DDL of the SQL object.
        /// </summary>
        internal string ObjDDL { get; set; }
    }


}
