using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Planet.Shared;

namespace Planet.Service;

/// <summary>
/// 把一整批地塊或小格編成二進位（逐欄排列的數值陣列），取代 JSON。
///
/// 為什麼：一次 5 萬個地塊的 JSON 有 13 MB，絕大部分是重複的欄位名與文字化的數字；
/// 序列化、壓縮、瀏覽器解析都跟著這個大小走。逐欄的二進位只有三分之一，前端用 TypedArray 直接讀。
/// JSON 仍是預設格式（不帶 Accept 標頭就拿到 JSON），方便除錯與比對。
///
/// 格式（全部小端序）：
/// <code>
/// u32 魔術數字 "PTB1" | u32 列數 n | u32 標頭 JSON 的位元組數 | u32 保留
/// 標頭 JSON（UTF-8，後面補空白到 8 的倍數）：kind 與各個文字欄位的字典
/// f64 × n   編號
/// f64 × n   所屬地塊編號（只有小格）
/// f64 × n   緯度
/// f64 × n   經度
/// f64 × 6n  鄰居編號（每列 6 個；沒有的填 NaN）
/// i32 × n   面編號（只有地塊）
/// i32 × n   高度分類、海拔、海平面、來源粗格、河道粗格（各一欄；沒有的填 int.MinValue）
/// f32 × n   河寬
/// f32 × n   離河距離（只有小格）
/// u8 × n    形狀、地形（字典索引）、鄰居陣列長度（255＝沒有鄰居陣列）
/// u8 × n    氾濫平原、建築、地形修改（只有小格；後兩者 0＝沒有，其餘是字典索引 + 1）
/// </code>
/// 欄位由寬到窄排，每一欄的起點自然對齊自己的寬度，前端不必複製就能套 TypedArray。
/// 前端的解碼在 <c>frontend/src/network/tileBinary.ts</c>，兩邊要一起改。
/// </summary>
public static class TileBinaryEncoder
{
    /// <summary>二進位格式的 MIME 類型；前端用 Accept 標頭要求。</summary>
    public const string ContentType = "application/vnd.planet.tiles";

    /// <summary>每列固定的鄰居欄位數（六角形 6 個；五角形第 6 個填 NaN）。</summary>
    private const int NeighborSlots = 6;
    private const uint Magic = 0x31425450; // "PTB1"
    private const int NoValue = int.MinValue;
    private const byte NoNeighborArray = 255;

    /// <summary>請求是否明確要求二進位格式。</summary>
    /// <param name="request">目前的請求。</param>
    public static bool IsRequested(HttpRequest request) =>
        request.Headers.Accept.Any(v => v is not null && v.Contains(ContentType, StringComparison.OrdinalIgnoreCase));

    /// <summary>編碼一批地塊。</summary>
    /// <param name="tiles">要編碼的地塊，順序會保留。</param>
    public static byte[] Encode(IReadOnlyList<TileDto> tiles)
    {
        var n = tiles.Count;
        var shapes = new StringTable();
        var terrains = new StringTable();
        var shape = new byte[n];
        var terrain = new byte[n];
        for (var i = 0; i < n; i++)
        {
            shape[i] = shapes.IndexOf(tiles[i].Shape);
            terrain[i] = terrains.IndexOf(tiles[i].Terrain);
        }

        var writer = new Writer(n, new { kind = "tile", shapes = shapes.Values, terrains = terrains.Values },
            doubleColumns: 3 + NeighborSlots, intColumns: 6, floatColumns: 1, byteColumns: 3);

        writer.Doubles(i => tiles[i].TileId);
        writer.Doubles(i => tiles[i].Latitude);
        writer.Doubles(i => tiles[i].Longitude);
        var neighborCount = writer.Neighbors(i => tiles[i].NeighborIds);
        writer.Ints(i => tiles[i].FaceIndex);
        writer.Ints(i => tiles[i].HeightCategory);
        writer.Ints(i => tiles[i].ElevationLevel);
        writer.Ints(i => tiles[i].SeaLevel);
        writer.Ints(i => tiles[i].SourceCellId ?? NoValue);
        writer.Ints(i => tiles[i].RiverCellId ?? NoValue);
        writer.Floats(i => tiles[i].RiverWidthMeters);
        writer.Bytes(shape);
        writer.Bytes(terrain);
        writer.Bytes(neighborCount);
        return writer.Finish();
    }

    /// <summary>編碼一批小格。</summary>
    /// <param name="subTiles">要編碼的小格，順序會保留。</param>
    public static byte[] Encode(IReadOnlyList<SubTileDto> subTiles)
    {
        var n = subTiles.Count;
        var shapes = new StringTable();
        var terrains = new StringTable();
        var structures = new StringTable();
        var terrainEdits = new StringTable();
        var shape = new byte[n];
        var terrain = new byte[n];
        var floodplain = new byte[n];
        var structure = new byte[n];
        var terrainEdit = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var sub = subTiles[i];
            shape[i] = shapes.IndexOf(sub.Shape);
            terrain[i] = terrains.IndexOf(sub.Terrain);
            floodplain[i] = sub.Floodplain ? (byte)1 : (byte)0;
            structure[i] = sub.Structure is null ? (byte)0 : (byte)(structures.IndexOf(sub.Structure) + 1);
            terrainEdit[i] = sub.TerrainEdit is null ? (byte)0 : (byte)(terrainEdits.IndexOf(sub.TerrainEdit) + 1);
        }

        var writer = new Writer(n,
            new
            {
                kind = "subTile", shapes = shapes.Values, terrains = terrains.Values,
                structures = structures.Values, terrainEdits = terrainEdits.Values,
            },
            doubleColumns: 4 + NeighborSlots, intColumns: 5, floatColumns: 2, byteColumns: 6);

        writer.Doubles(i => subTiles[i].SubTileId);
        writer.Doubles(i => subTiles[i].ParentTileId);
        writer.Doubles(i => subTiles[i].Latitude);
        writer.Doubles(i => subTiles[i].Longitude);
        var neighborCount = writer.Neighbors(i => subTiles[i].NeighborIds);
        writer.Ints(i => subTiles[i].HeightCategory);
        writer.Ints(i => subTiles[i].ElevationLevel);
        writer.Ints(i => subTiles[i].SeaLevel);
        writer.Ints(i => subTiles[i].SourceCellId ?? NoValue);
        writer.Ints(i => subTiles[i].RiverCellId ?? NoValue);
        writer.Floats(i => subTiles[i].RiverWidthMeters);
        writer.Floats(i => subTiles[i].RiverDistanceMeters);
        writer.Bytes(shape);
        writer.Bytes(terrain);
        writer.Bytes(neighborCount);
        writer.Bytes(floodplain);
        writer.Bytes(structure);
        writer.Bytes(terrainEdit);
        return writer.Finish();
    }

    /// <summary>文字欄位的字典：同一個字串只存一次，列裡只放索引。</summary>
    private sealed class StringTable
    {
        private readonly Dictionary<string, byte> _index = new(StringComparer.Ordinal);

        /// <summary>依索引順序排列的字串。</summary>
        public List<string> Values { get; } = [];

        /// <summary>取得字串的索引，沒見過就加進字典。</summary>
        /// <param name="value">要查的字串。</param>
        public byte IndexOf(string value)
        {
            if (_index.TryGetValue(value, out var index)) return index;
            // 254：建築與地形修改要把索引 + 1 存進一個位元組
            if (Values.Count >= 254) throw new InvalidOperationException("文字欄位的種類超過二進位格式的上限。");
            index = (byte)Values.Count;
            Values.Add(value);
            _index[value] = index;
            return index;
        }
    }

    /// <summary>照固定順序把各欄寫進一塊事先配好大小的緩衝區。</summary>
    private sealed class Writer
    {
        private readonly int _count;
        private readonly byte[] _buffer;
        private int _offset;

        /// <summary>配好整塊緩衝區並寫入檔頭。</summary>
        /// <param name="count">列數。</param>
        /// <param name="header">標頭 JSON 的內容。</param>
        /// <param name="doubleColumns">f64 的欄數（鄰居算 6 欄）。</param>
        /// <param name="intColumns">i32 的欄數。</param>
        /// <param name="floatColumns">f32 的欄數。</param>
        /// <param name="byteColumns">u8 的欄數。</param>
        public Writer(int count, object header, int doubleColumns, int intColumns, int floatColumns, int byteColumns)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("二進位格式只支援小端序的機器。");

            _count = count;
            var json = JsonSerializer.SerializeToUtf8Bytes(header);
            var paddedHeader = (json.Length + 7) & ~7;
            var size = 16 + paddedHeader
                + count * (doubleColumns * 8 + intColumns * 4 + floatColumns * 4 + byteColumns);
            _buffer = new byte[size];

            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(0), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(4), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(8), (uint)json.Length);
            json.CopyTo(_buffer, 16);
            _buffer.AsSpan(16 + json.Length, paddedHeader - json.Length).Fill((byte)' ');
            _offset = 16 + paddedHeader;
        }

        /// <summary>寫一欄 f64。</summary>
        public void Doubles(Func<int, double> value)
        {
            var span = Column<double>(_count);
            for (var i = 0; i < span.Length; i++) span[i] = value(i);
        }

        /// <summary>寫一欄 i32。</summary>
        public void Ints(Func<int, int> value)
        {
            var span = Column<int>(_count);
            for (var i = 0; i < span.Length; i++) span[i] = value(i);
        }

        /// <summary>寫一欄 f32。</summary>
        public void Floats(Func<int, float> value)
        {
            var span = Column<float>(_count);
            for (var i = 0; i < span.Length; i++) span[i] = value(i);
        }

        /// <summary>寫一欄 u8。</summary>
        public void Bytes(byte[] values)
        {
            values.CopyTo(_buffer, _offset);
            _offset += values.Length;
        }

        /// <summary>寫鄰居欄（每列 6 個），回傳每列鄰居陣列原本的長度。</summary>
        public byte[] Neighbors(Func<int, long?[]?> value)
        {
            var span = Column<double>(_count * NeighborSlots);
            var lengths = new byte[_count];
            for (var i = 0; i < _count; i++)
            {
                var neighbors = value(i);
                if (neighbors is { Length: > NeighborSlots })
                    throw new InvalidOperationException($"鄰居超過 {NeighborSlots} 個，二進位格式放不下。");
                lengths[i] = neighbors is null ? NoNeighborArray : (byte)neighbors.Length;
                for (var k = 0; k < NeighborSlots; k++)
                {
                    span[i * NeighborSlots + k] =
                        neighbors is not null && k < neighbors.Length && neighbors[k] is { } id ? id : double.NaN;
                }
            }
            return lengths;
        }

        /// <summary>確認每一欄都寫了，交出緩衝區。</summary>
        public byte[] Finish()
        {
            if (_offset != _buffer.Length) throw new InvalidOperationException("二進位欄位數與宣告的不一致。");
            return _buffer;
        }

        /// <summary>取下一欄的寫入範圍。機器是小端序（建構時檢查過），照記憶體配置寫就是小端序。</summary>
        private Span<T> Column<T>(int length) where T : struct
        {
            var bytes = length * Marshal.SizeOf<T>();
            var span = MemoryMarshal.Cast<byte, T>(_buffer.AsSpan(_offset, bytes));
            _offset += bytes;
            return span;
        }
    }
}
