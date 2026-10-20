using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Planet.Simulation.Tests;

// P8 儀表：抬升來源對帳（沒設 PROBE=1 不會跑）。草圖劇本種子 42 跑 PROBE_STEPS 步，每 5 Myr 印各種增厚來源與侵蝕刨掉的基岩（立方公里／百萬年）。
// PROBE=1、PROBE_DIR、PROBE_STEPS。
public class UpliftProbe(ITestOutputHelper output)
{
    [Fact]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("PROBE") != "1") return;
        var dir = Environment.GetEnvironmentVariable("PROBE_DIR")!;
        var steps = int.Parse(Environment.GetEnvironmentVariable("PROBE_STEPS") ?? "820");
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 150);
        var engine = new SimulationEngine(grid, 42, crustTransport: CrustTransport.PlateFrame,
            continentLayout: ContinentLayoutParameters.Default);
        var s = engine.State;
        var cellKm2 = grid.AverageCellAreaSquareMeters / 1e6;
        var src = engine.Tectonics.FrameUpliftSourcesKm!;
        var prev = (double[])src.Clone();
        var prevEroded = 0.0;
        var sb = new StringBuilder("Myr\t大陸碰撞\t撞太平洋\t安地斯型\t隱沒增生\t增積楔\t侵蝕刨掉的基岩\t（立方公里／百萬年）\t陸地平均高\t大陸地殼總體積 km3\n");
        const int window = 100;
        for (var i = 1; i <= steps; i++)
        {
            engine.Step(1);
            if (i % window != 0) continue;
            var myr = window * engine.YearsPerStep / 1e6;
            string Rate(int k) => $"{(src[k] - prev[k]) * cellKm2 / myr:F0}";
            var eroded = engine.FluvialErosion!.CrustErodedKm;
            var land = Enumerable.Range(0, s.CellCount).Where(c => s.WaterDepth[c] <= 0f).ToArray();
            var continental = Enumerable.Range(0, s.CellCount).Where(c => s.Crust[c] == CrustType.Continental).Sum(c => (double)s.CrustThickness[c]) * cellKm2;
            sb.AppendLine(string.Join('\t', $"{s.ElapsedYears / 1e6:F0}", Rate(0), Rate(1), Rate(2), Rate(3), Rate(4),
                $"{(eroded - prevEroded) * cellKm2 / myr:F0}", "", $"{land.Average(c => s.SurfaceElevationAt(c) - s.SeaLevelMeters):F0}", $"{continental:F0}"));
            prev = (double[])src.Clone();
            prevEroded = eroded;
            File.WriteAllText(Path.Combine(dir, "uplift.tsv"), sb.ToString());
        }
        output.WriteLine(sb.ToString());
    }
}
