using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class CentralDebugAndDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Definition",
                schema: "catalog",
                table: "Object",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ObjectColumn",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Nullable = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectColumn", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunHealthCheckMetric",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SeriesPoints = table.Column<int>(type: "int", nullable: false),
                    ImputedPoints = table.Column<int>(type: "int", nullable: false),
                    ImmaturePoints = table.Column<int>(type: "int", nullable: false),
                    Anomalies = table.Column<int>(type: "int", nullable: false),
                    LevelShifts = table.Column<int>(type: "int", nullable: false),
                    ModelTrained = table.Column<bool>(type: "bit", nullable: false),
                    ModelTrainer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunHealthCheckMetric", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunStatement",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunStatement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunSurrogateKey",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SurrogateKeyId = table.Column<int>(type: "int", nullable: false),
                    SurrogateTable = table.Column<string>(type: "nvarchar(776)", maxLength: 776, nullable: false),
                    SurrogateColumn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsRemote = table.Column<bool>(type: "bit", nullable: false),
                    KeysGenerated = table.Column<long>(type: "bigint", nullable: false),
                    RowsStamped = table.Column<long>(type: "bigint", nullable: false),
                    Executed = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunSurrogateKey", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectColumn_Name",
                schema: "catalog",
                table: "ObjectColumn",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectColumn_ObjectKey",
                schema: "catalog",
                table: "ObjectColumn",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_RunHealthCheckMetric_RunId",
                schema: "catalog",
                table: "RunHealthCheckMetric",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunStatement_RunId",
                schema: "catalog",
                table: "RunStatement",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunSurrogateKey_RunId",
                schema: "catalog",
                table: "RunSurrogateKey",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectColumn",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunHealthCheckMetric",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunStatement",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunSurrogateKey",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "Definition",
                schema: "catalog",
                table: "Object");
        }
    }
}
