namespace SqlFlow.Assistant;

/// <summary>
/// What a surface adds to the MCP server's instructions: how an answer is laid out where it is read, and how it links
/// what it names. Every rule about what to answer, with which tools and in which mode is the server's
/// (<see cref="McpServerInstructions"/>), the one text every client of it reads, from the GUI chat and the Slack bot to
/// an editor's assistant; a surface never adds one, so the assistant behaves alike wherever it is asked.
/// </summary>
public static class AssistantInstructions
{
    /// <summary>A run's system prompt: the MCP server's instructions, then what this surface renders.</summary>
    public static string Compose(string serverInstructions, AssistantSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverInstructions);
        ArgumentNullException.ThrowIfNull(settings);
        return $"{serverInstructions.TrimEnd()}\n\n{Surface(settings)}";
    }

    /// <summary>The surface's own section: its formatting, its links, and how it shows a query's result.</summary>
    public static string Surface(AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var gui = settings.GuiBaseUrl.TrimEnd('/');
        return settings.Surface == AssistantSurface.Slack ? Slack(gui) : Gui(gui);
    }

    private static string Slack(string gui)
    {
        var linkGuidance = gui.Length > 0
            ? $"""
               Every catalog tool result carries GUI deep links: each row has a `links` object with the
               page for the row itself (`page`, whatever the row is: a table, a flow, a run, a run group,
               a schedule, a repo, a schema folder, a report, the fleet board), its lineage graph
               (`lineage`), and the things it references (`flow`, `object`, `objectLineage`, `run`,
               `runGroup`, `schedule`, `lastRun`, `fromFlow`, `toFlow`). Addresses that leave SQLFlow come
               under their own names and are already absolute: `url` (a report's own address in Power
               BI/Tableau), `remote` (a repo's git remote), `source` (a flow's source location). When you
               name a table, flow, run, schedule, repo, or report, link that name with the URL the row
               gave you, as <URL|the name>, and show an external address as <URL|the name> too rather than
               as bare text. A link that starts with / is relative to the GUI: prefix it with {gui}.
               A result about something your CALL named rather than about its rows (a flow's columns, a
               repo's edges, the insights boards) carries the subject's links on the envelope beside
               `items`. Never invent a SQLFlow URL for a row that carried no links; name it instead.
               """
            : "Reference runs, flows, and tables by their names and ids from tool results, without links.";
        return $"""
            WHERE YOU ARE ANSWERING: a Slack thread. Format for Slack mrkdwn: *bold* for emphasis (never
            double-asterisk), bullet lists with the - character, `inline code` for object and flow names, code
            blocks only for SQL or YAML. Keep answers tight: lead with the finding, then only the supporting
            detail a data engineer needs. {linkGuidance}
            Slack cannot draw a query's result, so lay out a query answer as the tool result's layout fields
            describe: `approvalFormat` when you offer a query to run, and `answerFormat` with its `sqlIntro`,
            `sqlBlock` and `saveOffer` (and `chartLink` when there is one) when a query ran. In a channel a reply
            reaches you only when it mentions you, so whenever you ask the person to reply (to run an offered
            query, to save an answer), say so; in a direct message a plain reply works.
            """;
    }

    private static string Gui(string gui)
    {
        var linkBase = gui.Length > 0 ? gui : "";
        return $"""
            WHERE YOU ARE ANSWERING: the GUI's chat. Format answers as GitHub-flavored Markdown: **bold** for
            emphasis, bullet lists with the - character, `inline code` for object and flow names, and fenced code
            blocks tagged with their language (```sql, ```yaml) for SQL or YAML. Keep answers tight: lead with
            the finding, then only the supporting detail a data engineer needs.
            Every catalog tool result carries GUI deep links: each row has a `links` object with the page
            for the row itself (`page`, whatever the row is: a table, a flow, a run, a run group, a
            schedule, a repo, a schema folder, a report, the fleet board), its lineage graph (`lineage`),
            and the things it references (`flow`, `object`, `objectLineage`, `run`, `runGroup`,
            `schedule`, `lastRun`, `fromFlow`, `toFlow`). Addresses that leave SQLFlow come under their
            own names and are already absolute: `url` (a report's own address in Power BI/Tableau),
            `remote` (a repo's git remote), `source` (a flow's source location). Link the names you write
            with the URLs those rows gave you: a table as [sales.Orders](CATALOG_PAGE_URL) with its
            [lineage](LINEAGE_URL) when the question is about where data flows, a flow as
            [orders_ingest](FLOW_URL), a run as [the run](RUN_URL), a report as
            [Sales overview](REPORT_URL) beside its [catalog page](SUBSCRIBER_PAGE_URL). Never print a
            URL as bare text or inline code when you can link it. Prefer the row's own link over
            composing one; when a row carries none, fall back to [the run]({linkBase}/runs/RUN_ID) and
            [the pipeline]({linkBase}/pipelines/PIPELINE_ID) with real ids, and never invent a URL for
            anything else. A result about something your call named rather than about its rows (a flow's
            columns, a repo's edges, the insights boards) carries the subject's links on the envelope
            beside `items`. Link a thing once, on its first mention, rather than on every repetition.
            The chat draws a query's result itself. When a query ran and its result carries a `taskId`, put a
            fenced code block tagged query-result right after the sentence giving the finding, holding only the
            taskId on one line, and never restate the rows as a Markdown table; then give the SQL behind the
            answer in a ```sql block under a short "How this is calculated" line. When you offer a query that has
            not run, give it in a ```sql block and say that pressing Run shows the result, rather than asking
            whether to run it: the person runs and saves an answer with the buttons under it, so
            never ask them to reply to run or save it. Tool results carry layout fields for clients that cannot draw results
            (answerFormat, sqlIntro, sqlBlock, chartLink, saveOffer, approvalFormat): ignore them here, and never
            add a chartLink, since the chart is already shown in the answer.
            """;
    }
}
