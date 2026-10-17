using Microsoft.EntityFrameworkCore;
using Planet.Domain;

namespace Planet.Data;

/// <summary>星球資料庫的 EF Core 資料庫內容，目前只有一張 Tiles 表（見 <see cref="Tiles"/>）。</summary>
public class PlanetDbContext : DbContext
{
    /// <summary>以指定的連線設定建立資料庫內容（連線字串等由呼叫端透過 <paramref name="options"/> 注入）。</summary>
    public PlanetDbContext(DbContextOptions<PlanetDbContext> options) : base(options)
    {
    }

    /// <summary>星球上所有地塊。</summary>
    public DbSet<Tile> Tiles => Set<Tile>();

    /// <summary>每種地形的顯示樣式（中英文名稱＋依時段變化的顏色），見 <see cref="TerrainStyle"/>。</summary>
    public DbSet<TerrainStyle> TerrainStyles => Set<TerrainStyle>();

    /// <summary>描述「目前這顆星球實際上是什麼」的單筆記錄，見 <see cref="PlanetMetadata"/>。</summary>
    public DbSet<PlanetMetadata> PlanetMetadata => Set<PlanetMetadata>();

    /// <summary>模擬粗格的完整狀態（地形來自地質模擬時才有資料，見 <see cref="PlanetCell"/>）。</summary>
    public DbSet<PlanetCell> PlanetCells => Set<PlanetCell>();

    /// <summary>設定 Tile／TerrainStyle／PlanetMetadata 的主鍵、索引。</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tile>(entity =>
        {
            entity.HasKey(t => t.TileId);
            entity.Property(t => t.TileId).ValueGeneratedNever();

            // 依面編號查詢區塊資料，供REST API依經緯度找附近Tile時使用
            entity.HasIndex(t => new { t.FaceIndex, t.Row, t.Col }).IsUnique();
            entity.HasIndex(t => new { t.Latitude, t.Longitude });
        });

        modelBuilder.Entity<TerrainStyle>(entity =>
        {
            entity.HasKey(t => t.Terrain);
            entity.HasData(TerrainStyleSeedData.All);
        });

        modelBuilder.Entity<PlanetMetadata>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedNever();
            // 刻意不用 HasData 塞種子資料：這筆記錄描述的是「實際存在的星球」，
            // 全新的空資料庫還沒有星球，先塞一列反而是在宣稱一個不存在的事實。
            // 由 PlanetSeeder／PlanetRegenerationService 在真的生成完之後才寫入。
            entity.Property(m => m.SimulationSnapshotName).HasMaxLength(64);
            entity.Property(m => m.SimulationStateHash).HasMaxLength(16);
        });

        modelBuilder.Entity<PlanetCell>(entity =>
        {
            entity.HasKey(c => c.CellId);
            entity.Property(c => c.CellId).ValueGeneratedNever();
        });
    }
}
