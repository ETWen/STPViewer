using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CADability;
using CADability.GeoObject;

namespace STPViewer.Services;

/// <summary>
/// Mesh quality: Current uses the displayed mesh; Fine retessellates B-rep geometry.
/// </summary>
public enum StlMeshQuality { Current, Fine }

/// <summary>
/// Writes WPF triangle meshes as binary or ASCII STL; scale=1 uses millimeters.
/// </summary>
public static class StlExportService
{
    /// <summary>
    /// Tessellation factor relative to import precision; 0.15 produces a finer mesh.
    /// </summary>
    public static double PrecisionFactor(StlMeshQuality q) =>
        q == StlMeshQuality.Fine ? 0.15 : 1.0;

    /// <summary>Estimated file size: 84 + 50 bytes per binary triangle, about 230 per ASCII triangle.</summary>
    public static long EstimateBytes(long triangles, bool ascii) =>
        ascii ? 20 + triangles * 230 : 84 + triangles * 50;

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// Writes meshes to one STL file, skipping zero-area triangles. Returns the triangle count.
    /// </summary>
    public static int Write(string path, IReadOnlyList<MeshGeometry3D> meshes, bool ascii, double scale) =>
        ascii ? WriteAscii(path, meshes, scale) : WriteBinary(path, meshes, scale);

    private static int WriteBinary(string path, IReadOnlyList<MeshGeometry3D> meshes, double scale)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var bw = new BinaryWriter(fs);

        // Avoid "solid" in the header; some readers would mistake it for ASCII.
        var header = new byte[80];
        Encoding.ASCII.GetBytes("STPViewer binary STL (unit: mm x scale)").CopyTo(header, 0);
        bw.Write(header);
        bw.Write(0u); // Backfilled after writing triangles.

        int count = 0;
        foreach (MeshGeometry3D mesh in meshes)
        {
            var pos = mesh.Positions;
            var idx = mesh.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                Point3D a = pos[idx[i]], b = pos[idx[i + 1]], c = pos[idx[i + 2]];
                if (!TryNormal(a, b, c, out Vector3D n)) continue;
                bw.Write((float)n.X); bw.Write((float)n.Y); bw.Write((float)n.Z);
                WriteVertex(bw, a, scale);
                WriteVertex(bw, b, scale);
                WriteVertex(bw, c, scale);
                bw.Write((ushort)0); // attribute byte count
                count++;
            }
        }

        fs.Seek(80, SeekOrigin.Begin);
        bw.Write((uint)count);
        return count;
    }

    private static void WriteVertex(BinaryWriter bw, Point3D p, double scale)
    {
        bw.Write((float)(p.X * scale));
        bw.Write((float)(p.Y * scale));
        bw.Write((float)(p.Z * scale));
    }

    private static int WriteAscii(string path, IReadOnlyList<MeshGeometry3D> meshes, double scale)
    {
        var inv = CultureInfo.InvariantCulture;
        using var sw = new StreamWriter(path, append: false, Encoding.ASCII, 1 << 16);
        sw.WriteLine("solid STPViewer");

        int count = 0;
        foreach (MeshGeometry3D mesh in meshes)
        {
            var pos = mesh.Positions;
            var idx = mesh.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                Point3D a = pos[idx[i]], b = pos[idx[i + 1]], c = pos[idx[i + 2]];
                if (!TryNormal(a, b, c, out Vector3D n)) continue;
                sw.WriteLine(string.Format(inv, "  facet normal {0:e6} {1:e6} {2:e6}", n.X, n.Y, n.Z));
                sw.WriteLine("    outer loop");
                WriteAsciiVertex(sw, a, scale, inv);
                WriteAsciiVertex(sw, b, scale, inv);
                WriteAsciiVertex(sw, c, scale, inv);
                sw.WriteLine("    endloop");
                sw.WriteLine("  endfacet");
                count++;
            }
        }

        sw.WriteLine("endsolid STPViewer");
        return count;
    }

    private static void WriteAsciiVertex(StreamWriter sw, Point3D p, double scale, CultureInfo inv) =>
        sw.WriteLine(string.Format(inv, "      vertex {0:e6} {1:e6} {2:e6}",
            p.X * scale, p.Y * scale, p.Z * scale));

    /// <returns>False for a zero-area triangle.</returns>
    private static bool TryNormal(Point3D a, Point3D b, Point3D c, out Vector3D n)
    {
        n = Vector3D.CrossProduct(b - a, c - a);
        double len = n.Length;
        if (len < 1e-12) return false;
        n /= len;
        return true;
    }

    // B-rep retessellation

    /// <summary>
    /// Retessellates B-rep geometry into frozen meshes, one per face.
    /// Call sequentially because CADability tessellation is not thread-safe.
    /// </summary>
    public static List<MeshGeometry3D> Tessellate(IEnumerable<IGeoObject> geos, double precisionFactor)
    {
        var meshes = new List<MeshGeometry3D>();
        foreach (IGeoObject g in geos)
        {
            switch (g)
            {
                case Solid solid:
                    foreach (Shell sh in solid.Shells) TessellateShell(sh, precisionFactor, meshes);
                    break;
                case Shell shell:
                    TessellateShell(shell, precisionFactor, meshes);
                    break;
                case Face face:
                    AddFaceMesh(face, PrecisionFor(SafeBounds(face), precisionFactor), meshes);
                    break;
            }
        }
        return meshes;
    }

    private static void TessellateShell(Shell shell, double factor, List<MeshGeometry3D> meshes)
    {
        double precision = PrecisionFor(SafeBounds(shell), factor);
        foreach (Face f in shell.Faces) AddFaceMesh(f, precision, meshes);
    }

    /// <summary>
    /// Clamp the import precision before applying factor so Fine is finer than the cached mesh.
    /// </summary>
    private static double PrecisionFor(BoundingCube bc, double factor)
    {
        double diag;
        try { diag = bc.DiagonalLength; }
        catch { diag = 100; }
        if (diag <= 0 || double.IsNaN(diag)) diag = 100;
        double importDefault = Math.Clamp(diag * 0.0015, 0.02, 0.5);
        return Math.Max(importDefault * factor, 0.005);
    }

    private static BoundingCube SafeBounds(IGeoObject g)
    {
        try { return g.GetBoundingCube(); }
        catch { return new BoundingCube(); }
    }

    private static void AddFaceMesh(Face face, double precision, List<MeshGeometry3D> meshes)
    {
        GeoPoint[] pts; int[] ind;
        try { face.GetTriangulation(precision, out pts, out _, out ind, out _); }
        catch { return; }
        if (pts is null || ind is null || ind.Length < 3) return;

        var positions = new Point3DCollection(pts.Length);
        foreach (GeoPoint p in pts) positions.Add(new Point3D(p.x, p.y, p.z));
        var indices = new Int32Collection(ind.Length);
        foreach (int i in ind) indices.Add(i);

        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        meshes.Add(mesh);
    }
}
