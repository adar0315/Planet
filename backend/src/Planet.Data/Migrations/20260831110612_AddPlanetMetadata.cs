using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanetMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanetMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SubdivisionFrequency = table.Column<int>(type: "int", nullable: false),
                    RadiusMeters = table.Column<double>(type: "float", nullable: false),
                    TileEdgeLengthMeters = table.Column<double>(type: "float", nullable: false),
                    TotalTileCount = table.Column<long>(type: "bigint", nullable: false),
                    TerrainOptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsInferred = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanetMetadata", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanetMetadata");
        }
    }
}
