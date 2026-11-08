using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartyCreditAndAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CreditLimit",
                table: "Parties",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPercent",
                table: "Parties",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerName",
                table: "Parties",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Parties",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentTermDays",
                table: "Parties",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditLimit",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "DiscountPercent",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "OwnerName",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "PaymentTermDays",
                table: "Parties");
        }
    }
}
