using Microsoft.EntityFrameworkCore;
using Planet.Domain;

namespace Planet.Data;

/// <summary><see cref="IConstructionRepository"/> 的 EF Core 實作。</summary>
public class ConstructionRepository : IConstructionRepository
{
    private readonly PlanetDbContext _db;

    /// <summary>建立存取物件。</summary>
    /// <param name="db">資料庫內容。</param>
    public ConstructionRepository(PlanetDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Construction>> GetByParentsAsync(IReadOnlyCollection<long> parentTileIds, CancellationToken ct = default)
    {
        if (parentTileIds.Count == 0) return [];
        return await _db.Constructions.AsNoTracking()
            .Where(c => parentTileIds.Contains(c.ParentTileId))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Construction>> GetByIdsAsync(IReadOnlyCollection<long> subTileIds, CancellationToken ct = default)
    {
        if (subTileIds.Count == 0) return [];
        return await _db.Constructions.AsNoTracking()
            .Where(c => subTileIds.Contains(c.SubTileId))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<Construction?> GetAsync(long subTileId, CancellationToken ct = default) =>
        _db.Constructions.AsNoTracking().FirstOrDefaultAsync(c => c.SubTileId == subTileId, ct);

    /// <inheritdoc />
    public async Task SaveAsync(Construction construction, CancellationToken ct = default)
    {
        var existing = await _db.Constructions.FirstOrDefaultAsync(c => c.SubTileId == construction.SubTileId, ct);
        var empty = construction.Structure is null && construction.TerrainEdit is null;
        if (existing is null)
        {
            if (empty) return;
            _db.Constructions.Add(construction);
        }
        else if (empty)
        {
            _db.Constructions.Remove(existing);
        }
        else
        {
            existing.ParentTileId = construction.ParentTileId;
            existing.Structure = construction.Structure;
            existing.TerrainEdit = construction.TerrainEdit;
            existing.UpdatedAtUtc = construction.UpdatedAtUtc;
        }
        await _db.SaveChangesAsync(ct);
    }
}
