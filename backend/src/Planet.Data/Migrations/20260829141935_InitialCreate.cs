using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tiles",
                columns: table => new
                {
                    TileId = table.Column<long>(type: "bigint", nullable: false),
                    Shape = table.Column<int>(type: "int", nullable: false),
                    FaceIndex = table.Column<int>(type: "int", nullable: false),
                    Row = table.Column<int>(type: "int", nullable: false),
                    Col = table.Column<int>(type: "int", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    Terrain = table.Column<int>(type: "int", nullable: false),
                    Elevation = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tiles", x => x.TileId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tiles_FaceIndex_Row_Col",
                table: "Tiles",
                columns: new[] { "FaceIndex", "Row", "Col" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tiles_Latitude_Longitude",
                table: "Tiles",
                columns: new[] { "Latitude", "Longitude" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tiles");
        }
    }
}
