// 三點對齊剛體變換數學驗證：
// (1) 已知變換還原 (2) Matrix3D ↔ ModOp 兩種表示一致 (3) 共線拒絕
using System.Windows.Media.Media3D;
using STPViewer.Services;

namespace SmokeTest;

internal static class AlignTest
{
    public static int Run()
    {
        int failures = 0;

        // ── (1) 已知剛體變換：繞 Z 軸 90° + 平移 (5,-3,2)，三點求解後應還原同一變換 ──
        var truth = Matrix3D.Identity;
        truth.RotateAt(new Quaternion(new Vector3D(0, 0, 1), 90), new Point3D(2, 1, 0));
        truth.Translate(new Vector3D(5, -3, 2));

        var p1 = new Point3D(0, 0, 0);
        var p2 = new Point3D(10, 0, 0);
        var p3 = new Point3D(0, 7, 3);  // 不共線、不在同平面軸上
        Point3D q1 = truth.Transform(p1), q2 = truth.Transform(p2), q3 = truth.Transform(p3);

        bool ok = RigidAlign.TryRigidTransform(p1, p2, p3, q1, q2, q3, out Matrix3D m);
        failures += Check("三點求解成功", ok, true);

        // 三個對應點要精確貼合
        failures += CheckPoint("p1→q1", m.Transform(p1), q1);
        failures += CheckPoint("p2→q2", m.Transform(p2), q2);
        failures += CheckPoint("p3→q3", m.Transform(p3), q3);

        // 剛體變換唯一性：任意第 4 點也應該跟著正確變換
        var r = new Point3D(-4, 12, 8);
        failures += CheckPoint("任意第4點", m.Transform(r), truth.Transform(r));

        // ── (2) ToModOp：CADability ModOp 與 WPF Matrix3D 對同一點要算出同樣結果 ──
        CADability.ModOp op = RigidAlign.ToModOp(m);
        foreach (Point3D p in new[] { p1, p2, p3, r, new Point3D(1.5, -2.25, 9) })
        {
            CADability.GeoPoint gp = op * new CADability.GeoPoint(p.X, p.Y, p.Z);
            Point3D wpf = m.Transform(p);
            bool same = Dist(new Point3D(gp.x, gp.y, gp.z), wpf) < 1e-9;
            if (!same)
            {
                System.Console.WriteLine($"  FAIL: ModOp/Matrix3D 不一致 @ ({p.X},{p.Y},{p.Z})");
                failures++;
            }
        }
        System.Console.WriteLine($"  {(failures == 0 ? "PASS" : "----")}: ModOp ↔ Matrix3D 一致（5 個測試點）");

        // ── (3) 共線拒絕 ──
        bool collinearRejected = !RigidAlign.TryRigidTransform(
            new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(2, 0, 0), // 共線
            q1, q2, q3, out _);
        failures += Check("來源共線 → 拒絕", collinearRejected, true);
        failures += Check("Collinear 判定", RigidAlign.Collinear(
            new Point3D(0, 0, 0), new Point3D(1, 1, 1), new Point3D(3, 3, 3)), true);

        System.Console.WriteLine(failures == 0 ? "AlignTest: 全部通過" : $"AlignTest: {failures} 項失敗");
        return failures;
    }

    private static int Check(string name, bool actual, bool expected)
    {
        bool ok = actual == expected;
        System.Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}（{actual}）");
        return ok ? 0 : 1;
    }

    private static int CheckPoint(string name, Point3D actual, Point3D expected)
    {
        double d = Dist(actual, expected);
        bool ok = d < 1e-9;
        System.Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name} 誤差 {d:E2}");
        return ok ? 0 : 1;
    }

    private static double Dist(Point3D a, Point3D b) => (b - a).Length;
}
