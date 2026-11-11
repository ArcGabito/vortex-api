using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class FiscalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FiscalDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Series = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CustomerDocType = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: true),
                    CustomerDocNumber = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    Request = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedXml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QrText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ResponseCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ResponseDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CdrXml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalProfiles",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Environment = table.Column<int>(type: "int", nullable: false),
                    Ubigeo = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    SolUser = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SolPassword = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Certificate = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CertificatePassword = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CertificateSubject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CertificateExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignatureAlgorithm = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalProfiles", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_FiscalProfiles_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_OrganizationId_DocumentType_Series_Number",
                table: "FiscalDocuments",
                columns: new[] { "OrganizationId", "DocumentType", "Series", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_OrganizationId_Product_SourceId",
                table: "FiscalDocuments",
                columns: new[] { "OrganizationId", "Product", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_Status_NextAttemptAt",
                table: "FiscalDocuments",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalDocuments");

            migrationBuilder.DropTable(
                name: "FiscalProfiles");
        }
    }
}
