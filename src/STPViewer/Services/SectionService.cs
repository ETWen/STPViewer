using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace STPViewer.Services;

/// <summary>
/// Clips meshes and line segments to the half-space dot(p - p0, n) ≤ 0.
/// The original geometry remains available for restoration.
/// </summary>
public static class SectionService
{
    private static readonly MeshGeometry3D EmptyMesh = CreateEmpty();

    private static MeshGeometry3D CreateEmpty()
    {
        var m = new MeshGeometry3D();
        m.Freeze();
        return m;
    }

    public static MeshGeometry3D ClipMesh(MeshGeometry3D src, Point3D planePoint, Vector3D normal)
    {
        Point3DCollection pos = src.Positions;
        Int32Collection idx = src.TriangleIndices;

        // Cache signed distances for each vertex.
        var dist = new double[pos.Count];
        for (int i = 0; i < pos.Count; i++)
            dist[i] = Vector3D.DotProduct(pos[i] - planePoint, normal);

        var outPos = new Point3DCollection();
        var outIdx = new Int32Collection();

        Span<int> vi = stackalloc int[3];
        Span<Point3D> poly = stackalloc Point3D[4];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            vi[0] = idx[t]; vi[1] = idx[t + 1]; vi[2] = idx[t + 2];
            int keepCount = 0;
            for (int k = 0; k < 3; k++)
                if (dist[vi[k]] <= 0) keepCount++;

            if (keepCount == 0) continue;
            if (keepCount == 3)
            {
                EmitTriangle(outPos, outIdx, pos[vi[0]], pos[vi[1]], pos[vi[2]]);
                continue;
            }

            // Clip the polygon and triangulate it as a fan.
            int n = 0;
            for (int k = 0; k < 3; k++)
            {
                int a = vi[k], b = vi[(k + 1) % 3];
                double da = dist[a], db = dist[b];
                if (da <= 0) poly[n++] = pos[a];
                if ((da <= 0) != (db <= 0))
                {
                    double f = da / (da - db);
                    poly[n++] = pos[a] + (pos[b] - pos[a]) * f;
                }
            }
            if (n >= 3) EmitTriangle(outPos, outIdx, poly[0], poly[1], poly[2]);
            if (n == 4) EmitTriangle(outPos, outIdx, poly[0], poly[2], poly[3]);
        }

        if (outIdx.Count == 0) return EmptyMesh;
        var mesh = new MeshGeometry3D { Positions = outPos, TriangleIndices = outIdx };
        mesh.Freeze();
        return mesh;
    }

    private static void EmitTriangle(Point3DCollection pos, Int32Collection idx,
        Point3D a, Point3D b, Point3D c)
    {
        int i = pos.Count;
        pos.Add(a); pos.Add(b); pos.Add(c);
        idx.Add(i); idx.Add(i + 1); idx.Add(i + 2);
    }

    /// <summary>Clips paired line segment endpoints.</summary>
    public static Point3DCollection ClipSegments(Point3DCollection segments, Point3D planePoint, Vector3D normal)
    {
        var result = new Point3DCollection();
        for (int i = 0; i + 1 < segments.Count; i += 2)
        {
            Point3D a = segments[i], b = segments[i + 1];
            double da = Vector3D.DotProduct(a - planePoint, normal);
            double db = Vector3D.DotProduct(b - planePoint, normal);
            if (da <= 0 && db <= 0) { result.Add(a); result.Add(b); }
            else if (da <= 0 || db <= 0)
            {
                double f = da / (da - db);
                Point3D x = a + (b - a) * f;
                if (da <= 0) { result.Add(a); result.Add(x); }
                else { result.Add(x); result.Add(b); }
            }
        }
        result.Freeze();
        return result;
    }
}
