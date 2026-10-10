// A finished query's result on a page of its own, so an answer given outside the GUI (the MCP server in a desktop
// or terminal client, which cannot draw charts) can link to the same fitting chart the chat draws inline. The
// chart is QueryResultEmbed itself, not a second renderer, and like it this only READS the stored compute task:
// opening the link never runs the query again.

import { useQuery } from "@tanstack/react-query";
import { useParams } from "react-router-dom";
import { datasourceApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { isRunQueryResult, QueryResultEmbed, TASK_ID } from "./QueryResultEmbed";

export default function QueryResultPage() {
  const { taskId = "" } = useParams<{ taskId: string }>();
  // The same query key the embed uses, so the page and the chart share one fetch of the task.
  const task = useQuery({
    queryKey: ["compute-task", taskId],
    queryFn: () => datasourceApi.task(taskId),
    enabled: TASK_ID.test(taskId),
    staleTime: Infinity,
  });

  const data = task.data;
  const result = data?.status === "succeeded" && isRunQueryResult(data.result) ? data.result : null;
  const rows = result ? `${result.rowCount} row${result.rowCount === 1 ? "" : "s"}` : null;

  return (
    <Page data-testid="query-result-page">
      <PageHeader
        title="Query result"
        subtitle={result ? (result.database ? `${result.database}, ${rows}` : rows) : undefined}
      />
      <QueryResultEmbed code={taskId} />
      {result && <CodeView value={result.sql} language="sql" height={160} label="sql" />}
    </Page>
  );
}
