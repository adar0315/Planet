using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Constructions",
                columns: table => new
                {
                    SubTileId = table.Column<long>(type: "bigint", nullable: false),
                    ParentTileId = table.Column<long>(type: "bigint", nullable: false),
                    Structure = table.Column<int>(type: "int", nullable: true),
                    TerrainEdit = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Constructions", x => x.SubTileId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Constructions_ParentTileId",
                table: "Constructions",
                column: "ParentTileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Constructions");
        }
    }
}
