using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ExternalTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastSyncActivityId",
                schema: "catalog",
                table: "RepoSource",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalTrigger",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RepoName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AllSchedules = table.Column<bool>(type: "bit", nullable: false),
                    Schedules = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GitHubRepository = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GitHubRepositoryId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GitHubRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GitHubWorkflow = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GitHubEnvironment = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    KeyPrefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    KeyExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConfiguredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalTrigger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriggerRequest",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RepoSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TriggerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TriggerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OriginUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ExpectedCommit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Sync = table.Column<bool>(type: "bit", nullable: false),
                    Batch = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Chain = table.Column<bool>(type: "bit", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SyncRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SyncOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    SyncedCommit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SyncError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SyncCopiedThroughId = table.Column<long>(type: "bigint", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelRequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriggerRequestRun",
                schema: "catalog",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleOrdinal = table.Column<int>(type: "int", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ReportedStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRequestRun", x => new { x.RequestId, x.RunId });
                });

            migrationBuilder.CreateTable(
                name: "TriggerRequestSchedule",
                schema: "catalog",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    ScheduleName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MemberCount = table.Column<int>(type: "int", nullable: true),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FiringUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Succeeded = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Cancelled = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRequestSchedule", x => new { x.RequestId, x.Ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTrigger_GitHubRepository",
                schema: "catalog",
                table: "ExternalTrigger",
                column: "GitHubRepository");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTrigger_KeyHash",
                schema: "catalog",
                table: "ExternalTrigger",
                column: "KeyHash",
                unique: true,
                filter: "[KeyHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTrigger_Name",
                schema: "catalog",
                table: "ExternalTrigger",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRequest_Open",
                schema: "catalog",
                table: "TriggerRequest",
                column: "CreatedUtc",
                filter: "[CompletedUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRequest_RepoName_CreatedUtc",
                schema: "catalog",
                table: "TriggerRequest",
                columns: new[] { "RepoName", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRequest_RequestedBy_RequestKey",
                schema: "catalog",
                table: "TriggerRequest",
                columns: new[] { "RequestedBy", "RequestKey" },
                unique: true,
                filter: "[RequestKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRequest_TriggerId_CreatedUtc",
                schema: "catalog",
                table: "TriggerRequest",
                columns: new[] { "TriggerId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRequestRun_RunId",
                schema: "catalog",
                table: "TriggerRequestRun",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalTrigger",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "TriggerRequest",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "TriggerRequestRun",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "TriggerRequestSchedule",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "LastSyncActivityId",
                schema: "catalog",
                table: "RepoSource");
        }
    }
}
