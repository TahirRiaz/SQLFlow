using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class UserIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Role",
                schema: "catalog",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExternalObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalObjectId",
                schema: "catalog",
                table: "User",
                column: "ExternalObjectId",
                unique: true,
                filter: "[ExternalObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_Username",
                schema: "catalog",
                table: "User",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Role",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "User",
                schema: "catalog");
        }
    }
}
