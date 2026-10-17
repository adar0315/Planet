namespace Planet.Simulation.Representation;

/// <summary>
/// 讓 <see cref="RepresentationBenchmark"/> 能評比 <see cref="PlateFrameCrust"/> 的轉接層。
/// 演算法只有一份（在 <see cref="PlateFrameCrust"/>），這裡只負責把評比用的「厚度＋是不是大陸」
/// 跟完整的地殼欄位互相轉換。
/// </summary>
internal sealed class PlateFrameCarrier : ICrustCarrier
{
    private PlateFrameCrust? _crust;
    private SimulationState? _state;

    /// <inheritdoc />
    public string Name => "B：板塊座標系";

    /// <inheritdoc />
    public void Initialize(SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental)
    {
        _state = new SimulationState(grid.CellCount);
        Array.Copy(thicknessKm, _state.CrustThickness, thicknessKm.Length);
        for (var c = 0; c < continental.Length; c++)
        {
            _state.Crust[c] = continental[c] ? CrustType.Continental : CrustType.Oceanic;
        }
        _crust = new PlateFrameCrust(grid.CellCount, plates.Plates.Length);
        _crust.Initialize(_state, plates);
    }

    /// <inheritdoc />
    public void Step(SimulationGrid grid, PlateSet plates, double deltaMyr) =>
        _crust!.Step(grid, _state!, plates, deltaMyr);

    /// <inheritdoc />
    public void WriteBack(SimulationGrid grid, float[] thicknessKm)
    {
        Array.Copy(thicknessKm, _state!.CrustThickness, thicknessKm.Length);
        _crust!.WriteBack(grid, _state);
    }

    /// <inheritdoc />
    public void Read(float[] thicknessKm, bool[] continental)
    {
        _crust!.Read(_state!);
        Array.Copy(_state!.CrustThickness, thicknessKm, thicknessKm.Length);
        for (var c = 0; c < continental.Length; c++) continental[c] = _state.Crust[c] == CrustType.Continental;
    }
}
