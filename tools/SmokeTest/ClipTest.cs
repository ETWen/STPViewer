// Section clipping checks the retained side and triangle area.
using System.Windows.Media;
using System.Windows.Media.Media3D;
using STPViewer.Services;

namespace SmokeTest;

internal static class ClipTest
{
    public static int Run()
    {
        int failures = 0;

        // A single triangle with area 50.
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(0, 0, 0), new(10, 0, 0), new(0, 10, 0),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2 },
        };
        mesh.Freeze();

        // A plane at x=20 retains the full triangle.
        var m1 = SectionService.ClipMesh(mesh, new Point3D(20, 0, 0), new Vector3D(1, 0, 0));
        failures += Check("fully retained", Area(m1), 50.0);

        // A plane at x=-1 removes the full triangle.
        var m2 = SectionService.ClipMesh(mesh, new Point3D(-1, 0, 0), new Vector3D(1, 0, 0));
        failures += Check("fully removed", Area(m2), 0.0);

        // At x=5, the retained trapezoid has area 50 - 12.5 = 37.5.
        var m3 = SectionService.ClipMesh(mesh, new Point3D(5, 0, 0), new Vector3D(1, 0, 0));
        failures += Check("half clipped (x<=5)", Area(m3), 37.5);
        foreach (Point3D p in m3.Positions)
            if (p.X > 5 + 1e-9) { Console.WriteLine($"  FAIL: vertex x={p.X} is outside retained side"); failures++; }

        // Reverse the plane normal to retain the smaller triangle.
        var m4 = SectionService.ClipMesh(mesh, new Point3D(5, 0, 0), new Vector3D(-1, 0, 0));
        failures += Check("half clipped (x>=5)", Area(m4), 12.5);

        // Clip a line segment.
        var segs = new Point3DCollection { new(0, 0, 0), new(10, 0, 0) };
        Point3DCollection s1 = SectionService.ClipSegments(segs, new Point3D(4, 0, 0), new Vector3D(1, 0, 0));
        double segLen = s1.Count == 2 ? (s1[1] - s1[0]).Length : -1;
        failures += Check("clipped segment length", segLen, 4.0);

        Console.WriteLine(failures == 0 ? "ClipTest: all passed" : $"ClipTest: {failures} failed");
        return failures;
    }

    private static int Check(string name, double actual, double expected)
    {
        bool ok = Math.Abs(actual - expected) < 1e-9;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name} = {actual} (expected {expected})");
        return ok ? 0 : 1;
    }

    private static double Area(MeshGeometry3D m)
    {
        double a = 0;
        for (int i = 0; i + 2 < m.TriangleIndices.Count; i += 3)
        {
            Vector3D u = m.Positions[m.TriangleIndices[i + 1]] - m.Positions[m.TriangleIndices[i]];
            Vector3D v = m.Positions[m.TriangleIndices[i + 2]] - m.Positions[m.TriangleIndices[i]];
            a += Vector3D.CrossProduct(u, v).Length / 2;
        }
        return a;
    }
}
