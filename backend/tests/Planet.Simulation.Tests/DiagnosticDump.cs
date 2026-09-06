using Planet.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Planet.Simulation.Tests;

public class DiagnosticDump
{
    private readonly ITestOutputHelper _output;
    public DiagnosticDump(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpOceanicBudget()
    {
        var engine = new SimulationEngine(SimulationGrid.Build(1_852_259.92, 24), 42);
        engine.Step(1000);
        Array.Clear(engine.PhaseDeltas);
        Array.Clear(engine.Tectonics.SubPhaseDeltas);
        var before = engine.State.CrustThickness
            .Where((_, i) => engine.State.Crust[i] == CrustType.Oceanic).DefaultIfEmpty(0f).Average();
        engine.Step(500);
        var after = engine.State.CrustThickness
            .Where((_, i) => engine.State.Crust[i] == CrustType.Oceanic).DefaultIfEmpty(0f).Average();

        _output.WriteLine($"海洋地殼平均 {before:F2} → {after:F2}（淨 {after - before:+0.00;-0.00}）");
        _output.WriteLine("大階段（公里/步）：");
        for (var i = 0; i < engine.PhaseDeltas.Length; i++)
            _output.WriteLine($"  {SimulationEngine.PhaseNames[i],-24} {engine.PhaseDeltas[i] / 500:+0.0000;-0.0000}");
        _output.WriteLine("板塊子階段（公里/步）：");
        for (var i = 0; i < engine.Tectonics.SubPhaseDeltas.Length; i++)
            _output.WriteLine($"  {PlateTectonics.SubPhaseNames[i],-18} {engine.Tectonics.SubPhaseDeltas[i] / 500:+0.0000;-0.0000}");
    }
}
