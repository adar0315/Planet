using Planet.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Planet.Simulation.Tests;

public class DiagnosticDump
{
    private readonly ITestOutputHelper _output;
    public DiagnosticDump(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpConfig()
    {
        var grid = SimulationGrid.Build(1_852_259.92, 24);
        var engine = new SimulationEngine(grid, 42);
        engine.Step(4000);
        var s = engine.State;
        var con = Enumerable.Range(0, s.CellCount).Where(c => s.Crust[c] == CrustType.Continental).ToArray();
        var oce = Enumerable.Range(0, s.CellCount).Where(c => s.Crust[c] == CrustType.Oceanic)
            .Select(c => s.CrustThickness[c]).Order().ToArray();
        var seen = new bool[s.CellCount]; var sizes = new List<int>(); var stack = new Stack<int>();
        foreach (var start in con)
        {
            if (seen[start]) continue;
            var size = 0; stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                var cell = stack.Pop(); size++;
                var b = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var i = 0; i < SimulationGrid.MaxNeighborsPerCell; i++)
                {
                    var n = grid.Neighbors[b + i];
                    if (n == SimulationGrid.NoNeighbor || seen[n] || s.Crust[n] != CrustType.Continental) continue;
                    seen[n] = true; stack.Push(n);
                }
            }
            sizes.Add(size);
        }
        _output.WriteLine($"RESULT 大陸={con.Length} 最大塊={(sizes.Count > 0 ? sizes.Max() : 0)} " +
            $"塊數={sizes.Count} 大陸厚={(con.Length > 0 ? con.Average(c => s.CrustThickness[c]) : 0):F1} " +
            $"海洋厚中位={oce[oce.Length / 2]:F1} 陸地={engine.LandFraction():P1}");
    }
}
