using System.Collections.Generic;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents a collection of SQL character data types.
    /// </summary>
    /// <remarks>
    /// This class provides a method to get a list of SQL character data types.
    /// </remarks>
    internal class SQLCharacterTypes
    {
        /// <summary>
        /// Retrieves a list of SQL character data types.
        /// </summary>
        /// <returns>
        /// A list of strings representing SQL character data types.
        /// </returns>
        internal static List<string> CharacterTypes()
        {
            List<string> DataTypes = new List<string>();
            DataTypes.Add("char");
            DataTypes.Add("varchar");
            DataTypes.Add("text");
            DataTypes.Add("nchar");
            DataTypes.Add("nvarchar");
            DataTypes.Add("ntext");
            return DataTypes;
        }

    }
}

