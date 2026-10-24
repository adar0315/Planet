using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 依Tile的(FaceIndex,Row,Col)即時算出鄰居座標，不將鄰接關係存進資料庫。
///
/// 六角形（面內部或邊界）恰有6個鄰居；五角形（原二十面體頂點，含南北極）恰有5個。
/// 邊界/角點Tile實際同時屬於2~5個面，但GoldbergPlanetGenerator只存「第一次算到時」的單一面座標
/// （面索引最小的那個，因為生成時是照面索引0..19依序掃描並去重）。
/// 因此每個算出來的鄰居座標都會再做「正規化」：找出「同時涵蓋這些實際頂點」的所有候選面，
/// 取面索引最小的那一個，確保回傳的(FaceIndex,Row,Col)跟資料庫裡實際存的那筆Tile一致，
/// 而不是隨便一個在數學上等價、但DB查不到的座標。
/// </summary>
public class TileNeighborResolver : ITileNeighborResolver
{
    private readonly int _n;

    /// <summary>以正式的細分頻率（<see cref="GoldbergPlanetGenerator.SubdivisionFrequency"/>）建立鄰居解析器。</summary>
    public TileNeighborResolver() : this(GoldbergPlanetGenerator.SubdivisionFrequency)
    {
    }

    /// <summary>供測試使用：搭配較小細分頻率生成的星球驗證正確性。</summary>
    public TileNeighborResolver(int subdivisionFrequency)
    {
        _n = subdivisionFrequency;
    }

    /// <inheritdoc />
    public IReadOnlyList<(int FaceIndex, int Row, int Col)> GetNeighborCoordinates(Tile tile)
    {
        var (ia, ib, ic) = IcosahedronGeometry.Faces[tile.FaceIndex];
        var weights = new Dictionary<int, int>
        {
            [ia] = _n - tile.Row - tile.Col,
            [ib] = tile.Row,
            [ic] = tile.Col,
        };

        var candidates = tile.Shape == TileShape.Pentagon
            ? GetPentagonNeighborCandidates(weights)
            : GetHexagonNeighborCandidates(weights, tile.FaceIndex);

        return candidates.Select(Canonicalize).Distinct().ToList();
    }

    /// <summary>五角形固定5個鄰居：對應二十面體上與此頂點直接相鄰的另外5個頂點。</summary>
    private static IEnumerable<Dictionary<int, int>> GetPentagonNeighborCandidates(Dictionary<int, int> weights)
    {
        var n = weights.Values.Sum();
        var vertex = weights.First(kv => kv.Value == n).Key;

        foreach (var neighborVertex in IcosahedronGeometry.VertexAdjacency[vertex])
        {
            yield return new Dictionary<int, int> { [vertex] = n - 1, [neighborVertex] = 1 };
        }
    }

    /// <summary>
    /// 六角形恰6個鄰居：面內部的（用同一面內±1的重心座標移動即可）＋邊界上的（需跨到相鄰面，
    /// 見class註解開頭推導出的公式：跨過「權重為0那個頂點所對的邊」時，新面上第三個頂點權重=1，
    /// 另外兩個共用頂點裡挑一個權重-1、另一個不變，兩種組合各為一個候選鄰居）。
    /// </summary>
    private static IEnumerable<Dictionary<int, int>> GetHexagonNeighborCandidates(Dictionary<int, int> weights, int faceIndex)
    {
        var vertices = weights.Keys.ToArray();

        // 同一面內：3個頂點兩兩一組、各自+1/-1轉移權重，共6種嘗試，其中在邊界上的Tile會有部分無效（該方向需跨面）。
        for (var a = 0; a < vertices.Length; a++)
        {
            for (var b = 0; b < vertices.Length; b++)
            {
                if (a == b) continue;
                var vA = vertices[a];
                var vB = vertices[b];
                if (weights[vB] - 1 < 0) continue;

                var candidate = new Dictionary<int, int>(weights)
                {
                    [vA] = weights[vA] + 1,
                    [vB] = weights[vB] - 1,
                };
                yield return candidate;
            }
        }

        // 跨面：對每個目前權重為0的頂點（該點所在的邊），找出邊的另一側面，該面的第三個頂點權重設為1。
        foreach (var zeroVertex in weights.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList())
        {
            var others = vertices.Where(v => v != zeroVertex).ToArray();
            var edgeKey = others[0] < others[1] ? (others[0], others[1]) : (others[1], others[0]);
            var (f1, f2) = IcosahedronGeometry.EdgeFaces[edgeKey];
            var otherFace = f1 == faceIndex ? f2 : f1;

            var (oa, ob, oc) = IcosahedronGeometry.Faces[otherFace];
            var thirdVertex = new[] { oa, ob, oc }.Single(v => v != others[0] && v != others[1]);

            if (weights[others[0]] - 1 >= 0)
            {
                yield return new Dictionary<int, int>
                {
                    [others[0]] = weights[others[0]] - 1,
                    [others[1]] = weights[others[1]],
                    [thirdVertex] = 1,
                };
            }

            if (weights[others[1]] - 1 >= 0)
            {
                yield return new Dictionary<int, int>
                {
                    [others[0]] = weights[others[0]],
                    [others[1]] = weights[others[1]] - 1,
                    [thirdVertex] = 1,
                };
            }
        }
    }

    /// <summary>
    /// 把某個面上的格點座標換成正規的表示（面索引最小的那個面）。給近看細分的小六角用（<see cref="SubTileGrid"/>）：
    /// 它在各個面上列舉格點，要跟資料庫、鄰居推導用同一套表示，才查得到。
    /// </summary>
    /// <param name="faceIndex">面。</param>
    /// <param name="row">列（面的第二個頂點的權重）。</param>
    /// <param name="col">欄（面的第三個頂點的權重）。</param>
    public (int FaceIndex, int Row, int Col) Canonicalize(int faceIndex, int row, int col)
    {
        var (ia, ib, ic) = IcosahedronGeometry.Faces[faceIndex];
        return Canonicalize(new Dictionary<int, int> { [ia] = _n - row - col, [ib] = row, [ic] = col });
    }

    /// <summary>
    /// 給定一個點在「哪些physical頂點上有哪些權重」，找出能表達這個點的所有面中，面索引最小的那個
    /// （面內部的點只有唯一一個面能表達，沒有歧義；邊界點恰有2個候選面；角點最多5個候選面）。
    /// </summary>
    private (int FaceIndex, int Row, int Col) Canonicalize(Dictionary<int, int> weights)
    {
        var nonzero = weights.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(v => v).ToArray();

        int faceIndex = nonzero.Length switch
        {
            1 => IcosahedronGeometry.VertexFaces[nonzero[0]].Min(),
            2 => Min(IcosahedronGeometry.EdgeFaces[(nonzero[0], nonzero[1])]),
            3 => IcosahedronGeometry.FaceByVertexSet[(nonzero[0], nonzero[1], nonzero[2])],
            _ => throw new InvalidOperationException($"格點權重數量異常：{nonzero.Length}（應為1~3）"),
        };

        var (a, b, c) = IcosahedronGeometry.Faces[faceIndex];
        int WeightOf(int vertex) => weights.GetValueOrDefault(vertex, 0);

        return (faceIndex, WeightOf(b), WeightOf(c));
    }

    /// <summary>回傳一對面索引中較小的那個。</summary>
    private static int Min((int, int) pair) => Math.Min(pair.Item1, pair.Item2);
}
