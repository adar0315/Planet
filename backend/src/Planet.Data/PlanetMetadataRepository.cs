using Microsoft.EntityFrameworkCore;
using Planet.Domain;

namespace Planet.Data;

/// <summary><see cref="IPlanetMetadataRepository"/> 的 EF Core 實作。</summary>
public class PlanetMetadataRepository : IPlanetMetadataRepository
{
    /// <summary>星球記錄固定使用的主鍵值（這張表永遠只有一列）。</summary>
    private const int SingletonId = 1;

    private readonly PlanetDbContext _db;

    /// <summary>建立星球記錄存取物件。</summary>
    /// <param name="db">已設定好連線字串的資料庫內容。</param>
    public PlanetMetadataRepository(PlanetDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task<PlanetMetadata?> GetAsync(CancellationToken ct = default) =>
        _db.PlanetMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.Id == SingletonId, ct);

    /// <inheritdoc />
    public async Task UpsertAsync(PlanetMetadata metadata, CancellationToken ct = default)
    {
        metadata.Id = SingletonId;

        var existing = await _db.PlanetMetadata.FirstOrDefaultAsync(m => m.Id == SingletonId, ct);
        if (existing is null)
        {
            _db.PlanetMetadata.Add(metadata);
        }
        else
        {
            existing.SubdivisionFrequency = metadata.SubdivisionFrequency;
            existing.RadiusMeters = metadata.RadiusMeters;
            existing.TileEdgeLengthMeters = metadata.TileEdgeLengthMeters;
            existing.TotalTileCount = metadata.TotalTileCount;
            existing.TerrainOptionsJson = metadata.TerrainOptionsJson;
            existing.GeneratedAtUtc = metadata.GeneratedAtUtc;
            existing.IsInferred = metadata.IsInferred;
        }

        await _db.SaveChangesAsync(ct);
    }
}
