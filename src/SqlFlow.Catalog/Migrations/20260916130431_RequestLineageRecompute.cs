using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Requests a full lineage recompute on every repository's next sync. The lineage scan changed what it records
    /// (file locations anchored at their document, the files and datasets a registered flow kind declares) without any
    /// flow document changing, so the unchanged-estate shortcut would otherwise keep serving the graph computed before.
    /// The flag clears itself once the sync succeeds.
    /// </summary>
    public partial class RequestLineageRecompute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [catalog].[RepoSource] SET [ForceLineageOnNextSync] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A pending recompute request is harmless under the earlier scan, and which repositories had one before
            // this migration is not recorded, so there is nothing to undo.
        }
    }
}
