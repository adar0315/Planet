using System.Diagnostics;

namespace Planet.Simulation.Representation;

/// <summary>
/// v2 規劃 P2 的評比：讓所有候選在**同一組**板塊運動下並排推進（不做任何地質作用），
/// 每一步跟「精確解」比對。
///
/// ## 精確解
///
/// 純剛體旋轉時，板塊內部任一點在時間 t 的地殼，就是起始時刻在「反轉回去的那一點」的地殼。
/// 用起始網格的最近格當作那一點的值。只在**板塊內部**（離任何邊界兩格以上，起點與現在都是）比對——
/// 邊界上沒有唯一正確的答案（那裡是隱沒與擴張，屬於地質作用）。
/// 「整顆球同一個旋轉」的情境（<c>singleRotation</c>）沒有真正的邊界，全部的格子都比對。
/// </summary>
internal static class RepresentationBenchmark
{
    /// <summary>把每個候選跑 <paramref name="steps"/> 步並量出評比數字。</summary>
    /// <param name="grid">網格。</param>
    /// <param name="plates">板塊（會被推進；速度在評比期間固定，不跑動力學）。</param>
    /// <param name="thicknessKm">起始厚度。</param>
    /// <param name="continental">起始的大陸格。</param>
    /// <param name="carriers">候選。</param>
    /// <param name="steps">步數。</param>
    /// <param name="deltaMyr">每步百萬年。</param>
    /// <param name="singleRotation">true：所有板塊用 0 號板塊的歐拉極與角速度（整顆球一起轉）。</param>
    /// <param name="reverseAtStep">大於 0 時，在這一步之後把所有角速度反向（來回測試：最後應該回到起點）。</param>
    /// <param name="hotspotKmPerStep">
    /// 大於 0 時模擬地質作用的寫回：固定在網格上的幾個熱點，每步把半徑內的格子加厚這麼多，
    /// 再呼叫 <see cref="ICrustCarrier.WriteBack"/>。板塊在熱點上方移動，會拖出一條軌跡。
    /// </param>
    internal static IReadOnlyList<CarrierResult> Run(
        SimulationGrid grid, PlateSet plates, float[] thicknessKm, bool[] continental,
        IReadOnlyList<ICrustCarrier> carriers, int steps, double deltaMyr,
        bool singleRotation = false, int reverseAtStep = 0, float hotspotKmPerStep = 0f)
    {
        var n = grid.CellCount;
        var hot = hotspotKmPerStep > 0f ? HotspotCells(grid) : [];
        if (singleRotation)
        {
            var reference = plates.Plates[0];
            for (var p = 0; p < plates.Plates.Length; p++)
            {
                plates.Plates[p] = plates.Plates[p] with
                {
                    PoleX = reference.PoleX, PoleY = reference.PoleY, PoleZ = reference.PoleZ,
                    AngularVelocityDegPerMyr = reference.AngularVelocityDegPerMyr,
                };
            }
        }

        var initialOwner = (int[])plates.PlateIdByCell.Clone();
        var initialInterior = InteriorMask(grid, initialOwner);
        var rotation = Enumerable.Repeat((1.0, 0.0, 0.0, 0.0), plates.Plates.Length).ToArray();
        var exactBody = Enumerable.Range(0, n).ToArray();
        var exactOwner = (int[])initialOwner.Clone();
        var exactValid = new bool[n];
        var exactContinental = (bool[])continental.Clone();
        var previousExact = (bool[])continental.Clone();

        foreach (var carrier in carriers) carrier.Initialize(grid, plates, thicknessKm, continental);
        var stats = carriers.Select(c => new Accumulator(c.Name, continental, thicknessKm)).ToArray();
        var thickness = new float[n];
        var flags = new bool[n];
        long exactFlips = 0;

        for (var step = 1; step <= steps; step++)
        {
            plates.Advance(grid, deltaMyr);
            for (var p = 0; p < rotation.Length; p++)
            {
                rotation[p] = SphereMath.Normalize(SphereMath.Multiply(SphereMath.StepRotation(plates.Plates[p], deltaMyr), rotation[p]));
            }

            var stepMs = new double[carriers.Count];
            for (var i = 0; i < carriers.Count; i++)
            {
                var watch = Stopwatch.StartNew();
                carriers[i].Step(grid, plates, deltaMyr);
                stepMs[i] = watch.Elapsed.TotalMilliseconds;
            }

            // 精確解：地殼歸屬以候選回報的為準（板塊座標系會讓地殼越過 Voronoi 平分線）
            var owner = carriers.Select(c => c.Owners).FirstOrDefault(o => o is not null) ?? plates.PlateIdByCell;
            var interiorNow = singleRotation ? null : InteriorMask(grid, owner);
            var u = grid.UnitVector;
            Parallel.For(0, n, c =>
            {
                var p = singleRotation ? 0 : owner[c];
                var (bx, by, bz) = SphereMath.Rotate(rotation[p], u[c * 3], u[c * 3 + 1], u[c * 3 + 2], inverse: true);
                var body = p == exactOwner[c] ? SphereMath.ClimbToNearest(grid, exactBody[c], bx, by, bz) : grid.FindNearestCell(bx, by, bz);
                exactBody[c] = body;
                exactOwner[c] = p;
                exactContinental[c] = continental[body];
                exactValid[c] = singleRotation || (initialOwner[body] == p && initialInterior[body] && interiorNow![c]);
            });
            for (var c = 0; c < n; c++)
            {
                if (exactValid[c] && exactContinental[c] != previousExact[c]) exactFlips++;
                previousExact[c] = exactContinental[c];
            }

            for (var i = 0; i < carriers.Count; i++)
            {
                carriers[i].Read(thickness, flags);
                if (hot.Length > 0)
                {
                    foreach (var c in hot) thickness[c] += hotspotKmPerStep;
                    carriers[i].WriteBack(grid, thickness);
                    stats[i].AddedMass += hot.Length * (double)hotspotKmPerStep;
                    carriers[i].Read(thickness, flags);
                }
                stats[i].Record(thickness, flags, exactContinental, exactValid, stepMs[i]);
            }

            if (step == reverseAtStep)
            {
                for (var p = 0; p < plates.Plates.Length; p++)
                {
                    plates.Plates[p] = plates.Plates[p] with { AngularVelocityDegPerMyr = -plates.Plates[p].AngularVelocityDegPerMyr };
                }
            }
        }

        var exactMeanFlips = (double)exactFlips / steps;
        // 精確解本身的「厚度總和變化」：最近格取樣一張旋轉過的網格，總和本來就會小幅漂動
        var exactMass = 0.0;
        for (var c = 0; c < n; c++) exactMass += thicknessKm[exactBody[c]];
        var exactMassChange = singleRotation ? exactMass / thicknessKm.Sum(t => (double)t) - 1 : double.NaN;
        return stats.Select(s => s.Finish(grid, continental, exactMeanFlips, double.IsNaN(exactMassChange) ? 0 : exactMassChange)).ToList();
    }

    /// <summary>六個固定在網格上的熱點，半徑 150 公里內的格子。</summary>
    private static int[] HotspotCells(SimulationGrid grid)
    {
        (double Lat, double Lon)[] centres = [(10, 20), (-25, 100), (40, -60), (-50, -150), (0, 160), (60, 80)];
        var radius = 150_000.0 / grid.PlanetRadiusMeters;
        var cosRadius = Math.Cos(radius);
        var cells = new List<int>();
        var u = grid.UnitVector;
        for (var c = 0; c < grid.CellCount; c++)
        {
            foreach (var (lat, lon) in centres)
            {
                double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
                var dot = u[c * 3] * Math.Cos(la) * Math.Cos(lo) + u[c * 3 + 1] * Math.Cos(la) * Math.Sin(lo) + u[c * 3 + 2] * Math.Sin(la);
                if (dot < cosRadius) continue;
                cells.Add(c);
                break;
            }
        }
        return cells.ToArray();
    }

    /// <summary>比所有鄰居都低 1 公里以上的格子——沒收到地質作用的舊值，看起來就是這種坑。</summary>
    private static int CountPits(SimulationGrid grid, float[] thickness)
    {
        var pits = 0;
        for (var c = 0; c < grid.CellCount; c++)
        {
            var lowest = float.MaxValue;
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var nb = grid.Neighbors[baseIndex + s];
                if (nb != SimulationGrid.NoNeighbor) lowest = Math.Min(lowest, thickness[nb]);
            }
            if (thickness[c] < lowest - 1f) pits++;
        }
        return pits;
    }

    /// <summary>離任何別的板塊兩格以上的格子。</summary>
    private static bool[] InteriorMask(SimulationGrid grid, int[] owner)
    {
        var interior = new bool[grid.CellCount];
        Parallel.For(0, grid.CellCount, c =>
        {
            var p = owner[c];
            var baseIndex = c * SimulationGrid.MaxNeighborsPerCell;
            for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
            {
                var n1 = grid.Neighbors[baseIndex + s];
                if (n1 == SimulationGrid.NoNeighbor) continue;
                if (owner[n1] != p) return;
                var base2 = n1 * SimulationGrid.MaxNeighborsPerCell;
                for (var t = 0; t < SimulationGrid.MaxNeighborsPerCell; t++)
                {
                    var n2 = grid.Neighbors[base2 + t];
                    if (n2 != SimulationGrid.NoNeighbor && owner[n2] != p) return;
                }
            }
            interior[c] = true;
        });
        return interior;
    }

    private sealed class Accumulator(string name, bool[] initialContinental, float[] initialThickness)
    {
        private readonly bool[] _previous = (bool[])initialContinental.Clone();
        private readonly double _initialMass = initialThickness.Sum(t => (double)t);
        private readonly int _initialIntermediate = initialThickness.Count(IsIntermediate);
        private readonly List<int> _flips = [];
        private readonly float[] _initialThickness = initialThickness;
        private float[] _finalThickness = [];

        /// <summary>熱點總共加了多少厚度（公里·格）。</summary>
        public double AddedMass { get; set; }
        private long _mismatch, _compared;
        private int _finalMismatch, _finalCompared;
        private double _totalMs, _finalMass;
        private int _finalIntermediate, _finalContinental;

        public void Record(float[] thickness, bool[] flags, bool[] exact, bool[] valid, double ms)
        {
            var flips = 0;
            int mismatch = 0, compared = 0;
            for (var c = 0; c < flags.Length; c++)
            {
                if (flags[c] != _previous[c]) flips++;
                _previous[c] = flags[c];
                if (!valid[c]) continue;
                compared++;
                if (flags[c] != exact[c]) mismatch++;
            }
            _flips.Add(flips);
            _mismatch += mismatch;
            _compared += compared;
            _finalMismatch = mismatch;
            _finalCompared = compared;
            _totalMs += ms;
            _finalMass = thickness.Sum(t => (double)t);
            _finalIntermediate = thickness.Count(IsIntermediate);
            _finalContinental = flags.Count(f => f);
            _finalThickness = (float[])thickness.Clone();
        }

        /// <param name="grid">網格。</param>
        /// <param name="initialContinental">起始的大陸格。</param>
        /// <param name="exactMeanFlips">精確解每步翻面數。</param>
        /// <param name="exactMassChange">
        /// 精確解自己的厚度總和變化。熱點遺失要扣掉它：最近格取樣一張旋轉過的網格，總和本來就會漂
        /// （500 步約 −0.2%，對總量很小，但相對熱點加上去的量是一成多——2026-10-17 第一次就把它誤算成遺失）。
        /// </param>
        public CarrierResult Finish(SimulationGrid grid, bool[] initialContinental, double exactMeanFlips, double exactMassChange) => new()
        {
            ExactMassChangeShare = exactMassChange,
            EditLossShare = AddedMass > 0 ? (_initialMass * (1 + exactMassChange) + AddedMass - _finalMass) / AddedMass : 0,
            InitialPits = CountPits(grid, _initialThickness),
            FinalPits = CountPits(grid, _finalThickness),
            Name = name,
            MeanFlipsPerStep = _flips.Average(),
            ExactMeanFlipsPerStep = exactMeanFlips,
            MaxFlipsPerStep = _flips.Max(),
            ZeroFlipStepShare = _flips.Count(f => f == 0) / (double)_flips.Count,
            MeanMismatchShare = _compared == 0 ? 0 : (double)_mismatch / _compared,
            FinalMismatchShare = _finalCompared == 0 ? 0 : (double)_finalMismatch / _finalCompared,
            FinalMismatchCells = _finalMismatch,
            MassChangeShare = (_finalMass - _initialMass) / _initialMass,
            InitialIntermediateCells = _initialIntermediate,
            FinalIntermediateCells = _finalIntermediate,
            InitialContinentalCells = initialContinental.Count(f => f),
            FinalContinentalCells = _finalContinental,
            MeanMillisecondsPerStep = _totalMs / _flips.Count,
        };

        /// <summary>介於海洋與大陸之間的厚度（10~25 公里）——起始時很少，被抹開的邊界會讓它變多。</summary>
        private static bool IsIntermediate(float t) => t > 10f && t < Isostasy.ContinentalThresholdKm;
    }
}

/// <summary>一個候選的評比結果。</summary>
internal sealed record CarrierResult
{
    /// <summary>候選名稱。</summary>
    public required string Name { get; init; }

    /// <summary>平均每步有幾格在大陸／海洋之間翻面（v1 實測約 1,200）。</summary>
    public required double MeanFlipsPerStep { get; init; }

    /// <summary>精確解在比對範圍內每步翻面幾格（大陸真的在移動，翻面不會是 0）。</summary>
    public required double ExactMeanFlipsPerStep { get; init; }

    /// <summary>最多的一步翻了幾格——看有沒有批次化（平時 0、偶爾一次大量）。</summary>
    public required int MaxFlipsPerStep { get; init; }

    /// <summary>一格都沒翻的步數比例——批次化的另一面。</summary>
    public required double ZeroFlipStepShare { get; init; }

    /// <summary>板塊內部跟精確解不一致的格子比例（全程平均）。</summary>
    public required double MeanMismatchShare { get; init; }

    /// <summary>最後一步不一致的比例。</summary>
    public required double FinalMismatchShare { get; init; }

    /// <summary>最後一步不一致的格數。</summary>
    public required int FinalMismatchCells { get; init; }

    /// <summary>地殼厚度總和的變化比例（純旋轉時應該接近 0）。</summary>
    public required double MassChangeShare { get; init; }

    /// <summary>精確解自己的厚度總和變化（只在整顆球同一個旋轉時有定義，否則 NaN）。</summary>
    public double ExactMassChangeShare { get; init; }

    /// <summary>起始時介於 10~25 公里的格數。</summary>
    public required int InitialIntermediateCells { get; init; }

    /// <summary>最後介於 10~25 公里的格數——變多就是邊界被抹開了。</summary>
    public required int FinalIntermediateCells { get; init; }

    /// <summary>起始的大陸格數。</summary>
    public required int InitialContinentalCells { get; init; }

    /// <summary>最後的大陸格數。</summary>
    public required int FinalContinentalCells { get; init; }

    /// <summary>
    /// 熱點加上去的厚度有多少比例不見了（只在沒有邊界的情境有意義；正值＝遺失、負值＝多出來）。
    /// 已扣掉精確解自己的取樣漂移，所以搬運本身會增減質量的候選，那部分也會算進來。
    /// </summary>
    public required double EditLossShare { get; init; }

    /// <summary>起始時比所有鄰居都低 1 公里以上的格數。</summary>
    public required int InitialPits { get; init; }

    /// <summary>最後的坑數——變多就是有些地方沒收到地質作用。</summary>
    public required int FinalPits { get; init; }

    /// <summary>平均每步花幾毫秒（只算搬運）。</summary>
    public required double MeanMillisecondsPerStep { get; init; }
}
