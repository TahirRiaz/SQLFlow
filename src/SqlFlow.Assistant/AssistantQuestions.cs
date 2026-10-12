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

/// <summary>
/// The commands a person moves a conversation between modes with, read from a message's first characters, never from
/// its wording: a model asked whether a question "sounds like" one for the data gets it wrong both ways, so the person
/// decides. <c>!data</c> (or a bare <c>!</c>) starts data mode and <c>!exit</c> returns to estate mode; a message
/// without a command keeps the mode the conversation is in, so a follow-up never has to repeat it. Where a leading
/// <c>!</c> does not reach the model, the person switches in words and the MCP server's switch tools move the mode.
/// </summary>
public static class AssistantQuestions
{
    /// <summary>The command character: a message that starts with it carries a command.</summary>
    public const char Command = '!';

    /// <summary>The word that may follow the command for data mode ("!data how many customers").</summary>
    public const string CommandWord = "data";

    /// <summary>The word that, right after the command, returns the conversation to estate mode ("!exit").</summary>
    public const string ExitWord = "exit";

    /// <summary>
    /// The mode <paramref name="message"/>'s command sets, leading white space aside: <see cref="AssistantMode.Estate"/>
    /// for <c>!exit</c> (alone, or followed by white space and a question), <see cref="AssistantMode.Data"/> for any
    /// other message starting with <see cref="Command"/>, and null for a message without the command.
    /// </summary>
    public static AssistantMode? CommandOf(string? message)
    {
        if (message is null)
        {
            return null;
        }
        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith(Command))
        {
            return null;
        }
        var rest = trimmed[1..].TrimStart();
        var exits = rest.StartsWith(ExitWord, StringComparison.OrdinalIgnoreCase)
            && (rest.Length == ExitWord.Length || char.IsWhiteSpace(rest[ExitWord.Length]));
        return exits ? AssistantMode.Estate : AssistantMode.Data;
    }

    /// <summary>
    /// The mode a conversation answers <paramref name="message"/> in: its own command when it carries one; else, going
    /// back through <paramref name="priorTurns"/> newest first, the command of a message of the person's, or the mode
    /// an answer was recorded ending in (a switch the assistant made, by the person asking in words or by itself),
    /// whichever comes first; and estate mode when there is neither. An answer's TEXT never counts, since it can quote
    /// a command back.
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
