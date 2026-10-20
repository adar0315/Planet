using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanetCellRivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RiverDownstreamCellId",
                table: "PlanetCells",
                type: "int",
                nullable: false,
                // 既有的粗格都不是河（−1）；0 會被讀成「往第 0 格流」
                defaultValue: -1);

            migrationBuilder.AddColumn<float>(
                name: "RiverSize",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RiverDownstreamCellId",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "RiverSize",
                table: "PlanetCells");
        }
    }
}
