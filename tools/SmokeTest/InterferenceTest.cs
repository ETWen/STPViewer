// Interference checks for intersecting, separated, and touching meshes.
using System.Windows.Media;
using System.Windows.Media.Media3D;
using STPViewer.Services;

namespace SmokeTest;

internal static class InterferenceTest
{
    public static int Run()
    {
        int failures = 0;

        // Two tetrahedra overlap when B is offset by 5 along X.
        MeshGeometry3D a = Tetra(new Vector3D(0, 0, 0));
        MeshGeometry3D b = Tetra(new Vector3D(5, 0, 0));
        InterferenceResult r1 = InterferenceService.Check(new[] { a }, new[] { b });
        failures += Check("overlap intersects", r1.Intersects, true);
        failures += Check("overlap has intersection segments", r1.Segments.Count > 0, true);

        // B at X=30 leaves a gap of about 20.
        MeshGeometry3D c = Tetra(new Vector3D(30, 0, 0));
        InterferenceResult r2 = InterferenceService.Check(new[] { a }, new[] { c });
        failures += Check("separated meshes do not intersect", r2.Intersects, false);
        bool gapOk = Math.Abs(r2.GapDistance - 20.0) < 0.01;
        Console.WriteLine($"  {(gapOk ? "PASS" : "FAIL")}: separated gap = {r2.GapDistance:F4} (expected ~20)");
        if (!gapOk) failures++;

        // B at X=10 touches a vertex without penetrating A.
        MeshGeometry3D d = Tetra(new Vector3D(10, 0, 0));
        InterferenceResult r3 = InterferenceService.Check(new[] { a }, new[] { d });
        failures += Check("touching is not penetration", r3.Intersects, false);
        bool touchOk = r3.GapDistance < 0.01;
        Console.WriteLine($"  {(touchOk ? "PASS" : "FAIL")}: touching gap = {r3.GapDistance:F6} (expected ~0)");
        if (!touchOk) failures++;

        Console.WriteLine(failures == 0 ? "InterferenceTest: all passed" : $"InterferenceTest: {failures} failed");
        return failures;
    }

    private static int Check(string name, bool actual, bool expected)
    {
        bool ok = actual == expected;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name} ({actual})");
        return ok ? 0 : 1;
    }

    /// <summary>A tetrahedron with side length 10, translated by offset.</summary>
    private static MeshGeometry3D Tetra(Vector3D offset)
    {
        Point3D P(double x, double y, double z) => new Point3D(x, y, z) + offset;
        var m = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                P(0, 0, 0), P(10, 0, 0), P(0, 10, 0), P(0, 0, 10),
            },
            TriangleIndices = new Int32Collection
            {
                0, 1, 2,  0, 1, 3,  0, 2, 3,  1, 2, 3,
            },
        };
        m.Freeze();
        return m;
    }
}
