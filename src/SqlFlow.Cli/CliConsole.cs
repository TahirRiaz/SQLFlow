using System.Text;

namespace SqlFlow.Cli;

/// <summary>Shared console input for every verb that reads a secret: hidden when a terminal is attached, a
/// plain line read when stdin is piped (automation has no terminal to mask). A secret never travels through a
/// command-line flag, so it stays out of shell history on every path.</summary>
internal static class CliConsole
{
    /// <summary>Prompts for one secret value. With a terminal the prompt is written and keystrokes are not
    /// echoed; with redirected stdin one line is read as-is. Returns the (possibly empty) value; the caller
    /// decides what emptiness means for its flow.</summary>
    public static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        Console.Write(prompt);
        return ReadHiddenLine();
    }

    /// <summary>Reads one line from the terminal without echoing keystrokes. Backspace edits the buffer; Enter
    /// finishes. Control characters (arrows, tab) are ignored rather than injected into the value.</summary>
    public static string ReadHiddenLine()
    {
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
