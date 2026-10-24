using System.Linq;
using System.Runtime.InteropServices;

namespace Planet.Simulation.Tests;

/// <summary>
/// 模擬狀態的雜湊（FNV-1a，逐位元組）。用來斷言「兩次執行的結果位元相同」——
/// 比逐欄位比對好讀，而且任何一個位元不同都抓得到。
/// </summary>
public static class StateHash
{
    /// <summary>整個狀態（每個欄位依序）＋板塊配置的雜湊。</summary>
    public static ulong Of(SimulationEngine engine)
    {
        var s = engine.State;
        var h = Offset;
        h = Mix(h, s.BedrockElevation);
        h = Mix(h, s.SedimentThickness);
        h = Mix(h, s.OrganicThickness);
        h = Mix(h, s.VegetationThickness);
        h = Mix(h, s.IceThickness);
        h = Mix(h, s.WaterDepth);
        h = Mix(h, s.Moisture);
        h = Mix(h, s.AtmosphericMoisture);
        h = Mix(h, s.Precipitation);
        h = Mix(h, s.Temperature);
        h = Mix(h, s.PlateId);
        h = Mix(h, s.Crust);
        h = Mix(h, s.CrustAge);
        h = Mix(h, s.CrustDensity);
        h = Mix(h, s.CrustThickness);
        h = Mix(h, s.Boundary);
        h = Mix(h, s.VolcanicActivity);
        h = Mix(h, s.OceanicOnly);
        h = Mix(h, s.StructuralRelief);
        h = Mix(h, s.CoverThickness);
        h = Mix(h, s.CoverRock);
        h = Mix(h, s.IncisionRate);
        h = Mix(h, s.AggradationRate);
        h = Mix(h, s.GlacialIncisionRate);
        h = Mix(h, s.DeltaSupply);
        h = Mix(h, s.FloodFactor);
        h = Mix(h, s.DeflationRate);
        h = Mix(h, s.LoessRate);
        h = Mix(h, [s.SeaLevelMeters]);
        h = Mix(h, PlateFields(engine));
        h = Mix(h, engine.Plates.PlateIdByCell);
        return h;
    }

    /// <summary>每個欄位各自的雜湊，給「哪個欄位先分歧」的診斷用。</summary>
    public static (string Field, ulong Hash)[] PerField(SimulationEngine engine)
    {
        var s = engine.State;
        return
        [
            ("基岩高度", Mix(Offset, s.BedrockElevation)),
            ("沉積", Mix(Offset, s.SedimentThickness)),
            ("礁體", Mix(Offset, s.OrganicThickness)),
            ("冰厚", Mix(Offset, s.IceThickness)),
            ("水深", Mix(Offset, s.WaterDepth)),
            ("溫度", Mix(Offset, s.Temperature)),
            ("地殼類型", Mix(Offset, s.Crust)),
            ("地殼年齡", Mix(Offset, s.CrustAge)),
            ("地殼密度", Mix(Offset, s.CrustDensity)),
            ("地殼厚度", Mix(Offset, s.CrustThickness)),
            ("邊界", Mix(Offset, s.Boundary)),
            ("火山活動", Mix(Offset, s.VolcanicActivity)),
            ("構造起伏", Mix(Offset, s.StructuralRelief)),
            ("蓋層厚度", Mix(Offset, s.CoverThickness)),
            ("蓋層岩性", Mix(Offset, s.CoverRock)),
            ("下切速率", Mix(Offset, s.IncisionRate)),
            ("堆積速率", Mix(Offset, s.AggradationRate)),
            ("冰蝕速率", Mix(Offset, s.GlacialIncisionRate)),
            ("三角洲供砂", Mix(Offset, s.DeltaSupply)),
            ("洪峰係數", Mix(Offset, s.FloodFactor)),
            ("風蝕", Mix(Offset, s.DeflationRate)),
            ("黃土", Mix(Offset, s.LoessRate)),
            ("海平面", Mix(Offset, [s.SeaLevelMeters])),
            ("板塊", Mix(Offset, PlateFields(engine))),
            ("板塊歸屬", Mix(Offset, engine.Plates.PlateIdByCell)),
        ];
    }

    /// <summary>
    /// 板塊逐欄位攤平成 double。不直接雜湊 Plate 結構的原始位元組：它含 bool，
    /// 結構的填充位元組內容不保證一致，會產生假的分歧。
    /// </summary>
    private static double[] PlateFields(SimulationEngine engine) =>
        engine.Plates.Plates
            .SelectMany(p => new[]
            {
                p.Id, p.PoleX, p.PoleY, p.PoleZ, p.AngularVelocityDegPerMyr,
                p.DensityAnomalyKgPerM3, p.IsOceanicOnly ? 1.0 : 0.0,
            })
            .ToArray();

    private const ulong Offset = 14695981039346656037UL;

    private static ulong Mix<T>(ulong h, T[] values) where T : unmanaged
    {
        foreach (var b in MemoryMarshal.AsBytes<T>(values))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}
