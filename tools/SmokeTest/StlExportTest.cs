using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using STPViewer.Services;
using IOPath = System.IO.Path; // Avoid the CADability.GeoObject.Path name collision.

namespace SmokeTest;

/// <summary>
/// Headless STL export checks:
/// binary round trip, ASCII facet structure, and degenerate triangle filtering.
/// </summary>
public static class StlExportTest
{
    public static int Run(string? stepPath = null)
    {
        Console.WriteLine("=== StlExportTest ===");
        int failed = 0;
        string dir = IOPath.Combine(IOPath.GetTempPath(), "stpviewer_stl_test");
        Directory.CreateDirectory(dir);

        MeshGeometry3D cube = BuildCube(10); // 10x10x10 mm, 12 triangles.

        // Binary triangle count and scaled coordinate range.
        {
            string f = IOPath.Combine(dir, "cube_bin.stl");
            double scale = 1.0 / 25.4; // mm to inches.
            int written = StlExportService.Write(f, new[] { cube }, ascii: false, scale);
            (uint count, float min, float max) = ReadBinary(f);
            bool ok = written == 12 && count == 12
                      && Math.Abs(min - 0f) < 1e-5 && Math.Abs(max - 10f / 25.4f) < 1e-4;
            Report(ref failed, ok, $"binary round trip: 12 triangles, inch range [{min:F4}, {max:F4}]");
        }

        // ASCII facet count and structure.
        {
            string f = IOPath.Combine(dir, "cube_ascii.stl");
            int written = StlExportService.Write(f, new[] { cube }, ascii: true, scale: 1.0);
            string text = File.ReadAllText(f);
            int facets = CountOccurrences(text, "facet normal");
            bool ok = written == 12 && facets == 12
                      && text.StartsWith("solid ", StringComparison.Ordinal)
                      && text.Contains("endsolid");
            Report(ref failed, ok, $"ASCII structure valid, facets = {facets}");
        }

        // A zero-area triangle must be filtered from the export.
        {
            var degenerate = new MeshGeometry3D();
            degenerate.Positions.Add(new Point3D(0, 0, 0));
            degenerate.Positions.Add(new Point3D(1, 1, 1));
            degenerate.Positions.Add(new Point3D(2, 2, 2)); // Collinear: zero area.
            degenerate.TriangleIndices.Add(0);
            degenerate.TriangleIndices.Add(1);
            degenerate.TriangleIndices.Add(2);
            degenerate.Freeze();

            string f = IOPath.Combine(dir, "cube_degen.stl");
            int written = StlExportService.Write(f, new[] { cube, degenerate }, ascii: false, scale: 1.0);
            (uint count, _, _) = ReadBinary(f);
            bool ok = written == 12 && count == 12;
            Report(ref failed, ok, $"degenerate triangle filtered ({written} written)");
        }

        // Optional real STEP input: retessellate B-rep geometry at fine precision.
        if (stepPath is not null)
        {
            var data = new StepImportService().Import(stepPath);
            var geos = Collect(data.Root).ToList();
            int importTris = data.Root.TriangleCount;
            Console.WriteLine($"  Source {IOPath.GetFileName(stepPath)}: {geos.Count} B-rep objects, {importTris:N0} imported triangles");

            var meshes = StlExportService.Tessellate(
                geos, StlExportService.PrecisionFactor(StlMeshQuality.Fine));
            string f = IOPath.Combine(dir, "step_fine.stl");
            int written = StlExportService.Write(f, meshes, ascii: false, scale: 1.0);
            (uint count, _, _) = ReadBinary(f);
            Report(ref failed, meshes.Count > 0 && written > 0 && count == written,
                $"fine retessellation: {meshes.Count} faces, {written:N0} triangles");
            // CADability's triangle count is not monotonic in precision; 0.15x is measurably finer.
            Report(ref failed, written > importTris,
                $"fine ({written:N0}) > imported default ({importTris:N0})");
        }

        Console.WriteLine(failed == 0 ? "StlExportTest: PASS" : $"StlExportTest: FAIL ({failed} checks)");
        return failed;
    }

    private static IEnumerable<CADability.GeoObject.IGeoObject> Collect(ImportedNode n)
    {
        foreach (CADability.GeoObject.IGeoObject g in n.SourceGeos) yield return g;
        foreach (ImportedNode c in n.Children)
            foreach (CADability.GeoObject.IGeoObject g in Collect(c))
                yield return g;
    }

    private static void Report(ref int failed, bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failed++;
    }

    /// <summary>Axis-aligned cube from 0 to size, with 12 triangles.</summary>
    private static MeshGeometry3D BuildCube(double s)
    {
        var m = new MeshGeometry3D();
        Point3D[] v =
        {
            new(0, 0, 0), new(s, 0, 0), new(s, s, 0), new(0, s, 0),
            new(0, 0, s), new(s, 0, s), new(s, s, s), new(0, s, s),
        };
        foreach (Point3D p in v) m.Positions.Add(p);
        int[] quads = { 0,3,2,1,  4,5,6,7,  0,1,5,4,  1,2,6,5,  2,3,7,6,  3,0,4,7 };
        for (int q = 0; q < quads.Length; q += 4)
        {
            m.TriangleIndices.Add(quads[q]); m.TriangleIndices.Add(quads[q + 1]); m.TriangleIndices.Add(quads[q + 2]);
            m.TriangleIndices.Add(quads[q]); m.TriangleIndices.Add(quads[q + 2]); m.TriangleIndices.Add(quads[q + 3]);
        }
        m.Freeze();
        return m;
    }

    private static (uint Count, float Min, float Max) ReadBinary(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        byte[] header = br.ReadBytes(80);
        if (Encoding.ASCII.GetString(header, 0, 5) == "solid")
            throw new InvalidDataException("Binary STL header must not begin with solid");
        uint count = br.ReadUInt32();
        float min = float.MaxValue, max = float.MinValue;
        for (uint t = 0; t < count; t++)
        {
            br.ReadBytes(12); // normal
            for (int k = 0; k < 9; k++)
            {
                float f = br.ReadSingle();
                min = Math.Min(min, f);
                max = Math.Max(max, f);
            }
            br.ReadUInt16(); // attribute
        }
        return (count, min, max);
    }

    private static int CountOccurrences(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
