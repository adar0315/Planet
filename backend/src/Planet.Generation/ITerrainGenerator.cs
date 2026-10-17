using Planet.Domain;

namespace Planet.Generation;

/// <summary>某經緯度上的地形取樣結果。</summary>
/// <param name="Terrain">地表覆蓋類型。</param>
/// <param name="HeightCategory">地表覆蓋物高度/深度細分等級，意義依 Terrain 而定，無細分的地形固定為0。</param>
/// <param name="ElevationLevel">
/// 地形海拔等級，每級5公尺。程序化地形的範圍是 -500~+500；模擬出來的地形照實際高度換算，
/// 高山與深海會超出這個範圍。
/// </param>
/// <param name="SourceCellId">地形來自模擬粗格時，是哪一格（見 <see cref="Tile.SourceCellId"/>）；程序化地形是 null。</param>
public readonly record struct TerrainSample(
    TerrainType Terrain, int HeightCategory, int ElevationLevel, int? SourceCellId = null);

/// <summary>依經緯度程序化取樣地形（類型、海拔、高度細分）。同一組(latitude, longitude)在同一個實例上必須回傳同樣的結果（確定性），才能讓相鄰地塊的地形連續一致。</summary>
public interface ITerrainGenerator
{
    /// <summary>取樣指定經緯度的地形。</summary>
    /// <param name="latitude">緯度（度），北緯為正。</param>
    /// <param name="longitude">經度（度），東經為正。</param>
    TerrainSample Sample(double latitude, double longitude);
}
