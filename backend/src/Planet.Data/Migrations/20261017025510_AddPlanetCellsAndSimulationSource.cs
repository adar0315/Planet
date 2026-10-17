using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanetCellsAndSimulationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceCellId",
                table: "Tiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SimulationElapsedYears",
                table: "PlanetMetadata",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SimulationFrequency",
                table: "PlanetMetadata",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimulationSnapshotName",
                table: "PlanetMetadata",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimulationStateHash",
                table: "PlanetMetadata",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SimulationStepCount",
                table: "PlanetMetadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanetCells",
                columns: table => new
                {
                    CellId = table.Column<int>(type: "int", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    Terrain = table.Column<int>(type: "int", nullable: false),
                    HeightCategory = table.Column<int>(type: "int", nullable: false),
                    SurfaceElevationMeters = table.Column<float>(type: "real", nullable: false),
                    WaterDepthMeters = table.Column<float>(type: "real", nullable: false),
                    IsOcean = table.Column<bool>(type: "bit", nullable: false),
                    IceThicknessMeters = table.Column<float>(type: "real", nullable: false),
                    SedimentThicknessMeters = table.Column<float>(type: "real", nullable: false),
                    OrganicThicknessMeters = table.Column<float>(type: "real", nullable: false),
                    TemperatureCelsius = table.Column<float>(type: "real", nullable: false),
                    Precipitation = table.Column<float>(type: "real", nullable: false),
                    CrustType = table.Column<int>(type: "int", nullable: false),
                    CrustThicknessKm = table.Column<float>(type: "real", nullable: false),
                    CrustAgeMyr = table.Column<float>(type: "real", nullable: false),
                    CrustDensity = table.Column<float>(type: "real", nullable: false),
                    PlateId = table.Column<int>(type: "int", nullable: false),
                    Boundary = table.Column<int>(type: "int", nullable: false),
                    VolcanicActivity = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanetCells", x => x.CellId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "SourceCellId",
                table: "Tiles");

            migrationBuilder.DropColumn(
                name: "SimulationElapsedYears",
                table: "PlanetMetadata");

            migrationBuilder.DropColumn(
                name: "SimulationFrequency",
                table: "PlanetMetadata");

            migrationBuilder.DropColumn(
                name: "SimulationSnapshotName",
                table: "PlanetMetadata");

            migrationBuilder.DropColumn(
                name: "SimulationStateHash",
                table: "PlanetMetadata");

            migrationBuilder.DropColumn(
                name: "SimulationStepCount",
                table: "PlanetMetadata");
        }
    }
}
