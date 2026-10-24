using Planet.Domain;

namespace Planet.Generation;

/// <summary>近看細分出來的一個小六角（或五角）：只在玩家附近即時產生，不存資料庫。</summary>
/// <param name="SubTileId">小格的編號（由正規化的面、列、欄編碼，加上 <see cref="SubTileGrid.IdOffset"/>，不會跟地塊的編號重疊）。</param>
/// <param name="ParentTileId">它屬於哪一個地塊（離它最近的地塊中心）。</param>
/// <param name="FaceIndex">正規化的面。</param>
/// <param name="Row">細分網格的列。</param>
/// <param name="Col">細分網格的欄。</param>
/// <param name="Latitude">緯度（度）。</param>
/// <param name="Longitude">經度（度）。</param>
/// <param name="Shape">形狀（二十面體頂點上的是五角形）。</param>
public readonly record struct SubTile(
    long SubTileId, long ParentTileId, int FaceIndex, int Row, int Col, double Latitude, double Longitude, TileShape Shape);

/// <summary>
/// 地塊的細分（水文地貌規劃 H7，2026-10-24）：同一套 Goldberg 網格，細分頻率乘上 <see cref="Factor"/>。
///
/// 地塊（頻率 n）的格心 (i, j) 在細分網格（頻率 4n）上就是 (4i, 4j)——同一個點——其餘的小格散在它周圍。
/// **每個小格歸屬離它最近的地塊中心**（Voronoi），所以小格拼起來的範圍跟地塊的六角形不完全重合，邊界是鋸齒狀的
/// （六角形沒辦法剛好拼成一個大六角形）。這是使用者知道並接受的（2026-10-24）。
///
/// 小格的座標跟地塊一樣正規化成面索引最小的表示（<see cref="TileNeighborResolver.Canonicalize(int, int, int)"/>），
/// 鄰居也用同一支 <see cref="TileNeighborResolver"/> 推導（只是頻率不同）——鄰居座標的推導只准在後端一份（CLAUDE.md）。
/// </summary>
public sealed class SubTileGrid
{
    /// <summary>細分倍率：每邊切成 4 段，地塊約 1 公里、小格約 250 公尺，每個地塊約 16 個小格。</summary>
    public const int Factor = 4;

    /// <summary>小格編號的起點：地塊的編號最多約 2,000 萬，小格從一兆開始，兩邊不會撞號。</summary>
    public const long IdOffset = 1_000_000_000_000L;

    private readonly int _n;
    private readonly int _subN;
    private readonly TileNeighborResolver _parentResolver;
    private readonly TileNeighborResolver _subResolver;

    /// <summary>建立細分網格。</summary>
    /// <param name="parentFrequency">地塊的細分頻率（正式是 <see cref="GoldbergPlanetGenerator.SubdivisionFrequency"/>）。</param>
    public SubTileGrid(int parentFrequency)
    {
        _n = parentFrequency;
        _subN = parentFrequency * Factor;
        _parentResolver = new TileNeighborResolver(_n);
        _subResolver = new TileNeighborResolver(_subN);
    }

    /// <summary>細分網格的頻率（地塊的 <see cref="Factor"/> 倍）。</summary>
    public int SubFrequency => _subN;

    /// <summary>
    /// 這些地塊底下所有的小格，以及每個小格的鄰居（只連到同一批裡的小格，批次外的是 null；順序同 <see cref="TileNeighborResolver"/>）。
    /// </summary>
    /// <param name="parents">地塊（座標要是正規化的，資料庫裡存的就是）。</param>
    public (IReadOnlyList<SubTile> SubTiles, IReadOnlyList<long?[]> Neighbors) ChildrenOf(IReadOnlyList<Tile> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var result = new List<SubTile>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var parent in parents) AddChildren(parent, result, seen);

        var idByCoordinate = new Dictionary<(int, int, int), long>(result.Count);
        foreach (var sub in result) idByCoordinate[(sub.FaceIndex, sub.Row, sub.Col)] = sub.SubTileId;
        var neighbors = new long?[result.Count][];
        for (var i = 0; i < result.Count; i++)
        {
            var sub = result[i];
            var coordinates = _subResolver.GetNeighborCoordinates(new Tile { FaceIndex = sub.FaceIndex, Row = sub.Row, Col = sub.Col, Shape = sub.Shape });
            var ids = new long?[coordinates.Count];
            for (var k = 0; k < coordinates.Count; k++) ids[k] = idByCoordinate.TryGetValue(coordinates[k], out var id) ? id : null;
            neighbors[i] = ids;
        }
        return (result, neighbors);
    }

    private void AddChildren(Tile parent, List<SubTile> result, HashSet<(int, int, int)> seen)
    {
        var self = _parentResolver.Canonicalize(parent.FaceIndex, parent.Row, parent.Col);
        // 地塊在面的邊上、角上時，它的範圍橫跨好幾個面：每個面都要列舉一次
        foreach (var (face, row, col) in Representations(parent))
        {
            var faceVertices = IcosahedronGeometry.Faces[face];
            for (var i = Factor * row - Factor + 1; i <= Factor * row + Factor - 1; i++)
            {
                for (var j = Factor * col - Factor + 1; j <= Factor * col + Factor - 1; j++)
                {
                    var k = _subN - i - j;
                    if (i < 0 || j < 0 || k < 0) continue;
                    var point = PointOn(faceVertices, i, j, _subN);
                    if (NearestParent(face, i, j, point) != self) continue;
                    var canonical = _subResolver.Canonicalize(face, i, j);
                    if (!seen.Add(canonical)) continue;
                    var isPentagon = (canonical.Row == 0 && canonical.Col == 0)
                        || (canonical.Row == _subN && canonical.Col == 0) || (canonical.Row == 0 && canonical.Col == _subN);
                    var latitude = Math.Asin(Math.Clamp(point.Z, -1.0, 1.0)) * (180.0 / Math.PI);
                    var longitude = Math.Atan2(point.Y, point.X) * (180.0 / Math.PI);
                    result.Add(new SubTile(
                        IdOf(canonical), parent.TileId, canonical.FaceIndex, canonical.Row, canonical.Col,
                        latitude, longitude, isPentagon ? TileShape.Pentagon : TileShape.Hexagon));
                }
            }
        }
    }

    /// <summary>小格的編號（正規化座標的編碼加上 <see cref="IdOffset"/>）。</summary>
    private long IdOf((int FaceIndex, int Row, int Col) c) => IdOffset + ((long)c.FaceIndex * (_subN + 1) + c.Row) * (_subN + 1) + c.Col;

    /// <summary>
    /// 細分網格上 (face, i, j) 那一點離哪個地塊中心最近（回傳地塊的正規化座標）。候選是這個面上 (i/4, j/4) 附近的地塊格點，
    /// 包含面邊上的——地塊的半徑約 2.3 個小格距，最近的地塊一定在這個面上或邊上（跨到隔壁面內部的不會更近）。
    /// </summary>
    private (int, int, int) NearestParent(int face, int i, int j, Vec3 point)
    {
        var faceVertices = IcosahedronGeometry.Faces[face];
        var best = (-1, -1);
        var bestDot = double.NegativeInfinity;
        for (var pi = i / Factor - 1; pi <= i / Factor + 2; pi++)
        {
            for (var pj = j / Factor - 1; pj <= j / Factor + 2; pj++)
            {
                if (pi < 0 || pj < 0 || pi + pj > _n) continue;
                var center = PointOn(faceVertices, pi, pj, _n);
                var dot = center.X * point.X + center.Y * point.Y + center.Z * point.Z;
                if (dot <= bestDot) continue;
                bestDot = dot;
                best = (pi, pj);
            }
        }
        return _parentResolver.Canonicalize(face, best.Item1, best.Item2);
    }

    /// <summary>地塊在每一個包含它的面上的座標（面內部只有一個；邊上兩個；二十面體頂點五個）。</summary>
    private IEnumerable<(int Face, int Row, int Col)> Representations(Tile parent)
    {
        var (ia, ib, ic) = IcosahedronGeometry.Faces[parent.FaceIndex];
        var weights = new Dictionary<int, int> { [ia] = _n - parent.Row - parent.Col, [ib] = parent.Row, [ic] = parent.Col };
        var nonzero = weights.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(v => v).ToArray();
        IEnumerable<int> faces = nonzero.Length switch
        {
            1 => IcosahedronGeometry.VertexFaces[nonzero[0]],
            2 => [IcosahedronGeometry.EdgeFaces[(nonzero[0], nonzero[1])].Face1, IcosahedronGeometry.EdgeFaces[(nonzero[0], nonzero[1])].Face2],
            _ => [parent.FaceIndex],
        };
        foreach (var face in faces)
        {
            var (_, b, c) = IcosahedronGeometry.Faces[face];
            yield return (face, weights.GetValueOrDefault(b, 0), weights.GetValueOrDefault(c, 0));
        }
    }

    /// <summary>面上的格點 (i, j)（頻率 frequency）在單位球上的位置，跟 <see cref="GoldbergPlanetGenerator"/> 同一個算法。</summary>
    private static Vec3 PointOn((int A, int B, int C) face, int i, int j, int frequency)
    {
        var vertices = IcosahedronGeometry.Vertices;
        var k = frequency - i - j;
        return (vertices[face.A] * k + vertices[face.B] * i + vertices[face.C] * j).Normalized();
    }
}
