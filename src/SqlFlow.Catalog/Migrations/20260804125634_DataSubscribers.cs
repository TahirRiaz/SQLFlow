using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class DataSubscribers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subscriber",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    File = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriber", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriberQuery",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriberKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ServerRef = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ObjectKeys = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberQuery", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriber_Name",
                schema: "catalog",
                table: "Subscriber",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriber_ObjectKey",
                schema: "catalog",
                table: "Subscriber",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriber_RepoId",
                schema: "catalog",
                table: "Subscriber",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberQuery_RepoId",
                schema: "catalog",
                table: "SubscriberQuery",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberQuery_SubscriberKey",
                schema: "catalog",
                table: "SubscriberQuery",
                column: "SubscriberKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Subscriber",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SubscriberQuery",
                schema: "catalog");
        }
    }
}
