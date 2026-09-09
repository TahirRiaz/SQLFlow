using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityEventLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Terminal = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvent_Kind_SubjectKey_Id",
                schema: "catalog",
                table: "ActivityEvent",
                columns: new[] { "Kind", "SubjectKey", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvent",
                schema: "catalog");
        }
    }
}
