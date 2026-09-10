using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Chained schedules gain FAN-IN: <c>after:</c> becomes a set of parents rather than one, so a step whose inputs
    /// land on several independent schedules can wait for all of them instead of riding one parent's clock and hoping
    /// the rest have run. The single parent column becomes the ScheduleParent table, and the schedule gains a
    /// freshness window plus the staleness its last fire observed.
    /// <para>
    /// The scaffolded order was WRONG and is corrected here: EF dropped AfterSchedule before ScheduleParent existed,
    /// which would have silently unchained every schedule in the estate. The column is read into the new table first
    /// and dropped last. ParentFreshnessHours is also given a real default of 24 rather than the scaffolded 0, which
    /// means "check disabled" and would have quietly opted every existing chain out of the staleness reporting.
    /// </para>
    /// </summary>
    public partial class ScheduleMultiParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastStaleParents",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            // 24, not the scaffolded 0: zero disables the freshness check, and an upgrade must not silently turn a
            // feature off for every row that existed before it.
            migrationBuilder.AddColumn<int>(
                name: "ParentFreshnessHours",
                schema: "catalog",
                table: "Schedule",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.CreateTable(
                name: "ScheduleParent",
                schema: "catalog",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleParent", x => new { x.ScheduleId, x.ParentName });
                    table.ForeignKey(
                        name: "FK_ScheduleParent_Schedule_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "catalog",
                        principalTable: "Schedule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleParent_RepoId",
                schema: "catalog",
                table: "ScheduleParent",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleParent_RepoId_ParentName",
                schema: "catalog",
                table: "ScheduleParent",
                columns: new[] { "RepoId", "ParentName" });

            // Carry every existing chain across BEFORE the column goes. A sync would rebuild these rows from git
            // anyway, but not until the next push: without this, every chained schedule in the estate would be
            // unchained the moment the control plane started, and would fire nothing until someone happened to commit.
            migrationBuilder.Sql("""
                INSERT INTO [catalog].[ScheduleParent] ([ScheduleId], [ParentName], [RepoId], [Ordinal])
                SELECT [Id], [AfterSchedule], [RepoId], 0
                FROM [catalog].[Schedule]
                WHERE [AfterSchedule] IS NOT NULL;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Schedule_RepoId_AfterSchedule",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "AfterSchedule",
                schema: "catalog",
                table: "Schedule");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AfterSchedule",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            // Only single-parent chains can go back: the old shape cannot hold a fan-in. A schedule with several
            // parents comes back unchained rather than arbitrarily keeping one of them, because keeping one would
            // silently turn "wait for all four" into "fire behind whichever we picked", which is worse than a
            // schedule that visibly does not fire until someone re-syncs it.
            migrationBuilder.Sql("""
                UPDATE s
                SET s.[AfterSchedule] = p.[ParentName]
                FROM [catalog].[Schedule] s
                INNER JOIN [catalog].[ScheduleParent] p ON p.[ScheduleId] = s.[Id]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [catalog].[ScheduleParent] o
                    WHERE o.[ScheduleId] = p.[ScheduleId] AND o.[ParentName] <> p.[ParentName]);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId_AfterSchedule",
                schema: "catalog",
                table: "Schedule",
                columns: new[] { "RepoId", "AfterSchedule" },
                filter: "[AfterSchedule] IS NOT NULL");

            migrationBuilder.DropTable(
                name: "ScheduleParent",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "LastStaleParents",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "ParentFreshnessHours",
                schema: "catalog",
                table: "Schedule");
        }
    }
}
