using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using SQLFlowCore.Common;
using SQLFlowCore.Services.Schema;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Represents a set of parameters and methods for parsing SQL scripts.
    /// </summary>
    /// <remarks>
    /// This class includes methods for checking duplicate parameters, 
    /// and retrieving parameters from SQL scripts. It also includes a nested class 
    /// `ParameterVisitor` which is a visitor for traversing SQL script fragments.
    /// </remarks>
    public class ParserParameters
    {
        /// <summary>
        /// Represents a visitor that traverses SQL script fragments and collects parameters.
        /// </summary>
        /// <remarks>
        /// This class inherits from the TSqlFragmentVisitor and overrides its methods to visit
        /// different types of SQL parameters including procedure parameters, declared variables, 
        /// and variable references. It checks for duplicate parameters and adds unique parameters 
        /// to a list. Each parameter is represented as a ParameterObject.
        /// </remarks>
        public class ParameterVisitor : TSqlFragmentVisitor
        {
            internal List<ParameterObject> Parameters { get; } = new();

            // For stored procedures, functions, and triggers
            /// <summary>
            /// Visits a procedure parameter in the SQL script.
            /// </summary>
            /// <param name="parameter">The procedure parameter to visit.</param>
            /// <remarks>
            /// This method checks if the parameter is already present in the list of parameters. If not, it creates a new SqlParameter and ParameterObject for the parameter and adds it to the list. The SqlParameter is created with the parameter's name, value, and data type. The ParameterObject is created with the parameter's name, value, data type, and additional properties.
            /// </remarks>
            public override void Visit(ProcedureParameter parameter)
            {
                if (DupeParameter(Parameters, parameter.VariableName.Value) == false)
                {
                    SqlParameter p = new SqlParameter
                    {
                        ParameterName = parameter.VariableName.Value,
                        Value = parameter.Value?.ToString(),
                        DbType = DataTypeMapper.MapToDbType(parameter.DataType)
                    };

                    Parameters.Add(new ParameterObject
                    {
                        Name = parameter.VariableName.Value,
                        Value = parameter.Value?.ToString(),
                        DataTypeName = parameter.DataType.Name.BaseIdentifier.Value,
                        DataType = DataTypeMapper.MapToDotNetType(parameter.DataType),
                        sqlParameter = p,
                        ParameterType = "MSSQL",
                        DefaultValue = "0"
                    });
                }
            }

            // For standard DECLARE statements in scripts
            /// <summary>
            /// Visits a declared variable in the SQL script.
            /// </summary>
            /// <param name="variable">The declared variable to visit.</param>
            /// <remarks>
            /// This method creates a new SqlParameter and ParameterObject for the declared variable and adds it to the list of parameters if it's not a duplicate. The SqlParameter is created with the variable's name, value, and data type. The ParameterObject is created with the variable's name, value, data type, and additional properties.
            /// </remarks>
            public override void Visit(DeclareVariableElement variable)
            {
                SqlParameter p = new SqlParameter
                {
                    ParameterName = variable.VariableName.Value,
                    Value = variable.Value?.ToString(),
                    DbType = DataTypeMapper.MapToDbType(variable.DataType)
                };

                if (DupeParameter(Parameters, variable.VariableName.Value) == false)
                {
                    Parameters.Add(new ParameterObject
                    {
                        Name = variable.VariableName.Value,
                        Value = variable.Value?.ToString(),
                        DataTypeName = variable.DataType.Name.BaseIdentifier.Value,
                        DataType = DataTypeMapper.MapToDotNetType(variable.DataType),
                        sqlParameter = p,
                        ParameterType = "MSSQL",
                        DefaultValue = "0"
                    });
                }
            }


            // Visiting other scenarios where @variables might appear
            /// <summary>
            /// Visits a variable reference in the SQL script.
            /// </summary>
            /// <param name="variable">The variable reference to visit.</param>
            /// <remarks>
            /// This method checks if the variable is already present in the list of parameters. If not, it creates a new SqlParameter and ParameterObject for the variable and adds it to the list. The SqlParameter is created with the variable's name, a default value of "0", and a DbType of String. The ParameterObject is created with the variable's name, a default value of "0", a DbType of String, and additional properties.
            /// </remarks>
            public override void Visit(VariableReference variable)
            {
                if (DupeParameter(Parameters, variable.Name) == false)
                {
                    SqlParameter p = new SqlParameter
                    {
                        ParameterName = variable.Name,
                        DbType = DbType.String,
                        Value = "0"
                    };

                    Parameters.Add(new ParameterObject
                    {
                        Name = variable.Name,
                        DataTypeName = "",
                        sqlParameter = p,
                        ParameterType = "MSSQL",
                        DefaultValue = "0"
                    });
                }
            }
        }

        /// <summary>
        /// Checks if a parameter with the specified name already exists in the provided list of parameters.
        /// </summary>
        /// <param name="parameters">The list of parameters to check for duplicates.</param>
        /// <param name="ParamName">The name of the parameter to check for duplication.</param>
        /// <returns>Returns true if a parameter with the same name exists in the list, otherwise false.</returns>
        internal static bool DupeParameter(List<ParameterObject> parameters, string ParamName)
        {
            bool rValue = false;

            foreach (ParameterObject item in parameters)
            {
                if (item.Name == ParamName)
                {
                    rValue = true;
                }
            }

            return rValue;
        }

        /// <summary>
        /// Parses the provided T-SQL script and extracts the parameters used within it.
        /// </summary>
        /// <param name="tsqlScript">The T-SQL script to parse.</param>
        /// <returns>A list of <see cref="ParameterObject"/> instances representing the parameters used in the script.</returns>
        /// <exception cref="InvalidOperationException">Thrown when there are errors encountered during parsing.</exception>
        /// <remarks>
        /// This method uses the <see cref="ParserForSql.GetParser"/> to get an appropriate parser version for the provided script. 
        /// It then parses the script and visits each fragment using the <see cref="ParameterVisitor"/> to collect the parameters.
        /// </remarks>
        internal static List<ParameterObject> GetParametersFromSql(string tsqlScript)
        {
            var parser = ParserForSql.GetParser(tsqlScript); // Use appropriate parser version
            IList<ParseError> errors;
            var fragment = parser.Parse(new System.IO.StringReader(tsqlScript), out errors);

            if (errors != null && errors.Count > 0)
            {
                var errorMessage = string.Join(", ", errors.Select(e => e.Message));
                throw new InvalidOperationException($"Errors encountered during parsing: {errorMessage}");
            }

            var visitor = new ParameterVisitor();
            fragment.Accept(visitor);

            return visitor.Parameters;
        }
    }
}
