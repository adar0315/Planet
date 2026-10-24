using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanetCellWaterSurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "WaterSurfaceMeters",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WaterSurfaceMeters",
                table: "PlanetCells");
        }
    }
}
