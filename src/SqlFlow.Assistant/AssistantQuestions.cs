namespace SqlFlow.Assistant;

/// <summary>
/// The two modes a conversation answers in. The MCP server defines them (<c>tools/sqlflow-mcp/src/modes.rs</c>) and its
/// instructions say what each one is for; a gateway only names the conversation's mode on the MCP address, so the
/// session lists that mode's tools.
/// </summary>
public enum AssistantMode
{
    /// <summary>Questions about the estate (flows, runs, lineage, schedules, the catalog, the docs), reading no rows.
    /// The default.</summary>
    Estate,

    /// <summary>Questions answered from warehouse data, through the semantic layer, the stored questions and the query
    /// tools, and only the columns the approved AI contexts open.</summary>
    Data,
}

/// <summary>What a person's message asks of the conversation (<see cref="AssistantQuestions.Read"/>): the mode its
/// command sets, and the text after the command.</summary>
/// <param name="Mode">The mode the command sets.</param>
/// <param name="Question">The rest of the message without the command, its separator or white space at either end;
/// empty for the command alone.</param>
public sealed record AssistantCommand(AssistantMode Mode, string Question);

/// <summary>
/// The commands a person moves a conversation between modes with, read from a message's first characters, never from
/// its wording: a model asked whether a question "sounds like" one for the data gets it wrong both ways, so the person
/// decides. <c>!data</c> (or a bare <c>!</c>) starts data mode and <c>!exit</c> (or <c>!estate</c>) returns to estate
/// mode; a message without a command keeps the mode the conversation is in, so a follow-up never has to repeat it, and
/// nothing else moves it. The MCP server reads a message by the same grammar (<c>tools/sqlflow-mcp/src/modes.rs</c>,
/// <c>command_of</c>) when an editor's model relays it, and one table of cases
/// (<c>tools/sqlflow-mcp/tests/assistant-commands.json</c>) holds both readers to it.
/// </summary>
public static class AssistantQuestions
{
    /// <summary>The command character: a message that starts with it carries a command.</summary>
    public const char Command = '!';

    /// <summary>The word that may follow the command directly for data mode ("!data how many customers"). It counts only
    /// touching the <see cref="Command"/>, since it is an ordinary word too: "! data quality by region" is the bare command
    /// and a question about data quality.</summary>
    public const string CommandWord = "data";

    /// <summary>The word that, after the command, returns the conversation to estate mode ("!exit", "! exit").</summary>
    public const string ExitWord = "exit";

    /// <summary>The other word that returns the conversation to estate mode ("!estate").</summary>
    public const string EstateWord = "estate";

    /// <summary>The words that return the conversation to estate mode.</summary>
    private static readonly string[] ExitWords = [ExitWord, EstateWord];

    /// <summary>What may follow a command word besides white space and the end of the message: sentence punctuation, so
    /// "!exit." is the command and "!exit-interview" or "!data_quality" a question for the data.</summary>
    private const string WordEnds = ".,:;!?";

    /// <summary>Characters that show nothing and that a message may carry before its command or after it (pasted text,
    /// an editor's byte order mark), skipped as white space is.</summary>
    private const string Invisible = "\u180E\u200B\u200C\u200D\u2060\uFEFF";

    /// <summary>
    /// The mode <paramref name="message"/>'s command sets: <see cref="AssistantMode.Estate"/> for <c>!exit</c> or
    /// <c>!estate</c>, <see cref="AssistantMode.Data"/> for <c>!data</c> and any other message starting with
    /// <see cref="Command"/>, and null for a message without the command (<see cref="Read"/>).
    /// </summary>
    public static AssistantMode? CommandOf(string? message) => Read(message)?.Mode;

    /// <summary>
    /// The text after <paramref name="message"/>'s command, without the command, its separator or white space at either
    /// end (empty for the command alone), or null for a message without the command (<see cref="Read"/>).
    /// </summary>
    public static string? QuestionOf(string? message) => Read(message)?.Question;

    /// <summary>
    /// The command <paramref name="message"/> carries, when it starts with one. White space and the invisible characters
    /// are skipped, then one or more <see cref="Command"/> (invisible characters among them) start the command.
    /// <see cref="CommandWord"/> directly after them is the data command's word; otherwise, after any white space,
    /// <see cref="ExitWord"/> or <see cref="EstateWord"/> is the command that returns to estate mode. A word counts in any
    /// case of its ASCII letters (never Unicode case folding, which would read a dotless ı as i), ending the message or
    /// followed by white space, an invisible character or sentence punctuation. Anything else after the command is the
    /// question of a bare <c>!</c>, which is data mode.
    /// </summary>
    public static AssistantCommand? Read(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var at = Skip(message, 0, IsSkippable);
        if (at >= message.Length || message[at] != Command)
        {
            return null;
        }

        at = Skip(message, at + 1, c => c == Command || Invisible.Contains(c, StringComparison.Ordinal));
        if (AfterWord(message, at, CommandWord) is { } afterData)
        {
            return new AssistantCommand(AssistantMode.Data, QuestionAfter(message, afterData));
        }

        at = Skip(message, at, IsSkippable);
        foreach (var word in ExitWords)
        {
            if (AfterWord(message, at, word) is { } afterExit)
            {
                return new AssistantCommand(AssistantMode.Estate, QuestionAfter(message, afterExit));
            }
        }

        return new AssistantCommand(AssistantMode.Data, TrimEnd(message, at));
    }

    /// <summary>White space or a character that shows nothing.</summary>
    private static bool IsSkippable(char c) => char.IsWhiteSpace(c) || Invisible.Contains(c, StringComparison.Ordinal);

    /// <summary>The index of the first character at or after <paramref name="at"/> that <paramref name="skip"/> does not
    /// pass over.</summary>
    private static int Skip(string text, int at, Func<char, bool> skip)
    {
        while (at < text.Length && skip(text[at]))
        {
            at++;
        }
        return at;
    }

    /// <summary>Where <paramref name="word"/> ends when <paramref name="text"/> has it at <paramref name="at"/> as a word
    /// of its own: its ASCII letters in any case, then the end, white space, an invisible character or sentence
    /// punctuation; null otherwise.</summary>
    private static int? AfterWord(string text, int at, string word)
    {
        if (text.Length - at < word.Length)
        {
            return null;
        }
        for (var i = 0; i < word.Length; i++)
        {
            var c = text[at + i];
            if (c > 0x7F || char.ToLowerInvariant(c) != word[i])
            {
                return null;
            }
        }
        var end = at + word.Length;
        return end == text.Length || IsSkippable(text[end]) || WordEnds.Contains(text[end], StringComparison.Ordinal)
            ? end
            : null;
    }

    /// <summary>The question after a command word that ends at <paramref name="at"/>: its separator (white space and
    /// sentence punctuation) and trailing white space off.</summary>
    private static string QuestionAfter(string text, int at)
        => TrimEnd(text, Skip(text, at, c => IsSkippable(c) || WordEnds.Contains(c, StringComparison.Ordinal)));

    /// <summary>The text from <paramref name="at"/> without the white space and invisible characters it ends with.</summary>
    private static string TrimEnd(string text, int at)
    {
        var end = text.Length;
        while (end > at && IsSkippable(text[end - 1]))
        {
            end--;
        }
        return text[at..end];
    }

    /// <summary>
    /// The mode a conversation answers <paramref name="message"/> in: its own command when it carries one; else, going
    /// back through <paramref name="priorTurns"/> newest first, the command of a message of the person's, or the mode
    /// an answer was recorded in, whichever comes first; and estate mode when there is neither. An answer's recorded
    /// mode is the mode its run was given, which keeps the conversation in it when the command that set it is older than
    /// the turns a host replays. An answer's TEXT never counts, since it can quote a command back.
    /// </summary>
    public static AssistantMode ModeOf(string? message, IReadOnlyList<ConversationTurn> priorTurns)
    {
        ArgumentNullException.ThrowIfNull(priorTurns);
        if (CommandOf(message) is { } now)
        {
            return now;
        }
        for (var i = priorTurns.Count - 1; i >= 0; i--)
        {
            var turn = priorTurns[i];
            var earlier = turn.FromAssistant ? turn.Mode : CommandOf(turn.Text);
            if (earlier is { } mode)
            {
                return mode;
            }
        }
        return AssistantMode.Estate;
    }
}
