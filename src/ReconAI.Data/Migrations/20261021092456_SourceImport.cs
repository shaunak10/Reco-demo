using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReconAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SheetName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    HeaderJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    DataRowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BlankRowsSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidRowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RejectedRowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadedByEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    VoidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidedByEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ColumnMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HeaderSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MappingJson = table.Column<string>(type: "TEXT", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConfirmedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfirmedByEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ColumnMappings_SourceFiles_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "SourceFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceRows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RowNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RowKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RowSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    TransactionRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SettlementRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Amount = table.Column<long>(type: "INTEGER", nullable: true),
                    Fees = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    TxnDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceRows_SourceFiles_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "SourceFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMappings_SourceFileId",
                table: "ColumnMappings",
                column: "SourceFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMappings_TenantId_Kind_HeaderSignature",
                table: "ColumnMappings",
                columns: new[] { "TenantId", "Kind", "HeaderSignature" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_TenantId_Kind_Sha256",
                table: "SourceFiles",
                columns: new[] { "TenantId", "Kind", "Sha256" },
                unique: true,
                filter: "\"IsVoided\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_TenantId_UploadedUtc",
                table: "SourceFiles",
                columns: new[] { "TenantId", "UploadedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_RowKey",
                table: "SourceRows",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_SourceFileId_RowNumber",
                table: "SourceRows",
                columns: new[] { "SourceFileId", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_TenantId_Kind_SettlementRef",
                table: "SourceRows",
                columns: new[] { "TenantId", "Kind", "SettlementRef" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_TenantId_Kind_TransactionRef",
                table: "SourceRows",
                columns: new[] { "TenantId", "Kind", "TransactionRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnMappings");

            migrationBuilder.DropTable(
                name: "SourceRows");

            migrationBuilder.DropTable(
                name: "SourceFiles");
        }
    }
}
