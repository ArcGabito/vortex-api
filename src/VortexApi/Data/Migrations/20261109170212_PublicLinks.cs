using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublicLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Document = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ViewCount = table.Column<int>(type: "int", nullable: false),
                    FirstViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignerEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Signature = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResponderIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResponderUserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicLinks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicLinks_OrganizationId_Product_DocumentType_DocumentId",
                table: "PublicLinks",
                columns: new[] { "OrganizationId", "Product", "DocumentType", "DocumentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicLinks");
        }
    }
}
