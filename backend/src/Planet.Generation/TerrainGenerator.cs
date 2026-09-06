using Planet.Domain;

namespace Planet.Generation;

/// <summary>
/// 程序化地形生成（18種地表覆蓋類型）。整體流程：
/// 1. 用「兩個大陸中心（各自獨立南北/東西falloff半徑，見 <see cref="TerrainGenerationOptions"/>）＋noise海岸線」
///    決定陸地/海洋。
/// 2. 海洋範圍內另外散布小島（一部分單獨散布、一部分群聚成類似群島的效果，各自獨立的圓形falloff＋
///    grid空間索引加速查詢），依 <see cref="TerrainGenerationOptions.IslandVolcanicProbability"/> 決定
///    是火山地形還是一般岩地。
/// 3. 海拔用noise決定，換算成 -500~+500 的 ElevationLevel（每級5公尺，見 Tile.ElevationLevel 的說明）。
/// 4. 陸地依緯度（雪線/凍原）、海拔（雪線、岩地）、濕度noise（沙漠/沼澤/植被）、岩性noise（礫灘/懸崖）、
///    稀有特徵noise（火山岩/鹽鹼地/珊瑚礁）依序判斷出最終地形；海洋依緯度/深度決定海水/冰河/珊瑚礁，
///    落在小島範圍內則改成小島地形（優先於一般海洋判斷，但極區冰原範圍內不生小島）。
/// 5. 部分地形（草地/灌木叢/林地/淡水/海水）另外用一組獨立的noise決定 HeightCategory（表面覆蓋物的
///    高度/深度細分等級），這跟 ElevationLevel（地形本身多高）是兩個獨立的軸。
/// 用單位球方向向量取樣（而非直接對經緯度取樣），避免noise在極區/換日線出現接縫或扭曲。
/// </summary>
public class TerrainGenerator : ITerrainGenerator
{
    // 海拔等級範圍：-500~+500，每級5公尺（見 docs/architecture.md 的推導）。
    private const int MaxElevationLevel = 500;

    private const double ContinentCoreRadiusDegrees = 35.0;
    private const double ContinentEdgeRadiusDegrees = 70.0;
    // 大陸南北拉長注意事項：把falloff半徑依方位角橢圓縮放時，南北方向的縮放倍率不能設太大——
    // 球面上falloff半徑一旦接近90度，覆蓋面積會非線性暴增（球冠面積∝1-cos(半徑)，超過90度就蓋過半顆星球），
    // 實測南北倍率設1.6時兩塊大陸直接融合成橫跨170度緯度、359度經度的單一巨大陸塊，完全不是預期的
    // 「兩塊獨立大陸」。南北方向倍率建議維持接近1.0（安全，不會比原始圓形falloff更容易融合），
    // 東西方向倍率可以自由調小做出瘦長外觀、調大做出寬闊外觀（純粹縮放面積，沒有球面幾何的暴增風險）。
    private const double CoastNoiseFrequency = 2.2;
    private const double CoastNoiseAmplitude = 0.4;
    private const double LandThreshold = 0.5;

    // 海中小島：獨立於大陸之外，用顯式的島嶼中心點（而非noise門檻）確保數量大致可控，
    // 一部分單獨散布、一部分群聚成類似群島的效果（見 GenerateIslands）。
    private const double IslandMinRadiusDegrees = 0.15;
    private const double IslandMaxRadiusDegrees = 0.5;
    private const int IslandClusterMinSize = 2;
    private const int IslandClusterMaxSize = 6;
    private const double IslandClusterSpreadDegrees = 3.0; // 群聚裡的衛星小島，離群聚中心最多幾度
    // 小島峰頂海拔等級。注意：SampleIsland用線性半徑衰減（中心=peak、邊緣=海平面附近），對一個圓盤而言
    // 面積加權平均高度只有peak的1/3左右（大部分面積集中在半徑外側、海拔較低處），所以這個常數要設得
    // 比「島嶼平均海拔的目標值」高2~3倍，不能只看峰頂數字直覺覺得「這樣是不是太高了」。
    // 實測踩過的坑：一開始設220（約1100公尺），單元測試Sample_RockLikeTerrain_HasHigherElevationThanVegetatedLand
    // 開始失敗——島嶼的低海拔Rock/VolcanicRock樣本把「岩性地形平均海拔」拉到低於「植被地形平均海拔」，
    // 因為這個測試假設所有岩性地形都是大陸山地等級的高海拔。改到480（火山島特別陡峭陡升，現實中像夏威夷
    // 這類火山島從海床量起本來就非常高，這個數字對「火山地形」來說有敘事上的合理性）。
    private const int IslandPeakElevationLevel = 480;
    private const double IslandMinDistanceFromContinentFalloff = 0.02; // 大陸falloff值低於這個門檻才算「夠遠離大陸」
    private const double IslandGridCellDegrees = 10.0; // 空間索引網格大小，島嶼半徑遠小於這個值，查詢時查3x3鄰近格就夠
    private const int IslandGridLonCellCount = 36; // = 360 / IslandGridCellDegrees，經度網格要處理±180接縫wrap-around

    private const double ElevationFrequency = 3.0;

    private const double RiverFrequency = 6.0;
    private const double RiverBandHalfWidth = 0.012;

    private const double LakeFrequency = 9.0;
    private const double LakeThreshold = 0.82;

    private const double MoistureFrequency = 2.6;
    private const double RockFrequency = 5.0;
    private const double SpecialFrequency = 12.0;
    private const double CoverFrequency = 8.0;

    // 緯度分帶（度）。
    private const double GlacierLatitude = 82.0;   // 更靠近極點：冰原/冰河（陸地或海面都算）
    private const double SnowFieldLatitude = 70.0;  // 雪線以上緯度：雪地
    private const double TundraLatitudeMin = 55.0;  // 55~70度：苔原

    // 海拔分界（等級，5公尺/級）。
    // 注意：陸地海拔noise（4個octave的FBM）疊加後不是均勻分布，而是像常態分布一樣集中在中間值附近
    // （實測seed=42時，中位數落在約220~240級，<=80級的只占陸地約0.2%）。低地地形的門檻要照這個實際
    // 分布校準，不能直接假設0~500是均勻分布，不然LowLandMaxLevel設太低（例如原本的80）會讓沼澤/鹽鹼地
    // 幾乎不可能出現（2000萬格全星球生成後實測是完全0筆，才發現這個問題）。
    private const int SnowLineElevationLevel = 400;   // 陸地海拔夠高，不分緯度都算雪線以上
    private const int RockElevationLevel = 320;       // 岩地/懸崖/火山岩地的海拔下限（實測約佔陸地14.7%，還算合理）
    private const int LowLandMaxLevel = 190;          // 沼澤/鹽鹼地/礫灘等低地地形的海拔上限（實測約佔陸地22~30%）
    private const int MudMaxElevationLevel = 260;     // 泥地門檻較寬鬆（約佔陸地55%），跟LowLandMaxLevel分開設定

    private const double DesertMoistureThreshold = -0.35;
    private const double SwampMoistureThreshold = 0.55;
    private const double MudMoistureThreshold = 0.30;
    private const double ForestMoistureThreshold = 0.15;
    private const double ShrublandMoistureThreshold = -0.05;

    private const double CoralReefLatitudeMax = 28.0;
    private const double CoralReefDepthFraction = 0.22; // 淺海才有珊瑚礁

    private readonly TerrainGenerationOptions _options;

    private readonly SimplexNoise3D _coastNoise;
    private readonly SimplexNoise3D _elevationNoise;
    private readonly SimplexNoise3D _riverNoise;
    private readonly SimplexNoise3D _lakeNoise;
    private readonly SimplexNoise3D _moistureNoise;
    private readonly SimplexNoise3D _rockNoise;
    private readonly SimplexNoise3D _specialNoise;
    private readonly SimplexNoise3D _coverNoise;

    private readonly Vec3 _continentCenterA;
    private readonly Vec3 _continentCenterB;
    private readonly (Vec3 North, Vec3 East) _continentBasisA;
    private readonly (Vec3 North, Vec3 East) _continentBasisB;

    /// <summary>單一島嶼：中心點、半徑（度）、是否為火山地形（否則為一般岩地）。</summary>
    private readonly record struct Island(Vec3 Center, double RadiusDegrees, bool IsVolcanic);

    private readonly Island[] _islands;
    private readonly Dictionary<(int LatCell, int LonCell), List<int>> _islandGrid;

    /// <summary>用預設參數＋指定種子建立地形生成器（供既有呼叫端／測試沿用，等同於 <c>TerrainGenerationOptions.Default with { Seed = seed }</c>）。</summary>
    /// <param name="seed">地形亂數種子。</param>
    public TerrainGenerator(int seed) : this(TerrainGenerationOptions.Default with { Seed = seed })
    {
    }

    /// <summary>建立地形生成器：初始化8組獨立noise通道、2個大陸中心點、小島。同一組參數必定產生同樣的地形。</summary>
    /// <param name="options">地形生成參數（種子、大陸形狀、小島數量等）。</param>
    public TerrainGenerator(TerrainGenerationOptions options)
    {
        _options = options;
        var seed = options.Seed;

        _coastNoise = new SimplexNoise3D(seed);
        _elevationNoise = new SimplexNoise3D(seed + 1);
        _riverNoise = new SimplexNoise3D(seed + 2);
        _lakeNoise = new SimplexNoise3D(seed + 3);
        _moistureNoise = new SimplexNoise3D(seed + 4);
        _rockNoise = new SimplexNoise3D(seed + 5);
        _specialNoise = new SimplexNoise3D(seed + 6);
        _coverNoise = new SimplexNoise3D(seed + 7);

        var rng = new Random(seed + 100);
        _continentCenterA = RandomPointAwayFromPoles(rng, maxAbsLatitudeDegrees: 55.0);
        do
        {
            _continentCenterB = RandomPointAwayFromPoles(rng, maxAbsLatitudeDegrees: 55.0);
        } while (AngularDistanceDegrees(_continentCenterA, _continentCenterB) < 110.0);

        _continentBasisA = TangentBasisAt(_continentCenterA);
        _continentBasisB = TangentBasisAt(_continentCenterB);

        _islands = GenerateIslands(new Random(seed + 200));
        _islandGrid = BuildIslandGrid(_islands);
    }

    /// <inheritdoc />
    public TerrainSample Sample(double latitude, double longitude)
    {
        var point = ToUnitVector(latitude, longitude);
        var absLatitude = Math.Abs(latitude);

        var falloff = ContinentFalloff(point);
        var coast = _coastNoise.Fbm(point.X, point.Y, point.Z, octaves: 4, baseFrequency: CoastNoiseFrequency);
        var landScore = falloff + coast * CoastNoiseAmplitude;

        if (landScore < LandThreshold)
        {
            // 小島只長在一般海域（極區冰原範圍內不生小島，維持極區整片冰封的觀感）。
            if (absLatitude < GlacierLatitude && FindIsland(latitude, longitude, point) is { } island)
            {
                return SampleIsland(point, island);
            }
            return SampleOcean(point, absLatitude, landScore);
        }

        return SampleLand(point, latitude, absLatitude);
    }

    /// <summary>兩個大陸（各自獨立南北/東西falloff半徑）中較大的那個falloff值，落地判斷跟避開大陸放小島都用這個。</summary>
    private double ContinentFalloff(Vec3 point) =>
        Math.Max(
            EllipticalContinentFalloff(point, _continentCenterA, _continentBasisA, _options.ContinentALatitudeStretch, _options.ContinentALongitudeStretch),
            EllipticalContinentFalloff(point, _continentCenterB, _continentBasisB, _options.ContinentBLatitudeStretch, _options.ContinentBLongitudeStretch));

    /// <summary>
    /// 大陸falloff，但依方位角（相對大陸中心，0=正北）橢圓縮放coreRadius/edgeRadius，做出南北/東西比例不對稱的形狀：
    /// 正北/正南方向（bearing=0/180）用 latitudeStretch 縮放；正東/正西方向（bearing=90/270）用
    /// longitudeStretch 縮放；其他角度依橢圓極座標公式內插。
    /// </summary>
    private static double EllipticalContinentFalloff(Vec3 point, Vec3 center, (Vec3 North, Vec3 East) basis, double latitudeStretch, double longitudeStretch)
    {
        var distance = AngularDistanceDegrees(point, center);
        var bearingRad = BearingRadians(point, center, basis.North, basis.East);
        var cosB = Math.Cos(bearingRad);
        var sinB = Math.Sin(bearingRad);
        var stretch = 1.0 / Math.Sqrt(
            cosB * cosB / (latitudeStretch * latitudeStretch) +
            sinB * sinB / (longitudeStretch * longitudeStretch));

        return SmoothFalloff(distance, ContinentCoreRadiusDegrees * stretch, ContinentEdgeRadiusDegrees * stretch);
    }

    /// <summary>海洋地塊的地形判斷：極區為冰河，低緯度淺海有機率長珊瑚礁，其餘為一般海水。</summary>
    private TerrainSample SampleOcean(Vec3 point, double absLatitude, double landScore)
    {
        var oceanness = Math.Clamp((LandThreshold - landScore) / LandThreshold, 0, 1);
        var elevationLevel = -(int)Math.Round(30 + oceanness * (MaxElevationLevel - 30));

        if (absLatitude >= GlacierLatitude)
        {
            return new TerrainSample(TerrainType.Glacier, 0, Math.Max(elevationLevel, -60));
        }

        if (absLatitude <= CoralReefLatitudeMax && oceanness < CoralReefDepthFraction)
        {
            var coralRoll = _specialNoise.Sample(point.X * SpecialFrequency, point.Y * SpecialFrequency, point.Z * SpecialFrequency);
            if (coralRoll > 0.55)
            {
                return new TerrainSample(TerrainType.CoralReef, WaterDepthCategory(0), elevationLevel);
            }
        }

        return new TerrainSample(TerrainType.Seawater, WaterDepthCategory(oceanness), elevationLevel);
    }

    /// <summary>
    /// 陸地地塊的地形判斷：依序檢查冰河 → 淡水（河流/湖泊）→ 雪地 → 高海拔岩性地形（岩地/懸崖/火山岩）→
    /// 乾燥地形（沙地/鹽鹼地）→ 低地濕地（沼澤）→ 苔原 → 低地岩性（礫灘）→ 泥地 → 黃土地 →
    /// 植被（依濕度分林地/灌木叢/草地三級，各自再用cover noise決定HeightCategory）。
    /// </summary>
    private TerrainSample SampleLand(Vec3 point, double latitude, double absLatitude)
    {
        var elevationRaw = _elevationNoise.Fbm(point.X, point.Y, point.Z, octaves: 4, baseFrequency: ElevationFrequency);
        var elevationLevel = (int)Math.Round(Math.Clamp((elevationRaw + 1) / 2, 0, 1) * MaxElevationLevel);

        if (absLatitude >= GlacierLatitude)
        {
            return new TerrainSample(TerrainType.Glacier, 0, elevationLevel);
        }

        // 淡水（河流/湖泊）：河流是noise細窄帶，湖泊是noise閾值以上的離散水塘，兩者現在都算Freshwater，
        // 差別只在HeightCategory（河較淺、湖較深）。
        var river = _riverNoise.Fbm(point.X, point.Y, point.Z, octaves: 2, baseFrequency: RiverFrequency);
        if (Math.Abs(river) < RiverBandHalfWidth)
        {
            return new TerrainSample(TerrainType.Freshwater, WaterDepthCategory(0.15), elevationLevel);
        }

        var lake = _lakeNoise.Sample(point.X * LakeFrequency, point.Y * LakeFrequency, point.Z * LakeFrequency);
        if (lake > LakeThreshold)
        {
            var lakeDepthFraction = Math.Clamp((lake - LakeThreshold) / (1 - LakeThreshold), 0, 1);
            return new TerrainSample(TerrainType.Freshwater, WaterDepthCategory(0.4 + lakeDepthFraction * 0.6), elevationLevel);
        }

        var isSnowZone = absLatitude >= SnowFieldLatitude || elevationLevel >= SnowLineElevationLevel;
        if (isSnowZone)
        {
            return new TerrainSample(TerrainType.SnowField, 0, elevationLevel);
        }

        var rockiness = _rockNoise.Fbm(point.X, point.Y, point.Z, octaves: 3, baseFrequency: RockFrequency);
        var special = _specialNoise.Sample(point.X * SpecialFrequency, point.Y * SpecialFrequency, point.Z * SpecialFrequency);

        if (elevationLevel >= RockElevationLevel)
        {
            if (special > 0.75) return new TerrainSample(TerrainType.VolcanicRock, 0, elevationLevel);
            if (rockiness > 0.6) return new TerrainSample(TerrainType.Cliff, 0, elevationLevel);
            return new TerrainSample(TerrainType.Rock, 0, elevationLevel);
        }

        var moisture = _moistureNoise.Fbm(point.X, point.Y, point.Z, octaves: 3, baseFrequency: MoistureFrequency);

        if (moisture < DesertMoistureThreshold)
        {
            if (elevationLevel <= LowLandMaxLevel && special > 0.8)
            {
                return new TerrainSample(TerrainType.SaltFlat, 0, elevationLevel);
            }
            return new TerrainSample(TerrainType.Sand, 0, elevationLevel);
        }

        if (elevationLevel <= LowLandMaxLevel && moisture > SwampMoistureThreshold)
        {
            return new TerrainSample(TerrainType.Swamp, 0, elevationLevel);
        }

        if (absLatitude >= TundraLatitudeMin)
        {
            return new TerrainSample(TerrainType.Tundra, 0, elevationLevel);
        }

        if (elevationLevel <= LowLandMaxLevel && rockiness > 0.55)
        {
            return new TerrainSample(TerrainType.Gravel, 0, elevationLevel);
        }

        if (moisture > MudMoistureThreshold && elevationLevel <= MudMaxElevationLevel)
        {
            return new TerrainSample(TerrainType.Mud, 0, elevationLevel);
        }

        if (moisture is > -0.05 and < 0.15 && special is > 0.3 and < 0.6)
        {
            return new TerrainSample(TerrainType.Loess, 0, elevationLevel);
        }

        var cover = _coverNoise.Fbm(point.X, point.Y, point.Z, octaves: 2, baseFrequency: CoverFrequency);
        var coverFraction = Math.Clamp((cover + 1) / 2, 0, 1);

        if (moisture > ForestMoistureThreshold)
        {
            return new TerrainSample(TerrainType.Forest, ForestHeightCategory(coverFraction, moisture), elevationLevel);
        }

        if (moisture > ShrublandMoistureThreshold)
        {
            return new TerrainSample(TerrainType.Shrubland, BucketCategory(coverFraction, 4), elevationLevel);
        }

        return new TerrainSample(TerrainType.Grassland, BucketCategory(coverFraction, 5), elevationLevel);
    }

    /// <summary>林地 HeightCategory = 高度(0~3：人高/一層樓/二層樓/三層樓) × 2 + 疏密(0疏/1密)，共0~7。</summary>
    private static int ForestHeightCategory(double coverFraction, double moisture)
    {
        var heightIndex = BucketCategory(coverFraction, 4); // 0~3
        var isDense = moisture > 0.5; // 濕度越高，林相越密
        return heightIndex * 2 + (isDense ? 1 : 0);
    }

    /// <summary>淡水/海水的深度等級（0~6），depthFraction越大代表越深。</summary>
    private static int WaterDepthCategory(double depthFraction) => BucketCategory(depthFraction, 7);

    /// <summary>把[0,1]區間的分數切成 categoryCount 個等寬桶，回傳落在第幾桶（0-based，含邊界保護）。</summary>
    private static int BucketCategory(double fraction01, int categoryCount)
    {
        var index = (int)Math.Floor(Math.Clamp(fraction01, 0, 0.9999) * categoryCount);
        return Math.Clamp(index, 0, categoryCount - 1);
    }

    /// <summary>把經緯度換算成單位球面上的3D方向向量（noise取樣用，避免經緯度網格在極區/換日線出現接縫）。</summary>
    private static Vec3 ToUnitVector(double latitudeDegrees, double longitudeDegrees)
    {
        var lat = latitudeDegrees * (Math.PI / 180.0);
        var lon = longitudeDegrees * (Math.PI / 180.0);
        var cosLat = Math.Cos(lat);
        return new Vec3(cosLat * Math.Cos(lon), cosLat * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>兩個單位球面方向向量之間的夾角（度），即大圓角距離。</summary>
    private static double AngularDistanceDegrees(Vec3 a, Vec3 b) =>
        Math.Acos(Math.Clamp(a.Dot(b), -1.0, 1.0)) * (180.0 / Math.PI);

    /// <summary>coreRadius內回傳1，edgeRadius外回傳0，之間用smoothstep平滑過渡。</summary>
    private static double SmoothFalloff(double angularDistanceDegrees, double coreRadius, double edgeRadius)
    {
        if (angularDistanceDegrees <= coreRadius) return 1.0;
        if (angularDistanceDegrees >= edgeRadius) return 0.0;

        var t = (edgeRadius - angularDistanceDegrees) / (edgeRadius - coreRadius);
        return t * t * (3 - 2 * t);
    }

    /// <summary>在單位球面上均勻隨機取一點，但排除緯度超過 maxAbsLatitudeDegrees 的極區（避免大陸中心落在極點附近）。</summary>
    private static Vec3 RandomPointAwayFromPoles(Random rng, double maxAbsLatitudeDegrees)
    {
        while (true)
        {
            var z = rng.NextDouble() * 2 - 1;
            var latitudeDegrees = Math.Asin(Math.Clamp(z, -1, 1)) * (180.0 / Math.PI);
            if (Math.Abs(latitudeDegrees) > maxAbsLatitudeDegrees) continue;

            var theta = rng.NextDouble() * 2 * Math.PI;
            var r = Math.Sqrt(Math.Max(0, 1 - z * z));
            return new Vec3(r * Math.Cos(theta), r * Math.Sin(theta), z);
        }
    }

    /// <summary>
    /// 算出centerAt這一點的當地切平面上「正北」「正東」單位向量，供 <see cref="BearingRadians"/> 用來
    /// 算方位角。用全域極軸（本類別用Z當極軸，見 <see cref="ToUnitVector"/>）投影到切平面取得north；
    /// east＝north×center（這個叉積順序在Z-up座標系下才會正確指向東方，已用赤道上的已知點手algebra算過：
    /// 在(1,0,0)這點，north=(0,0,1)、east應該是(0,1,0)，cross((0,0,1),(1,0,0))=(0,1,0)驗證正確；
    /// 相反順序center×north會得到西方向，別搞反）。
    /// </summary>
    private static (Vec3 North, Vec3 East) TangentBasisAt(Vec3 center)
    {
        var globalPole = new Vec3(0, 0, 1);
        var north = globalPole - center * globalPole.Dot(center);
        if (north.Length < 1e-8)
        {
            // center幾乎就在極點上（正負Z），跟極軸平行，改用固定參考方向投影。
            var reference = new Vec3(1, 0, 0);
            north = reference - center * center.Dot(reference);
        }
        north = north.Normalized();
        var east = north.Cross(center).Normalized();
        return (north, east);
    }

    /// <summary>point相對center的方位角（弧度，0=正北，順時針增加，跟一般羅盤方位角同義）。</summary>
    private static double BearingRadians(Vec3 point, Vec3 center, Vec3 north, Vec3 east)
    {
        var tangentOffset = point - center * point.Dot(center);
        var northComponent = tangentOffset.Dot(north);
        var eastComponent = tangentOffset.Dot(east);
        return Math.Atan2(eastComponent, northComponent);
    }

    /// <summary>把單位球面上的3D座標換算成經緯度（度），供小島的空間索引網格、群聚小島的偏移計算用。</summary>
    private static (double Latitude, double Longitude) ToLatLon(Vec3 point)
    {
        var latitude = Math.Asin(Math.Clamp(point.Z, -1.0, 1.0)) * (180.0 / Math.PI);
        var longitude = Math.Atan2(point.Y, point.X) * (180.0 / Math.PI);
        return (latitude, longitude);
    }

    /// <summary>
    /// 從origin出發，沿著方位角bearingRadians（0=正北，順時針）前進distanceDegrees度（大圓距離）後的落點。
    /// 用標準的球面「已知起點/方位角/距離求終點」公式，供群聚小島把衛星小島散布在群聚中心附近用。
    /// </summary>
    private static Vec3 OffsetPoint(Vec3 origin, double bearingRadians, double distanceDegrees)
    {
        var (originLatDeg, originLonDeg) = ToLatLon(origin);
        var lat1 = originLatDeg * (Math.PI / 180.0);
        var lon1 = originLonDeg * (Math.PI / 180.0);
        var delta = distanceDegrees * (Math.PI / 180.0);

        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(delta) + Math.Cos(lat1) * Math.Sin(delta) * Math.Cos(bearingRadians));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearingRadians) * Math.Sin(delta) * Math.Cos(lat1),
            Math.Cos(delta) - Math.Sin(lat1) * Math.Sin(lat2));

        return ToUnitVector(lat2 * (180.0 / Math.PI), lon2 * (180.0 / Math.PI));
    }

    /// <summary>經緯度所在的空間索引網格格子。經度方向做wrap-around正規化，緯度方向不用（極區本來就没有小島）。</summary>
    private static (int LatCell, int LonCell) IslandCellOf(double latitudeDegrees, double longitudeDegrees)
    {
        var latCell = (int)Math.Floor(latitudeDegrees / IslandGridCellDegrees);
        var lonCellRaw = (int)Math.Floor(longitudeDegrees / IslandGridCellDegrees);
        var lonCell = ((lonCellRaw % IslandGridLonCellCount) + IslandGridLonCellCount) % IslandGridLonCellCount;
        return (latCell, lonCell);
    }

    /// <summary>
    /// 生成 <see cref="TerrainGenerationOptions.IslandCount"/> 個小島（依 <see cref="TerrainGenerationOptions.IslandClusterRatio"/>
    /// 分成兩批）：一批單獨散布（各自獨立找一個離大陸夠遠的位置），一批群聚（先找一個群聚中心，
    /// 再圍著它散布 <see cref="IslandClusterMinSize"/>~<see cref="IslandClusterMaxSize"/> 個衛星小島，
    /// 形成類似群島的效果）。半徑在[<see cref="IslandMinRadiusDegrees"/>, <see cref="IslandMaxRadiusDegrees"/>]之間隨機，
    /// 依 <see cref="TerrainGenerationOptions.IslandVolcanicProbability"/> 決定是火山地形還是一般岩地。
    /// 刻意不檢查散布小島彼此是否重疊——少數小島剛好靠在一起也是自然的效果，不算bug。
    /// </summary>
    private Island[] GenerateIslands(Random rng)
    {
        var totalCount = _options.IslandCount;
        var clusterTarget = (int)Math.Round(totalCount * Math.Clamp(_options.IslandClusterRatio, 0, 1));
        var scatteredTarget = totalCount - clusterTarget;

        var islands = new List<Island>(totalCount);
        var maxAttempts = Math.Max(totalCount, 100) * 50; // 避免大陸範圍設太大導致理論上找不到足夠空間時無窮迴圈
        var attempts = 0;

        while (islands.Count < scatteredTarget && attempts < maxAttempts)
        {
            attempts++;
            var candidate = RandomPointAwayFromPoles(rng, maxAbsLatitudeDegrees: 80.0);
            if (ContinentFalloff(candidate) > IslandMinDistanceFromContinentFalloff) continue;
            islands.Add(CreateIsland(candidate, rng));
        }

        while (islands.Count < totalCount && attempts < maxAttempts)
        {
            attempts++;
            var clusterCenter = RandomPointAwayFromPoles(rng, maxAbsLatitudeDegrees: 80.0);
            if (ContinentFalloff(clusterCenter) > IslandMinDistanceFromContinentFalloff) continue;

            var clusterSize = Math.Min(totalCount - islands.Count, rng.Next(IslandClusterMinSize, IslandClusterMaxSize + 1));
            for (var i = 0; i < clusterSize; i++)
            {
                var bearingRad = rng.NextDouble() * 2 * Math.PI;
                var distanceDegrees = rng.NextDouble() * IslandClusterSpreadDegrees;
                var satellite = OffsetPoint(clusterCenter, bearingRad, distanceDegrees);
                if (ContinentFalloff(satellite) > IslandMinDistanceFromContinentFalloff) continue; // 剛好偏移進大陸範圍就跳過這一個

                islands.Add(CreateIsland(satellite, rng));
            }
        }

        return islands.ToArray();
    }

    private Island CreateIsland(Vec3 center, Random rng)
    {
        var radius = IslandMinRadiusDegrees + rng.NextDouble() * (IslandMaxRadiusDegrees - IslandMinRadiusDegrees);
        var isVolcanic = rng.NextDouble() < _options.IslandVolcanicProbability;
        return new Island(center, radius, isVolcanic);
    }

    /// <summary>把每個島嶼登記到它所在的空間索引網格格子，供 <see cref="FindIsland"/> 查詢用。</summary>
    private static Dictionary<(int, int), List<int>> BuildIslandGrid(Island[] islands)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < islands.Length; i++)
        {
            var (lat, lon) = ToLatLon(islands[i].Center);
            var cell = IslandCellOf(lat, lon);
            if (!grid.TryGetValue(cell, out var list))
            {
                list = [];
                grid[cell] = list;
            }
            list.Add(i);
        }
        return grid;
    }

    /// <summary>
    /// 查詢(latitude,longitude)這個點是否落在某個小島範圍內。只查該點所在網格格子＋周圍3x3共9格
    /// （島嶼半徑遠小於網格大小，這樣查一定不會漏掉邊界上的島），不用對全部島嶼逐一算距離。
    /// </summary>
    private Island? FindIsland(double latitude, double longitude, Vec3 point)
    {
        var (baseLatCell, baseLonCell) = IslandCellOf(latitude, longitude);

        for (var dLat = -1; dLat <= 1; dLat++)
        {
            for (var dLon = -1; dLon <= 1; dLon++)
            {
                var lonCell = ((baseLonCell + dLon) % IslandGridLonCellCount + IslandGridLonCellCount) % IslandGridLonCellCount;
                if (!_islandGrid.TryGetValue((baseLatCell + dLat, lonCell), out var candidates)) continue;

                foreach (var index in candidates)
                {
                    var island = _islands[index];
                    if (AngularDistanceDegrees(point, island.Center) <= island.RadiusDegrees)
                    {
                        return island;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 小島地形：中心是海拔最高的「火山峰頂」（或岩地最高點），往邊緣線性下降到接近海平面，
    /// 邊緣至少保留5級（25公尺）海拔，確保島嶼邊界的取樣點還是明確的陸地，不會意外變回海平面下。
    /// </summary>
    private static TerrainSample SampleIsland(Vec3 point, Island island)
    {
        var distance = AngularDistanceDegrees(point, island.Center);
        var peakFraction = Math.Clamp(1.0 - distance / island.RadiusDegrees, 0, 1);
        var elevationLevel = Math.Max(5, (int)Math.Round(peakFraction * IslandPeakElevationLevel));
        var terrain = island.IsVolcanic ? TerrainType.VolcanicRock : TerrainType.Rock;
        return new TerrainSample(terrain, 0, elevationLevel);
    }
}
