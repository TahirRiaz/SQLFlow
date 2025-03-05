using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SQLFlowCore.ExecParams;
using Microsoft.Data.SqlClient;
using SQLFlowCore.Engine.Utils.Markdown;
using SQLFlowCore.Common;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// Represents a class that provides functionality to execute system documentation prompts.
    /// </summary>
    /// <remarks>
    /// The ExecSysDocPrompt class is used to execute system documentation prompts, log execution details, and handle related events.
    /// It provides methods for executing the prompt, replacing placeholders, generating documentation, making OpenAI API calls, parsing object names, and checking object types.
    /// </remarks>
    public class ExecSysDocPrompt : EventArgs
    {
        private static string _lineageLog = "";
        private static int _total = 1;
        private static Dictionary<string, string> _sampleData;
        #region ExecSysDocPrompt
        /// <summary>
        /// Executes the system documentation prompt.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to log the execution details.</param>
        /// <param name="sqlFlowConString">The SQL Flow connection string.</param>
        /// <param name="objectName">The name of the object to be processed.</param>
        /// <param name="useDbPayload">A boolean indicating whether to use the database payload.</param>
        /// <param name="openAiPayLoad">The payload for the OpenAI API call.</param>
        /// <remarks>
        /// This method is responsible for opening the SQL Flow connection, executing the system documentation prompt, and logging the execution details.
        /// </remarks>
        public static void Exec(StreamWriter logWriter, string sqlFlowConString, string objectName, bool useDbPayload, OpenAIPayLoad openAiPayLoad)
        {
            _sampleData = new Dictionary<string, string>();

            if (objectName == null)
            {
                objectName = "";
            }

            _lineageLog = "";
            new object();
            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };

            string dbName = conStringParser.ConBuilderMsSql.InitialCatalog;

            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;
            int commandTimeOutInSek = 180;

            long logDurationPre = 0;
            var totalTime = new Stopwatch();
            new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    sqlFlowCon.Open();
                    //objectName = "[flw].[Ingestion]";

                    string cmdPromptSql = @"SELECT PromptName FROM flw.SysAIPrompt ORDER BY RunOrder";

                    DataTable promtTbl = CommonDB.FetchData(sqlFlowCon, cmdPromptSql, commandTimeOutInSek);

                    foreach (DataRow dr in promtTbl.Rows)
                    {
                        string pName = dr["PromptName"]?.ToString() ?? string.Empty;
                        string objectCountCmd = @$"flw.GetRVSysDocPrompt @ObjectName='{objectName}', @PromptName='{pName}'";

                        DataTable dataTbls = CommonDB.FetchData(sqlFlowCon, objectCountCmd, commandTimeOutInSek);

                        _total = dataTbls.Rows.Count;

                        if (_total > 0)
                        {
                            logWriter.Write($"## Total number of objects in {pName} {_total.ToString()} {Environment.NewLine}");
                            logWriter.Flush();

                            //Run Prompt Logic
                            NewMethod(logWriter, useDbPayload, openAiPayLoad, dataTbls, sqlFlowCon, objectName);
                        }
                    }

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    _lineageLog += e.Message + Environment.NewLine + e.StackTrace + Environment.NewLine;
                    logWriter.Write(e.Message);
                }

                totalTime.Stop();
                logDurationPre = totalTime.ElapsedMilliseconds / 1000;
                _lineageLog += Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}",
                    logDurationPre.ToString(), Environment.NewLine);
                logWriter.Write(Environment.NewLine + string.Format("## Info: Total processing time {0} (sec) {1}",
                    logDurationPre.ToString(), Environment.NewLine));
                logWriter.Flush();
            }

            //return result;
        }

        private static void NewMethod(StreamWriter logWriter, bool useDbPayload, OpenAIPayLoad openAiPayLoad,
            DataTable dataTbls, SqlConnection sqlFlowCon, string objectName)
        {
            try
            {
                foreach (DataRow dr in dataTbls.Rows)
                {
                    OpenAIPayLoad aiPayLoad = JsonConvert.DeserializeObject<OpenAIPayLoad>(dr["PayLoadJson"]?.ToString() ?? string.Empty);
                    string srcObjectType = dr["ObjectType"]?.ToString() ?? string.Empty;
                    string promptName = dr["PromptName"]?.ToString() ?? string.Empty;
                    objectName = dr["ObjectName"]?.ToString() ?? string.Empty;

                    if (IsSampleObjectType(srcObjectType))
                    {
                        if (_sampleData.ContainsKey(objectName) == false)
                        {
                            string sampleDataQuery = @$"SELECT TOP 5 * FROM {objectName}";

                            if (srcObjectType.Equals("column", StringComparison.InvariantCultureIgnoreCase))
                            {
                                SchemaTableColumnLocal obj = ParseObjName(objectName);
                                sampleDataQuery = @$"SELECT DISTINCT TOP 5  CAST({obj.Column} as nvarchar(max)) FROM {obj.Schema}.{obj.Table}";
                            }

                            string tempMarkup = SqlToMarkdown.ConvertTableToMarkdown(sqlFlowCon, sampleDataQuery);
                            _sampleData.Add(objectName, tempMarkup);
                            logWriter.Write($"Sample data fetched from {objectName} {Environment.NewLine}");
                            logWriter.Flush();
                        }
                    }

                    if (useDbPayload == false)
                    {
                        aiPayLoad = openAiPayLoad;
                    }

                    string markupTable = "";
                    if (_sampleData.ContainsKey(objectName))
                    {
                        markupTable = _sampleData[objectName].ToString();
                    }

                    var prompt = ReplacePlaceholders(dr, aiPayLoad, markupTable);
                    var promptDb = prompt;
                    string response = GenerateDocumenation(dr, aiPayLoad, markupTable, prompt).Result;

                    if (promptName.Contains("sqlflow-question-", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string cmdSql = @"[flw].[AddSysDoc]";
                        using (var command = new SqlCommand(cmdSql, sqlFlowCon))
                        {
                            command.CommandType = CommandType.StoredProcedure;
                            command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = objectName;
                            command.Parameters.Add("@Question", SqlDbType.NVarChar).Value = response;
                            command.Parameters.Add("@PromptQuestion", SqlDbType.NVarChar).Value = promptDb;

                            command.ExecuteNonQuery();
                        }
                    }
                    else if (promptName.Contains("sqlflow-summary-", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string cmdSql = @"[flw].[AddSysDoc]";
                        using (var command = new SqlCommand(cmdSql, sqlFlowCon))
                        {
                            command.CommandType = CommandType.StoredProcedure;
                            command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = objectName;
                            command.Parameters.Add("@Summary", SqlDbType.NVarChar).Value = response;
                            command.Parameters.Add("@PromptSummary", SqlDbType.NVarChar).Value = promptDb;

                            command.ExecuteNonQuery();
                        }
                    }
                    else if (promptName.Contains("sqlflow-label-", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string cmdSql = @"[flw].[AddSysDoc]";
                        using (var command = new SqlCommand(cmdSql, sqlFlowCon))
                        {
                            command.CommandType = CommandType.StoredProcedure;
                            command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = objectName;
                            command.Parameters.Add("@Label", SqlDbType.NVarChar).Value = response;
                            command.ExecuteNonQuery();
                        }

                    }
                    else
                    {
                        string cmdSql = @"[flw].[AddSysDoc]";
                        using (var command = new SqlCommand(cmdSql, sqlFlowCon))
                        {
                            command.CommandType = CommandType.StoredProcedure;
                            command.Parameters.Add("@ObjectName", SqlDbType.NVarChar).Value = objectName;
                            command.Parameters.Add("@Description", SqlDbType.NVarChar).Value = response;
                            command.Parameters.Add("@PromptDescription", SqlDbType.NVarChar).Value = promptDb;


                            command.ExecuteNonQuery();
                        }

                    }

                    _lineageLog += $"## {promptName} generated for {objectName}{Environment.NewLine}";
                    logWriter.Write($"## {promptName} generated for {objectName}{Environment.NewLine}");
                    logWriter.Flush();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                logWriter.Write($"## Error Log " + Environment.NewLine + $"{e.Message}");
                logWriter.Flush();
            }
            finally
            {

            }
        }

        #endregion ExecSysDocPrompt

        /// <summary>
        /// Replaces placeholders in the prompt template with the corresponding values from the DataRow.
        /// </summary>
        /// <param name="row">The DataRow containing the data to replace placeholders.</param>
        /// <param name="openAiPayLoad">The OpenAIPayLoad object containing the prompt template.</param>
        /// <param name="markupTable">The string representation of the markup table.</param>
        /// <returns>A string with placeholders replaced with the corresponding values from the DataRow.</returns>
        internal static string ReplacePlaceholders(DataRow row, OpenAIPayLoad openAiPayLoad, string markupTable)
        {
            var promptTemplate = openAiPayLoad.prompt;
            return promptTemplate

                    .Replace("@ParentCode", row["ParentCode"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@Code", row["Code"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@SampleData", markupTable, StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@Relations", row["Relations"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@DependsOn", row["DependsOn"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@DependsOnBy", row["DependsOnBy"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@AdditionalInfo", row["AdditionalInfo"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@ObjectName", row["ObjectName"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@ObjectType", row["ObjectType"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@ObjectType", row["ObjectType"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@Description", row["Description"].ToString(), StringComparison.CurrentCultureIgnoreCase)
                    .Replace("@Summary", row["Summary"].ToString(), StringComparison.CurrentCultureIgnoreCase)


                ;
        }

        /// <summary>
        /// Generates documentation for a given DataRow, OpenAIPayLoad, and markupTable.
        /// </summary>
        /// <param name="dr">The DataRow containing the data to be documented.</param>
        /// <param name="payLoad">The OpenAIPayLoad object containing the AI model parameters.</param>
        /// <param name="markupTable">The string representation of the markup table.</param>
        /// <returns>A Task resulting in a string that represents the generated documentation.</returns>
        internal static async Task<string> GenerateDocumenation(DataRow dr, OpenAIPayLoad payLoad, string markupTable, string prompt)
        {
            string rValue = "";

            string secretKey = dr["SecretKey"]?.ToString() ?? string.Empty;

            var response = await CallOpenAiApi(payLoad, prompt, secretKey);
            var responseObj = JsonConvert.DeserializeObject<ChatCompletionResponse>(response);
            if (responseObj?.Choices != null && responseObj.Choices.Count > 0)
            {
                rValue = responseObj.Choices[0].Message.Content;
            }
            await Task.Delay(2000); // Delay after each API call

            return rValue;
        }

        /// <summary>
        /// Calls the OpenAI API to generate a response based on the provided payload and prompt.
        /// </summary>
        /// <param name="openAiPayLoad">The payload containing the parameters for the OpenAI API call.</param>
        /// <param name="finalPrompt">The final prompt to be sent to the OpenAI API.</param>
        /// <param name="secretKey">The secret key used for authentication with the OpenAI API.</param>
        /// <returns>A Task that represents the asynchronous operation. The task result contains the response from the OpenAI API as a string.</returns>
        internal static async Task<string> CallOpenAiApi(OpenAIPayLoad openAiPayLoad, string finalPrompt, string secretKey)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secretKey);

            // model = $"{openAIPayLoad.model}",
            //max_tokens = 100, // Adjust as needed
            var data = new
            {
                model = $"{openAiPayLoad.model}",
                openAiPayLoad.max_tokens,
                openAiPayLoad.temperature,
                openAiPayLoad.top_p,
                openAiPayLoad.frequency_penalty,
                openAiPayLoad.presence_penalty,
                messages = new[]
                {
                    new { role = "user", content =  $"{finalPrompt}" } // Example of starting a conversation
                }
            };

            string jsonbody = JsonConvert.SerializeObject(data);
            var content = new StringContent(jsonbody, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"https://api.openai.com/v1/chat/completions", content); // Replace {engine_id} with the appropriate engine ID
            var responseString = await response.Content.ReadAsStringAsync();

            return responseString;
        }

        /// <summary>
        /// Parses the input string to extract the schema, table, and column information.
        /// </summary>
        /// <param name="input">The input string to parse. It can be in one of the following formats: [Table].[Column], [Schema].[Table].[Column], or just [Table].</param>
        /// <returns>A <see cref="SchemaTableColumnLocal"/> object that contains the parsed schema, table, and column information.</returns>
        public static SchemaTableColumnLocal ParseObjName(string input)
        {
            var parts = input.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            var result = new SchemaTableColumnLocal();

            switch (parts.Length)
            {
                case 0:
                case 1: // Format: [Table].[Column]
                    result.Table = input;
                    break;
                case 2: // Format: [Table].[Column]
                    result.Table = parts[0];
                    result.Column = parts[1];
                    break;
                case 3: // Format: [Schema].[Table].[Column]
                    result.Schema = parts[0];
                    result.Table = parts[1];
                    result.Column = parts[2];
                    break;
            }

            return result;
        }

        /// <summary>
        /// Determines whether the given object type is a sample object.
        /// </summary>
        /// <param name="objectTypeString">The type of the object as a string.</param>
        /// <returns>Returns true if the object type is either 'table' or 'column', otherwise returns false.</returns>
        public static bool IsSampleObjectType(string objectTypeString)
        {
            bool isSampleObject = false;
            switch (objectTypeString.Trim().ToLower())
            {
                case "table":
                    return true;
                case "column":
                    return true;
                default:
                    return isSampleObject;
            }
        }


    }

    /// <summary>
    /// Represents a response from a chat completion API call.
    /// </summary>
    /// <remarks>
    /// The ChatCompletionResponse class is used to store the choices returned from a chat completion API call.
    /// It provides a property for accessing the list of choices.
    /// </remarks>
    internal class ChatCompletionResponse
    {
        [JsonProperty("choices")]
        internal List<Choice> Choices { get; set; }
    }

    /// <summary>
    /// Represents a choice returned from an API call.
    /// </summary>
    /// <remarks>
    /// The Choice class is used to store the message associated with a choice returned from an API call.
    /// It provides a property for accessing the message of the choice.
    /// </remarks>
    internal class Choice
    {
        [JsonProperty("message")]
        internal Message Message { get; set; }
    }

    /// <summary>
    /// Represents a message returned from an API call.
    /// </summary>
    /// <remarks>
    /// The Message class is used to store the content of a message returned from an API call.
    /// It provides a property for accessing the content of the message.
    /// </remarks>
    internal class Message
    {
        [JsonProperty("content")]
        internal string Content { get; set; }
    }


    /// <summary>
    /// Represents a local schema table column in the database.
    /// </summary>
    /// <remarks>
    /// The SchemaTableColumnLocal class is used to store the schema, table, and column information of a database object.
    /// It provides properties for accessing the schema name, table name, and column name.
    /// </remarks>
    public class SchemaTableColumnLocal
    {
        /// <summary>
        /// Gets or sets the name of the schema.
        /// </summary>
        public string Schema { get; set; }
        /// <summary>
        /// Gets or sets the name of the table.
        /// </summary>
        public string Table { get; set; }
        /// <summary>
        /// Gets or sets the name of the column.
        /// </summary>
        public string Column { get; set; }
    }

}