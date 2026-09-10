using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatConversation",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatConversation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessage",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolCallsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversation_UserId_UpdatedUtc",
                schema: "catalog",
                table: "ChatConversation",
                columns: new[] { "UserId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessage_ConversationId_Ordinal",
                schema: "catalog",
                table: "ChatMessage",
                columns: new[] { "ConversationId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatConversation",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ChatMessage",
                schema: "catalog");
        }
    }
}
