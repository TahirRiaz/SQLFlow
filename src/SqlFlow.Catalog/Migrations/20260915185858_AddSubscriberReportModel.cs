using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriberReportModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriberModelField",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ReportFile = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Expression = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberModelField", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriberModelRelationship",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ReportFile = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    FromTable = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    FromColumn = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    ToTable = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ToColumn = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Cardinality = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberModelRelationship", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriberModelTable",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ReportFile = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    PowerQuery = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceDatabase = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberModelTable", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelField_RepoId",
                schema: "catalog",
                table: "SubscriberModelField",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelField_SubscriberKey",
                schema: "catalog",
                table: "SubscriberModelField",
                column: "SubscriberKey");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelRelationship_RepoId",
                schema: "catalog",
                table: "SubscriberModelRelationship",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelRelationship_SubscriberKey",
                schema: "catalog",
                table: "SubscriberModelRelationship",
                column: "SubscriberKey");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelTable_RepoId",
                schema: "catalog",
                table: "SubscriberModelTable",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelTable_SubscriberKey",
                schema: "catalog",
                table: "SubscriberModelTable",
                column: "SubscriberKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriberModelField",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SubscriberModelRelationship",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SubscriberModelTable",
                schema: "catalog");
        }
    }
}
