using System.Windows.Media.Media3D;

namespace STPViewer.Models;

public enum UnitSystem
{
    Millimeter,
    Inch,
}

/// <summary>量測值格式化（內部一律 mm，顯示時換算）</summary>
public static class Units
{
    private const double MmPerInch = 25.4;

    /// <summary>長度</summary>
    public static string L(double mm, UnitSystem u) =>
        u == UnitSystem.Millimeter ? $"{mm:F3} mm" : $"{mm / MmPerInch:F4} in";

    /// <summary>面積</summary>
    public static string A(double mm2, UnitSystem u) =>
        u == UnitSystem.Millimeter ? $"{mm2:N3} mm²" : $"{mm2 / (MmPerInch * MmPerInch):N4} in²";

    /// <summary>體積</summary>
    public static string V(double mm3, UnitSystem u) =>
        u == UnitSystem.Millimeter ? $"{mm3:N2} mm³" : $"{mm3 / (MmPerInch * MmPerInch * MmPerInch):N4} in³";

    /// <summary>座標分量（無單位字尾）</summary>
    public static string C(double mm, UnitSystem u) =>
        u == UnitSystem.Millimeter ? $"{mm:F3}" : $"{mm / MmPerInch:F4}";

    /// <summary>座標點 (x, y, z)</summary>
    public static string P(Point3D p, UnitSystem u) =>
        $"({C(p.X, u)}, {C(p.Y, u)}, {C(p.Z, u)})";
}
