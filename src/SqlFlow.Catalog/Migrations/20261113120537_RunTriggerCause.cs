using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Records what started a run with nothing left to infer. <c>TriggerRunId</c> links the runs of a chained fire to the
    /// parent fire they followed, so a run started by its parents traces back to whoever or whatever started the first
    /// link (new runs only; a chained fire made before it has no link to recover).
    /// <para>
    /// A schedule's run-now and an external trigger's request were recorded with the trigger source <c>schedule</c>, the
    /// value for a schedule firing on its own, though a person or a client asked: their runs read as automatic. Those runs
    /// name who asked in <c>RequestedBy</c>, where a fire of the schedule's own names the schedule (<c>schedule:</c>),
    /// so they are told apart exactly and recorded as <c>manual</c>, the value every other asked-for run carries. A run
    /// enqueued before <c>RequestedBy</c> was recorded keeps <c>schedule</c>: nothing on it says which it was.
    /// </para>
    /// </summary>
    public partial class RunTriggerCause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TriggerRunId",
                schema: "catalog",
                table: "Run",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Run_TriggerRunId",
                schema: "catalog",
                table: "Run",
                column: "TriggerRunId",
                filter: "[TriggerRunId] IS NOT NULL");

            migrationBuilder.Sql("""
                UPDATE [catalog].[Run]
                SET [TriggerSource] = 'manual'
                WHERE [TriggerSource] = 'schedule'
                  AND [RequestedBy] IS NOT NULL
                  AND [RequestedBy] NOT LIKE 'schedule:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A manual run that names a schedule was one of its fires (a run asked for directly names none), and a chained
            // fire was recorded as the schedule's own before this, so both go back to 'schedule' exactly.
            migrationBuilder.Sql("""
                UPDATE [catalog].[Run]
                SET [TriggerSource] = 'schedule'
                WHERE ([TriggerSource] = 'manual' AND [TriggerScheduleId] IS NOT NULL)
                   OR [TriggerSource] = 'chain';
                """);

            migrationBuilder.DropIndex(
                name: "IX_Run_TriggerRunId",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "TriggerRunId",
                schema: "catalog",
                table: "Run");
        }
    }
}
