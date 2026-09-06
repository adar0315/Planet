using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class TerrainSystemOverhaul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Elevation",
                table: "Tiles");

            migrationBuilder.AlterColumn<int>(
                name: "SeaLevel",
                table: "Tiles",
                type: "int",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "float");

            migrationBuilder.AddColumn<int>(
                name: "ElevationLevel",
                table: "Tiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HeightCategory",
                table: "Tiles",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ElevationLevel",
                table: "Tiles");

            migrationBuilder.DropColumn(
                name: "HeightCategory",
                table: "Tiles");

            migrationBuilder.AlterColumn<double>(
                name: "SeaLevel",
                table: "Tiles",
                type: "float",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<double>(
                name: "Elevation",
                table: "Tiles",
                type: "float",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
