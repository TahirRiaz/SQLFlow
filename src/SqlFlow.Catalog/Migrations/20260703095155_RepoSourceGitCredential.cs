using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RepoSourceGitCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CredentialReference",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialUsername",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CredentialReference",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "CredentialUsername",
                schema: "catalog",
                table: "RepoSource");
        }
    }
}
