using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemPresentations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Presentations",
                table: "Items",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tracking",
                table: "Items",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UnitCode",
                table: "Items",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightKg",
                table: "Items",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Presentations",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Tracking",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "UnitCode",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "WeightKg",
                table: "Items");
        }
    }
}
