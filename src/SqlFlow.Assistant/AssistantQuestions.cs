namespace SqlFlow.Assistant;

/// <summary>Which of the assistant's two tool sets, and the instructions that go with it, answers a message.</summary>
public enum AssistantQuestionKind
{
    /// <summary>
    /// A question about the estate: flows, runs, lineage, schedules, the catalog, the docs. It sees every table and
    /// column the catalog holds, since schema is not sensitive, and it reads no rows.
    /// </summary>
    Estate,

    /// <summary>
    /// A business question, answered from warehouse data: a message that starts with the business-question command
    /// (<see cref="AssistantQuestions.Command"/>). Its schema is the semantic layer alone (what the approved AI contexts
    /// open), and its queries read only the columns those contexts open.
    /// </summary>
    Business,
}

/// <summary>
/// How a message is told apart: by its first character, never by its wording. A model asked to judge whether a
/// question "sounds like" a business question gets it wrong both ways, so the person decides, by typing the command,
/// and the tools a run is given follow from that alone.
/// </summary>
public static class AssistantQuestions
{
    /// <summary>The business-question command: a message that starts with it is a business question.</summary>
    public const char Command = '!';

    /// <summary>The word that may follow the command ("!cwd how many customers"), the command's earlier spelling.</summary>
    public const string CommandWord = "cwd";

    /// <summary>Which kind of question <paramref name="message"/> is: <see cref="AssistantQuestionKind.Business"/>
    /// when, leading white space aside, it starts with <see cref="Command"/>.</summary>
    public static AssistantQuestionKind KindOf(string? message)
        => message is not null && message.TrimStart().StartsWith(Command)
            ? AssistantQuestionKind.Business
            : AssistantQuestionKind.Estate;
}
