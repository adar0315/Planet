using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 產生整個星球的地塊資料：12個五角形＋約2000萬個六角形（邊長約1公里），
/// 兩大洲＋南北極冰帽、其餘為海，北極放置一個五角形。
/// </summary>
public interface IPlanetGenerator
{
    /// <summary>星球半徑（公尺），由細分頻率與目標地塊邊長換算而得。</summary>
    double PlanetRadiusMeters { get; }

    /// <summary><see cref="GenerateTiles"/> 總共會產生幾個地塊，不必實際跑一次生成就能先知道（供顯示生成進度用）。</summary>
    long TotalTileCount { get; }

    /// <summary>依序產生星球上每一個地塊（含座標、地形、海拔）。呼叫端通常直接串流寫入資料庫，不會整個集合先留在記憶體。</summary>
    IEnumerable<Tile> GenerateTiles();
}
