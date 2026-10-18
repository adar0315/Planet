using System.Reflection;

namespace Planet.Simulation;

/// <summary>
/// 使用者在第 0 步板塊圖上直接改過的海洋板塊範圍（2026-10-18，<c>Templates/plate-override.png</c>）：
/// 1024×512 的等距圓柱投影（左＝經度 −180°、上＝北極），每塊板塊一種顏色（顏色照板塊圖探針的配色，見 <see cref="Palette"/>）。
/// <see cref="ContinentLayout.ApplyTo"/> 先照規則（最近的大陸、洋脊、兩極、太平洋南）分好海，再用這張圖覆寫**海洋格**的板塊；陸地不受影響。
///
/// 使用者的修改：太平洋往北、往南延伸進兩極的範圍；太平洋南變成 C、E 南岸以南的一大塊。
/// 第二版另外重畫了 E 的裂開方式：C 的陸地用白線切開，切線東南邊是 E、切線上圍起來的是小島（見 <see cref="CPartAt"/>）。
/// 這張圖是對著目前的配置（洋脊兩側拉近、空出的海歸太平洋，見 <see cref="ContinentLayout.PullTowardRidge"/>）畫的，
/// 配置的位置改了就要重畫。
/// </summary>
internal sealed class ContinentPlateMap
{
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _plate;
    private readonly int[] _cPart;

    private ContinentPlateMap(int width, int height, int[] plate, int[] cPart)
    {
        _width = width;
        _height = height;
        _plate = plate;
        _cPart = cPart;
    }

    /// <summary><see cref="CPartAt"/> 的值：C 本體。</summary>
    public const int PartC = 0;

    /// <summary><see cref="CPartAt"/> 的值：E（切線東南邊那一大塊）。</summary>
    public const int PartE = 1;

    /// <summary><see cref="CPartAt"/> 的值：切線上用白線圍起來的小島。</summary>
    public const int PartIsland = 2;

    /// <summary><see cref="CPartAt"/> 的值：切線本身（裂開時沉成海床）。</summary>
    public const int PartCut = 3;

    /// <summary>C 的陸地顏色。</summary>
    private static readonly (int R, int G, int B) CLandColor = (140, 0, 122);

    /// <summary>C 的陸地裡被白線圍起來的色塊，至少這麼大（像素）才算小島，更小的是線交叉處的縫。</summary>
    private const int MinIslandPixels = 10;

    /// <summary>判斷切線時，沿一個方向最多走幾個像素去找線兩邊的顏色（白線約 3~5 個像素寬）。</summary>
    private const int CutReachPixels = 6;

    /// <summary>
    /// 板塊圖探針的配色：海洋是淺色、陸地是深色，兩者都對應到同一塊板塊。白色是邊界線、其餘是反鋸齒的過渡色，都用周圍的顏色補。
    /// </summary>
    private static readonly (int Plate, int R, int G, int B)[] Palette =
    [
        (ContinentLayout.PacificPlate, 143, 207, 196),
        (ContinentLayout.SouthPacificPlate, 193, 143, 207),
        (ContinentLayout.NorthPolarPlate, 143, 183, 207),
        (ContinentLayout.SouthPolarPlate, 201, 207, 143),
        (0, 207, 143, 143), (0, 140, 0, 0),       // A 北
        (1, 143, 161, 207), (1, 0, 40, 140),      // A 南
        (2, 180, 207, 143), (2, 81, 140, 0),      // B
        (3, 207, 143, 199), (3, 140, 0, 122),     // C
    ];

    /// <summary>顏色離調色盤多遠以內算那一塊板塊（RGB 距離的平方）。</summary>
    private const int MaxColorDistanceSquared = 12 * 12;

    /// <summary>讀內嵌的板塊圖。</summary>
    public static ContinentPlateMap LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plate-override.png")
            ?? throw new InvalidOperationException("找不到內嵌的板塊圖 plate-override.png。");
        var (width, height, rgb) = ContinentSketch.DecodePng(stream);
        var plate = new int[width * height];
        var queue = new Queue<int>();
        for (var i = 0; i < plate.Length; i++)
        {
            plate[i] = -1;
            int r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            foreach (var (p, pr, pg, pb) in Palette)
            {
                var d = (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb);
                if (d > MaxColorDistanceSquared) continue;
                plate[i] = p;
                queue.Enqueue(i);
                break;
            }
        }
        var cPart = PartitionCLand(width, height, rgb, plate);
        Fill(width, height, plate, queue);

        // 太小的色塊是沒塗乾淨的殘渣（使用者把 C 的海塗成太平洋南時留下幾點原本的粉紅），當成沒認出來、用周圍的顏色重補
        var component = new int[plate.Length];
        Array.Fill(component, -1);
        var members = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < plate.Length; start++)
        {
            if (component[start] >= 0) continue;
            members.Clear();
            component[start] = start;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                members.Add(i);
                foreach (var n in Neighbors(width, height, i))
                {
                    if (component[n] >= 0 || plate[n] != plate[start]) continue;
                    component[n] = start;
                    stack.Push(n);
                }
            }
            if (members.Count < MinRegionPixels)
                foreach (var i in members) plate[i] = -1;
        }
        for (var i = 0; i < plate.Length; i++)
        {
            if (plate[i] < 0) continue;
            foreach (var n in Neighbors(width, height, i))
            {
                if (plate[n] >= 0) continue;
                queue.Enqueue(i);
                break;
            }
        }
        Fill(width, height, plate, queue);
        return new ContinentPlateMap(width, height, plate, cPart);
    }

    /// <summary>
    /// 把圖上 C 的陸地切開（2026-10-18 使用者在板塊圖上畫的 E 裂開方式）：C 的陸地被白線切成好幾塊，
    /// 最大的是 C、第二大的是 E、其他夠大的是小島；白線本身（不靠海的那些）是切線。其餘的白色與過渡色從旁邊補，
    /// 但不跨過海洋顏色。不是 C 的陸地是 −1。
    /// </summary>
    private static int[] PartitionCLand(int width, int height, byte[] rgb, int[] raw)
    {
        var n = width * height;
        var isCLand = new bool[n];
        for (var i = 0; i < n; i++)
        {
            int dr = rgb[i * 3] - CLandColor.R, dg = rgb[i * 3 + 1] - CLandColor.G, db = rgb[i * 3 + 2] - CLandColor.B;
            isCLand[i] = dr * dr + dg * dg + db * db <= MaxColorDistanceSquared;
        }
        // 海洋顏色要成片才算：白線跟 C 的陸地之間的反鋸齒過渡色有些剛好接近 C 的海（粉紅），散在切線旁邊，
        // 當成海的話切線旁邊會多出一顆顆小洞。太小的海色碎點當成白線處理。
        raw = (int[])raw.Clone();
        var seen = new bool[n];
        var stack0 = new Stack<int>();
        var speck = new List<int>();
        for (var start = 0; start < n; start++)
        {
            if (seen[start] || raw[start] < 0 || isCLand[start]) continue;
            speck.Clear();
            seen[start] = true;
            stack0.Push(start);
            while (stack0.Count > 0)
            {
                var i = stack0.Pop();
                speck.Add(i);
                foreach (var nb in Neighbors(width, height, i))
                {
                    if (seen[nb] || raw[nb] < 0 || isCLand[nb] || raw[nb] != raw[start]) continue;
                    seen[nb] = true;
                    stack0.Push(nb);
                }
            }
            if (speck.Count < MinRegionPixels)
                foreach (var i in speck) raw[i] = -1;
        }
        bool IsOceanColor(int i) => raw[i] >= 0 && !isCLand[i];

        var part = new int[n];
        Array.Fill(part, -1);
        var component = new int[n];
        Array.Fill(component, -1);
        var groups = new List<List<int>>();
        var stack = new Stack<int>();
        for (var start = 0; start < n; start++)
        {
            if (!isCLand[start] || component[start] >= 0) continue;
            var members = new List<int>();
            component[start] = groups.Count;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                members.Add(i);
                foreach (var nb in Neighbors(width, height, i))
                {
                    if (!isCLand[nb] || component[nb] >= 0) continue;
                    component[nb] = groups.Count;
                    stack.Push(nb);
                }
            }
            groups.Add(members);
        }
        var order = Enumerable.Range(0, groups.Count).OrderByDescending(g => groups[g].Count).ToList();
        for (var rank = 0; rank < order.Count; rank++)
        {
            var members = groups[order[rank]];
            var value = rank switch { 0 => PartC, 1 => PartE, _ => members.Count >= MinIslandPixels ? PartIsland : -1 };
            foreach (var i in members) part[i] = value;
        }

        // 切線：白線（不是任何顏色），而且沿某一個方向穿過它、兩邊碰到的都是 C 的陸地。
        // 海岸線（板塊圖本來就畫的邊界）一邊是海，不算。原本用「附近沒有海洋顏色」判斷：
        // 距離取大了，北端通到海邊的細長小島之間的線被當成海岸線、3 條細島併成一塊；取小了，兩個像素寬的海岸線內側又被當成切線。
        (int Dx, int Dy)[] axes = [(1, 0), (0, 1), (1, 1), (1, -1)];
        int FirstColored(int x, int y, int dx, int dy)
        {
            for (var step = 1; step <= CutReachPixels; step++)
            {
                var yy = y + dy * step;
                if (yy < 0 || yy >= height) return -1;
                var j = yy * width + ((x + dx * step) % width + width) % width;
                if (raw[j] >= 0) return j;
            }
            return -1;
        }
        for (var i = 0; i < n; i++)
        {
            if (raw[i] >= 0) continue;
            int x = i % width, y = i / width;
            foreach (var (dx, dy) in axes)
            {
                int a = FirstColored(x, y, dx, dy), b = FirstColored(x, y, -dx, -dy);
                if (a < 0 || b < 0 || !isCLand[a] || !isCLand[b]) continue;
                part[i] = PartCut;
                break;
            }
        }

        // 其他白色、過渡色、線交叉處的小縫：從旁邊補，不跨過海洋顏色
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++) if (part[i] >= 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var nb in Neighbors(width, height, i))
            {
                if (part[nb] >= 0 || IsOceanColor(nb)) continue;
                part[nb] = part[i];
                queue.Enqueue(nb);
            }
        }
        return part;
    }

    /// <summary>某個經緯度在 C 的陸地裡屬於哪一部分（<see cref="PartC"/> 等），不在 C 的陸地上是 −1。</summary>
    /// <param name="latitudeDegrees">緯度（−90 ~ 90）。</param>
    /// <param name="longitudeDegrees">經度（任意，會換算到 −180 ~ 180）。</param>
    public int CPartAt(double latitudeDegrees, double longitudeDegrees) => _cPart[Index(latitudeDegrees, longitudeDegrees)];

    /// <summary>比這個小（像素）的色塊當成沒塗乾淨。圖上最小的真色塊（A 北東北角那一小條海）有好幾百個像素。</summary>
    private const int MinRegionPixels = 200;

    /// <summary>邊界線與過渡色：從認得的像素往外補（經度方向繞一圈）。</summary>
    private static void Fill(int width, int height, int[] plate, Queue<int> queue)
    {
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var n in Neighbors(width, height, i))
            {
                if (plate[n] >= 0) continue;
                plate[n] = plate[i];
                queue.Enqueue(n);
            }
        }
    }

    private static IEnumerable<int> Neighbors(int width, int height, int i)
    {
        int x = i % width, y = i / width;
        yield return y * width + (x + width - 1) % width;
        yield return y * width + (x + 1) % width;
        if (y > 0) yield return i - width;
        if (y < height - 1) yield return i + width;
    }

    /// <summary>某個經緯度在圖上是哪一塊板塊。</summary>
    /// <param name="latitudeDegrees">緯度（−90 ~ 90）。</param>
    /// <param name="longitudeDegrees">經度（任意，會換算到 −180 ~ 180）。</param>
    public int PlateAt(double latitudeDegrees, double longitudeDegrees) => _plate[Index(latitudeDegrees, longitudeDegrees)];

    private int Index(double latitudeDegrees, double longitudeDegrees)
    {
        var lon = ((longitudeDegrees + 180.0) % 360.0 + 360.0) % 360.0;
        var x = Math.Clamp((int)(lon / 360.0 * _width), 0, _width - 1);
        var y = Math.Clamp((int)((90.0 - latitudeDegrees) / 180.0 * _height), 0, _height - 1);
        return y * _width + x;
    }
}
