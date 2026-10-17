namespace Planet.Simulation.Representation;

/// <summary>表示法候選共用的球面幾何：旋轉、局部最近格搜尋、重心內插。</summary>
internal static class SphereMath
{
    /// <summary>
    /// 繞單位軸旋轉 <paramref name="angleRad"/> 的四元數 (w, x, y, z)。
    /// 用四元數而不是累加角度：之後板塊的歐拉極會隨動力學改變，累加角度只在極點固定時成立。
    /// </summary>
    internal static (double W, double X, double Y, double Z) AxisAngle(double ax, double ay, double az, double angleRad)
    {
        var half = angleRad / 2;
        var s = Math.Sin(half);
        return (Math.Cos(half), ax * s, ay * s, az * s);
    }

    /// <summary>四元數乘法 a·b（先套用 b 再套用 a）。</summary>
    internal static (double W, double X, double Y, double Z) Multiply(
        (double W, double X, double Y, double Z) a, (double W, double X, double Y, double Z) b) =>
    (
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W
    );

    /// <summary>正規化（每步都做，幾萬步的浮點誤差才不會讓旋轉慢慢不再是旋轉）。</summary>
    internal static (double W, double X, double Y, double Z) Normalize((double W, double X, double Y, double Z) q)
    {
        var length = Math.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        return (q.W / length, q.X / length, q.Y / length, q.Z / length);
    }

    /// <summary>用四元數旋轉向量；<paramref name="inverse"/> 為 true 時做反向旋轉。</summary>
    internal static (double X, double Y, double Z) Rotate(
        (double W, double X, double Y, double Z) q, double vx, double vy, double vz, bool inverse = false)
    {
        var (w, x, y, z) = q;
        if (inverse) { x = -x; y = -y; z = -z; }

        // v' = v + 2w(u×v) + 2u×(u×v)
        var cx = y * vz - z * vy;
        var cy = z * vx - x * vz;
        var cz = x * vy - y * vx;
        var ccx = y * cz - z * cy;
        var ccy = z * cx - x * cz;
        var ccz = x * cy - y * cx;
        return (vx + 2 * (w * cx + ccx), vy + 2 * (w * cy + ccy), vz + 2 * (w * cz + ccz));
    }

    /// <summary>
    /// 從 <paramref name="start"/> 開始往更近的鄰居爬，直到沒有更近的為止。
    /// 球面上近乎均勻的網格是 Delaunay 的，爬山一定會停在真正最近的格子；
    /// 每步位移遠小於一格，所以通常 0~1 次就到，比全域的桶搜尋便宜很多。
    /// </summary>
    internal static int ClimbToNearest(SimulationGrid grid, int start, double x, double y, double z)
    {
        var u = grid.UnitVector;
        var best = start;
        var bestDot = x * u[best * 3] + y * u[best * 3 + 1] + z * u[best * 3 + 2];
        while (true)
        {
            var improved = false;
            var baseIndex = best * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor) continue;
                var dot = x * u[n * 3] + y * u[n * 3 + 1] + z * u[n * 3 + 2];
                if (dot <= bestDot) continue;
                bestDot = dot;
                best = n;
                improved = true;
            }
            if (!improved) return best;
        }
    }

    /// <summary>
    /// 在「最近格＋離這點最近的兩個鄰居」組成的三角形上算重心座標（負值歸零後正規化）。
    /// 跟 <see cref="SimulatedTerrainSampler"/> 同一套做法。
    /// </summary>
    internal static void Barycentric(
        SimulationGrid grid, int nearest, double x, double y, double z,
        out int c0, out int c1, out int c2, out double w0, out double w1, out double w2)
    {
        var u = grid.UnitVector;
        c0 = nearest; c1 = -1; c2 = -1;
        double d1 = double.NegativeInfinity, d2 = double.NegativeInfinity;
        var baseIndex = nearest * SimulationGrid.MaxNeighborsPerCell;
        for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
        {
            var n = grid.Neighbors[baseIndex + s];
            if (n == SimulationGrid.NoNeighbor) continue;
            var dot = x * u[n * 3] + y * u[n * 3 + 1] + z * u[n * 3 + 2];
            if (dot > d1) { c2 = c1; d2 = d1; c1 = n; d1 = dot; }
            else if (dot > d2) { c2 = n; d2 = dot; }
        }

        w0 = 1; w1 = 0; w2 = 0;
        if (c2 < 0) return;

        double ax = u[c0 * 3], ay = u[c0 * 3 + 1], az = u[c0 * 3 + 2];
        double bx = u[c1 * 3], by = u[c1 * 3 + 1], bz = u[c1 * 3 + 2];
        double qx = u[c2 * 3], qy = u[c2 * 3 + 1], qz = u[c2 * 3 + 2];
        var det = Det(ax, bx, qx, ay, by, qy, az, bz, qz);
        if (Math.Abs(det) < 1e-15) return;

        var a = Math.Max(0, Det(x, bx, qx, y, by, qy, z, bz, qz) / det);
        var b = Math.Max(0, Det(ax, x, qx, ay, y, qy, az, z, qz) / det);
        var c = Math.Max(0, Det(ax, bx, x, ay, by, y, az, bz, z) / det);
        var sum = a + b + c;
        if (sum <= 0) return;
        w0 = a / sum; w1 = b / sum; w2 = c / sum;
    }

    /// <summary>板塊這一步的旋轉（四元數）。</summary>
    internal static (double W, double X, double Y, double Z) StepRotation(Plate plate, double deltaMyr) =>
        AxisAngle(plate.PoleX, plate.PoleY, plate.PoleZ, plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr);

    private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
