namespace Planet.Generation;

/// <summary>雙精度3D向量，供測地線細分計算使用（避免float精度在2000萬級別的細分中累積誤差）。</summary>
internal readonly struct Vec3
{
    /// <summary>X、Y、Z分量。</summary>
    public readonly double X, Y, Z;

    /// <summary>建立一個3D向量。</summary>
    public Vec3(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
    }

    /// <summary>向量加法。</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>向量減法。</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>純量乘法。</summary>
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    /// <summary>內積。</summary>
    public double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;

    /// <summary>外積（右手座標系）。</summary>
    public Vec3 Cross(Vec3 other) => new(
        Y * other.Z - Z * other.Y,
        Z * other.X - X * other.Z,
        X * other.Y - Y * other.X);

    /// <summary>向量長度（歐幾里得範數）。</summary>
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    /// <summary>回傳長度為1、方向不變的單位向量。</summary>
    public Vec3 Normalized()
    {
        var len = Length;
        return new Vec3(X / len, Y / len, Z / len);
    }
}
