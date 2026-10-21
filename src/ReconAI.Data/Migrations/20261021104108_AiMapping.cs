using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReconAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiAcceptedCount",
                table: "ColumnMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiChangedColumns",
                table: "ColumnMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "AiInferenceId",
                table: "ColumnMappings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiSuggestedCount",
                table: "ColumnMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AIInferenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Task = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    InputSummary = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIInferenceLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIInferenceLogs_SourceFileId",
                table: "AIInferenceLogs",
                column: "SourceFileId");

            migrationBuilder.CreateIndex(
                name: "IX_AIInferenceLogs_TenantId_CreatedUtc",
                table: "AIInferenceLogs",
                columns: new[] { "TenantId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AIInferenceLogs");

            migrationBuilder.DropColumn(
                name: "AiAcceptedCount",
                table: "ColumnMappings");

            migrationBuilder.DropColumn(
                name: "AiChangedColumns",
                table: "ColumnMappings");

            migrationBuilder.DropColumn(
                name: "AiInferenceId",
                table: "ColumnMappings");

            migrationBuilder.DropColumn(
                name: "AiSuggestedCount",
                table: "ColumnMappings");
        }
    }
}
