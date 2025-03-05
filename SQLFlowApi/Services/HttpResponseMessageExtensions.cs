using System.Text.Json;
using System.Xml.Linq;
namespace SQLFlowApi.Services.Extention
{
    public class HttpResponseMessageExtensions
    {
        public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        {
            // Ensure the response is successful; otherwise, throw an exception.
            response.EnsureSuccessStatusCode();
            // Deserialize JSON content to the specified type T.
            return await DeserializeJsonContentAsync<T>(response.Content);
        }
        private static async Task<T> DeserializeJsonContentAsync<T>(HttpContent content)
        {
            // Return default(T) if content is null.
            if (content == null) return default;
            try
            {
                // Read the stream only if it is not empty.
                using (Stream stream = await content.ReadAsStreamAsync())
                {
                    if (stream.Length > 0)
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            PropertyNameCaseInsensitive = true,
                        };
                        // Deserialize the JSON content to the specified type T.
                        return await JsonSerializer.DeserializeAsync<T>(stream, options);
                    }
                    else
                    {
                        // Return default(T) if the stream is empty.
                        return default;
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle the parsing error for different content types.
                await HandleContentParsingError(content, ex);
                // Re-throw if the specific content errors are not handled.
                throw;
            }
        }
        private static async Task HandleContentParsingError(HttpContent content, Exception originalException)
        {
            string responseString = await content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(responseString))
            {
                if (content.Headers.ContentType.MediaType == "application/json")
                {
                    // Parse JSON error and throw a detailed exception if possible.
                    ParseAndThrowJsonError(responseString);
                }
                else
                {
                    // Parse XML error and throw a detailed exception if possible.
                    ParseAndThrowXMLError(responseString);
                }
            }
            // If no specific content errors are found, throw the original exception.
            throw originalException;
        }
        private static void ParseAndThrowJsonError(string jsonString)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(jsonString))
                {
                    if (document.RootElement.TryGetProperty("error", out JsonElement errorElement) &&
                        errorElement.TryGetProperty("message", out JsonElement messageElement))
                    {
                        throw new Exception(messageElement.GetString());
                    }
                }
            }
            catch
            {
                // If there's an error parsing the JSON error, ignore it and throw a generic error.
                throw new Exception("Unable to parse the JSON response.");
            }
        }
        private static void ParseAndThrowXMLError(string xmlString)
        {
            try
            {
                XDocument xdocument = XDocument.Parse(xmlString);
                XElement errorElement = xdocument.Descendants().FirstOrDefault(d => d.Name.LocalName == "error" || d.Name.LocalName == "internalexception");
                if (errorElement != null)
                {
                    throw new Exception(errorElement.Value);
                }
            }
            catch
            {
                // If there's an error parsing the XML error, ignore it and throw a generic error.
                throw new Exception("Unable to parse the XML response.");
            }
        }
    }
}
