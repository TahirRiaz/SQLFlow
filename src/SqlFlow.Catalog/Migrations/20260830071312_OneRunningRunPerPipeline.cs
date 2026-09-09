using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Enforces one running execution per pipeline in the database. The run queue's claim already gated on it, but
    /// that gate is a read under READ COMMITTED holding no lock on the sibling rows, so two nodes claiming two
    /// queued runs of one pipeline could each see no running sibling and both claim it. The engine names a flow's
    /// work tables per flow (one canonical staging table per flow id), so the two overlapping executions then shared
    /// and dropped one staging table. The filtered unique index makes the gate atomic: the loser's claim fails with
    /// a duplicate key and takes the next-eligible run instead.
    /// </summary>
    public partial class OneRunningRunPerPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing data can already violate the new rule: every overlapping pair this bug produced, plus any run
            // orphaned in 'running' by a node that died before the reaper reached it. Creating the index over that
            // would fail, and migrations apply at control-plane startup, so the estate would not start. Resolve it
            // deterministically first: per pipeline, the most recently started running row stays (it is the live
            // claim; its own worker still owns it and completes it under its fence), and every older one is recorded
            // failed with the reason. Nothing that is still executing is disturbed by this write: the fenced
            // completion path (CompleteFromArtifactAsync) applies only while a row is still 'running' under the
            // caller's claim, so a superseded worker's late write is dropped as a stale claim, which is exactly the
            // outcome the row now records. No-op on a fresh database.
            migrationBuilder.Sql("""
                WITH [ranked] AS (
                    SELECT [RunId],
                           ROW_NUMBER() OVER (
                               PARTITION BY [PipelineId]
                               ORDER BY [StartUtc] DESC, [Attempt] DESC, [RunId] DESC) AS [Rank]
                    FROM [catalog].[Run]
                    WHERE [Status] = 'running')
                UPDATE [r]
                SET [r].[Status] = 'failed',
                    [r].[Success] = 0,
                    [r].[EndUtc] = SYSUTCDATETIME(),
                    [r].[WrittenUtc] = SYSUTCDATETIME(),
                    [r].[Error] = N'Superseded: this run was left in ''running'' alongside a newer execution of the '
                                + N'same pipeline. Concurrent executions of one flow share (and drop) its staging '
                                + N'table, so they are no longer permitted; the newer execution is authoritative.'
                FROM [catalog].[Run] AS [r]
                INNER JOIN [ranked] ON [ranked].[RunId] = [r].[RunId]
                WHERE [ranked].[Rank] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run",
                column: "PipelineId",
                unique: true,
                filter: "[Status] = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the index is reversible. The rows the Up pass recorded failed are history: reviving them as
            // 'running' would hand the queue executions no node owns, so they stay as they are.
            migrationBuilder.DropIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run");
        }
    }
}
