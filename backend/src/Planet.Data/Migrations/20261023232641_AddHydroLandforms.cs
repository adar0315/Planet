using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHydroLandforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RiverCellId",
                table: "Tiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "RiverWidthMeters",
                table: "Tiles",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "AggradationRate",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "BaseflowShare",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "DeflationRate",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "DeltaSupply",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "ExposedRock",
                table: "PlanetCells",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FlowRegime",
                table: "PlanetCells",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FrozenMonths",
                table: "PlanetCells",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "GlacialIncisionRate",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "IncisionRate",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "LoessRate",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "MeltShare",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<byte[]>(
                name: "MonthlyDischargeRatio",
                table: "PlanetCells",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "MonthlyPrecipitation",
                table: "PlanetCells",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "MonthlyTemperature",
                table: "PlanetCells",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<float>(
                name: "RiverWidthMeters",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "SnowMonths",
                table: "PlanetCells",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "WaveExposure",
                table: "PlanetCells",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RiverCellId",
                table: "Tiles");

            migrationBuilder.DropColumn(
                name: "RiverWidthMeters",
                table: "Tiles");

            migrationBuilder.DropColumn(
                name: "AggradationRate",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "BaseflowShare",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "DeflationRate",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "DeltaSupply",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "ExposedRock",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "FlowRegime",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "FrozenMonths",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "GlacialIncisionRate",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "IncisionRate",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "LoessRate",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "MeltShare",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "MonthlyDischargeRatio",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "MonthlyPrecipitation",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "MonthlyTemperature",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "RiverWidthMeters",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "SnowMonths",
                table: "PlanetCells");

            migrationBuilder.DropColumn(
                name: "WaveExposure",
                table: "PlanetCells");
        }
    }
}
