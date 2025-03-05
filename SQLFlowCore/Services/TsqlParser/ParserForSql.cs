using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// The `ParserForSql` class is responsible for parsing T-SQL scripts. 
    /// It contains a method `GetParser` that returns a parser for a given T-SQL script.
    /// This class supports multiple versions of SQL Server, from 2005 to 2022.
    /// </summary>
    internal class ParserForSql
    {
        /// <summary>
        /// Returns a T-SQL parser that can successfully parse the provided T-SQL script.
        /// </summary>
        /// <param name="tsqlScript">The T-SQL script to parse.</param>
        /// <returns>A T-SQL parser that can parse the provided T-SQL script.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the T-SQL script cannot be parsed with any known parser versions.</exception>
        internal static TSqlParser GetParser(string tsqlScript)
        {
            // List of parsers in descending order of versions
            List<TSqlParser> parsers = new List<TSqlParser>
            {
                new TSql160Parser(true), // SQL Server 2022
                new TSql150Parser(true), // SQL Server 2019
                new TSql140Parser(true), // SQL Server 2017
                new TSql130Parser(true), // SQL Server 2016
                new TSql120Parser(true), // SQL Server 2014
                new TSql110Parser(true), // SQL Server 2012
                new TSql100Parser(true), // SQL Server 2008
                new TSql90Parser(true)   // SQL Server 2005
            };

            foreach (var parser in parsers)
            {
                IList<ParseError> errors;
                parser.Parse(new System.IO.StringReader(tsqlScript), out errors);
                if (errors == null || errors.Count == 0)
                {
                    return parser; // Successful parsing, return this parser
                }
            }

            throw new InvalidOperationException(@$"T-SQL could not be parsed with any known parser versions.  {tsqlScript}");
        }
    }
}
