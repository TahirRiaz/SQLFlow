using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticReportSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubscriberInputHash",
                schema: "catalog",
                table: "Repo",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SemanticReportSpec",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ReportFile = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Spec = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdentityHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    Visuals = table.Column<int>(type: "int", nullable: false),
                    Tables = table.Column<int>(type: "int", nullable: false),
                    Measures = table.Column<int>(type: "int", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticReportSpec", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SemanticReportSpec_IdentityHash",
                schema: "catalog",
                table: "SemanticReportSpec",
                column: "IdentityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemanticReportSpec_RepoId",
                schema: "catalog",
                table: "SemanticReportSpec",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticReportSpec_SubscriberKey",
                schema: "catalog",
                table: "SemanticReportSpec",
                column: "SubscriberKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SemanticReportSpec",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "SubscriberInputHash",
                schema: "catalog",
                table: "Repo");
        }
    }
}
