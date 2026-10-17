using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Planet.Simulation.Representation;
using Xunit;
using Xunit.Abstractions;

namespace Planet.Simulation.Tests;

// 暫時的探針：v2 P2 表示法評比（n=150、第 1,160 步的存檔）。PROBE=1 才執行。
public class RepresentationProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("PROBE") != "1") return;
        var dir = Environment.GetEnvironmentVariable("PROBE_DIR")!;
        var grid = SimulationGrid.Build(TestPlanet.RadiusMeters, 150);
        var steps = int.Parse(Environment.GetEnvironmentVariable("PROBE_STEPS") ?? "500");

        (PlateSet Plates, float[] Thickness, bool[] Continental) Load()
        {
            var engine = new SimulationEngine(grid, 42);
            using (var f = File.OpenRead(Path.Combine(dir, "stop.snapshot"))) SimulationSnapshot.Restore(engine, f);
            var s = engine.State;
            return (engine.Plates, (float[])s.CrustThickness.Clone(), s.Crust.Select(c => c == CrustType.Continental).ToArray());
        }

        static List<ICrustCarrier> Candidates() =>
        [
            new MarkerCarrier(1, averageThickness: false),
            new MarkerCarrier(4, averageThickness: false),
            new MarkerCarrier(4, averageThickness: true),
            new PlateFrameCarrier(),
            new SemiLagrangianCarrier(),
        ];

        var report = new StringBuilder();
        void Scenario(string title, bool single, int reverseAt, float hotspot = 0f)
        {
            var (plates, thickness, continental) = Load();
            var results = RepresentationBenchmark.Run(grid, plates, thickness, continental, Candidates(), steps, 0.05, single, reverseAt, hotspot);
            report.AppendLine($"### {title}（{steps} 步）");
            report.AppendLine();
            report.AppendLine("| 候選 | 翻面／步 | 精確解翻面／步 | 最多一步 | 零翻面步 | 內部不一致（平均） | 最後不一致 | 厚度總和變化（精確解） | 10~25km 格數（起→終） | 大陸格（起→終） | 坑（起→終） | 熱點遺失 | 毫秒／步 |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in results)
            {
                report.AppendLine(
                    $"| {r.Name} | {r.MeanFlipsPerStep:F1} | {r.ExactMeanFlipsPerStep:F1} | {r.MaxFlipsPerStep} | {r.ZeroFlipStepShare:P0} | " +
                    $"{r.MeanMismatchShare:P2} | {r.FinalMismatchShare:P2}（{r.FinalMismatchCells}） | {r.MassChangeShare:P2}（{r.ExactMassChangeShare:P2}） | " +
                    $"{r.InitialIntermediateCells}→{r.FinalIntermediateCells} | {r.InitialContinentalCells}→{r.FinalContinentalCells} | {r.InitialPits}→{r.FinalPits} | {r.EditLossShare:P2} | {r.MeanMillisecondsPerStep:F1} |");
            }
            report.AppendLine();
            output.WriteLine(report.ToString());
            File.WriteAllText(Path.Combine(dir, "p2_results.md"), report.ToString());
        }

        Scenario("S1 整顆球同一個剛體旋轉（沒有邊界，只量搬運雜訊）", single: true, reverseAt: 0);
        Scenario("S2 來回：前半正轉、後半反轉（最後應該回到起點）", single: true, reverseAt: steps / 2);
        Scenario("S3 真實的多板塊運動（第 1,160 步的速度，固定）", single: false, reverseAt: 0);
        Scenario("S4 寫回：整顆球同一個旋轉＋六個固定熱點每步加厚 0.05 公里", single: true, reverseAt: 0, hotspot: 0.05f);
    }
}
