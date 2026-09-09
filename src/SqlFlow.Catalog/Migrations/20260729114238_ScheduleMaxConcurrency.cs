using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleMaxConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrency",
                schema: "catalog",
                table: "Schedule",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupMaxConcurrency",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxConcurrency",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "GroupMaxConcurrency",
                schema: "catalog",
                table: "Run");
        }
    }
}
