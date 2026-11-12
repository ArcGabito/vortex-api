using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartyConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsentCapturedBy",
                table: "Parties",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentChannels",
                table: "Parties",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentDataProcessing",
                table: "Parties",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentMarketing",
                table: "Parties",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentPolicyVersion",
                table: "Parties",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentSource",
                table: "Parties",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentUpdatedAt",
                table: "Parties",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsentCapturedBy",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentChannels",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentDataProcessing",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentMarketing",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentPolicyVersion",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentSource",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ConsentUpdatedAt",
                table: "Parties");
        }
    }
}
