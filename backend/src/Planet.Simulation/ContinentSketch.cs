using System.IO.Compression;
using System.Reflection;

namespace Planet.Simulation;

/// <summary>
/// 使用者畫的大陸配置草圖（2026-10-18，<c>Templates/continent-sketch.png</c>）：左＝西、上＝北，
/// 寬度是一整圈經度、高度是北極到南極（等距圓柱投影）。每種顏色代表一塊陸地或一種海，見 <see cref="SketchClass"/>。
///
/// 要改大陸配置就重畫這張圖：顏色照下表、字可以寫在圖上（黑色的字會被周圍的顏色補掉）。
///
/// | 顏色 | 代表 |
/// |---|---|
/// | 白 | 一般的海 |
/// | 橘 (255,106,0) | 太平洋板塊 |
/// | 紅 (255,0,0) | A（北半與南半，以東側的海灣分開） |
/// | 藍 (0,38,255) | D：之後從 A 南半裂出的大島 |
/// | 綠 (0,255,33) | B |
/// | 青 (0,255,255) | F：之後從 B 南部裂出的一塊 |
/// | 黃 (255,216,0) | C |
/// | 紫 (178,0,255) | E：之後從 C 東南側裂出的島 |
/// | 淺藍線 | 洋脊（A 與 B、C 之間） |
/// </summary>
public sealed class ContinentSketch
{
    private ContinentSketch(int width, int height, SketchClass[] classes)
    {
        Width = width;
        Height = height;
        Classes = classes;
    }

    /// <summary>草圖寬度（像素）。</summary>
    public int Width { get; }

    /// <summary>草圖高度（像素）。</summary>
    public int Height { get; }

    /// <summary>每個像素的分類（列優先，第 0 列是北極）。</summary>
    public SketchClass[] Classes { get; }

    /// <summary>同樣大小、換一份分類的草圖（配置調整位置用，見 ContinentLayout）。</summary>
    /// <param name="classes">新的分類，長度要等於寬×高。</param>
    internal ContinentSketch WithClasses(SketchClass[] classes)
    {
        if (classes.Length != Width * Height) throw new ArgumentException("分類的長度要等於寬×高。", nameof(classes));
        return new ContinentSketch(Width, Height, classes);
    }

    /// <summary>某個經緯度在草圖上的分類。</summary>
    /// <param name="latitudeDegrees">緯度（−90 ~ 90）。</param>
    /// <param name="longitudeDegrees">經度（任意，會換算到 −180 ~ 180）。</param>
    public SketchClass At(double latitudeDegrees, double longitudeDegrees)
    {
        var (x, y) = Pixel(latitudeDegrees, longitudeDegrees);
        return Classes[y * Width + x];
    }

    /// <summary>經緯度對應的像素。</summary>
    public (int X, int Y) Pixel(double latitudeDegrees, double longitudeDegrees)
    {
        var lon = ((longitudeDegrees + 180.0) % 360.0 + 360.0) % 360.0;
        var x = Math.Clamp((int)(lon / 360.0 * Width), 0, Width - 1);
        var y = Math.Clamp((int)((90.0 - latitudeDegrees) / 180.0 * Height), 0, Height - 1);
        return (x, y);
    }

    /// <summary>像素中心的經緯度。</summary>
    public (double Latitude, double Longitude) LatLon(int x, int y) =>
        (90.0 - (y + 0.5) * 180.0 / Height, (x + 0.5) * 360.0 / Width - 180.0);

    /// <summary>讀內嵌的草圖（<c>Templates/continent-sketch.png</c>）。</summary>
    public static ContinentSketch LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("continent-sketch.png")
            ?? throw new InvalidOperationException("找不到內嵌的大陸草圖 continent-sketch.png。");
        return Load(stream);
    }

    /// <summary>從 PNG 讀草圖並分類。</summary>
    public static ContinentSketch Load(Stream png)
    {
        var (width, height, rgb) = DecodePng(png);
        var classes = new SketchClass[width * height];
        for (var i = 0; i < classes.Length; i++)
            classes[i] = Classify(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
        FillText(width, height, classes);
        return new ContinentSketch(width, height, classes);
    }

    private static readonly (SketchClass Class, int R, int G, int B)[] Palette =
    [
        (SketchClass.Ocean, 255, 255, 255),
        (SketchClass.Pacific, 255, 106, 0),
        (SketchClass.A, 255, 0, 0),
        (SketchClass.D, 0, 38, 255),
        (SketchClass.B, 0, 255, 33),
        (SketchClass.F, 0, 255, 255),
        (SketchClass.C, 255, 216, 0),
        (SketchClass.E, 178, 0, 255),
        (SketchClass.Ridge, 119, 197, 255),
        (SketchClass.Text, 0, 148, 255), // 「洋脊」兩個字的顏色，不是洋脊本身
        (SketchClass.Ridge, 223, 241, 255),
        (SketchClass.Text, 0, 0, 0),
    ];

    /// <summary>
    /// 最接近的調色盤顏色；離每一種都太遠（超過 <see cref="MaxColorDistance"/>）的當成不確定，之後跟字一樣用周圍的顏色補。
    /// 不能硬歸給最像的那一類：兩塊陸地交界處的反鋸齒過渡色（例如黃與紫混出來的）最像的可能是橘色，
    /// 一整排像素就變成太平洋，夾在 C 與 E 之間。
    /// </summary>
    private static SketchClass Classify(int r, int g, int b)
    {
        var best = SketchClass.Text;
        var bestDistance = int.MaxValue;
        foreach (var (cls, pr, pg, pb) in Palette)
        {
            var d = (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb);
            if (d < bestDistance) { bestDistance = d; best = cls; }
        }
        return bestDistance <= MaxColorDistance * MaxColorDistance ? best : SketchClass.Text;
    }

    /// <summary>像素的顏色離調色盤多遠以內才算數（RGB 歐氏距離）。</summary>
    private const int MaxColorDistance = 60;

    /// <summary>字（黑色）與不確定的像素用最近的確定像素補上（多源廣度優先，左右邊界相連）。</summary>
    private static void FillText(int width, int height, SketchClass[] classes)
    {
        var queue = new Queue<int>();
        var seen = new bool[classes.Length];
        for (var i = 0; i < classes.Length; i++)
            if (classes[i] != SketchClass.Text) { seen[i] = true; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % width;
            var y = i / width;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var ny = y + dy;
                if (ny < 0 || ny >= height) continue;
                var j = ny * width + (x + dx + width) % width;
                if (seen[j]) continue;
                seen[j] = true;
                classes[j] = classes[i];
                queue.Enqueue(j);
            }
        }
    }

    /// <summary>最小的 PNG 解碼：8 位元、不交錯；灰階、RGB、調色盤、帶透明度的都可以（透明度忽略）。</summary>
    internal static (int Width, int Height, byte[] Rgb) DecodePng(Stream png)
    {
        using var reader = new BinaryReader(png);
        var signature = reader.ReadBytes(8);
        if (signature.Length != 8 || signature[1] != 'P' || signature[2] != 'N' || signature[3] != 'G')
            throw new InvalidDataException("不是 PNG。");

        int width = 0, height = 0, colorType = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        while (true)
        {
            var length = ReadInt32BigEndian(reader);
            var type = new string(reader.ReadChars(4));
            var data = reader.ReadBytes(length);
            reader.ReadBytes(4); // CRC
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                if (data[8] != 8) throw new InvalidDataException($"只支援 8 位元的 PNG（這張是 {data[8]}）。");
                colorType = data[9];
                if (data[12] != 0) throw new InvalidDataException("不支援交錯的 PNG。");
            }
            else if (type == "PLTE") palette = data;
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"不支援的 PNG 顏色類型 {colorType}。") };
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(inflated);
        var raw = inflated.ToArray();

        var stride = width * channels;
        var previous = new byte[stride];
        var line = new byte[stride];
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? line[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"不支援的 PNG 濾波 {filter}。"),
                };
                line[i] = (byte)(line[i] + predictor);
            }
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                switch (colorType)
                {
                    case 2:
                    case 6:
                        rgb[o] = line[x * channels]; rgb[o + 1] = line[x * channels + 1]; rgb[o + 2] = line[x * channels + 2];
                        break;
                    case 3:
                        var k = line[x] * 3;
                        rgb[o] = palette![k]; rgb[o + 1] = palette[k + 1]; rgb[o + 2] = palette[k + 2];
                        break;
                    default:
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = line[x * channels];
                        break;
                }
            }
            (previous, line) = (line, previous);
        }
        return (width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }
}

/// <summary>草圖上的分類（見 <see cref="ContinentSketch"/> 的顏色表）。</summary>
public enum SketchClass : byte
{
    /// <summary>一般的海。</summary>
    Ocean,

    /// <summary>太平洋板塊。</summary>
    Pacific,

    /// <summary>A（北半與南半）。</summary>
    A,

    /// <summary>D：之後從 A 南半裂出的大島。</summary>
    D,

    /// <summary>B。</summary>
    B,

    /// <summary>F：之後從 B 南部裂出的一塊。</summary>
    F,

    /// <summary>C。</summary>
    C,

    /// <summary>E：之後從 C 東南側裂出的島。</summary>
    E,

    /// <summary>洋脊。</summary>
    Ridge,

    /// <summary>圖上的字與不確定的顏色（讀圖時就被周圍的顏色補掉，不會留下來）。</summary>
    Text,
}
