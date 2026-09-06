namespace Planet.Domain;

/// <summary>
/// 地表覆蓋類型。部分類型搭配 Tile.HeightCategory 表示細分的高度/深度等級（見該欄位的說明）。
/// </summary>
public enum TerrainType
{
    /// <summary>草地。<see cref="Tile.HeightCategory"/> 0~4 對應腳板高／小腿肚高／腰部高／肩膀高／高過頭。</summary>
    Grassland,

    /// <summary>灌木叢。<see cref="Tile.HeightCategory"/> 0~3 對應小腿肚高／腰部高／肩膀高／高過頭。</summary>
    Shrubland,

    /// <summary>
    /// 林地。<see cref="Tile.HeightCategory"/> 0~7＝(高度0~3：人高／一層樓／二層樓／三層樓) × (疏0／密1)，
    /// 即 HeightCategory = 高度索引×2 + (密林?1:0)。
    /// </summary>
    Forest,

    /// <summary>淡水（河流／湖泊）。<see cref="Tile.HeightCategory"/> 0~6 對應小腿肚深至高過十人深，共7級。</summary>
    Freshwater,

    /// <summary>海水。<see cref="Tile.HeightCategory"/> 分級同 <see cref="Freshwater"/>。</summary>
    Seawater,

    /// <summary>泥地。無 HeightCategory 細分（固定為0）。</summary>
    Mud,

    /// <summary>黃土地。無 HeightCategory 細分。</summary>
    Loess,

    /// <summary>岩地。無 HeightCategory 細分。</summary>
    Rock,

    /// <summary>沙地／沙漠。無 HeightCategory 細分。</summary>
    Sand,

    /// <summary>雪地（高緯度、雪線以上但未到冰原程度）。無 HeightCategory 細分。</summary>
    SnowField,

    /// <summary>冰原／冰河（極區）。無 HeightCategory 細分。</summary>
    Glacier,

    /// <summary>沼澤／濕地（低海拔且高濕度）。無 HeightCategory 細分。</summary>
    Swamp,

    /// <summary>鹽鹼地／鹽田（乾燥低地）。無 HeightCategory 細分。</summary>
    SaltFlat,

    /// <summary>火山岩地／熔岩地（高海拔且岩石噪聲值特別高）。無 HeightCategory 細分。</summary>
    VolcanicRock,

    /// <summary>珊瑚礁（低緯度淺海）。無 HeightCategory 細分。</summary>
    CoralReef,

    /// <summary>懸崖／峭壁（陡峭高地）。無 HeightCategory 細分。</summary>
    Cliff,

    /// <summary>苔原（高緯度低海拔）。無 HeightCategory 細分。</summary>
    Tundra,

    /// <summary>河床／礫灘（低地、岩石噪聲偏高但未達 <see cref="Rock"/> 門檻）。無 HeightCategory 細分。</summary>
    Gravel,
}
