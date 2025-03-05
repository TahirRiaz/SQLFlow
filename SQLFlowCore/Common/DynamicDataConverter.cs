using System.Data;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Newtonsoft.Json;
using Microsoft.Data.SqlClient;
using NCalc;
using System.Linq.Dynamic.Core;
using System.IO.Compression;
using Azure.Storage.Files.DataLake;
using SQLFlowCore.Services;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Xml;
using System;
using System.IO;
using Renci.SshNet;
using SQLFlowCore.Logger;

namespace SQLFlowCore.Common
{
    /// <summary>
    /// Custom exception for handling dynamic code execution errors
    /// </summary>
    public class DynamicCodeException : Exception
    {
        public string SourceCode { get; }
        public string FileName { get; }

        public DynamicCodeException(string message, string sourceCode, string fileName, Exception innerException = null)
            : base(message, innerException)
        {
            SourceCode = sourceCode;
            FileName = fileName;
        }
    }

    internal static class DynamicDataConverter
    {
        /// <summary>
        /// Dynamically compiles and executes user-provided code to transform a string
        /// into a <see cref="DataTable"/>. Includes compile-time and run-time error handling.
        /// </summary>
        /// <param name="toDataTableCode">Code defining the 'ToDataTable' function.</param>
        /// <param name="dataAsString">Input data as a string that the generated code will process.</param>
        /// <param name="fileName">Optional file name to be passed to the script.</param>
        /// <param name="trgSqlCon">Optional SQL connection to be passed to the script.</param>
        /// <returns>A <see cref="DataTable"/> result from executing the script code.</returns>
        /// <exception cref="ArgumentException">Thrown when required arguments are missing or invalid.</exception>
        /// <exception cref="InvalidOperationException">Thrown on script compilation errors.</exception>
        /// <exception cref="Exception">Thrown on script runtime errors.</exception>
        internal static DataTable ToDataTableDynamically(
            RealTimeLogger logger,
            string toDataTableCode,
            string dataAsString,
            string fileName = "",
            SqlConnection trgSqlCon = null)
        {
            try
            {
                // Basic argument validation
                if (string.IsNullOrWhiteSpace(toDataTableCode))
                {
                    throw new ArgumentException("Script code cannot be null or empty.", nameof(toDataTableCode));
                }
                if (string.IsNullOrWhiteSpace(dataAsString))
                {
                    throw new ArgumentException("Data string cannot be null or empty.", nameof(dataAsString));
                }

                // Build the complete script text to be compiled
                string code = BuildDynamicCode(toDataTableCode);

                // Configure script options
                var options = ScriptOptions.Default
                    .AddReferences(
                        // .NET standard libraries
                        typeof(object).Assembly, // System.Private.CoreLib.dll
                        typeof(Console).Assembly, // System.Console.dll
                        typeof(DataTable).Assembly, // System.Data.Common.dll
                        typeof(SqlConnection).Assembly, // Microsoft.Data.SqlClient.dll
                        typeof(XmlDocument).Assembly, // System.Xml.ReaderWriter.dll
#if NETFRAMEWORK
                        typeof(System.Net.AuthenticationManager).Assembly,
#else
                        typeof(HttpClientHandler).Assembly,
#endif
                        typeof(System.Xml.Linq.XElement).Assembly, // System.Private.Xml.Linq.dll
                        typeof(HttpClient).Assembly, // System.Net.Http.dll
                        typeof(AuthenticationHeaderValue).Assembly, // System.Net.Http.Headers.dll
                        typeof(File).Assembly, // System.IO.FileSystem.dll
                        typeof(Thread).Assembly, // System.Threading.Thread.dll
                        typeof(Enumerable).Assembly, // System.Linq.dll
                        typeof(Task).Assembly, // System.Threading.Tasks.dll
                        typeof(System.IO.Compression.GZipStream).Assembly,
                        // Newtonsoft.Json
                        typeof(JsonConvert).Assembly, // Newtonsoft.Json.dll

                        // Azure SDKs and related libraries
                        typeof(Azure.JsonPatchDocument).Assembly,
                        typeof(DataLakeServiceClient).Assembly,
                        typeof(Azure.Storage.Files.DataLake.Models.FileSystemItem).Assembly,

                        
                        
                        // Sqlflow libraries
                        typeof(ProcessInvoke).Assembly,

                        // Renci SSH
                        typeof(AuthenticationMethod).Assembly
                    )
                    .AddImports(
                        "System",
                        "System.Collections.Generic",
                        "System.Data",
                        "Microsoft.Data.SqlClient",
                        "System.Diagnostics",
                        "System.IO",
                        "System.Net",
                        "System.Net.Http",
                        "System.Net.Http.Headers",
                        "System.Text",
                        "System.Threading",
                        "System.Linq",
                        "System.Xml",
                        "System.Xml.Linq",
                        "System.Threading.Tasks",
                        "System.IO.Compression",
                        // Newtonsoft.Json
                        "Newtonsoft.Json",
                        "Newtonsoft.Json.Linq",
                        // Azure related namespaces
                        "Azure",
                        // Other namespaces
                        "Renci.SshNet",
                        "SQLFlowCore.Common", 
                        "SQLFlowCore.Logger",
                        "SQLFlowCore.Services.AzureResources"
                    );

                // Create the script (expects a DataTable return)
                // Append 'return ToDataTable(...)' so that the script will return a DataTable from that method
                var script = CSharpScript.Create<DataTable>(
                    code + "\nreturn ToDataTable(logger, data, fileName, trgSqlCon);",
                    options,
                    globalsType: typeof(Globals));

                // First compile to check for errors
                var compilationDiagnostics = script.Compile();
                var errors = compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    var message = "Script compilation failed with the following errors:\n" +
                                string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
                    throw new DynamicCodeException(
                        message,
                        toDataTableCode,
                        fileName,
                        new CompilationErrorException(
                            string.Join(Environment.NewLine, errors),
                            errors.ToImmutableArray()));
                }

                try
                {
                    // Run the script synchronously (avoid using .Result to reduce deadlock risk)
                    var result = script.RunAsync(new Globals
                    {
                        data = dataAsString,
                        fileName = fileName,
                        trgSqlCon = trgSqlCon,
                        logger = logger
                    }).GetAwaiter().GetResult();

                    // If script ran successfully, return the resulting DataTable
                    return result.ReturnValue;
                }
                catch (CompilationErrorException cex)
                {
                    // Handle cases where script compilation fails at runtime
                    throw new DynamicCodeException(
                        "Script compilation failed at runtime.",
                        toDataTableCode,
                        fileName,
                        cex);
                }
                catch (Exception ex)
                {
                    // Catch any runtime errors from within the script
                    throw new DynamicCodeException(
                        "An error occurred while executing the dynamic script.",
                        toDataTableCode,
                        fileName,
                        ex);
                }
            }
            catch (DynamicCodeException)
            {
                // Re-throw DynamicCodeException as it already contains the context we need
                throw;
            }
            catch (Exception ex)
            {
                // Wrap any other exceptions with our custom exception
                throw new DynamicCodeException(
                    "An unexpected error occurred during script processing.",
                    toDataTableCode,
                    fileName,
                    ex);
            }
        }

        /// <summary>
        /// Builds the base class code by combining a set of using directives and class definitions
        /// with the user-supplied script for 'ToDataTable'.
        /// </summary>
        /// <param name="additionalMethod">User-supplied code that includes a method 'ToDataTable(...)'.</param>
        /// <returns>The complete C# source code as a string.</returns>
        private static string BuildDynamicCode(string additionalMethod)
        {
            // Base class or method definitions that the script might need
            const string baseClassCode = @"
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Threading.Tasks;

// Newtonsoft.Json
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Azure related namespaces
using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;

// Other namespaces
using Renci.SshNet;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Logger;
using System.IO.Compression;
";

            return baseClassCode + additionalMethod;
        }
    }

    /// <summary>
    /// Globally available variables for the Roslyn script
    /// </summary>
    public class Globals
    {
        public string data;
        public string fileName;
        public SqlConnection trgSqlCon;
        public RealTimeLogger logger = null;
    }
}