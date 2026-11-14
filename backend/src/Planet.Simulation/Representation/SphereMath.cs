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

    /// <summary>板塊這一步的旋轉（四元數）。</summary>
    internal static (double W, double X, double Y, double Z) StepRotation(Plate plate, double deltaMyr) =>
        AxisAngle(plate.PoleX, plate.PoleY, plate.PoleZ, plate.AngularVelocityDegPerMyr * Math.PI / 180.0 * deltaMyr);

    /// <summary>3×3 矩陣的行列式（依列排列 a~i）。</summary>
    /// <param name="a">第 1 列第 1 欄。</param>
    /// <param name="b">第 1 列第 2 欄。</param>
    /// <param name="c">第 1 列第 3 欄。</param>
    /// <param name="d">第 2 列第 1 欄。</param>
    /// <param name="e">第 2 列第 2 欄。</param>
    /// <param name="f">第 2 列第 3 欄。</param>
    /// <param name="g">第 3 列第 1 欄。</param>
    /// <param name="h">第 3 列第 2 欄。</param>
    /// <param name="i">第 3 列第 3 欄。</param>
    /// <returns>行列式。</returns>
    private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
