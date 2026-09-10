using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Schedules become named owners of a MEMBER SET instead of per-flow rows with a scope.
    /// <para>
    /// The data move is not a pure rename, because a schedule's identity changes with it: a row used to be keyed by
    /// flow (<c>{repo}/{flow}/schedule</c>) and is now keyed by name (<c>{repo}/schedule/{name}</c>). That id is a
    /// hash computed in application code and cannot be recomputed in T-SQL, so the rows cannot simply be re-keyed
    /// here. The two sources are handled on their own terms:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>yaml</c> schedules are a MIRROR of git, which is their source of truth. They are dropped
    /// and re-mirrored by the next full <c>db sync</c>, which computes the new ids and the member sets from the
    /// documents. This is the only way to get member sets that are actually right: membership now comes from
    /// <c>schedule: &lt;name&gt;</c> lines, which this migration cannot see.</description></item>
    /// <item><description><c>api</c> schedules cannot be re-derived from anything, so they are preserved. Their ids
    /// were always random (never the flow-derived hash), so they survive unchanged; each one gains the single member
    /// it already pointed at through <c>PipelineId</c>.</description></item>
    /// </list>
    /// <para>
    /// OPERATIONAL NOTE: an operator's pause on a <c>yaml</c> schedule does not survive this migration. A paused git
    /// schedule comes back unpaused after the next sync and will fire. Pauses cannot be carried across, because a
    /// schedule's new name usually differs from the flow name the old row carried (a source's schedule is named for
    /// the source, not for its first flow), and only the new YAML knows that mapping. Re-apply any deliberate pauses
    /// after upgrading.
    /// </para>
    /// <para>
    /// The estate does not fire between this migration and the first full sync after it, since the yaml mirror is
    /// empty in that window. Run a sync as part of the deploy rather than waiting for a scheduled one.
    /// </para>
    /// </summary>
    public partial class ScheduleMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The name a flow joins replaces the flow the schedule pointed at. Renaming (rather than add + drop)
            // keeps the api rows' cadence and operational state attached to their row.
            migrationBuilder.RenameColumn(
                name: "FlowName",
                schema: "catalog",
                table: "Schedule",
                newName: "Name");

            migrationBuilder.CreateTable(
                name: "ScheduleMember",
                schema: "catalog",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleMember", x => new { x.ScheduleId, x.PipelineId });
                });

            // An api schedule keeps running exactly the one flow it always ran, now expressed as a membership.
            // Done while PipelineId still exists, and only for api rows, since the yaml rows are about to go.
            migrationBuilder.Sql("""
                INSERT INTO catalog.ScheduleMember (ScheduleId, PipelineId, RepoId, FlowName)
                SELECT Id, PipelineId, RepoId, Name
                FROM catalog.Schedule
                WHERE Source = 'api';
                """);

            // The yaml mirror is rebuilt from git by the next full sync, with the new ids and the real member sets.
            migrationBuilder.Sql("DELETE FROM catalog.Schedule WHERE Source = 'yaml';");

            // A name identifies one schedule per repo. Nothing enforced that before, and a flow was allowed to carry
            // several ad-hoc api schedules, so two surviving api rows can now collide on (RepoId, Name). Keep the
            // oldest under the plain name and suffix the rest rather than dropping anyone's schedule on upgrade.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT Id, Name,
                           ROW_NUMBER() OVER (PARTITION BY RepoId, Name ORDER BY CreatedUtc, Id) AS rn
                    FROM catalog.Schedule
                )
                UPDATE ranked
                SET Name = LEFT(Name, 380) + '_' + CAST(rn AS nvarchar(10))
                WHERE rn > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Schedule_PipelineId",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "PipelineId",
                schema: "catalog",
                table: "Schedule");

            // Membership replaced the scope: what a fire runs is who joined, not a label match or a lineage closure.
            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "catalog",
                table: "Schedule");

            // Created only after the delete and the de-duplication above, so it cannot fail on pre-existing rows.
            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId_Name",
                schema: "catalog",
                table: "Schedule",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleMember_PipelineId",
                schema: "catalog",
                table: "ScheduleMember",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleMember_RepoId",
                schema: "catalog",
                table: "ScheduleMember",
                column: "RepoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Schedule_RepoId_Name",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.AddColumn<Guid>(
                name: "PipelineId",
                schema: "catalog",
                table: "Schedule",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "flow");

            // Restore the one-flow-per-schedule shape from the memberships. A schedule with several members cannot be
            // represented by the old schema at all, so the lowest member id wins and the rest are lost; the yaml rows
            // are rebuilt from git by the next sync on the old code, exactly as they are on the way up.
            migrationBuilder.Sql("""
                UPDATE s
                SET PipelineId = m.PipelineId
                FROM catalog.Schedule s
                CROSS APPLY (
                    SELECT TOP 1 PipelineId FROM catalog.ScheduleMember
                    WHERE ScheduleId = s.Id ORDER BY PipelineId
                ) m;
                """);

            migrationBuilder.Sql("DELETE FROM catalog.Schedule WHERE Source = 'yaml';");

            migrationBuilder.DropTable(
                name: "ScheduleMember",
                schema: "catalog");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "catalog",
                table: "Schedule",
                newName: "FlowName");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_PipelineId",
                schema: "catalog",
                table: "Schedule",
                column: "PipelineId");
        }
    }
}
