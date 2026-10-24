using System.Linq;
using Xunit;

namespace Planet.Simulation.Tests;

/// <summary>
/// 岩性（水文地貌規劃 H1）：蓋層的厚度與種類掛在板塊座標系的地殼點上，侵蝕先刨蓋層、刨穿才露出基盤。
/// </summary>
public class LithologyTests
{
    [Fact]
    public void Exposed_ThickSedimentWins_ThenCover_ThenBasement()
    {
        Assert.Equal(ExposedRock.LooseSediment, Lithology.Exposed(CrustType.Continental, 500f, CoverRock.Volcanic, 50f));
        Assert.Equal(ExposedRock.Volcanic, Lithology.Exposed(CrustType.Continental, 500f, CoverRock.Volcanic, 5f));
        Assert.Equal(ExposedRock.Crystalline, Lithology.Exposed(CrustType.Continental, 0f, CoverRock.Sedimentary, 0f));
        Assert.Equal(ExposedRock.OceanicBasalt, Lithology.Exposed(CrustType.Oceanic, 0f, CoverRock.Sedimentary, 0f));
    }

    [Fact]
    public void HardRockErodesSlowerAndHoldsLessGroundwater()
    {
        Assert.True(Lithology.Erodibility(ExposedRock.Crystalline) < Lithology.Erodibility(ExposedRock.Sedimentary));
        Assert.True(Lithology.Erodibility(ExposedRock.Sedimentary) < Lithology.Erodibility(ExposedRock.LooseSediment));
        Assert.True(Lithology.Permeability(ExposedRock.Crystalline) < Lithology.Permeability(ExposedRock.Sedimentary));
    }

    [Fact]
    public void StripCover_ErodesThroughToTheBasement()
    {
        var state = new SimulationState(1);
        state.Crust[0] = CrustType.Continental;
        state.CoverThickness[0] = 100f;
        state.CoverRock[0] = CoverRock.Sedimentary;

        Lithology.StripCover(state, 0, 60);
        Assert.Equal(40f, state.CoverThickness[0], 3);
        Assert.Equal(ExposedRock.Sedimentary, Lithology.ExposedAt(state, 0));

        Lithology.StripCover(state, 0, 60);
        Assert.Equal(0f, state.CoverThickness[0]);
        Assert.Equal(ExposedRock.Crystalline, Lithology.ExposedAt(state, 0));
    }

    /// <summary>
    /// 起始大陸：海岸有沉積蓋層、深內陸大部分露出結晶基盤，海洋沒有蓋層。
    /// 三種都要有——全部一樣的話岩性對侵蝕不會有任何差別。
    /// </summary>
    [Fact]
    public void ContinentLayout_StartsWithCoastalCoverAndExposedBasement()
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);
        var state = engine.State;

        var continental = Enumerable.Range(0, state.CellCount).Where(c => state.Crust[c] == CrustType.Continental).ToList();
        Assert.NotEmpty(continental);
        Assert.Contains(continental, c => state.CoverThickness[c] > 1_000f);
        Assert.Contains(continental, c => state.CoverThickness[c] == 0f);
        Assert.All(Enumerable.Range(0, state.CellCount).Where(c => state.Crust[c] == CrustType.Oceanic),
            c => Assert.Equal(0f, state.CoverThickness[c]));
    }

    /// <summary>
    /// 跑一段之後：火山弧蓋上了火山岩、隱沒帶刮出了混雜岩，起始的沉積蓋層還在。
    /// 任何一條路徑沒接上，這裡就會少一種岩石（CLAUDE.md 3.5：參數改了結果不動＝路沒接上）。
    /// </summary>
    [Fact]
    public void Evolution_AddsVolcanicAndMelangeCover()
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);
        var state = engine.State;

        engine.Step(200);

        var rocks = Enumerable.Range(0, state.CellCount).Where(c => state.CoverThickness[c] > 0f).Select(c => state.CoverRock[c]).ToHashSet();
        Assert.Contains(CoverRock.Volcanic, rocks);
        Assert.Contains(CoverRock.Melange, rocks);
        Assert.Contains(CoverRock.Sedimentary, rocks);
    }

    /// <summary>
    /// 流水刨到基岩的格子，蓋層一定變薄。直接呼叫一次流水侵蝕來比（整步的話板塊運動會把欄位搬走、比不出來）。
    /// </summary>
    [Fact]
    public void FluvialErosion_StripsCoverWhereItCutsBedrock()
    {
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 24);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame, continentLayout: ContinentLayoutParameters.Default);
        var state = engine.State;
        engine.Step(20);

        var coverBefore = (float[])state.CoverThickness.Clone();
        var crustBefore = (float[])state.CrustThickness.Clone();
        engine.FluvialErosion!.Step(state, engine.Hydrology!, engine.YearsPerStep);

        var cut = Enumerable.Range(0, state.CellCount)
            .Where(c => state.CrustThickness[c] < crustBefore[c] && coverBefore[c] > 0f).ToList();
        Assert.NotEmpty(cut);
        Assert.All(cut, c => Assert.True(state.CoverThickness[c] < coverBefore[c], $"格 {c} 的基岩被刨了，蓋層卻沒變薄"));
    }
}
