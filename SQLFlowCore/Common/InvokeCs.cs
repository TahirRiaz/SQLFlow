using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Azure.Storage.Files.DataLake;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Renci.SshNet;
using System.Linq;
using System.Xml;
using Microsoft.Extensions.Logging;
using SQLFlowCore.Services;
using SQLFlowCore.Services.AzureResources;
using SQLFlowCore.Logger;

namespace SQLFlowCore.Common
{
    internal class InvokeCs
    {
        public static async Task<bool> DynamicCode(RealTimeLogger logger, string csharpCode, AzureKeyVaultManager keyVaultManager, DataLakeFileSystemClient srcFileSystemClient, DataLakeFileSystemClient trgFileSystemClient)
        {
            bool rvalue = false;
            string code = BuildDynamicCode(csharpCode);
            string paramData = "data";
            var options = ScriptOptions.Default
                    .AddReferences(
                        // .NET standard libraries
                        typeof(object).Assembly, // System.Private.CoreLib.dll
                        typeof(Console).Assembly, // System.Console.dll
                        typeof(DataTable).Assembly, // System.Data.Common.dll
                        typeof(SqlConnection).Assembly, // Microsoft.Data.SqlClient.dll
                        typeof(XmlDocument).Assembly, // System.Xml.ReaderWriter.dll
#if NETFRAMEWORK
                        typeof(System.Net.AuthenticationManager).Assembly, // System.Net.Requests.dll
#else
                        typeof(HttpClientHandler).Assembly, // Alternative for AuthenticationManager in .NET Core/.NET 5+
#endif
                        typeof(System.Xml.Linq.XElement).Assembly, // System.Private.Xml.Linq.dll
                        typeof(HttpClient).Assembly, // System.Net.Http.dll
                        typeof(AuthenticationHeaderValue).Assembly, // System.Net.Http.Headers.dll
                        typeof(File).Assembly, // System.IO.FileSystem.dll
                        typeof(Thread).Assembly, // System.Threading.Thread.dll
                        typeof(Enumerable).Assembly, // System.Linq.dll
                        typeof(Task).Assembly, // System.Threading.Tasks.dll
                                                                      // Newtonsoft.Json
                        typeof(JsonConvert).Assembly, // Newtonsoft.Json.dll
                                                                      // Azure SDKs and related libraries
                        typeof(Azure.JsonPatchDocument).Assembly,
                        typeof(DataLakeServiceClient).Assembly,
                        typeof(Azure.Storage.Files.DataLake.Models.FileSystemItem).Assembly,
                        // Sqlflow libraries
                        typeof(ProcessInvoke).Assembly,
                        //other libraries
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

            var script = CSharpScript.Create<string>(
                code: code,
                options: options,
                globalsType: typeof(InvokeCsGlobals));

            // Fixed: Await the asynchronous call instead of blocking with .Result.
            var result = await script.RunAsync(new InvokeCsGlobals { logger = logger, data = paramData, keyVaultManager = keyVaultManager, trgFileSystemClient = trgFileSystemClient, srcFileSystemClient = srcFileSystemClient });

            // Execute the method and get the DataTable
            return rvalue;
        }

        private static string BuildDynamicCode(string additionalMethod)
        {
            string baseClassCode = @"
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
            //Newtonsoft.Json
            using Newtonsoft.Json;
            using Newtonsoft.Json.Linq;
            //Azure related namespaces
            using Azure;
            using Azure.Storage.Files.DataLake;
            using Azure.Storage.Files.DataLake.Models;
            //Other namespaces
            using Renci.SshNet;
            using SQLFlowCore.Logger;
            using SQLFlowCore.Services.AzureResources;
            "
             + additionalMethod;

            return baseClassCode;
        }
    }

    public class InvokeCsGlobals
    {
        public string data;
        public AzureKeyVaultManager keyVaultManager;
        public DataLakeFileSystemClient srcFileSystemClient;
        public DataLakeFileSystemClient trgFileSystemClient;
        public RealTimeLogger logger = null;
    }
}
