using System.Text;
using System.Text.Json;

namespace SqlFlow.Cli.Remote;

internal static partial class RemoteVerbs
{
    // ---- ai-context ---------------------------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow ai-context list|show|approve|revoke|export': the AI contexts repository files propose. A file only
    /// proposes; an admin approves the exact version shown, and only then does an assistant use it.
    /// </summary>
    public static async Task<int> AiContextAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        using var cts = InterceptCtrlC();
        return sub switch
        {
            "list" => await ListAiContextsAsync(args, cts.Token).ConfigureAwait(false),
            "show" => await ShowAiContextAsync(positional, args, cts.Token).ConfigureAwait(false),
            "approve" => await ApproveAiContextAsync(positional, args, cts.Token).ConfigureAwait(false),
            "revoke" => await RevokeAiContextAsync(positional, args, cts.Token).ConfigureAwait(false),
            "export" => await ExportEarlierAiContentAsync(args, cts.Token).ConfigureAwait(false),
            _ => UnknownAiContextVerb(),
        };
    }

    private static int UnknownAiContextVerb()
    {
        Console.Error.WriteLine(
            "ERROR  'ai-context' supports: list, show <name>, approve <name> --version <v>, revoke <name>, export [--out <file>|-].");
        return 1;
    }

    private static async Task<int> ListAiContextsAsync(string[] args, CancellationToken ct)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var list = await client.ListAiContextsAsync(token).ConfigureAwait(false);
            if (args.Contains("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(list, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine($"{"CONTEXT",-28}  {"STATUS",-18}  {"VERSION",-12}  {"TABLES",6}  {"SERVED",6}  {"REPO",-20}  FILE");
            foreach (var context in list.Contexts)
            {
                Console.WriteLine(
                    $"{Truncate(context.Name, 28),-28}  {context.Status,-18}  {ShortVersion(context.CurrentVersion),-12}  "
                    + $"{context.TableCount,6}  {context.ServedTableCount,6}  {Truncate(context.RepoName, 20),-20}  {context.Path}"
                    + (context.ProblemCount > 0 ? $"  ({context.ProblemCount} problem(s))" : string.Empty));
            }

            Console.WriteLine($"({list.Contexts.Count} context(s))");
            if (list.HasEarlierContent)
            {
                Console.WriteLine(
                    "Content written in the GUI before AI contexts were files is no longer used: "
                    + "'sqlflow ai-context export' writes it as a context file to commit.");
            }

            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> ShowAiContextAsync(string[] positional, string[] args, CancellationToken ct)
    {
        if (RequireContextName(positional, "show") is not { } name)
        {
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var detail = await client.GetAiContextAsync(name, token).ConfigureAwait(false);
            if (detail is null)
            {
                Console.Error.WriteLine($"ERROR  no AI context is named '{name}' (see 'sqlflow ai-context list').");
                return 1;
            }

            if (args.Contains("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(detail, ControlPlaneClient.JsonIndented));
                return 0;
            }

            var summary = detail.Summary;
            Console.WriteLine($"{summary.Name}  [{summary.Status}]");
            if (summary.Description is not null)
            {
                Console.WriteLine($"  {summary.Description}");
            }

            Console.WriteLine($"  file      {summary.RepoName}: {summary.Path} (read {FormatUtc(summary.SyncedUtc)})");
            Console.WriteLine($"  proposes  {summary.CurrentVersion ?? "nothing: the file does not read"}");
            Console.WriteLine(summary.ApprovedVersion is null
                ? "  approved  never"
                : $"  approved  {summary.ApprovedVersion} by {summary.ApprovedBy} at {FormatUtc(summary.ApprovedUtc)}; serves {summary.ServedTableCount} table(s)");

            if (detail.Current is { } current)
            {
                foreach (var table in current.Tables)
                {
                    Console.WriteLine($"  table {table.Display}: {string.Join(", ", table.Columns.Select(c => c.Name))}");
                }

                if (current.Examples.Count > 0)
                {
                    Console.WriteLine($"  {current.Examples.Count} example question(s)");
                }
            }

            if (detail.Problems.Count > 0)
            {
                Console.WriteLine("Problems (the version cannot be approved until they are fixed):");
                foreach (var problem in detail.Problems)
                {
                    Console.WriteLine($"  {problem}");
                }
            }
            else if (detail.Changes.Count > 0 && summary.CurrentVersion is not null)
            {
                Console.WriteLine(summary.ApprovedVersion is null ? "Approving it serves:" : "Approving it changes:");
                foreach (var change in detail.Changes)
                {
                    Console.WriteLine($"  {change.Kind,-8} {change.Subject}{(change.Detail is null ? string.Empty : $": {change.Detail}")}");
                }

                Console.WriteLine($"Approve with: sqlflow ai-context approve {summary.Name} --version {summary.CurrentVersion}");
            }

            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> ApproveAiContextAsync(string[] positional, string[] args, CancellationToken ct)
    {
        if (RequireContextName(positional, "approve") is not { } name)
        {
            return 1;
        }

        // The version is required, so what is approved is always the version someone looked at: 'show' prints it.
        if (Program.GetOption(args, "--version") is not { Length: > 0 } version)
        {
            Console.Error.WriteLine(
                $"ERROR  'ai-context approve' needs --version: run 'sqlflow ai-context show {name}' to review the version "
                + "and the command that approves it.");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var detail = await client.ApproveAiContextAsync(name, version.Trim(), token).ConfigureAwait(false);
            Console.WriteLine(
                $"OK   approved '{name}' version {ShortVersion(detail.Summary.ApprovedVersion)}; it serves {detail.Summary.ServedTableCount} table(s).");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> RevokeAiContextAsync(string[] positional, string[] args, CancellationToken ct)
    {
        if (RequireContextName(positional, "revoke") is not { } name)
        {
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            await client.RevokeAiContextAsync(name, token).ConfigureAwait(false);
            Console.WriteLine($"OK   revoked '{name}'; an assistant no longer uses it until it is approved again.");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> ExportEarlierAiContentAsync(string[] args, CancellationToken ct)
    {
        var url = RequireUrl(args);
        var output = Program.GetOption(args, "--out", "-o") ?? "earlier-gui-content.ai-context.yaml";
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var yaml = await client.ExportEarlierAiContentAsync(token).ConfigureAwait(false);
            if (yaml is null)
            {
                Console.WriteLine("Nothing was written in the GUI before AI contexts were files; there is nothing to export.");
                return 0;
            }

            if (output == "-")
            {
                Console.Out.Write(yaml);
                return 0;
            }

            if (File.Exists(output))
            {
                Console.Error.WriteLine($"ERROR  '{output}' exists; name another file with --out, or '-' for standard output.");
                return 1;
            }

            await File.WriteAllTextAsync(output, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), token).ConfigureAwait(false);
            Console.WriteLine(
                $"OK   wrote {output}. Review it (list the columns each table opens), commit it to a synced repository, "
                + "and approve it once the sync has read it.");
            return 0;
        }).ConfigureAwait(false);
    }

    private static string? RequireContextName(string[] positional, string verb)
    {
        if (positional.Length > 2 && positional[2].Trim() is { Length: > 0 } name)
        {
            return name;
        }

        Console.Error.WriteLine($"ERROR  'ai-context {verb}' requires the context's name (see 'sqlflow ai-context list').");
        return null;
    }

    private static string ShortVersion(string? version)
        => version is null ? "-" : version.Length <= 12 ? version : version[..12];
}
