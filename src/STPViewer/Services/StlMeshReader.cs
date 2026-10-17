using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace STPViewer.Services;

/// <summary>
/// Reads binary and ASCII STL triangles directly into WPF meshes.
/// </summary>
public static class StlMeshReader
{
    /// <summary>Mesh for one solid section; vertices remain unwelded.</summary>
    public record MeshGroup(string Name, Point3DCollection Positions, Int32Collection Indices,
        string? NameKey = null, int NameNumber = 0);

    public static List<MeshGroup> Read(string path)
    {
        return IsBinary(path) ? ReadBinary(path) : ReadAscii(path);
    }

    /// <summary>
    /// Binary STL has exactly 84 + 50 × triangle count bytes; its header may also start with "solid".
    /// </summary>
    private static bool IsBinary(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 84) return false;
        Span<byte> head = stackalloc byte[84];
        using (FileStream fs = File.OpenRead(path))
        {
            int read = 0;
            while (read < 84)
            {
                int n = fs.Read(head[read..]);
                if (n <= 0) break;
                read += n;
            }
            if (read < 84) return false;
        }
        uint count = BitConverter.ToUInt32(head[80..]);
        return fi.Length == 84L + 50L * count;
    }

    private static List<MeshGroup> ReadBinary(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        uint count = BitConverter.ToUInt32(bytes, 80);
        var positions = new Point3DCollection((int)Math.Min(count * 3, int.MaxValue));
        var indices = new Int32Collection((int)Math.Min(count * 3, int.MaxValue));

        var span = bytes.AsSpan();
        int off = 84, vi = 0;
        for (uint t = 0; t < count; t++)
        {
            if (off + 50 > bytes.Length) break;
            off += 12; // WPF computes the normal.
            for (int v = 0; v < 3; v++)
            {
                float x = BitConverter.ToSingle(span[off..]);
                float y = BitConverter.ToSingle(span[(off + 4)..]);
                float z = BitConverter.ToSingle(span[(off + 8)..]);
                positions.Add(new Point3D(x, y, z));
                indices.Add(vi++);
                off += 12;
            }
            off += 2; // attribute byte count
        }
        return new List<MeshGroup> { new(LocalizationService.Get("Runtime.MeshName"), positions, indices, "Runtime.MeshName") };
    }

    /// <summary>
    /// ASCII STL: read vertex triples and start a new group at each solid section.
    /// </summary>
    private static List<MeshGroup> ReadAscii(string path)
    {
        var groups = new List<MeshGroup>();
        Point3DCollection? pos = null;
        Int32Collection? idx = null;
        int vi = 0;

        void FlushGroup()
        {
            if (pos is { Count: > 0 } && idx is not null)
                groups.Add(new MeshGroup(groups.Count == 0 ? LocalizationService.Get("Runtime.MeshName") : LocalizationService.Format("Runtime.MeshNameNumbered", groups.Count + 1), pos, idx,
                    groups.Count == 0 ? "Runtime.MeshName" : "Runtime.MeshNameNumbered", groups.Count + 1));
            pos = null; idx = null; vi = 0;
        }

        char[] sep = { ' ', '\t' };
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                FlushGroup();
                pos = new Point3DCollection();
                idx = new Int32Collection();
                continue;
            }
            if (line.StartsWith("endsolid", StringComparison.OrdinalIgnoreCase))
            {
                FlushGroup();
                continue;
            }
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;

            pos ??= new Point3DCollection();
            idx ??= new Int32Collection();
            string[] tok = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 4) continue;
            if (double.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            {
                pos.Add(new Point3D(x, y, z));
                idx.Add(vi++);
            }
        }
        FlushGroup();
        return groups;
    }
}
