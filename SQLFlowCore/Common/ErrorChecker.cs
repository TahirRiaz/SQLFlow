using System;
using System.Collections.Generic;

namespace SQLFlowCore.Common
{
    public static class ErrorChecker
    {
        /// <summary>
        /// Checks if the provided text contains any of the predefined keywords.
        /// </summary>
        /// <param name="text">The text to check.</param>
        /// <returns>True if any of the predefined keywords are found in the text, otherwise false.</returns>
        public static (bool hasError, string matchDetails) DetectError(string text)
        {
            // If text is null or empty, there can't be an error
            if (string.IsNullOrEmpty(text))
                return (false, "Text is null or empty");

            // Convert to lowercase once for efficiency
            string lowerText = text.ToLower();

            // 1. Check for explicit error level indicators
            if (lowerText.Contains("[error]"))
                return (true, "Found '[error]' log level indicator");

            if (lowerText.Contains("level=error"))
                return (true, "Found 'level=error' indicator");

            if (lowerText.Contains("severity=error"))
                return (true, "Found 'severity=error' indicator");

            // 2. Check for common error keywords, but only when they appear as complete words
            string[] errorKeywords = new string[]
            {
        "error", "exception", "failed", "failure", "cannot",
        "rejected", "invalid", "incorrect", "unexpected",
        "has not been", "is not supported", "index was out of range"
            };

            foreach (string keyword in errorKeywords)
            {
                // Use regex pattern to match whole words only
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    lowerText,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(keyword)}\b");

                if (matches.Count > 0)
                {
                    // For each match, extract and show context
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        int index = match.Index;
                        int startPos = Math.Max(0, index - 30);
                        int length = Math.Min(60, text.Length - startPos);
                        string context = text.Substring(startPos, length);

                        return (true, $"Found keyword '{keyword}' at position {index}. Context: '...{context}...'");
                    }
                }
            }

            // 3. Check for stack trace patterns
            if (lowerText.Contains("stack trace:"))
                return (true, "Found 'stack trace:' pattern");

            var stackTraceMatch = System.Text.RegularExpressions.Regex.Match(lowerText, @"at .+\.cs:line \d+");
            if (stackTraceMatch.Success)
                return (true, $"Found stack trace pattern: '{stackTraceMatch.Value}'");

            // 4. Check for HTTP error status codes
            var httpStatusMatch = System.Text.RegularExpressions.Regex.Match(lowerText, @"status\s*[:=]\s*(4\d\d|5\d\d)");
            if (httpStatusMatch.Success)
                return (true, $"Found HTTP error status code: '{httpStatusMatch.Value}'");

            var httpCodeMatch = System.Text.RegularExpressions.Regex.Match(lowerText, @"(4\d\d|5\d\d)\s+\w+");
            if (httpCodeMatch.Success)
                return (true, $"Found HTTP error code phrase: '{httpCodeMatch.Value}'");

            // 5. Check for exit codes
            var exitCodeMatch = System.Text.RegularExpressions.Regex.Match(lowerText, @"exit code\s*[:=]\s*[1-9]\d*");
            if (exitCodeMatch.Success)
                return (true, $"Found non-zero exit code: '{exitCodeMatch.Value}'");

            var exitedWithMatch = System.Text.RegularExpressions.Regex.Match(lowerText, @"exited with\s+[1-9]\d*");
            if (exitedWithMatch.Success)
                return (true, $"Found non-zero exit indicator: '{exitedWithMatch.Value}'");

            // 6. Check for Unicode variations of "error" (sometimes hidden in logs)
            string[] unicodeVariations = new string[] {
        "е\u0440\u0440\u043E\u0440", // Cyrillic characters that look like "error"
        "ｅｒｒｏｒ",               // Fullwidth characters 
        "error",                   // With zero-width spaces between letters
    };

            foreach (string variation in unicodeVariations)
            {
                int index = lowerText.IndexOf(variation);
                if (index >= 0)
                {
                    int startPos = Math.Max(0, index - 20);
                    int length = Math.Min(40, text.Length - startPos);
                    string context = text.Substring(startPos, length);

                    return (true, $"Found Unicode variation of 'error' at position {index}. Context: '...{context}...'");
                }
            }

            // 7. Character by character inspection
            const int CONTEXT_SIZE = 10;
            for (int i = 0; i < text.Length - 4; i++)
            {
                if ((text[i] == 'e' || text[i] == 'E') &&
                    (text[i + 1] == 'r' || text[i + 1] == 'R') &&
                    (text[i + 2] == 'r' || text[i + 2] == 'R') &&
                    (text[i + 3] == 'o' || text[i + 3] == 'O') &&
                    (text[i + 4] == 'r' || text[i + 4] == 'R'))
                {
                    int startPos = Math.Max(0, i - CONTEXT_SIZE);
                    int endPos = Math.Min(text.Length, i + 5 + CONTEXT_SIZE);
                    string context = text.Substring(startPos, endPos - startPos);

                    return (true, $"Found 'error' sequence at position {i}. ASCII values: [{(int)text[i]},{(int)text[i + 1]},{(int)text[i + 2]},{(int)text[i + 3]},{(int)text[i + 4]}]. Context: '...{context}...'");
                }
            }

            // No error patterns found
            return (false, "No error patterns detected");
        }

        // Wrapper function to maintain compatibility
        public static bool HasError(string text)
        {
            var (hasError, details) = DetectError(text);
            
            if (hasError)
            {
                Console.WriteLine($"Details: {details}");
            }
            return hasError;
        }

    }
}
