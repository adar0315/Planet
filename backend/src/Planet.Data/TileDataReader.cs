using System.Data;
using Planet.Domain;

namespace Planet.Data;

/// <summary>
/// 把 IEnumerable&lt;Tile&gt; 包成 IDataReader，供 SqlBulkCopy 邊讀邊寫。
/// 2000萬筆規模不能像 DataTable 那樣先把所有列都放進記憶體，用streaming reader可以邊生成邊寫入DB。
/// </summary>
internal sealed class TileDataReader : IDataReader
{
    /// <summary>Tiles 資料表的欄位名稱，依序對應 SqlBulkCopy 寫入的欄位順序，與 <see cref="GetValue"/> 的switch分支一一對應。</summary>
    internal static readonly string[] ColumnNames =
    [
        nameof(Tile.TileId), nameof(Tile.Shape), nameof(Tile.FaceIndex),
        nameof(Tile.Row), nameof(Tile.Col), nameof(Tile.Latitude),
        nameof(Tile.Longitude), nameof(Tile.Terrain), nameof(Tile.HeightCategory),
        nameof(Tile.ElevationLevel), nameof(Tile.SeaLevel), nameof(Tile.SourceCellId),
    ];

    private readonly IEnumerator<Tile> _enumerator;
    private bool _closed;

    /// <summary>用一個Tile集合建立reader，實際列舉延後到 <see cref="Read"/> 逐一被呼叫時才發生。</summary>
    /// <param name="tiles">要串流讀出的地塊集合。</param>
    public TileDataReader(IEnumerable<Tile> tiles)
    {
        _enumerator = tiles.GetEnumerator();
    }

    /// <inheritdoc />
    public int FieldCount => ColumnNames.Length;

    /// <inheritdoc />
    public int Depth => 0;

    /// <inheritdoc />
    public bool IsClosed => _closed;

    /// <inheritdoc />
    public int RecordsAffected => -1;

    /// <summary>移到下一個Tile。SqlBulkCopy靠這個方法逐筆推進讀取。</summary>
    public bool Read() => _enumerator.MoveNext();

    /// <inheritdoc />
    public bool NextResult() => false;

    /// <inheritdoc />
    public void Close() => _closed = true;

    /// <inheritdoc />
    public DataTable? GetSchemaTable() => null;

    /// <inheritdoc />
    public void Dispose()
    {
        _enumerator.Dispose();
        _closed = true;
    }

    /// <summary>取得目前Tile在第i欄的值，欄位順序依 <see cref="ColumnNames"/>。</summary>
    public object GetValue(int i)
    {
        var tile = _enumerator.Current;
        return ColumnNames[i] switch
        {
            nameof(Tile.TileId) => tile.TileId,
            nameof(Tile.Shape) => (int)tile.Shape,
            nameof(Tile.FaceIndex) => tile.FaceIndex,
            nameof(Tile.Row) => tile.Row,
            nameof(Tile.Col) => tile.Col,
            nameof(Tile.Latitude) => tile.Latitude,
            nameof(Tile.Longitude) => tile.Longitude,
            nameof(Tile.Terrain) => (int)tile.Terrain,
            nameof(Tile.HeightCategory) => tile.HeightCategory,
            nameof(Tile.ElevationLevel) => tile.ElevationLevel,
            nameof(Tile.SeaLevel) => tile.SeaLevel,
            nameof(Tile.SourceCellId) => tile.SourceCellId is { } source ? source : DBNull.Value,
            _ => throw new IndexOutOfRangeException(),
        };
    }

    /// <inheritdoc />
    public int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    /// <inheritdoc />
    public string GetName(int i) => ColumnNames[i];

    /// <inheritdoc />
    public int GetOrdinal(string name) => Array.IndexOf(ColumnNames, name);

    /// <summary>Tile的所有欄位都是非null值型別，永遠回傳false。</summary>
    public bool IsDBNull(int i) => GetValue(i) is DBNull;

    /// <inheritdoc cref="GetValue" />
    public object this[int i] => GetValue(i);

    /// <inheritdoc cref="GetValue" />
    public object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc cref="GetValue" />
    public int GetInt32(int i) => (int)GetValue(i);

    /// <inheritdoc cref="GetValue" />
    public long GetInt64(int i) => (long)GetValue(i);

    /// <inheritdoc cref="GetValue" />
    public double GetDouble(int i) => (double)GetValue(i);

    // SqlBulkCopy不會用到以下成員，本Reader只需支援上面用到的最小介面，故意不實作、呼叫就丟例外。
    /// <inheritdoc />
    public bool GetBoolean(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public byte GetByte(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
    /// <inheritdoc />
    public char GetChar(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
    /// <inheritdoc />
    public IDataReader GetData(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public string GetDataTypeName(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public DateTime GetDateTime(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public decimal GetDecimal(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public Type GetFieldType(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public float GetFloat(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public Guid GetGuid(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public short GetInt16(int i) => throw new NotSupportedException();
    /// <inheritdoc />
    public string GetString(int i) => throw new NotSupportedException();
}
