using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerBiReportStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriberReportField",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisualKey = table.Column<string>(type: "nvarchar(924)", maxLength: 924, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    QueryRef = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ColumnOrMeasure = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    IsMeasure = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberReportField", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriberReportPage",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    PageKey = table.Column<string>(type: "nvarchar(912)", maxLength: 912, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberReportPage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriberReportVisual",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageKey = table.Column<string>(type: "nvarchar(912)", maxLength: 912, nullable: false),
                    VisualKey = table.Column<string>(type: "nvarchar(924)", maxLength: 924, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    VisualType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberReportVisual", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportField_RepoId",
                schema: "catalog",
                table: "SubscriberReportField",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportField_VisualKey",
                schema: "catalog",
                table: "SubscriberReportField",
                column: "VisualKey");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportPage_RepoId",
                schema: "catalog",
                table: "SubscriberReportPage",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportPage_SubscriberKey",
                schema: "catalog",
                table: "SubscriberReportPage",
                column: "SubscriberKey");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportVisual_PageKey",
                schema: "catalog",
                table: "SubscriberReportVisual",
                column: "PageKey");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportVisual_RepoId",
                schema: "catalog",
                table: "SubscriberReportVisual",
                column: "RepoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriberReportField",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SubscriberReportPage",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SubscriberReportVisual",
                schema: "catalog");
        }
    }
}
