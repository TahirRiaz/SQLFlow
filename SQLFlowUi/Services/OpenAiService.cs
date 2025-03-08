using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json; 

namespace SQLFlowUi
{
    public class OpenAiService
    {
        private readonly HttpClient _httpClient;
        private string ApiKey = Environment.GetEnvironmentVariable("SQLFlowOpenAiApiKey");

        public OpenAiService()
        {
            _httpClient = new HttpClient();
            // Set up HttpClient instance with OpenAI API key
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        }

        public async Task<string> CallChatCompletionAsync(string language, string inputText)
        {
            // Format input text into your template
            string promptTemplate = @$"CONTEXT:
## Role:
You are a technical writer for a Data Engineering Experts Company. Your task is to generate technical documentation for the database schema used in the SQLFlow Framework. 

## Background Info:
SQLFlow framework  is a state-of-the-art, metadata-centric solution for building and managing enterprise data warehouses. Ideal for enterprises looking to develop efficient, compliant, and scalable data warehouses.

## SourceText:
${inputText}

Can you rewrite the SourceText bellow with logical flow and grammar? Please note that the source text is a transcription that may contain several misspelled words and wrong punctuation. Output only the final text.
The output should avoid jargon. Be objective, and do NOT use the words ""SQLFlow"", ""framework"", ""Crucial"", ""essential"", ""pivotal"", ""significant"", ""warehouses"", ""role"" and ""Critical"". 
";

            if (language == "no")
            {
                promptTemplate = @$"
## Rolle:
Du er en teknisk skribent for et selskap som spesialiserer seg på dataingeniørfag. Oppgaven din er å generere teknisk dokumentasjon for databaseskjemaet som brukes i SQLFlow-rammeverket.

## Background Info:
SQLFlow-rammeverket er en toppmoderne, metadata-sentrert løsning for å bygge og administrere bedriftsdataarkiver. Ideell for bedrifter som ønsker å utvikle effektive, kompatible og skalerbare dataarkiver.

## Kildetekst:
${inputText}

Kan du skrive om Kildetekst under med logisk flyt og grammatikk? Vær oppmerksom på at kildeteksten er en transkripsjon som kan inneholde flere feilstavede ord og feil tegnsetting. Bare send ut den endelige teksten.
Utdataen bør unngå fagspråk. Vær objektiv, og IKKE bruk ordene ""SQLFlow"", ""datavarehus"", ""avgjørende"", ""betydelig"", ""rolle"" og ""kritisk"".
${inputText}";
            }

            var requestData = new
            {
                model = "gpt-4-0125-preview", // Replace with the model you intend to use
                temperature = 0.0,
                frequency_penalty = 0.0,
                presence_penalty = 0,
                max_tokens = 4065,
                messages = new[]
                {
                    new { role = "user", content =  $"{promptTemplate}" } // Example of starting a conversation
                }
            };


            string jsonbody = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(jsonbody, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"https://api.openai.com/v1/chat/completions", content); // Replace {engine_id} with the appropriate engine ID
            var responseString = await response.Content.ReadAsStringAsync();

            string rValue = "";
            var responseObj = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseString);
            if (responseObj?.Choices != null && responseObj.Choices.Count > 0)
            {
                rValue = responseObj.Choices[0].Message.Content;
            }

            return rValue;
        }


        public async Task<string> CreateTitleAsync(string language, string inputText)
        {
            // Format input text into your template
            string promptTemplate = @$"
CONTEXT:
## Role:
You are a technical writer for a Data Engineering Experts Company. Your task is to generate technical documentation for the database schema used in the SQLFlow Framework. 

## Background Info:
SQLFlow framework  is a state-of-the-art, metadata-centric solution for building and managing enterprise data warehouses. Ideal for enterprises looking to develop efficient, compliant, and scalable data warehouses.

## SourceText:
${inputText}

Can you suggest a very short summary title for the SourceText bellow? Do not use : ""in SQLFlow"".
The output should avoid jargon. Be objective, and do NOT use the words ""SQLFlow"", ""framework"", ""Crucial"", ""essential"", ""pivotal"", ""significant"", ""warehouses"", ""role"" and ""Critical"". 
";

            if (language == "no")
            {
                promptTemplate = @$"
## Rolle:
Du er en teknisk skribent for et selskap som spesialiserer seg på dataingeniørfag. Oppgaven din er å generere teknisk dokumentasjon for databaseskjemaet som brukes i SQLFlow-rammeverket.

## Background Info:
SQLFlow-rammeverket er en toppmoderne, metadata-sentrert løsning for å bygge og administrere bedriftsdataarkiver. Ideell for bedrifter som ønsker å utvikle effektive, kompatible og skalerbare dataarkiver.

## Kildetekst:
${inputText}

Kan du foreslå en veldig kort oppsummerende tittel for Kildetekst nedenfor? Ikke bruk : ""i SQLFlow""
Utdataen bør unngå fagspråk. Vær objektiv, IKKE bruk ordene ""SQLFlow"", ""datavarehus"", ""avgjørende"", ""betydelig"", ""rolle"" og ""kritisk"".
";
            }

            var requestData = new
            {
                model = "gpt-4-0125-preview", // Replace with the model you intend to use
                temperature = 0.0,
                frequency_penalty = 0.0,
                presence_penalty = 0,
                max_tokens = 4065,
                messages = new[]
                {
                    new { role = "user", content =  $"{promptTemplate}" } // Example of starting a conversation
                }
            };


            string jsonbody = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(jsonbody, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"https://api.openai.com/v1/chat/completions", content); // Replace {engine_id} with the appropriate engine ID
            var responseString = await response.Content.ReadAsStringAsync();

            string rValue = "";
            var responseObj = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseString);
            if (responseObj?.Choices != null && responseObj.Choices.Count > 0)
            {
                rValue = responseObj.Choices[0].Message.Content;
            }

            return rValue;
        }

    }


    internal class ChatCompletionResponse
    {
        [JsonProperty("choices")]
        internal List<Choice> Choices { get; set; }
    }

    internal class Choice
    {
        [JsonProperty("message")]
        internal Message Message { get; set; }
    }

    internal class Message
    {
        [JsonProperty("content")]
        internal string Content { get; set; }
    }

}
