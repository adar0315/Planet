using Microsoft.EntityFrameworkCore;
using Planet.Domain;

namespace Planet.Data;

/// <summary><see cref="ITerrainStyleRepository"/> 的 EF Core 實作。</summary>
public class TerrainStyleRepository : ITerrainStyleRepository
{
    private readonly PlanetDbContext _db;

    /// <summary>建立地形樣式存取物件。</summary>
    /// <param name="db">已設定好連線字串的資料庫內容。</param>
    public TerrainStyleRepository(PlanetDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TerrainStyle>> GetAllAsync(CancellationToken ct = default) =>
        await _db.TerrainStyles.AsNoTracking().OrderBy(s => s.Terrain).ToListAsync(ct);

    /// <inheritdoc />
    public Task<TerrainStyle?> GetByTerrainAsync(TerrainType terrain, CancellationToken ct = default) =>
        _db.TerrainStyles.AsNoTracking().FirstOrDefaultAsync(s => s.Terrain == terrain, ct);

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(TerrainStyle style, CancellationToken ct = default)
    {
        var existing = await _db.TerrainStyles.FirstOrDefaultAsync(s => s.Terrain == style.Terrain, ct);
        if (existing is null) return false;

        existing.NameEn = style.NameEn;
        existing.NameZh = style.NameZh;
        existing.ColorDawn = style.ColorDawn;
        existing.ColorDay = style.ColorDay;
        existing.ColorDusk = style.ColorDusk;
        existing.ColorNight = style.ColorNight;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
