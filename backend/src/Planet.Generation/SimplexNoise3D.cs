namespace Planet.Generation;

/// <summary>
/// 可種子化的3D Simplex noise（標準演算法，來源：Stefan Gustavson "Simplex noise demystified"）。
/// 用單位球面上的(x,y,z)方向向量取樣，天然無縫（不會像2D noise套在經緯度上那樣在極區/換日線出現接縫）。
/// 輸出範圍約為[-1,1]。
/// </summary>
internal class SimplexNoise3D
{
    private readonly int[] _perm = new int[512];

    private static readonly int[][] Grad3 =
    [
        [1, 1, 0], [-1, 1, 0], [1, -1, 0], [-1, -1, 0],
        [1, 0, 1], [-1, 0, 1], [1, 0, -1], [-1, 0, -1],
        [0, 1, 1], [0, -1, 1], [0, 1, -1], [0, -1, -1],
    ];

    /// <summary>用指定種子建立一組排列表（permutation table），同種子在同一版本演算法下永遠產生同樣的noise場。</summary>
    /// <param name="seed">亂數種子，決定排列表內容。</param>
    public SimplexNoise3D(int seed)
    {
        var p = new int[256];
        for (var i = 0; i < 256; i++) p[i] = i;

        var rng = new Random(seed);
        for (var i = 255; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        for (var i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    private const double F3 = 1.0 / 3.0;
    private const double G3 = 1.0 / 6.0;

    /// <summary>取樣3D空間中某一點的noise值。</summary>
    /// <param name="xin">X座標。</param>
    /// <param name="yin">Y座標。</param>
    /// <param name="zin">Z座標。</param>
    /// <returns>約落在[-1,1]範圍的noise值。</returns>
    public double Sample(double xin, double yin, double zin)
    {
        var s = (xin + yin + zin) * F3;
        var i = FastFloor(xin + s);
        var j = FastFloor(yin + s);
        var k = FastFloor(zin + s);

        var t = (i + j + k) * G3;
        var x0 = xin - (i - t);
        var y0 = yin - (j - t);
        var z0 = zin - (k - t);

        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }

        var x1 = x0 - i1 + G3;
        var y1 = y0 - j1 + G3;
        var z1 = z0 - k1 + G3;
        var x2 = x0 - i2 + 2.0 * G3;
        var y2 = y0 - j2 + 2.0 * G3;
        var z2 = z0 - k2 + 2.0 * G3;
        var x3 = x0 - 1.0 + 3.0 * G3;
        var y3 = y0 - 1.0 + 3.0 * G3;
        var z3 = z0 - 1.0 + 3.0 * G3;

        var ii = i & 255;
        var jj = j & 255;
        var kk = k & 255;

        var gi0 = _perm[ii + _perm[jj + _perm[kk]]] % 12;
        var gi1 = _perm[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]] % 12;
        var gi2 = _perm[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]] % 12;
        var gi3 = _perm[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]] % 12;

        var n0 = Contribution(gi0, x0, y0, z0);
        var n1 = Contribution(gi1, x1, y1, z1);
        var n2 = Contribution(gi2, x2, y2, z2);
        var n3 = Contribution(gi3, x3, y3, z3);

        return 32.0 * (n0 + n1 + n2 + n3);
    }

    /// <summary>
    /// Fractal Brownian Motion：疊加多個頻率的noise，得到較自然的地形起伏。回傳值約落在[-1,1]，
    /// 但實際分布接近常態分布、集中在中間值附近（不是均勻分布），門檻校準時要注意這點
    /// （見 docs/architecture.md「踩過的坑（低地地形門檻）」）。
    /// </summary>
    /// <param name="x">X座標。</param>
    /// <param name="y">Y座標。</param>
    /// <param name="z">Z座標。</param>
    /// <param name="octaves">疊加的頻率層數，越多細節越豐富但運算量越高。</param>
    /// <param name="baseFrequency">最低（第一層）頻率。</param>
    /// <param name="persistence">每多一層octave，振幅乘上的衰減係數（預設0.5）。</param>
    /// <param name="lacunarity">每多一層octave，頻率乘上的增益係數（預設2.0）。</param>
    public double Fbm(double x, double y, double z, int octaves, double baseFrequency, double persistence = 0.5, double lacunarity = 2.0)
    {
        double sum = 0;
        double amplitude = 1;
        double frequency = baseFrequency;
        double amplitudeSum = 0;

        for (var o = 0; o < octaves; o++)
        {
            sum += Sample(x * frequency, y * frequency, z * frequency) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return sum / amplitudeSum;
    }

    /// <summary>算出單一單純形（simplex）角落對取樣點的noise貢獻量，距離角落越遠貢獻越趨近0。</summary>
    private static double Contribution(int gradIndex, double x, double y, double z)
    {
        var t = 0.6 - x * x - y * y - z * z;
        if (t < 0) return 0.0;

        var g = Grad3[gradIndex];
        t *= t;
        return t * t * (g[0] * x + g[1] * y + g[2] * z);
    }

    /// <summary>無條件捨去取整數（比 <see cref="Math.Floor(double)"/> 快，因為不需要處理浮點數的所有邊界情況）。</summary>
    private static int FastFloor(double v) => v > 0 ? (int)v : (int)v - 1;
}
