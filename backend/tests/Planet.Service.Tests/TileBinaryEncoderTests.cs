using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Planet.Service;
using Planet.Shared;

namespace Planet.Service.Tests;

/// <summary>
/// <see cref="TileBinaryEncoder"/> 的測試。解碼的另一半在前端（<c>frontend/src/network/tileBinary.ts</c>），
/// 兩邊靠 <c>Fixtures/*.bin</c> 這兩個標準檔對齊：這裡驗「編碼器的輸出跟標準檔逐位元組相同」，
/// 前端的 tileBinary.spec.ts 驗「解碼器讀得懂同一份標準檔」。任何一邊改了格式，兩邊的測試至少有一邊會紅。
///
/// 另外再用一個獨立寫的小解析器（照格式文件逐欄讀）檢查標準檔的內容，這樣就算標準檔是「編碼器自己生出來的」，
/// 也不會變成編碼器自己驗自己。
/// </summary>
public class TileBinaryEncoderTests
{
    private static readonly string FixtureDirectory = Path.Combine(SourceDirectory(), "Fixtures");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>涵蓋各種情況的地塊：有河、沒有鄰居陣列、五角形（5 個鄰居）、鄰居缺一格、跨半球的座標。</summary>
    internal static IReadOnlyList<TileDto> SampleTiles() =>
    [
        new(15471163, "Hexagon", 15, 6.668932, 22.262288, "Rock", 0, 900, 0,
            [15470108, 15471162, null, 15472216, 15471164, 15470109], 174772),
        new(7, "Pentagon", 0, -89.5, -179.999999, "Seawater", 6, -964, 0,
            [1, 2, 3, 4, 5], null, 12.5f, 4321),
        new(8, "Hexagon", 3, 0, 0, "Seawater", 6, -964, 0),
    ];

    /// <summary>涵蓋各種情況的小格：建築、地形修改、氾濫平原、河、沒有任何選用欄位。</summary>
    internal static IReadOnlyList<SubTileDto> SampleSubTiles() =>
    [
        new(1000486946029, 15379144, "Hexagon", 10.274593, 20.132742, "Rock", 0, 723, 0,
            [1000486940368, null, null, null, null, null], 173738, null, 0f, 0f, false, "Castle"),
        new(1000486946030, 15379144, "Hexagon", 10.28, 20.14, "Freshwater", 2, 700, 0,
            [1, 2, 3, 4, 5, 6], 173738, 99, 250f, 8f, true, null, "Channel"),
        new(1000486946031, 15379144, "Pentagon", -10.28, -20.14, "Sand", 1, 5, 0, [10, 11, 12, 13, 14]),
    ];

    [Fact]
    public void Tiles_MatchTheGoldenFile_UsedByTheFrontendSpec()
    {
        AssertGolden("tiles.bin", TileBinaryEncoder.Encode(SampleTiles()));
    }

    [Fact]
    public void SubTiles_MatchTheGoldenFile_UsedByTheFrontendSpec()
    {
        AssertGolden("subtiles.bin", TileBinaryEncoder.Encode(SampleSubTiles()));
    }

    /// <summary>
    /// 格式是前後端的契約：要改格式時，先確定前端解碼器也改了，再用 <c>PLANET_UPDATE_GOLDEN=1</c> 重新產生標準檔，
    /// 並把標準檔的差異一起提交。
    /// </summary>
    private static void AssertGolden(string name, byte[] actual)
    {
        var path = Path.Combine(FixtureDirectory, name);
        if (Environment.GetEnvironmentVariable("PLANET_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(FixtureDirectory);
            File.WriteAllBytes(path, actual);
        }
        Assert.True(File.Exists(path), $"找不到標準檔 {path}；用 PLANET_UPDATE_GOLDEN=1 產生。");
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(actual),
            $"編碼結果跟標準檔 {name} 不同。如果是刻意改格式：先改前端 tileBinary.ts，再用 PLANET_UPDATE_GOLDEN=1 更新標準檔。");
    }

    [Fact]
    public void Tiles_ReadBackFieldByField_FollowingTheDocumentedLayout()
    {
        var tiles = SampleTiles();
        var reader = new LayoutReader(TileBinaryEncoder.Encode(tiles));

        Assert.Equal(3, reader.Count);
        Assert.Equal("tile", reader.Header.GetProperty("kind").GetString());
        string[] shapes = [.. reader.Header.GetProperty("shapes").EnumerateArray().Select(e => e.GetString()!)];
        string[] terrains = [.. reader.Header.GetProperty("terrains").EnumerateArray().Select(e => e.GetString()!)];

        var tileId = reader.Doubles();
        var latitude = reader.Doubles();
        var longitude = reader.Doubles();
        var neighbors = reader.Doubles(6);
        var faceIndex = reader.Ints();
        var heightCategory = reader.Ints();
        var elevationLevel = reader.Ints();
        var seaLevel = reader.Ints();
        var sourceCellId = reader.Ints();
        var riverCellId = reader.Ints();
        var riverWidth = reader.Floats();
        var shape = reader.Bytes();
        var terrain = reader.Bytes();
        var neighborCount = reader.Bytes();
        reader.AssertConsumed();

        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            Assert.Equal(t.TileId, tileId[i]);
            Assert.Equal(t.Shape, shapes[shape[i]]);
            Assert.Equal(t.Terrain, terrains[terrain[i]]);
            Assert.Equal(t.FaceIndex, faceIndex[i]);
            Assert.Equal(t.Latitude, latitude[i]);
            Assert.Equal(t.Longitude, longitude[i]);
            Assert.Equal(t.HeightCategory, heightCategory[i]);
            Assert.Equal(t.ElevationLevel, elevationLevel[i]);
            Assert.Equal(t.SeaLevel, seaLevel[i]);
            Assert.Equal(t.SourceCellId ?? int.MinValue, sourceCellId[i]);
            Assert.Equal(t.RiverCellId ?? int.MinValue, riverCellId[i]);
            Assert.Equal(t.RiverWidthMeters, riverWidth[i]);

            if (t.NeighborIds is null)
            {
                Assert.Equal(255, neighborCount[i]);
            }
            else
            {
                Assert.Equal(t.NeighborIds.Length, neighborCount[i]);
                for (var k = 0; k < 6; k++)
                {
                    var expected = k < t.NeighborIds.Length && t.NeighborIds[k] is { } id ? id : double.NaN;
                    Assert.Equal(expected, neighbors[i * 6 + k]);
                }
            }
        }
    }

    [Fact]
    public void SubTiles_ReadBackFieldByField_FollowingTheDocumentedLayout()
    {
        var subTiles = SampleSubTiles();
        var reader = new LayoutReader(TileBinaryEncoder.Encode(subTiles));

        Assert.Equal("subTile", reader.Header.GetProperty("kind").GetString());
        string[] structures = [.. reader.Header.GetProperty("structures").EnumerateArray().Select(e => e.GetString()!)];
        string[] terrainEdits = [.. reader.Header.GetProperty("terrainEdits").EnumerateArray().Select(e => e.GetString()!)];

        var subTileId = reader.Doubles();
        var parentTileId = reader.Doubles();
        reader.Doubles(); // 緯度
        reader.Doubles(); // 經度
        var neighbors = reader.Doubles(6);
        reader.Ints(); // 高度分類
        reader.Ints(); // 海拔
        reader.Ints(); // 海平面
        var sourceCellId = reader.Ints();
        var riverCellId = reader.Ints();
        var riverWidth = reader.Floats();
        var riverDistance = reader.Floats();
        reader.Bytes(); // 形狀
        reader.Bytes(); // 地形
        var neighborCount = reader.Bytes();
        var floodplain = reader.Bytes();
        var structure = reader.Bytes();
        var terrainEdit = reader.Bytes();
        reader.AssertConsumed();

        for (var i = 0; i < subTiles.Count; i++)
        {
            var s = subTiles[i];
            Assert.Equal(s.SubTileId, subTileId[i]);
            Assert.Equal(s.ParentTileId, parentTileId[i]);
            Assert.Equal(s.SourceCellId ?? int.MinValue, sourceCellId[i]);
            Assert.Equal(s.RiverCellId ?? int.MinValue, riverCellId[i]);
            Assert.Equal(s.RiverWidthMeters, riverWidth[i]);
            Assert.Equal(s.RiverDistanceMeters, riverDistance[i]);
            Assert.Equal(s.Floodplain ? 1 : 0, floodplain[i]);
            Assert.Equal(s.NeighborIds.Length, neighborCount[i]);
            Assert.Equal(s.NeighborIds[0] is { } first ? first : double.NaN, neighbors[i * 6]);
            // 0＝沒有；其餘是字典索引 + 1
            Assert.Equal(s.Structure is null ? null : s.Structure, structure[i] == 0 ? null : structures[structure[i] - 1]);
            Assert.Equal(s.TerrainEdit is null ? null : s.TerrainEdit, terrainEdit[i] == 0 ? null : terrainEdits[terrainEdit[i] - 1]);
        }
    }

    [Fact]
    public void EveryColumnStartsAlignedToItsOwnWidth()
    {
        // 前端不複製就直接套 TypedArray：Float64Array 的起點不是 8 的倍數就會丟例外。
        // 各種標頭長度都要對齊（標頭 JSON 補空白到 8 的倍數）。
        foreach (var bytes in new[] { TileBinaryEncoder.Encode(SampleTiles()), TileBinaryEncoder.Encode(SampleSubTiles()), TileBinaryEncoder.Encode(Array.Empty<TileDto>()) })
        {
            var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            var dataStart = 16 + (int)((headerLength + 7) & ~7u);
            Assert.Equal(0, dataStart % 8);
            Assert.True(bytes.AsSpan(16 + (int)headerLength, dataStart - 16 - (int)headerLength).ToArray().All(b => b == (byte)' '));
        }
    }

    [Fact]
    public void EmptyBatch_IsAValidFileWithZeroRows()
    {
        var reader = new LayoutReader(TileBinaryEncoder.Encode(Array.Empty<TileDto>()));

        Assert.Equal(0, reader.Count);
        reader.AssertConsumed();
    }

    [Fact]
    public void MoreThanSixNeighbors_IsRejectedInsteadOfTruncated()
    {
        var bad = new TileDto(1, "Hexagon", 0, 0, 0, "Rock", 0, 0, 0, [1, 2, 3, 4, 5, 6, 7]);

        Assert.Throws<InvalidOperationException>(() => TileBinaryEncoder.Encode([bad]));
    }

    [Theory]
    [InlineData("application/vnd.planet.tiles", true)]
    [InlineData("application/vnd.planet.tiles, application/json;q=0.9", true)]
    [InlineData("APPLICATION/VND.PLANET.TILES", true)]
    [InlineData("application/json", false)]
    [InlineData("*/*", false)]
    [InlineData("", false)]
    public void IsRequested_OnlyWhenTheBinaryTypeIsAsked(string accept, bool expected)
    {
        var context = new DefaultHttpContext();
        if (accept.Length > 0) context.Request.Headers.Accept = accept;

        Assert.Equal(expected, TileBinaryEncoder.IsRequested(context.Request));
    }

    /// <summary>獨立照格式文件寫的解析器：跟編碼器的實作無關，只認文件裡寫的欄位順序與寬度。</summary>
    private sealed class LayoutReader
    {
        private readonly byte[] _bytes;
        private int _offset;

        public int Count { get; }
        public JsonElement Header { get; }

        public LayoutReader(byte[] bytes)
        {
            _bytes = bytes;
            Assert.Equal(0x31425450u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0))); // "PTB1"
            Count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            Header = JsonDocument.Parse(bytes.AsMemory(16, headerLength)).RootElement.Clone();
            _offset = 16 + ((headerLength + 7) & ~7);
        }

        public double[] Doubles(int perRow = 1) => Read(Count * perRow, 8, (b, o) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o)));
        public int[] Ints() => Read(Count, 4, (b, o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)));
        public float[] Floats() => Read(Count, 4, (b, o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)));
        public byte[] Bytes() => Read(Count, 1, (b, o) => b[o]);

        public void AssertConsumed() => Assert.Equal(_bytes.Length, _offset);

        private T[] Read<T>(int length, int width, Func<byte[], int, T> read)
        {
            var result = new T[length];
            for (var i = 0; i < length; i++) result[i] = read(_bytes, _offset + i * width);
            _offset += length * width;
            return result;
        }
    }
}
