namespace Planet.Generation;

/// <summary>
/// 正二十面體的12個頂點與20個三角面，並將其中一個頂點旋轉對齊到北極（其對蹠頂點自動對齊南極）。
/// 頂點/面資料為業界常用的標準座標組合（來源：Andreas Kahler 的 icosphere 生成法）。
/// </summary>
internal static class IcosahedronGeometry
{
    /// <summary>對齊北極後的12個單位球面頂點。</summary>
    public static readonly Vec3[] Vertices = BuildPoleAlignedVertices();

    /// <summary>20個三角面，每個面是3個頂點索引（逆時針或順時針皆可，細分演算法不依賴繞向）。</summary>
    public static readonly (int A, int B, int C)[] Faces =
    [
        (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
        (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
        (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
        (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
    ];

    /// <summary>被選為北極的原始頂點索引（旋轉前）。其對蹠頂點（索引3）旋轉後會落在南極。</summary>
    public const int NorthPoleVertexIndex = 0;

    /// <summary>每條邊（依兩端頂點索引排序為鍵）對應的兩個面索引。供跨面鄰居計算查詢「這條邊的另一側是哪個面」。</summary>
    public static readonly IReadOnlyDictionary<(int Low, int High), (int Face1, int Face2)> EdgeFaces = BuildEdgeFaces();

    /// <summary>每個頂點對應的所有面索引（每個頂點恰被5個面共用）。</summary>
    public static readonly IReadOnlyDictionary<int, int[]> VertexFaces = BuildVertexFaces();

    /// <summary>每個頂點直接相鄰的其他頂點（二十面體的邊，每個頂點恰有5個相鄰頂點）。</summary>
    public static readonly IReadOnlyDictionary<int, int[]> VertexAdjacency = BuildVertexAdjacency();

    /// <summary>依3個頂點索引（排序後）反查是哪一個面（每個面的頂點三元組在20個面中互不相同）。</summary>
    public static readonly IReadOnlyDictionary<(int, int, int), int> FaceByVertexSet = BuildFaceByVertexSet();

    /// <summary>建構 <see cref="FaceByVertexSet"/>：把每個面的3個頂點索引排序後當鍵，方便之後反查面編號。</summary>
    private static Dictionary<(int, int, int), int> BuildFaceByVertexSet()
    {
        var result = new Dictionary<(int, int, int), int>();
        for (var f = 0; f < Faces.Length; f++)
        {
            var (a, b, c) = Faces[f];
            var sorted = new[] { a, b, c };
            Array.Sort(sorted);
            result[(sorted[0], sorted[1], sorted[2])] = f;
        }
        return result;
    }

    /// <summary>建構 <see cref="EdgeFaces"/>：掃描20個面的所有邊，把「共用同一條邊的兩個面」建立索引。</summary>
    private static Dictionary<(int, int), (int, int)> BuildEdgeFaces()
    {
        var result = new Dictionary<(int, int), List<int>>();

        void AddEdge(int u, int v, int faceIndex)
        {
            var key = u < v ? (u, v) : (v, u);
            if (!result.TryGetValue(key, out var faces))
            {
                faces = [];
                result[key] = faces;
            }
            faces.Add(faceIndex);
        }

        for (var f = 0; f < Faces.Length; f++)
        {
            var (a, b, c) = Faces[f];
            AddEdge(a, b, f);
            AddEdge(b, c, f);
            AddEdge(a, c, f);
        }

        return result.ToDictionary(kv => kv.Key, kv => (kv.Value[0], kv.Value[1]));
    }

    /// <summary>建構 <see cref="VertexFaces"/>：反查每個頂點被哪些面共用。</summary>
    private static Dictionary<int, int[]> BuildVertexFaces()
    {
        var result = new Dictionary<int, List<int>>();

        for (var f = 0; f < Faces.Length; f++)
        {
            var (a, b, c) = Faces[f];
            foreach (var v in new[] { a, b, c })
            {
                if (!result.TryGetValue(v, out var faces))
                {
                    faces = [];
                    result[v] = faces;
                }
                faces.Add(f);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>建構 <see cref="VertexAdjacency"/>：掃描每個面的3條邊，收集每個頂點直接相鄰的其他頂點。</summary>
    private static Dictionary<int, int[]> BuildVertexAdjacency()
    {
        var result = new Dictionary<int, HashSet<int>>();

        void AddAdjacency(int u, int v)
        {
            if (!result.TryGetValue(u, out var set))
            {
                set = [];
                result[u] = set;
            }
            set.Add(v);
        }

        foreach (var (a, b, c) in Faces)
        {
            AddAdjacency(a, b); AddAdjacency(b, a);
            AddAdjacency(b, c); AddAdjacency(c, b);
            AddAdjacency(a, c); AddAdjacency(c, a);
        }

        return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>算出正二十面體標準座標的12個頂點，正規化到單位球面後，旋轉對齊使 <see cref="NorthPoleVertexIndex"/> 落在北極。</summary>
    private static Vec3[] BuildPoleAlignedVertices()
    {
        var t = (1.0 + Math.Sqrt(5.0)) / 2.0;

        Vec3[] raw =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        ];

        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = raw[i].Normalized();
        }

        var (basisX, basisY, basisZ) = BuildPoleAlignedBasis(raw[NorthPoleVertexIndex]);

        var aligned = new Vec3[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var v = raw[i];
            aligned[i] = new Vec3(v.Dot(basisX), v.Dot(basisY), v.Dot(basisZ));
        }

        return aligned;
    }

    /// <summary>
    /// 建立一組正交基底，使 pole 方向對應到新座標系的 +Z（北極）。
    /// 任一原始向量 P 轉換為 (P·X, P·Y, P·Z) 後，pole 本身會變成 (0,0,1)。
    /// </summary>
    private static (Vec3 X, Vec3 Y, Vec3 Z) BuildPoleAlignedBasis(Vec3 pole)
    {
        var z = pole.Normalized();
        var reference = Math.Abs(z.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        var x = reference.Cross(z).Normalized();
        var y = z.Cross(x);
        return (x, y, z);
    }
}
