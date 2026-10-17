namespace Planet.Simulation.Representation;

/// <summary>
/// 純運動學的邊界分類：只看兩側板塊在這一點的速度是靠近還是分開。
/// 比較表示法時不跑 v1 的 <c>ClassifyBoundaries</c>（它跟地殼厚度、線段追蹤綁在一起），
/// 只需要知道「這裡的空缺是真的張裂、還是取樣的錯配」。
/// </summary>
internal static class PlateBoundaryKinds
{
    /// <summary>非邊界。</summary>
    internal const byte None = 0;

    /// <summary>兩側在靠近。</summary>
    internal const byte Convergent = 1;

    /// <summary>兩側在分開——這裡的空缺要生新的海洋地殼。</summary>
    internal const byte Divergent = 2;

    /// <summary>依目前的歸屬與速度把每格分類，寫進 <paramref name="kinds"/>。</summary>
    internal static void Classify(SimulationGrid grid, PlateSet plates, byte[] kinds)
    {
        var owner = plates.PlateIdByCell;
        var u = grid.UnitVector;
        Parallel.For(0, grid.CellCount, c =>
        {
            var p = owner[c];
            double x = u[c * 3], y = u[c * 3 + 1], z = u[c * 3 + 2];
            var kind = None;
            var strongest = 0.0;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n = grid.Neighbors[baseIndex + s];
                if (n == SimulationGrid.NoNeighbor || owner[n] == p) continue;

                // 相對速度（對方相對自己）在「往對方那一格」方向的分量：正＝對方在遠離
                var (vpx, vpy, vpz) = Velocity(plates.Plates[p], x, y, z);
                var (vqx, vqy, vqz) = Velocity(plates.Plates[owner[n]], x, y, z);
                double dx = u[n * 3] - x, dy = u[n * 3 + 1] - y, dz = u[n * 3 + 2] - z;
                var separation = (vqx - vpx) * dx + (vqy - vpy) * dy + (vqz - vpz) * dz;
                if (Math.Abs(separation) <= strongest) continue;
                strongest = Math.Abs(separation);
                kind = separation > 0 ? Divergent : Convergent;
            }
            kinds[c] = kind;
        });
    }

    /// <summary>板塊在單位球面上某點的速度方向（ω × r，只用來比方向，單位不重要）。</summary>
    private static (double X, double Y, double Z) Velocity(Plate plate, double x, double y, double z)
    {
        var w = plate.AngularVelocityDegPerMyr;
        double ox = plate.PoleX * w, oy = plate.PoleY * w, oz = plate.PoleZ * w;
        return (oy * z - oz * y, oz * x - ox * z, ox * y - oy * x);
    }
}
