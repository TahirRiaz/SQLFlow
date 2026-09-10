using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class NotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SlackBlocksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    FirstEventId = table.Column<long>(type: "bigint", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FlowKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSubscription",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Kinds = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FlowPattern = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EmailAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SlackTarget = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DigestIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    NextDueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSubscription", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationWatermark",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RunsWatermarkUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationWatermark", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_CreatedUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_Status_NextAttemptUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_UserId_CreatedUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_DetectedUtc",
                schema: "catalog",
                table: "NotificationEvent",
                column: "DetectedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_RunId_Kind",
                schema: "catalog",
                table: "NotificationEvent",
                columns: new[] { "RunId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscription_Enabled_NextDueUtc",
                schema: "catalog",
                table: "NotificationSubscription",
                columns: new[] { "Enabled", "NextDueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscription_UserId",
                schema: "catalog",
                table: "NotificationSubscription",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationEvent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationSubscription",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationWatermark",
                schema: "catalog");
        }
    }
}
