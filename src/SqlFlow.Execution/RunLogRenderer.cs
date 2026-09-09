using System.Globalization;
using System.Text;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Model;
using SqlFlow.SourceControl;

namespace SqlFlow.Execution;

/// <summary>Renders the canonical <c>run.log</c> text for each flow kind that writes one (file flow,
/// source control, and the batch summary). Pure text builders: they never touch the console, so the run path
/// can write the log to the run-history folder and the CLI can choose to print it.</summary>
public static class RunLogRenderer
{
    /// <summary>Renders a file flow's canonical run.log from its trace (operation, duration, rows, outcome).</summary>
    public static string RenderFileFlowLog(FlowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append("flow '").Append(result.FlowName).Append("' run ").Append(result.RunId).Append(": ")
          .Append(result.Status == FlowStatus.Success ? "SUCCESS" : "FAILED")
          .Append(", ").Append(result.RowsLoaded).Append(" row(s) in ")
          .Append(result.TotalMs.ToString("0", CultureInfo.InvariantCulture)).AppendLine("ms");
        if (result.Error is not null)
        {
            sb.Append("error: ").AppendLine(result.Error);
        }

        foreach (var entry in result.Trace)
        {
            sb.Append(entry.Succeeded ? "OK    " : "FAIL  ")
              .Append(entry.Operation, 0, Math.Min(entry.Operation.Length, 40)).Append(' ', Math.Max(1, 42 - entry.Operation.Length))
              .Append(entry.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms");
            if (entry.Rows is { } rows)
            {
                sb.Append("  rows=").Append(rows);
            }

            if (entry.Detail is not null)
            {
                sb.Append("  ").Append(entry.Detail);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string RenderSourceControlLog(SourceControlResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append("source-control run ").Append(result.RunId).Append(": ")
          .Append(result.Success ? "SUCCESS" : "FAILED").AppendLine();
        if (result.Error is not null)
        {
            sb.Append("error: ").AppendLine(result.Error);
        }

        sb.Append("database: ").AppendLine(result.DatabaseName ?? "(unresolved)");
        sb.Append("repository: ").Append(result.WorkingDirectory).Append(" [").Append(result.Branch).Append(']').AppendLine();
        if (result.Remote is not null)
        {
            sb.Append("remote: ").AppendLine(result.Remote);
        }

        sb.Append("scripted: ").Append(result.ObjectsScripted).Append(" object(s); ")
          .Append(result.Added).Append(" added, ").Append(result.Changed).Append(" changed, ")
          .Append(result.Deleted).Append(" deleted, ").Append(result.Unchanged).Append(" unchanged").AppendLine();
        sb.Append("commit: ").AppendLine(result.DryRun
            ? "dry run, not committed"
            : result.Committed ? $"{result.CommitSha} (pushed: {result.Pushed})" : "nothing to commit");

        foreach (var warning in result.Warnings)
        {
            sb.Append("WARN  ").AppendLine(warning);
        }

        return sb.ToString();
    }

    public static string RenderBatchLog(BatchRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.Append("batch '").Append(result.BatchName).Append("' run ").Append(result.RunId).Append(": ")
          .Append(result.Success ? "SUCCESS" : "FAILED").AppendLine();
        if (result.Error is not null)
        {
            sb.Append("error: ").AppendLine(result.Error);
        }

        sb.Append("onError: ").AppendLine(result.OnError);
        foreach (var wave in result.Waves)
        {
            sb.Append("wave ").Append(wave.Wave).Append(": ").AppendLine(string.Join(", ", wave.Members));
        }

        foreach (var member in result.Members)
        {
            sb.Append("  ").Append(member.Status.ToString().ToUpperInvariant()).Append("  ")
              .Append(member.FlowName).Append(" (").Append(member.FlowKind).Append(", wave ").Append(member.Wave).Append(')');
            if (member.Error is not null)
            {
                sb.Append(": ").Append(member.Error);
            }

            sb.AppendLine();
        }

        if (result.Unordered.Count > 0)
        {
            sb.Append("unordered (cycle): ").AppendLine(string.Join(", ", result.Unordered));
        }

        foreach (var warning in result.Warnings)
        {
            sb.Append("WARN  ").AppendLine(warning);
        }

        return sb.ToString();
    }
}
