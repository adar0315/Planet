using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Planet.Simulation.Tests;

// P8 校準儀表（固定儀表，沒設 PROBE=1 不會跑）。從存檔（PROBE_SNAP）或從頭（沒給存檔）跑到停止條件觸發（最多 PROBE_STEPS 步），
// 量 P8 的目標：海洋比例、高度分布、洋脊深度、主洋佔水域、冰帽。PROBE=1、PROBE_DIR。
public class CalibProbe(ITestOutputHelper output)
{
    [Fact]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("PROBE") != "1") return;
        var dir = Environment.GetEnvironmentVariable("PROBE_DIR")!;
        var maxSteps = int.Parse(Environment.GetEnvironmentVariable("PROBE_STEPS") ?? "1200");
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 150);
        double Env(string name, double fallback) => Environment.GetEnvironmentVariable(name) is { } v ? double.Parse(v) : fallback;
        var layout = ContinentLayoutParameters.Default with { InitialSeaLevelMeters = (float)Env("PROBE_SEA", ContinentLayoutParameters.Default.InitialSeaLevelMeters) };
        var engine = new SimulationEngine(grid, 42, (float)Env("PROBE_OCEAN", SimulationState.DefaultOceanDepthMeters),
            crustTransport: CrustTransport.PlateFrame, continentLayout: layout);
        engine.FluvialErosion!.StreamPowerCoefficient = Env("PROBE_K", engine.FluvialErosion.StreamPowerCoefficient);
        var climateField = typeof(SimulationEngine).GetField("_climate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var climate = (Climate)climateField.GetValue(engine)!;
        climate.GlobalTemperatureOffsetC = (float)Env("PROBE_WARM", climate.GlobalTemperatureOffsetC);
        var seaAt = new System.Collections.Generic.Queue<double>();
        var erodedRecent = new System.Collections.Generic.Queue<double>();
        if (Environment.GetEnvironmentVariable("PROBE_SNAP") is { } snap)
            using (var f = File.OpenRead(snap)) SimulationSnapshot.Restore(engine, f);
        var s = engine.State;
        var sb = new StringBuilder();
        var steps = 0;
        while (!engine.StopConditionTriggered && steps < maxSteps)
        {
            engine.Step(1);
            steps++;
            erodedRecent.Enqueue(engine.FluvialErosion!.LastErodedVolumeKm3);
            if (erodedRecent.Count > 20) erodedRecent.Dequeue();
            seaAt.Enqueue(engine.FluvialErosion!.SeaDepositedKm3);
            if (seaAt.Count > 21) seaAt.Dequeue();
            if (steps % 100 == 0) sb.AppendLine($"  {s.ElapsedYears / 1e6:F1} Myr 還沒觸發");
        }
        sb.AppendLine($"{(engine.StopConditionTriggered ? "觸發" : "沒觸發")}：{s.ElapsedYears / 1e6:F1} Myr（第 {s.StepCount} 步）");
        Measure(engine, grid, sb);
        var landKm2 = Enumerable.Range(0, s.CellCount).Count(c => s.WaterDepth[c] <= 0f) * grid.AverageCellAreaSquareMeters / 1e6;
        if (seaAt.Count > 1) sb.AppendLine($"淨輸出到海（最後 20 步）{(seaAt.Last() - seaAt.First()) / (seaAt.Count - 1) * 1e9 / (landKm2 * 1e6) / (engine.YearsPerStep / 1e6):F0} m/Myr（地球約 50）");
        sb.AppendLine($"剝蝕率（最後 20 步，物質厚度）{(erodedRecent.Count > 0 ? erodedRecent.Average() : 0) * 1e9 / (landKm2 * 1e6) / (engine.YearsPerStep / 1e6):F0} m/Myr（地球約 50）");
        MeasureLandforms(engine, sb);
        sb.AppendLine($"每步平均耗時（ms）：{string.Join("、", engine.Timings.AverageMillisecondsPerStep().Select(t => $"{t.Section} {t.AverageMilliseconds:F0}"))}");
        MeasureSeasons(engine, grid, dir, sb);
        File.WriteAllText(Path.Combine(dir, "calib.txt"), sb.ToString());
        var renderer = new SimulationFieldRenderer(grid);
        File.WriteAllBytes(Path.Combine(dir, "calib_elev.rgba"), renderer.Render(s, SimulationField.ElevationWithPlateBoundaries));
        File.WriteAllBytes(Path.Combine(dir, "calib_lithology.rgba"), renderer.Render(s, SimulationField.Lithology));
        File.WriteAllBytes(Path.Combine(dir, "calib_landforms.rgba"), renderer.Render(s, SimulationField.Landforms));
        File.WriteAllBytes(Path.Combine(dir, "calib_rivers.rgba"), renderer.Render(s, SimulationField.RiversAndLakes, engine.Hydrology));
        using (var f = File.Create(Path.Combine(dir, "calib_snap.bin"))) SimulationSnapshot.Write(engine, f);
        output.WriteLine(sb.ToString());
    }

    /// <summary>水文地貌規劃 H3、H4：12 個月的氣候與水情，畫 1 月、7 月的氣溫、降水、積雪，以及河的水情。</summary>
    internal static void MeasureSeasons(SimulationEngine engine, SimulationGrid grid, string dir, StringBuilder sb)
    {
        var s = engine.State;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (climate, hydro) = engine.SolveSeasons(12);
        sb.AppendLine($"12 個月的氣候＋水文：{sw.ElapsedMilliseconds} ms");
        var min = Rivers.MinDischarge(grid, s) / 10f; // 規劃 H4：河的門檻降到 1,000 km²
        var rivers = Enumerable.Range(0, s.CellCount).Where(c => s.WaterDepth[c] <= 0f && engine.Hydrology!.Discharge[c] >= min).ToArray();
        var regimes = rivers.GroupBy(c => hydro.Regime[c]).OrderBy(g => g.Key).Select(g => $"{g.Key} {100.0 * g.Count() / rivers.Length:F0}%");
        sb.AppendLine($"河（≥1,000 km²，{rivers.Length} 格）：{string.Join("、", regimes)}；基流佔比平均 {rivers.Average(c => hydro.BaseflowShare[c]):F2}、融雪 {rivers.Average(c => hydro.MeltShare[c]):F2}、洪峰係數平均 {rivers.Average(c => hydro.FloodFactor[c]):F2}");
        var land = Enumerable.Range(0, s.CellCount).Where(c => s.WaterDepth[c] <= 0f).ToArray();
        sb.AppendLine($"積雪佔陸地：1 月 {100.0 * land.Count(c => hydro.SnowCovered[0][c]) / land.Length:F0}%、7 月 {100.0 * land.Count(c => hydro.SnowCovered[6][c]) / land.Length:F0}%；海冰佔海：1 月 {100.0 * Enumerable.Range(0, s.CellCount).Count(c => s.WaterDepth[c] > 0f && hydro.Frozen[0][c]) / (s.CellCount - land.Length):F0}%、7 月 {100.0 * Enumerable.Range(0, s.CellCount).Count(c => s.WaterDepth[c] > 0f && hydro.Frozen[6][c]) / (s.CellCount - land.Length):F0}%");

        var renderer = new SimulationFieldRenderer(grid);
        var w = SimulationFieldRenderer.Width;
        var h = SimulationFieldRenderer.Height;
        void Paint(string name, Func<int, (byte R, byte G, byte B)> color)
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = color(renderer.CellAtPixel(x, y));
                var i = (y * w + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
            }
            File.WriteAllBytes(Path.Combine(dir, name + ".rgba"), px);
        }
        (byte, byte, byte) Lerp(double t, (byte, byte, byte) a, (byte, byte, byte) b)
        {
            t = Math.Clamp(t, 0, 1);
            return ((byte)(a.Item1 + (b.Item1 - a.Item1) * t), (byte)(a.Item2 + (b.Item2 - a.Item2) * t), (byte)(a.Item3 + (b.Item3 - a.Item3) * t));
        }
        var meanP = s.Precipitation.Average();
        foreach (var (m, label) in new[] { (0, "jan"), (6, "jul") })
        {
            Paint($"season_temp_{label}", c => Lerp((climate.Temperature[m][c] + 30) / 65, (0x2b, 0x5f, 0xd9), (0xd9, 0x33, 0x2b)));
            Paint($"season_precip_{label}", c => s.WaterDepth[c] > 0f ? ((byte)0x10, (byte)0x18, (byte)0x28)
                : Lerp(Math.Sqrt(climate.Precipitation[m][c] / (3 * meanP)), (0xc9, 0xa0, 0x5c), (0x0a, 0x4d, 0x2e)));
            Paint($"season_snow_{label}", c => s.WaterDepth[c] > 0f
                ? (hydro.Frozen[m][c] ? ((byte)0xd8, (byte)0xe8, (byte)0xf0) : ((byte)0x10, (byte)0x20, (byte)0x38))
                : hydro.SnowCovered[m][c] ? ((byte)0xff, (byte)0xff, (byte)0xff) : ((byte)0x50, (byte)0x60, (byte)0x40));
        }
        Paint("season_regime", c => s.WaterDepth[c] > 0f ? ((byte)0x10, (byte)0x18, (byte)0x28)
            : engine.Hydrology!.Discharge[c] < min ? ((byte)0x30, (byte)0x30, (byte)0x30)
            : hydro.Regime[c] switch
            {
                FlowRegime.Perennial => ((byte)0x30, (byte)0x80, (byte)0xff),
                FlowRegime.Seasonal => ((byte)0x40, (byte)0xe0, (byte)0x80),
                _ => ((byte)0xe0, (byte)0xa0, (byte)0x40),
            });
    }

    /// <summary>水文地貌規劃 H1、H2：陸地露出的岩石、沖積地形與三角洲的範圍。</summary>
    internal static void MeasureLandforms(SimulationEngine engine, StringBuilder sb)
    {
        var s = engine.State;
        var land = Enumerable.Range(0, s.CellCount).Where(c => s.WaterDepth[c] <= 0f).ToArray();
        var rocks = land.GroupBy(c => Lithology.ExposedAt(s, c)).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {100.0 * g.Count() / land.Length:F1}%");
        sb.AppendLine($"陸地露出的岩石：{string.Join("、", rocks)}");
        var aggrading = land.Count(c => s.AggradationRate[c] > 1f && s.AggradationRate[c] > s.IncisionRate[c]);
        var incising = land.Count(c => s.IncisionRate[c] > 1f);
        var glacial = land.Count(c => s.GlacialIncisionRate[c] > 1f);
        var deltas = Enumerable.Range(0, s.CellCount).Count(c => s.DeltaSupply[c] > 0f && s.WaterDepth[c] > 0f);
        sb.AppendLine($"陸地：堆積為主 {100.0 * aggrading / land.Length:F1}%、下切 {100.0 * incising / land.Length:F1}%、冰蝕 {100.0 * glacial / land.Length:F1}%；有三角洲供砂的河口 {deltas} 格");
        var top = land.MaxBy(c => s.SurfaceElevationAt(c));
        sb.AppendLine($"最高的格 {top}：高 {s.SurfaceElevationAt(top) - s.SeaLevelMeters:F0} m＝基岩 {s.BedrockElevation[top]:F0}＋褶皺 {s.StructuralRelief[top]:F0}＋沉積 {s.SedimentThickness[top]:F0}＋冰 {s.IceThickness[top]:F0}（地殼 {s.CrustThickness[top]:F1} km、黃土 {s.LoessRate[top]:F1}、堆積 {s.AggradationRate[top]:F0} m/Myr）");
        var f = engine.FluvialErosion!;
        sb.AppendLine($"最後一步：侵蝕 {f.LastErodedVolumeKm3:F0} km³，陸上堆積 {f.LastLandDepositKm3:F0}、三角洲 {f.LastDeltaDepositKm3:F0}、湖 {f.LastLakeDepositKm3:F0} km³");
    }

    internal static void Measure(SimulationEngine engine, SimulationGrid grid, StringBuilder sb)
    {
        var s = engine.State;
        var n = s.CellCount;
        var sea = s.SeaLevelMeters;
        var water = Enumerable.Range(0, n).Count(c => s.WaterDepth[c] > 0f);
        var lakes = engine.Hydrology!.LakeOf;
        var lakeCells = Enumerable.Range(0, n).Count(c => lakes[c] >= 0);
        var ice = Enumerable.Range(0, n).Count(c => s.IceThickness[c] > 1f);
        var landIce = Enumerable.Range(0, n).Count(c => s.IceThickness[c] > 1f && s.WaterDepth[c] <= 0f);
        var land = n - water;
        var r = ContinentConfiguration.Analyze(grid, s, engine.StopCriteria);
        sb.AppendLine($"海洋（有水的格）{100.0 * water / n:F1}%（目標 60~72）、陸地 {100.0 * land / n:F1}%、湖 {lakeCells} 格");
        sb.AppendLine($"主洋佔水域 {100.0 * (water - lakeCells) / water:F2}%（目標 > 95）");
        sb.AppendLine($"冰：佔表面 {100.0 * ice / n:F1}%、佔陸地 {100.0 * landIce / Math.Max(1, land):F1}%（目標冰帽 < 10）");
        sb.AppendLine($"停止條件：最大 {100 * r.LargestShare:F1}%、第二 {100 * r.SecondShare:F1}%、大島 {r.BigIslandCount}、破碎 {r.LargestRaggedness:F1}/{r.SecondRaggedness:F1}、成立 {r.IsMet}");
        sb.AppendLine($"海平面 {sea:F0} m");
        var landCells = Enumerable.Range(0, n).Where(c => s.WaterDepth[c] <= 0f).ToArray();
        var heights = landCells.Select(c => (double)(s.SurfaceElevationAt(c) - sea)).OrderBy(x => x).ToArray();
        string Share(double lo, double hi) => $"{100.0 * heights.Count(h => h >= lo && h < hi) / heights.Length:F0}";
        sb.AppendLine($"陸地高度：平均 {heights.Average():F0}、中位 {heights[heights.Length / 2]:F0}、最高 {heights[^1]:F0} m｜陸地佔比% 0~0.5k {Share(-1e9, 500)}、0.5~1k {Share(500, 1000)}、1~1.5k {Share(1000, 1500)}、1.5~2k {Share(1500, 2000)}、2~3k {Share(2000, 3000)}、>3k {Share(3000, 1e9)}（地球約 47/22/13/8/6/4）");
        // 大陸棚寬度：主洋裡水深 ≤ 200 m 的面積 ÷ 海岸線長（陸海相鄰的邊數 × 邊長）
        var shelf = Enumerable.Range(0, n).Count(c => s.WaterDepth[c] > 0f && s.WaterDepth[c] <= 200f && engine.Hydrology!.LakeOf[c] < 0);
        var edges = 0;
        foreach (var c in landCells)
            for (var k = 0; k < SimulationGrid.MaxNeighborsPerCell; k++)
            {
                var nb = grid.Neighbors[c * SimulationGrid.MaxNeighborsPerCell + k];
                if (nb != SimulationGrid.NoNeighbor && s.WaterDepth[nb] > 0f && engine.Hydrology!.LakeOf[nb] < 0) edges++;
            }
        var edgeKm = grid.AverageCellSpacingMeters / Math.Sqrt(3) / 1000;
        sb.AppendLine($"大陸棚（主洋 ≤ 200 m）{100.0 * shelf / n:F1}% 表面、平均寬 {shelf * grid.AverageCellAreaSquareMeters / 1e6 / (edges * edgeKm):F0} km（地球約 80 km、7.5% 表面）");

        // 洋脊：最年輕的海洋地殼（< 1 Myr）的地表相對海平面
        var ridge = Enumerable.Range(0, n).Where(c => s.Crust[c] == CrustType.Oceanic && s.CrustAge[c] < 1f).ToArray();
        if (ridge.Length > 0)
        {
            var depths = ridge.Select(c => s.SurfaceElevationAt(c) - sea).OrderBy(x => x).ToArray();
            sb.AppendLine($"洋脊（<1 Myr 洋殼，{ridge.Length} 格）：中位 {depths[depths.Length / 2]:F0}、P10 {depths[depths.Length / 10]:F0}、P90 {depths[depths.Length * 9 / 10]:F0} m（目標 −2,000~−3,000）");
        }
        var oceanic = Enumerable.Range(0, n).Where(c => s.Crust[c] == CrustType.Oceanic && s.WaterDepth[c] > 0f && lakes[c] < 0).ToArray();
        var od = oceanic.Select(c => s.SurfaceElevationAt(c) - sea).OrderBy(x => x).ToArray();
        sb.AppendLine($"海洋地殼的海床：中位 {od[od.Length / 2]:F0}、P10 {od[od.Length / 10]:F0}、P90 {od[od.Length * 9 / 10]:F0} m；平均年齡 {oceanic.Average(c => s.CrustAge[c]):F1} Myr");

        // 高度分布（相對海平面，每 500 m 一格；儀表要每 100 m，這裡印粗的看雙峰）
        var hist = new int[24];
        for (var c = 0; c < n; c++)
        {
            var e = s.SurfaceElevationAt(c) - sea;
            var bin = Math.Clamp((int)Math.Floor((e + 7000) / 500), 0, 23);
            hist[bin]++;
        }
        sb.AppendLine("高度分布（相對海平面，500 m 一格，% 表面）：");
        for (var b = 0; b < 24; b++)
        {
            var lo = -7000 + b * 500;
            var pct = 100.0 * hist[b] / n;
            sb.AppendLine($"  {lo,6}~{lo + 500,6}: {pct,5:F1} {new string('#', (int)Math.Round(pct * 2))}");
        }
    }
}
