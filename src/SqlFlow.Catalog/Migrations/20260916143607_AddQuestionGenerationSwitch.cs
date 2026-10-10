using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionGenerationSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "QuestionGeneration",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuestionGenerationUpdatedBy",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuestionGenerationUpdatedUtc",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuestionGeneration",
                schema: "catalog",
                table: "SemanticLayerSettings");

            migrationBuilder.DropColumn(
                name: "QuestionGenerationUpdatedBy",
                schema: "catalog",
                table: "SemanticLayerSettings");

            migrationBuilder.DropColumn(
                name: "QuestionGenerationUpdatedUtc",
                schema: "catalog",
                table: "SemanticLayerSettings");
        }
    }
}
