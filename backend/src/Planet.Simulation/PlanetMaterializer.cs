using Planet.Domain;

namespace Planet.Simulation;

/// <summary>
/// 把模擬粗網格的狀態整理成 <see cref="PlanetCell"/>：每格分類出地表覆蓋類型，並記下完整的地質狀態
/// （v2 規劃 P1「端到端管線」）。放大成 2,000 萬地塊的取樣在 <see cref="SimulatedTerrainSampler"/>。
///
/// ## 植被用降水的**排名**分，不用絕對門檻
///
/// 模擬的降水是相對尺度（見 <see cref="SimulationState.Precipitation"/>），拿地球的公釐門檻去切，
/// 會把所有陸地都判成沙漠（2026-10-17 第一次試寫就是這樣：陸地全是沙、沒有一格草或林）。
/// 所以先算每一格在全部陸地裡的降水百分位，再依地球大致的比例分：最乾的 <see cref="DesertShare"/>
/// 是沙漠，接著灌木、草原，最濕的是林地。
///
/// ## 分類規則刻意很粗
///
/// 使用者的決策是「每格記錄狀態資訊，先不做細節，未來當 3D 場景的參考」。這裡只用水深、冰厚、溫度、
/// 降水、高度把格子分進走動模式已經有的地形類型，讓畫面上看得出海、冰、沙漠、森林的分布；
/// 細節（河流、海岸地貌、植被演替）屬於之後的階段，到時再改這支就好，不必重寫地塊。
/// </summary>
public static class PlanetMaterializer
{
    /// <summary>降水排名最低的這個比例的陸地是沙漠（地球約 1/5～1/3 的陸地是乾燥區）。</summary>
    public const float DesertShare = 0.20f;

    /// <summary>沙漠之後這個比例是灌木。</summary>
    public const float ShrubShare = 0.15f;

    /// <summary>再之後這個比例是草原，剩下的是林地。</summary>
    public const float GrassShare = 0.30f;

    /// <summary>降水排名最高的這個比例、又是低地的陸地是沼澤。</summary>
    public const float SwampShare = 0.05f;

    /// <summary>冰厚超過這個值（公尺）就算冰原／冰河。</summary>
    public const float GlacierIceMeters = 10f;

    /// <summary>有礁體、而且水深在這以內（公尺）的海就算珊瑚礁。</summary>
    public const float ReefMaxDepthMeters = 50f;

    /// <summary>高於海平面這麼多（公尺）的陸地就算岩地（不長植被）。</summary>
    public const float RockAboveMeters = 3_500f;

    /// <summary>
    /// 水深分級的上界（公尺），對應 <see cref="TerrainType.Seawater"/> 的 HeightCategory 0~6
    /// （小腿肚深、膝深、腰深、胸深、高過頭、三人深、高過十人深）。
    /// </summary>
    private static readonly float[] WaterDepthBounds = [0.5f, 0.8f, 1.1f, 1.4f, 2f, 5f];

    /// <summary>
    /// 從目前的模擬狀態建立每一格的記錄。**不會改動傳進來的狀態**；碎島清除要由呼叫端先做
    /// （它會改地形，所以要在一份複本上做，見 <see cref="SmallIslandCleanup"/>）。
    /// </summary>
    /// <param name="grid">模擬網格。</param>
    /// <param name="state">模擬狀態。</param>
    /// <param name="hydrology">水系（v2 才有）：給了就把河記進每一格（見 <see cref="PlanetCell.RiverDownstreamCellId"/>）。</param>
    /// <param name="seasonal">
    /// 12 個月的氣候與水文（水文地貌規劃 H6，見 <c>SimulationEngine.SolveSeasons</c>）：給了就記下每月的氣溫、降水、流量、
    /// 積雪與結冰，以及河的水情。河寬用四季的平均流量（有沙漠的河道損失）。
    /// </param>
    public static PlanetCell[] BuildCells(
        SimulationGrid grid, SimulationState state, Hydrology? hydrology = null,
        (SeasonalClimate Climate, SeasonalHydrologyResult Hydrology)? seasonal = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(state);
        // 畫出來的河：集水 1,000 平方公里（H4）；河的相對大小照舊以這個門檻起算
        var minDischarge = hydrology is null ? 0f : Rivers.MinDischarge(grid, state, Rivers.MinDrawnDrainageAreaSquareKm);
        var waveExposure = WaveExposure(grid, state);

        var isOcean = MainOceanMask(grid, state);
        var wetness = LandPrecipitationRank(state);
        var cells = new PlanetCell[state.CellCount];

        for (var c = 0; c < state.CellCount; c++)
        {
            var water = state.WaterDepth[c];
            var ice = state.IceThickness[c];
            // 陸地的走動面是冰面（冰蓋上面走）；水域的高度是水底。
            // SurfaceElevationAt 已經含冰厚，不可以再加一次（水文地貌規劃 H0 修掉的重複計算）
            var elevation = state.SurfaceElevationAt(c) - state.SeaLevelMeters;
            var waterSurface = water > 0f ? state.WaterSurfaceAt(c) - state.SeaLevelMeters : 0f;
            var (terrain, category) = Classify(state, c, elevation, isOcean[c], wetness[c]);

            cells[c] = new PlanetCell
            {
                CellId = c,
                Latitude = grid.Latitude[c],
                Longitude = grid.Longitude[c],
                Terrain = terrain,
                HeightCategory = category,
                SurfaceElevationMeters = elevation,
                WaterDepthMeters = water,
                WaterSurfaceMeters = waterSurface,
                IsOcean = isOcean[c],
                IceThicknessMeters = ice,
                SedimentThicknessMeters = state.SedimentThickness[c],
                OrganicThicknessMeters = state.OrganicThickness[c],
                TemperatureCelsius = state.Temperature[c],
                Precipitation = state.Precipitation[c],
                CrustType = (int)state.Crust[c],
                CrustThicknessKm = state.CrustThickness[c],
                CrustAgeMyr = state.CrustAge[c],
                CrustDensity = state.CrustDensity[c],
                PlateId = state.PlateId[c],
                Boundary = (int)state.Boundary[c],
                VolcanicActivity = state.VolcanicActivity[c],
                ExposedRock = (int)Lithology.ExposedAt(state, c),
                IncisionRate = state.IncisionRate[c],
                AggradationRate = state.AggradationRate[c],
                GlacialIncisionRate = state.GlacialIncisionRate[c],
                DeltaSupply = state.DeltaSupply[c],
                DeflationRate = state.DeflationRate[c],
                LoessRate = state.LoessRate[c],
                WaveExposure = waveExposure[c],
            };

            if (seasonal is { } season)
            {
                var months = season.Climate.Seasons;
                var temperature = new float[months];
                var precipitation = new float[months];
                var ratio = new float[months];
                var mean = season.Hydrology.MeanDischarge[c];
                for (var m = 0; m < months; m++)
                {
                    temperature[m] = season.Climate.Temperature[m][c];
                    precipitation[m] = season.Climate.Precipitation[m][c];
                    ratio[m] = mean > 0f ? season.Hydrology.Discharge[m][c] / mean : 1f;
                    if (season.Hydrology.SnowCovered[m][c]) cells[c].SnowMonths |= 1 << m;
                    if (season.Hydrology.Frozen[m][c]) cells[c].FrozenMonths |= 1 << m;
                }
                cells[c].MonthlyTemperature = ToBytes(temperature);
                cells[c].MonthlyPrecipitation = ToBytes(precipitation);
                cells[c].MonthlyDischargeRatio = ToBytes(ratio);
            }

            // 河：陸地、流量過門檻、有下游（碎島清除之後變成水的格子不算）
            if (hydrology is not null && water <= 0f && Rivers.IsRiver(hydrology.Discharge[c], minDischarge) && hydrology.FlowParent[c] >= 0)
            {
                cells[c].RiverDownstreamCellId = hydrology.FlowParent[c];
                cells[c].RiverSize = Rivers.Size(hydrology.Discharge[c], minDischarge);
                // 河寬用四季平均（有沙漠的河道損失：外來河流進沙漠會越來越窄）；沒有四季就用年平均
                var flow = seasonal?.Hydrology.MeanDischarge[c] ?? hydrology.Discharge[c];
                cells[c].RiverWidthMeters = (float)Math.Max(Rivers.MinChannelWidthMeters * 0.5, Rivers.ChannelWidthMeters(flow, minDischarge));
                if (seasonal is { } s2)
                {
                    cells[c].FlowRegime = (int)s2.Hydrology.Regime[c];
                    cells[c].BaseflowShare = s2.Hydrology.BaseflowShare[c];
                    cells[c].MeltShare = s2.Hydrology.MeltShare[c];
                }
            }
        }

        return cells;
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>把 <see cref="PlanetCell.MonthlyTemperature"/> 這類欄位還原成 float 陣列（空的回傳空陣列）。</summary>
    /// <param name="bytes">欄位的內容。</param>
    public static float[] FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
        return values;
    }

    /// <summary>海岸受浪的範圍（公尺）：看這麼遠以內的海有多少比浪基面深。</summary>
    private const double WaveExposureRadiusMeters = 100_000;

    /// <summary>浪基面（公尺），同 <see cref="FluvialErosion"/>。</summary>
    private const float WaveBaseMeters = 200f;

    /// <summary>
    /// 每一格靠海的陸地受浪的程度（0~1）：<see cref="WaveExposureRadiusMeters"/> 內的海格裡，比浪基面深的比例。
    /// 寬的大陸棚、海灣把浪的能量消耗掉；面對開闊深海的海岸浪大，硬岩削成海崖、軟的沉積物堆不住。
    /// </summary>
    private static float[] WaveExposure(SimulationGrid grid, SimulationState state)
    {
        var n = state.CellCount;
        var exposure = new float[n];
        var hops = Math.Max(1, (int)Math.Round(WaveExposureRadiusMeters / grid.AverageCellSpacingMeters));
        Parallel.For(0, n, () => (Stamp: new int[n], Id: 0, Queue: new List<int>()), (c, _, local) =>
        {
            if (state.WaterDepth[c] > 0f) return local;
            Span<int> neighbors = stackalloc int[SimulationGrid.MaxNeighborsPerCell];
            var count = grid.GetNeighbors(c, neighbors);
            var coastal = false;
            for (var k = 0; k < count && !coastal; k++) coastal = state.WaterDepth[neighbors[k]] > 0f;
            if (!coastal) return local;

            local.Id++;
            var queue = local.Queue;
            queue.Clear();
            queue.Add(c);
            local.Stamp[c] = local.Id;
            int sea = 0, deep = 0, ringStart = 0;
            for (var ring = 0; ring < hops && ringStart < queue.Count; ring++)
            {
                var ringEnd = queue.Count;
                for (var i = ringStart; i < ringEnd; i++)
                {
                    var nbCount = grid.GetNeighbors(queue[i], neighbors);
                    for (var k = 0; k < nbCount; k++)
                    {
                        var nb = neighbors[k];
                        if (local.Stamp[nb] == local.Id) continue;
                        local.Stamp[nb] = local.Id;
                        queue.Add(nb);
                        if (state.WaterDepth[nb] <= 0f) continue;
                        sea++;
                        if (state.WaterDepth[nb] > WaveBaseMeters) deep++;
                    }
                }
                ringStart = ringEnd;
            }
            exposure[c] = sea > 0 ? deep / (float)sea : 0f;
            return local;
        }, _ => { });
        return exposure;
    }

    private static (TerrainType Terrain, int HeightCategory) Classify(
        SimulationState state, int c, float elevation, bool isOcean, float wetness)
    {
        var water = state.WaterDepth[c];
        var ice = state.IceThickness[c];
        var temperature = state.Temperature[c];

        if (water > 0f)
        {
            if (ice >= GlacierIceMeters) return (TerrainType.Glacier, 0);
            if (state.OrganicThickness[c] > 0f && water <= ReefMaxDepthMeters) return (TerrainType.CoralReef, 0);
            return (isOcean ? TerrainType.Seawater : TerrainType.Freshwater, WaterDepthCategory(water));
        }

        if (ice >= GlacierIceMeters) return (TerrainType.Glacier, 0);
        if (temperature < -5f) return (TerrainType.SnowField, 0);
        if (temperature < 2f) return (TerrainType.Tundra, 0);
        if (elevation >= RockAboveMeters) return (TerrainType.Rock, 0);
        const float shrubTo = DesertShare + ShrubShare;
        const float grassTo = shrubTo + GrassShare;
        if (wetness < DesertShare) return (TerrainType.Sand, 0);
        if (wetness >= 1f - SwampShare && elevation < 20f) return (TerrainType.Swamp, 0);
        if (wetness < shrubTo) return (TerrainType.Shrubland, Bucket(wetness, DesertShare, shrubTo, 4));
        if (wetness < grassTo) return (TerrainType.Grassland, Bucket(wetness, shrubTo, grassTo, 5));

        // 林地：HeightCategory = 高度(0~3)×2 + 疏密，見 TerrainType.Forest
        var height = Bucket(wetness, grassTo, 1f, 4);
        var dense = wetness >= (grassTo + 1f) / 2 ? 1 : 0;
        return (TerrainType.Forest, height * 2 + dense);
    }

    private static int WaterDepthCategory(float depth)
    {
        for (var i = 0; i < WaterDepthBounds.Length; i++)
        {
            if (depth < WaterDepthBounds[i]) return i;
        }
        return WaterDepthBounds.Length;
    }

    private static int Bucket(float value, float from, float to, int count) =>
        Math.Clamp((int)((value - from) / (to - from) * count), 0, count - 1);

    /// <summary>
    /// 每一格陸地的降水在全部陸地裡的百分位（0 最乾、接近 1 最濕）；水格是 0，用不到。
    /// 同樣的降水量拿到同樣的百分位（取第一個出現的排名），結果才不會受格子順序影響。
    /// </summary>
    private static float[] LandPrecipitationRank(SimulationState state)
    {
        var rank = new float[state.CellCount];
        var land = Enumerable.Range(0, state.CellCount).Where(c => state.WaterDepth[c] <= 0f).ToArray();
        if (land.Length == 0) return rank;

        var sorted = land.Select(c => state.Precipitation[c]).ToArray();
        Array.Sort(sorted);
        foreach (var c in land)
        {
            var index = Array.BinarySearch(sorted, state.Precipitation[c]);
            while (index > 0 && sorted[index - 1] == sorted[index]) index--;
            rank[c] = (float)index / land.Length;
        }
        return rank;
    }

    /// <summary>最大的那一片連通水域是主要大洋；其他水域是湖或內海。</summary>
    private static bool[] MainOceanMask(SimulationGrid grid, SimulationState state)
    {
        var label = new int[state.CellCount];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < state.CellCount; start++)
        {
            if (label[start] >= 0 || state.WaterDepth[start] <= 0f) continue;
            var id = sizes.Count;
            var size = 0;
            label[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                size++;
                var baseIndex = cell * SimulationGrid.MaxNeighborsPerCell;
                for (var s = 0; s < SimulationGrid.MaxNeighborsPerCell; s++)
                {
                    var neighbor = grid.Neighbors[baseIndex + s];
                    if (neighbor == SimulationGrid.NoNeighbor) continue;
                    if (label[neighbor] >= 0 || state.WaterDepth[neighbor] <= 0f) continue;
                    label[neighbor] = id;
                    stack.Push(neighbor);
                }
            }
            sizes.Add(size);
        }

        var mask = new bool[state.CellCount];
        if (sizes.Count == 0) return mask;
        var main = sizes.IndexOf(sizes.Max());
        for (var c = 0; c < state.CellCount; c++) mask[c] = label[c] == main;
        return mask;
    }
}
