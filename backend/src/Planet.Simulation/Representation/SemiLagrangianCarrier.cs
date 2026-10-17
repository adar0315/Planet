namespace Planet.Simulation.Representation;

/// <summary>
/// 候選 C：半拉格朗日平流。場直接存在格子上，每一步對每一格「倒推」：
/// 這一格的地殼一步之前在哪裡（沿所屬板塊的旋轉反推），在那一點用重心內插取舊值。
///
/// 一步只走 0.5~5 公里、格距約 15 公里，所以倒推點永遠落在自己這一格附近——
/// 用最近格取樣的話值**永遠不會動**，只能用內插；而內插每一步都會抹開一點邊界（數值擴散），
/// 這就是這個候選要量的風險。
///
/// 大陸用一個 0~1 的「大陸比例」場一起內插，≥ 0.5 算大陸，不另外搬型別（型別取最近格會卡住不動）。
/// 只從**同一個板塊**的格子內插；倒推點周圍沒有同板塊的格子 → 新露出的海床。
/// </summary>
internal sealed class SemiLagrangianCarrier : ICrustCarrier
{
    private float[] _thickness = [], _fraction = [];
    private float[] _nextThickness = [], _nextFraction = [];
    private int[] _owner = [];

    /// <inheritdoc />
    public string Name => "C：半拉格朗日（線性內插）";

    /// <inheritdoc />
    public void Initialize(SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental)
    {
        _thickness = (float[])thicknessKm.Clone();
        _fraction = continental.Select(c => c ? 1f : 0f).ToArray();
        _nextThickness = new float[grid.CellCount];
        _nextFraction = new float[grid.CellCount];
        _owner = (int[])plates.PlateIdByCell.Clone();
    }

    /// <inheritdoc />
    public void Step(SimulationGrid grid, PlateSet plates, double deltaMyr)
    {
        var rotations = plates.Plates.Select(p => SphereMath.StepRotation(p, deltaMyr)).ToArray();
        var owner = plates.PlateIdByCell;
        var u = grid.UnitVector;

        Parallel.For(0, grid.CellCount, c =>
        {
            var p = owner[c];
            var (x, y, z) = SphereMath.Rotate(rotations[p], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
            var nearest = SphereMath.ClimbToNearest(grid, c, x, y, z);
            SphereMath.Barycentric(grid, nearest, x, y, z, out var c0, out var c1, out var c2, out var w0, out var w1, out var w2);

            double weight = 0, thickness = 0, fraction = 0;
            Accumulate(c0, w0); Accumulate(c1, w1); Accumulate(c2, w2);

            if (weight <= 1e-9)
            {
                _nextThickness[c] = Isostasy.OceanicCrustThicknessKm;
                _nextFraction[c] = 0f;
                return;
            }
            _nextThickness[c] = (float)(thickness / weight);
            _nextFraction[c] = (float)(fraction / weight);

            void Accumulate(int cell, double w)
            {
                if (cell < 0 || w <= 0 || _owner[cell] != p) return;
                weight += w;
                thickness += w * _thickness[cell];
                fraction += w * _fraction[cell];
            }
        });

        (_thickness, _nextThickness) = (_nextThickness, _thickness);
        (_fraction, _nextFraction) = (_nextFraction, _fraction);
        Array.Copy(owner, _owner, owner.Length);
    }

    /// <inheritdoc />
    public void WriteBack(SimulationGrid grid, float[] thicknessKm) =>
        Array.Copy(thicknessKm, _thickness, thicknessKm.Length);

    /// <inheritdoc />
    public void Read(float[] thicknessKm, bool[] continental)
    {
        Array.Copy(_thickness, thicknessKm, _thickness.Length);
        for (var c = 0; c < _fraction.Length; c++) continental[c] = _fraction[c] >= 0.5f;
    }
}
