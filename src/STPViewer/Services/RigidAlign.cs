using System;
using System.Windows.Media.Media3D;

namespace STPViewer.Services;

/// <summary>
/// 三點對齊 / 旋轉的剛體變換數學。
/// 同一個變換需要兩種表示：WPF <see cref="Matrix3D"/>（列向量約定，給網格/邊線）與
/// CADability <see cref="CADability.ModOp"/>（行向量約定，給 B-rep Modify）— 用 <see cref="ToModOp"/> 轉換。
/// </summary>
public static class RigidAlign
{
    /// <summary>
    /// 來源 3 點 → 目標 3 點 的剛體變換（不縮放）：
    /// p1→q1 精確貼合、p1p2 方向對齊 q1q2、三點平面對齊。共線/退化回傳 false。
    /// </summary>
    public static bool TryRigidTransform(
        Point3D p1, Point3D p2, Point3D p3,
        Point3D q1, Point3D q2, Point3D q3, out Matrix3D m)
    {
        m = Matrix3D.Identity;
        if (!TryFrame(p1, p2, p3, out Vector3D xp, out Vector3D yp, out Vector3D zp)) return false;
        if (!TryFrame(q1, q2, q3, out Vector3D xq, out Vector3D yq, out Vector3D zq)) return false;

        // R = Bq · Bpᵀ（行向量約定）；WPF Matrix3D 為列向量約定 → 轉置擺放
        double R(int r, int c)
        {
            double xpc = c == 0 ? xp.X : c == 1 ? xp.Y : xp.Z;
            double ypc = c == 0 ? yp.X : c == 1 ? yp.Y : yp.Z;
            double zpc = c == 0 ? zp.X : c == 1 ? zp.Y : zp.Z;
            double xqr = r == 0 ? xq.X : r == 1 ? xq.Y : xq.Z;
            double yqr = r == 0 ? yq.X : r == 1 ? yq.Y : yq.Z;
            double zqr = r == 0 ? zq.X : r == 1 ? zq.Y : zq.Z;
            return xqr * xpc + yqr * ypc + zqr * zpc;
        }

        // t = q1 − R·p1
        double tx = q1.X - (R(0, 0) * p1.X + R(0, 1) * p1.Y + R(0, 2) * p1.Z);
        double ty = q1.Y - (R(1, 0) * p1.X + R(1, 1) * p1.Y + R(1, 2) * p1.Z);
        double tz = q1.Z - (R(2, 0) * p1.X + R(2, 1) * p1.Y + R(2, 2) * p1.Z);

        m = new Matrix3D(
            R(0, 0), R(1, 0), R(2, 0), 0,
            R(0, 1), R(1, 1), R(2, 1), 0,
            R(0, 2), R(1, 2), R(2, 2), 0,
            tx, ty, tz, 1);
        return true;
    }

    /// <summary>WPF Matrix3D（列向量約定）→ CADability ModOp（行向量約定，3×4）</summary>
    public static CADability.ModOp ToModOp(Matrix3D m) => new(new double[3, 4]
    {
        { m.M11, m.M21, m.M31, m.OffsetX },
        { m.M12, m.M22, m.M32, m.OffsetY },
        { m.M13, m.M23, m.M33, m.OffsetZ },
    });

    public static bool Collinear(Point3D a, Point3D b, Point3D c) =>
        Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-12;

    /// <summary>三點 → 正交座標架（x=1→2 方向、z=平面法向、y=z×x）；共線回傳 false</summary>
    private static bool TryFrame(Point3D a, Point3D b, Point3D c,
        out Vector3D x, out Vector3D y, out Vector3D z)
    {
        x = b - a;
        z = Vector3D.CrossProduct(x, c - a);
        y = default;
        if (x.LengthSquared < 1e-12 || z.LengthSquared < 1e-12) return false;
        x.Normalize();
        z.Normalize();
        y = Vector3D.CrossProduct(z, x);
        return true;
    }
}
