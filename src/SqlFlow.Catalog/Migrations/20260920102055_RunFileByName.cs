using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Finds the runs that handled a file by its name. The catalog already answers "what did this run process"; this
    /// index answers the other direction, "what has been done with this file", which is what tracing a row back through
    /// the flows that landed and loaded it needs. Without it that question scans every processed file the estate has
    /// ever recorded. The run id rides in the key so the index covers the join back to the run.
    /// </summary>
    public partial class RunFileByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RunFile_Name_RunId",
                schema: "catalog",
                table: "RunFile",
                columns: new[] { "Name", "RunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunFile_Name_RunId",
                schema: "catalog",
                table: "RunFile");
        }
    }
}
