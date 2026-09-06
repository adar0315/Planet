using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Planet.Data;
using Planet.Generation;
using Planet.Service;
using Planet.Service.Hubs;
using Planet.Service.Security;
using Scalar.AspNetCore;

// Planet 星球地圖走動系統的後端進入點。啟動時（若資料庫是空的）會生成整個 Goldberg 多面體星球
// （12個五角形＋約2,002萬個六角形，見 GoldbergPlanetGenerator）並寫入 SQL Server，
// 之後透過 REST API（Planet.Service.Controllers.TilesController）與
// SignalR（MovementHub）提供地塊查詢與玩家移動服務。
// 可用 dotnet publish 打包成獨立EXE，透過 NSSM 安裝為 Windows 服務常駐執行
// （見 backend/deploy/nssm、docs/architecture.md「EXE打包與NSSM部署」）。
// 註：這裡用一般註解而非XML doc comment（///），因為top-level statements沒有一個實際的方法
// 宣告可以讓XML doc comment掛上去（掛了會噴CS1587「不是有效的語言項目」警告）。
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// 連線字串刻意不放在appsettings.json（那個檔案會進版控，帳密不該躺在裡面）。
// 缺少時直接啟動失敗並說明怎麼設定——比帶著一個沒有帳密的連線字串跑到第一次查詢才爆掉清楚得多。
var connectionString = builder.Configuration.GetConnectionString("PlanetDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "找不到 PlanetDb 連線字串。請擇一設定：\n" +
        "  開發：cd backend/src/Planet.Service && dotnet user-secrets set \"ConnectionStrings:PlanetDb\" \"<連線字串>\"\n" +
        "  服務：設定環境變數 ConnectionStrings__PlanetDb（NSSM 用 AppEnvironmentExtra，見 deploy/nssm/install-service.ps1）");
}

builder.Services.AddDbContext<PlanetDbContext>(options =>
    options.UseSqlServer(
        connectionString,
        // 預設30秒逾時對2000萬筆規模的一次性schema異動（如加空間索引）太短，migrations也會用到這個設定。
        sql => sql.CommandTimeout(600)));

// 管理端點的存取金鑰。沒設定時管理端點會停用（回503），不會退化成不用驗證——見 AdminApiKeyFilter。
builder.Services.Configure<AdminApiKeyOptions>(builder.Configuration.GetSection(AdminApiKeyOptions.SectionName));
builder.Services.AddScoped<AdminApiKeyFilter>();

// 附近地塊查詢的查詢框大小（依要求筆數反推）；沒有設定區段時用 NearbyQueryOptions 的預設值。
builder.Services.Configure<NearbyQueryOptions>(builder.Configuration.GetSection("NearbyQuery"));

builder.Services.AddScoped<ITileRepository, TileRepository>();
builder.Services.AddScoped<ITerrainStyleRepository, TerrainStyleRepository>();
builder.Services.AddScoped<IPlanetMetadataRepository, PlanetMetadataRepository>();
builder.Services.AddSingleton<ITileNeighborResolver, TileNeighborResolver>();
builder.Services.AddSingleton<OverviewTextureGenerator>();
builder.Services.AddSingleton<SeedingStatus>();
builder.Services.AddSingleton<TerrainOptionsStore>();
// 「實際產生目前資料的參數」與上面的「下次要用的參數」是兩件不同的事，刻意分成兩個服務。
builder.Services.AddSingleton<PlanetMetadataService>();
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddSingleton<PlanetRegenerationService>();
// 注意：這裡沒有註冊 IPlanetGenerator／ITerrainGenerator 的DI singleton——地形參數現在可以透過
// RegenerationController在執行期間改變，不能只在服務啟動時建構一次就固定死。需要生成器的地方
// （PlanetRegenerationService、OverviewTextureGenerator）都是要用的當下才依目前的TerrainOptionsStore
// 內容現組一個新的GoldbergPlanetGenerator/TerrainGenerator。

// 首次啟動（DB無Tile資料時）會在背景生成並用SqlBulkCopy寫入約2000萬筆Tile；HTTP監聽本身立刻開始
// （不等生成完成），前端可以透過 /api/status 輪詢進度，見 PlanetSeeder 的說明。
builder.Services.AddHostedService<PlanetSeeder>();

// 地塊查詢的JSON回應動輒數百KB（一次載入數千格），而內容是高度重複的欄位名＋整數，壓縮率很好。
// 刻意「不」擴充 MimeTypes：預設清單已含 application/json，而不含 application/octet-stream——
// 後者正好是星球總覽貼圖（2MB RGBA、已經在記憶體快取）的content type，讓它維持不壓縮才對，
// 不然每次請求都要重壓一次2MB。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
// 明確指定Fastest：Brotli的Optimal是quality 11，對數百KB的payload會吃掉數百毫秒的伺服器CPU，
// 完全抵銷掉傳輸省下的時間（各版本的預設值也不一致，不要依賴預設）。
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        // 5173 是開發用的 Vite dev server；8080 是正式部署的 Nginx（C:\DCIM\Nginx，serve frontend/dist）。
        policy.WithOrigins("http://localhost:5173", "http://localhost:8080")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// 壓縮要放在整條pipeline的最前面，才能涵蓋後面所有middleware產生的回應。
app.UseResponseCompression();

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MovementHub>("/hubs/movement");

app.Run();
