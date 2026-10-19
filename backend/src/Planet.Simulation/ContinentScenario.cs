namespace Planet.Simulation;

/// <summary>
/// 大陸配置的劇本（v2 方向 2，2026-10-18 使用者的草圖與說明）：照時間表控制陸地板塊的運動；太平洋板塊固定不動、不會被消耗，其他海洋板塊交給動力學。
/// 配置見 <see cref="ContinentLayout"/>，參數（速度、時間）見 <see cref="ContinentLayoutParameters"/>。
///
/// | 板塊 | 運動 |
/// |---|---|
/// | A 北 | 全程往西（跟太平洋板塊相撞，西岸隱沒堆出山脈） |
/// | A 南 | 跟著 A 北，再以西側相連處為支點慢慢轉開（東側的海灣張開，西側一直連著） |
/// | D | 裂開之前是 A 南的一部分；裂開後往遠離 A 南的方向推開 |
/// | C | 全程往東北（擠向太平洋板塊，東北側長出高地與山） |
/// | B | 跟著 C，再往東北相對靠近；撞上 C 之後改成慢慢推到劇本結束（高山才不會被重力擴展削平） |
/// | F | 裂開之前是 B 的一部分；裂開後往遠離 B 的方向推開 |
/// | E、小島群 | 裂開之前是 C 的一部分（E 連同周圍的海）；裂開後貼著太平洋的西緣往正南滑出去（小島群一半速度，落在兩者之間），裂谷期結束後停住 |
/// | 太平洋 | 固定不動：A 北與 C 撞上它時被吃掉的是大陸前緣，厚度堆成山；A 南那一段是太平洋隱沒進去（見 <see cref="IsUnconsumable"/>） |
///
/// **大島裂開之前不是獨立的板塊**（2026-10-18 使用者指出：C 與 E 之間一開始不該有高山）：起始時它們的格子歸給母大陸的板塊，
/// 自己的板塊編號休眠；到裂開的那一刻才沿草圖的切線切成新板塊（<see cref="DueSplits"/>）。
/// 之前讓它們一開始就是獨立板塊、速度照抄母大陸，切線上的板塊邊界會被碰撞規則的雜訊堆出山。
///
/// 撞上的時間用碰撞規則實際偵測（<see cref="BContactMyr"/>），不用距離估。
/// 劇本結束時 B 焊進 C、A 南焊進 A 北，之後交給動力學、開始檢查停止條件。
/// 大島與小島群永遠不准自動焊回母大陸；劇本期間受控板塊彼此之間也不准自動焊接。
///
/// 劇本的狀態只有「現在幾百萬年」與「各板塊有沒有格子」，都在存檔裡；撞上的時間不存，續跑時接觸中的兩塊下一步立刻又會偵測到。
/// </summary>
public sealed class ContinentScenario
{
    private readonly ContinentLayoutParameters _p;
    private readonly double _radiusMeters;
    private readonly int[] _plate;
    private readonly int[] _label;
    private readonly bool[] _gap;
    private readonly int[] _oceanJoin;
    private readonly (double X, double Y, double Z) _hinge;
    private readonly double _hingeSign;
    private readonly HashSet<(int, int)> _scripted = [];
    private readonly HashSet<(int, int)> _neverWeld = [];
    private readonly (int LandMass, int Parent, double Start)[] _splits;

    /// <param name="parameters">配置與劇本參數。</param>
    /// <param name="plateOfLandMass">每塊陸地之後要用的板塊（<see cref="ContinentLayout.ApplyTo"/> 的回傳值）。</param>
    /// <param name="layout">配置（每格的陸塊、A 南北轉開的支點）。</param>
    /// <param name="radiusMeters">星球半徑。</param>
    internal ContinentScenario(ContinentLayoutParameters parameters, int[] plateOfLandMass, ContinentLayoutResult layout, double radiusMeters)
    {
        _p = parameters;
        _radiusMeters = radiusMeters;
        _plate = plateOfLandMass;
        _label = layout.Label;
        _gap = layout.RiftGap;
        _oceanJoin = layout.OceanJoin;
        _hinge = layout.Hinge;

        // 支點轉開的方向：讓支點東邊一點點的地方往南走（東側的海灣張開、A 南往南轉離 A 北）
        var east = ContinentLayout.Normalize(ContinentLayout.Cross((0, 0, 1), _hinge));
        var probe = ContinentLayout.Normalize((_hinge.X + east.X * 0.01, _hinge.Y + east.Y * 0.01, _hinge.Z + east.Z * 0.01));
        _hingeSign = ContinentLayout.Cross(_hinge, probe).Z < 0 ? 1.0 : -1.0;

        _splits =
        [
            (ContinentLayout.D, ContinentLayout.ASouth, parameters.DRiftStart),
            (ContinentLayout.F, ContinentLayout.B, parameters.FRiftStart),
            (ContinentLayout.E, ContinentLayout.C, parameters.ERiftStart),
            (ContinentLayout.SmallIslands, ContinentLayout.C, parameters.ERiftStart),
        ];

        var scriptedPlates = Enumerable.Range(0, ContinentLayout.LandMassCount).Select(i => _plate[i]).ToArray();
        foreach (var a in scriptedPlates)
            foreach (var b in scriptedPlates)
                if (a != b) _scripted.Add(Key(a, b));
        foreach (var (island, others) in new[]
        {
            (ContinentLayout.D, new[] { ContinentLayout.ANorth, ContinentLayout.ASouth }),
            (ContinentLayout.F, new[] { ContinentLayout.B, ContinentLayout.C }),
            (ContinentLayout.E, new[] { ContinentLayout.C, ContinentLayout.B, ContinentLayout.SmallIslands }),
            (ContinentLayout.SmallIslands, new[] { ContinentLayout.C, ContinentLayout.B }),
        })
        {
            foreach (var other in others) _neverWeld.Add(Key(_plate[island], _plate[other]));
        }
        EndMyr = parameters.EndMyr;
    }

    /// <summary>劇本結束、強制焊接、開始檢查停止條件的時間（百萬年）。</summary>
    public double EndMyr { get; }

    /// <summary>B 第一次撞上 C 的時間（百萬年），還沒撞上是 null。之後改用推的速度。</summary>
    public double? BContactMyr { get; private set; }

    /// <summary>每塊陸地（依陸塊編號）的板塊。</summary>
    public IReadOnlyList<int> PlateOfLandMass => _plate;

    /// <summary>
    /// 起始時要休眠的板塊（還沒裂開的大島）與休眠到什麼時候（百萬年）：**永遠**，只有裂開（<see cref="DueSplits"/>）會叫醒它。
    /// </summary>
    /// <remarks>
    /// 原本休眠到裂開的時間，結果休眠一到期、同一步的板塊重生就先拿這個空編號從海洋板塊切了一塊海，
    /// 下一步劇本看到它已經有格子、就不切了——D、F、E 全部沒裂開（草圖版第一次跑，2026-10-18）。
    /// </remarks>
    internal IEnumerable<(int Plate, double UntilMyr)> InitialDormancy =>
        _splits.Select(s => (_plate[s.LandMass], double.PositiveInfinity));

    /// <summary>
    /// 現在該裂開的大島：（母板塊、新板塊、哪些座標系的點要搬過去）。新板塊已經有格子（裂過了、或續跑）就跳過。
    /// 起始時板塊座標系的點就是格子索引，所以「要搬的點」就是起始配置裡那塊陸地的格子。
    /// </summary>
    /// <remarks>
    /// 小島群裂開時整條窄帶一起切出去，小島之間的段落（<see cref="ContinentLayoutResult.RiftGap"/>）在新板塊上沉成海床（Sink）。
    /// 原本那些段落留在母板塊上沉，小島群的板塊就是 5 塊互不相連的島，歸屬平滑（每個板塊只留一個本體）
    /// 把其中 4 塊當碎片併進 C 或 E，只剩一個島（草圖版第一次跑，2026-10-18）。
    /// </remarks>
    internal IEnumerable<(int Parent, int Child, Func<int, bool> Region, Func<int, bool>? Sink)> DueSplits(double elapsedMyr, int[] ownerByCell)
    {
        foreach (var (landMass, parent, start) in _splits)
        {
            if (elapsedMyr < start || Array.IndexOf(ownerByCell, _plate[landMass]) >= 0) continue;
            var target = landMass;
            if (landMass == ContinentLayout.SmallIslands)
                yield return (_plate[parent], _plate[landMass], body => _label[body] == target,
                    body => _label[body] == target && _gap[body]);
            else if (landMass == ContinentLayout.E)
                // E 連同周圍的海一起切（2026-10-18 使用者指定，見 ContinentLayoutResult.OceanJoin）
                yield return (_plate[parent], _plate[landMass], body => _label[body] == target || _oceanJoin[body] == target, null);
            else
                yield return (_plate[parent], _plate[landMass], body => _label[body] == target, null);
        }
    }

    /// <summary>這兩塊板塊現在准不准自動焊接（見類別說明）。</summary>
    internal bool IsWeldBlocked(int a, int b, double elapsedMyr)
    {
        var key = Key(a, b);
        if (_neverWeld.Contains(key)) return true;
        return elapsedMyr < EndMyr && _scripted.Contains(key);
    }

    /// <summary>
    /// 這個板塊現在是不是不會被消耗：劇本期間的太平洋（2026-10-18 使用者指定：固定不動、演化中面積也不減）。
    /// 原本太平洋交給動力學，被 C 那一側的隱沒拉著往西走約 2.9 公分／年，比 A 快，A 西岸跟它是分開的、長不出山。
    /// </summary>
    /// <remarks>
    /// 例外是 A 南（2026-10-19 使用者指定）：A 南繞西側支點轉開時西岸會往西推，撞上這道牆每百萬年被吃掉約 1.5 萬平方公里，
    /// 40 Myr 少了約 22%；改成讓太平洋在 A 南那一段像安地斯那樣隱沒進去，A 南不被吃，太平洋在那一段縮小一點。
    /// </remarks>
    /// <param name="plate">這個板塊。</param>
    /// <param name="other">碰上的板塊。</param>
    /// <param name="elapsedMyr">現在幾百萬年。</param>
    internal bool IsUnconsumable(int plate, int other, double elapsedMyr) =>
        plate == ContinentLayout.PacificPlate && other != _plate[ContinentLayout.ASouth] && elapsedMyr < EndMyr;

    /// <summary>劇本結束時要強制焊接的（留下、併掉）；被併掉的那塊已經沒有格子就跳過（續跑時已經焊過）。</summary>
    internal IEnumerable<(int Keep, int Gone)> DueWelds(double elapsedMyr, int[] ownerByCell)
    {
        if (elapsedMyr < EndMyr) yield break;
        foreach (var (keep, gone) in new[] { (ContinentLayout.C, ContinentLayout.B), (ContinentLayout.ANorth, ContinentLayout.ASouth) })
        {
            if (Array.IndexOf(ownerByCell, _plate[gone]) >= 0) yield return (_plate[keep], _plate[gone]);
        }
    }

    /// <summary>
    /// 覆寫劇本控制的板塊的角速度（在動力學解完之後呼叫，下一步生效）。方向用各板塊**大陸地殼**的重心算。
    /// 順序有意義：跟隨的板塊排在被跟隨的後面。還沒裂開（沒有格子）的板塊不管。
    /// </summary>
    /// <param name="grid">網格。</param>
    /// <param name="state">狀態（大陸地殼在哪裡）。</param>
    /// <param name="plates">板塊，劇本控制的板塊角速度會被覆寫。</param>
    /// <param name="t">現在幾百萬年。</param>
    /// <param name="wereColliding">上一步兩塊板塊之間有沒有大陸碰撞。</param>
    internal void ApplyVelocities(SimulationGrid grid, SimulationState state, PlateSet plates, double t, Func<int, int, bool> wereColliding)
    {
        if (t >= EndMyr) return;
        if (BContactMyr is null && wereColliding(_plate[ContinentLayout.B], _plate[ContinentLayout.C])) BContactMyr = t;

        var owner = plates.OwnerByCell;
        var center = new (double X, double Y, double Z)?[ContinentLayout.LandMassCount];
        for (var i = 0; i < center.Length; i++) center[i] = ContinentalCentroid(grid, state, owner, _plate[i]);
        var omega = new (double X, double Y, double Z)[ContinentLayout.LandMassCount];
        const int an = ContinentLayout.ANorth, aS = ContinentLayout.ASouth, d = ContinentLayout.D, b = ContinentLayout.B,
            f = ContinentLayout.F, c = ContinentLayout.C, e = ContinentLayout.E, small = ContinentLayout.SmallIslands;

        // A 北往西；A 南跟著它，再繞西側支點轉開（角速度向量平行於支點的位置向量，支點本身不動；速度以離支點 30° 處計）
        omega[an] = Bearing(center[an], 270, _p.AWestSpeed);
        omega[aS] = Add(omega[an], Scale(_hinge, ToDegPerMyr(_p.AOpeningSpeed) / Math.Sin(30.0 * Math.PI / 180.0) * _hingeSign));
        // 再往東滑，抵銷轉動讓西岸往西推的那一份（見 ContinentLayoutParameters.ASouthEastSpeed）
        omega[aS] = Add(omega[aS], Bearing(center[aS], 90, _p.ASouthEastSpeed));
        omega[d] = omega[aS];
        if (t >= _p.DRiftStart && t < _p.DRiftEnd) omega[d] = Add(omega[d], Away(center[d], center[aS], _p.DRiftSpeed));

        // C 往東北；B 跟著 C 再往東北相對靠近，撞上之後慢慢推
        omega[c] = Bearing(center[c], _p.CBearing, _p.CSpeed);
        omega[b] = Add(omega[c], Bearing(center[b], _p.BBearing, BContactMyr is null ? _p.BApproachSpeed : _p.BPushSpeed));
        omega[f] = omega[b];
        if (t >= _p.FRiftStart && t < _p.FRiftEnd) omega[f] = Add(omega[f], Away(center[f], center[b], _p.FRiftSpeed));

        // E 與小島群：裂谷期間往 EBearing（正南）貼著太平洋的西緣滑出去，小島群一半速度；之後停住。
        // 不疊加 C 的速度——C 往東北，疊上去 E 就會擠進固定的太平洋、東岸被吃掉（2026-10-18 那一輪就是這樣）
        var eRifting = t >= _p.ERiftStart && t < _p.ERiftEnd;
        omega[e] = eRifting ? Bearing(center[e], _p.EBearing, _p.ERiftSpeed) : (0, 0, 0);
        omega[small] = eRifting ? Bearing(center[small], _p.EBearing, _p.ERiftSpeed / 2) : (0, 0, 0);

        for (var i = 0; i < ContinentLayout.LandMassCount; i++)
        {
            if (center[i] is null) continue; // 還沒裂開、已經被焊掉或吃光的不管
            SetVelocity(plates, _plate[i], omega[i]);
        }

        // 太平洋固定不動（見 IsUnconsumable）
        SetVelocity(plates, ContinentLayout.PacificPlate, (0, 0, 0));
    }

    /// <summary>讓 at 以 cmPerYear 往 bearing（從北順時針，度）移動的角速度向量（度／百萬年）；at 是 null 就是 0。</summary>
    private (double X, double Y, double Z) Bearing((double X, double Y, double Z)? at, double bearingDegrees, double cmPerYear)
    {
        if (at is not { } p) return (0, 0, 0);
        var east = ContinentLayout.Normalize(ContinentLayout.Cross((0, 0, 1), p));
        var north = ContinentLayout.Cross(p, east);
        var rad = bearingDegrees * Math.PI / 180.0;
        var direction = Add(Scale(north, Math.Cos(rad)), Scale(east, Math.Sin(rad)));
        return Scale(ContinentLayout.Normalize(ContinentLayout.Cross(p, direction)), ToDegPerMyr(cmPerYear));
    }

    /// <summary>讓 at 以 cmPerYear 往遠離 from 的方向移動的角速度向量；任何一個是 null 就是 0。</summary>
    private (double X, double Y, double Z) Away((double X, double Y, double Z)? at, (double X, double Y, double Z)? from, double cmPerYear)
    {
        if (at is not { } a || from is not { } fr) return (0, 0, 0);
        var direction = Scale(ContinentLayout.TangentToward(a, fr), -1.0);
        return Scale(ContinentLayout.Normalize(ContinentLayout.Cross(a, direction)), ToDegPerMyr(cmPerYear));
    }

    /// <summary>地表速度（公分／年，離歐拉極 90° 處）換成角速度（度／百萬年）。</summary>
    private double ToDegPerMyr(double cmPerYear) => cmPerYear / 100.0 / _radiusMeters * 1_000_000.0 * 180.0 / Math.PI;

    private static void SetVelocity(PlateSet plates, int plate, (double X, double Y, double Z) omega)
    {
        var magnitude = Math.Sqrt(omega.X * omega.X + omega.Y * omega.Y + omega.Z * omega.Z);
        plates.Plates[plate] = magnitude < 1e-12
            ? plates.Plates[plate] with { AngularVelocityDegPerMyr = 0 }
            : plates.Plates[plate] with
            {
                PoleX = omega.X / magnitude,
                PoleY = omega.Y / magnitude,
                PoleZ = omega.Z / magnitude,
                AngularVelocityDegPerMyr = magnitude,
            };
    }

    private static (double X, double Y, double Z) Add((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static (double X, double Y, double Z) Scale((double X, double Y, double Z) a, double s) => (a.X * s, a.Y * s, a.Z * s);

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>板塊上大陸地殼的重心；一格都沒有就是 null。</summary>
    private static (double X, double Y, double Z)? ContinentalCentroid(SimulationGrid grid, SimulationState state, int[] owner, int plate)
    {
        var u = grid.UnitVector;
        double x = 0, y = 0, z = 0;
        var any = false;
        for (var cell = 0; cell < grid.CellCount; cell++)
        {
            if (owner[cell] != plate || state.Crust[cell] != CrustType.Continental) continue;
            x += u[cell * 3]; y += u[cell * 3 + 1]; z += u[cell * 3 + 2];
            any = true;
        }
        return any ? ContinentLayout.Normalize((x, y, z)) : null;
    }
}
