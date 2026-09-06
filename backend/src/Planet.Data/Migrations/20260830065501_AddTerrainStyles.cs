using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Planet.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTerrainStyles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TerrainStyles",
                columns: table => new
                {
                    Terrain = table.Column<int>(type: "int", nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameZh = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColorDawn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColorDay = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColorDusk = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColorNight = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerrainStyles", x => x.Terrain);
                });

            migrationBuilder.InsertData(
                table: "TerrainStyles",
                columns: new[] { "Terrain", "ColorDawn", "ColorDay", "ColorDusk", "ColorNight", "NameEn", "NameZh" },
                values: new object[,]
                {
                    { 0, "#88943a", "#8fae4e", "#81822f", "#24341b", "Grassland", "草地" },
                    { 1, "#667a2e", "#6b8f3e", "#606b25", "#1b2b15", "Shrubland", "灌木叢" },
                    { 2, "#2c5b27", "#2e6b34", "#29501f", "#0c2012", "Forest", "林地" },
                    { 3, "#5993a9", "#5dade2", "#548288", "#18344e", "Freshwater", "淡水" },
                    { 4, "#1a4355", "#1b4f72", "#183b44", "#071827", "Seawater", "海水" },
                    { 5, "#66472b", "#6b5439", "#603f22", "#1b1914", "Mud", "泥地" },
                    { 6, "#b98b46", "#c2a45e", "#af7b38", "#313120", "Loess", "黃土地" },
                    { 7, "#756a5e", "#7b7d7d", "#6f5e4b", "#1f262b", "Rock", "岩地" },
                    { 8, "#d5ab66", "#e0c988", "#ca9752", "#393c2f", "Sand", "沙地" },
                    { 9, "#e6d1bb", "#f2f6fa", "#dab896", "#3e4a56", "SnowField", "雪地" },
                    { 10, "#c5c5b4", "#cfe8f0", "#baae90", "#354653", "Glacier", "冰原" },
                    { 11, "#494c2b", "#4d5a3a", "#454423", "#141b14", "Swamp", "沼澤" },
                    { 12, "#ddc4a6", "#e8e6de", "#d1ac85", "#3b454d", "SaltFlat", "鹽鹼地" },
                    { 13, "#372521", "#3a2c2c", "#34211a", "#0f0d0f", "VolcanicRock", "火山岩地" },
                    { 14, "#379078", "#3aa9a0", "#347f60", "#0f3337", "CoralReef", "珊瑚礁" },
                    { 15, "#58493d", "#5c5652", "#534031", "#171a1c", "Cliff", "懸崖" },
                    { 16, "#938861", "#9aa082", "#8b784e", "#27302d", "Tundra", "苔原" },
                    { 17, "#957c63", "#9c9284", "#8c6e4f", "#282c2e", "Gravel", "礫灘" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TerrainStyles");
        }
    }
}
