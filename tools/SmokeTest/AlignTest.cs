// Three-point alignment checks: known transform, Matrix3D/ModOp agreement, and collinear rejection.
using System.Windows.Media.Media3D;
using STPViewer.Services;

namespace SmokeTest;

internal static class AlignTest
{
    public static int Run()
    {
        int failures = 0;

        // Recover a known 90-degree Z rotation followed by translation (5,-3,2).
        var truth = Matrix3D.Identity;
        truth.RotateAt(new Quaternion(new Vector3D(0, 0, 1), 90), new Point3D(2, 1, 0));
        truth.Translate(new Vector3D(5, -3, 2));

        var p1 = new Point3D(0, 0, 0);
        var p2 = new Point3D(10, 0, 0);
        var p3 = new Point3D(0, 7, 3);  // Not collinear with the first two points.
        Point3D q1 = truth.Transform(p1), q2 = truth.Transform(p2), q3 = truth.Transform(p3);

        bool ok = RigidAlign.TryRigidTransform(p1, p2, p3, q1, q2, q3, out Matrix3D m);
        failures += Check("three-point transform solved", ok, true);

        // Corresponding points must coincide.
        failures += CheckPoint("p1→q1", m.Transform(p1), q1);
        failures += CheckPoint("p2→q2", m.Transform(p2), q2);
        failures += CheckPoint("p3→q3", m.Transform(p3), q3);

        // An independent fourth point checks the full rigid transform.
        var r = new Point3D(-4, 12, 8);
        failures += CheckPoint("fourth point", m.Transform(r), truth.Transform(r));

        // CADability ModOp and WPF Matrix3D must transform points identically.
        CADability.ModOp op = RigidAlign.ToModOp(m);
        foreach (Point3D p in new[] { p1, p2, p3, r, new Point3D(1.5, -2.25, 9) })
        {
            CADability.GeoPoint gp = op * new CADability.GeoPoint(p.X, p.Y, p.Z);
            Point3D wpf = m.Transform(p);
            bool same = Dist(new Point3D(gp.x, gp.y, gp.z), wpf) < 1e-9;
            if (!same)
            {
                System.Console.WriteLine($"  FAIL: ModOp/Matrix3D mismatch at ({p.X},{p.Y},{p.Z})");
                failures++;
            }
        }
        System.Console.WriteLine($"  {(failures == 0 ? "PASS" : "----")}: ModOp/Matrix3D agree at five points");

        // Collinear source points must be rejected.
        bool collinearRejected = !RigidAlign.TryRigidTransform(
            new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(2, 0, 0), // Collinear.
            q1, q2, q3, out _);
        failures += Check("collinear source rejected", collinearRejected, true);
        failures += Check("collinearity detection", RigidAlign.Collinear(
            new Point3D(0, 0, 0), new Point3D(1, 1, 1), new Point3D(3, 3, 3)), true);

        System.Console.WriteLine(failures == 0 ? "AlignTest: all passed" : $"AlignTest: {failures} failed");
        return failures;
    }

    private static int Check(string name, bool actual, bool expected)
    {
        bool ok = actual == expected;
        System.Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name} ({actual})");
        return ok ? 0 : 1;
    }

    private static int CheckPoint(string name, Point3D actual, Point3D expected)
    {
        double d = Dist(actual, expected);
        bool ok = d < 1e-9;
        System.Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name} error {d:E2}");
        return ok ? 0 : 1;
    }

    private static double Dist(Point3D a, Point3D b) => (b - a).Length;
}
