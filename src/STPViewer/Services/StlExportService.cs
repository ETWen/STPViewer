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
/// 網格精度：Current = 目前顯示網格；Fine = 由 B-rep 以較細精度重新三角化（無 B-rep 的來源維持現有網格）。
/// 不提供「較粗」：CADability 的 GetTriangulation 對「比快取粗」的精度要求會直接回傳既有較細快取，
/// 匯入時已三角化過 → 要求粗化實際上拿到同一份網格（實測驗證），故誠實不提供。
/// </summary>
public enum StlMeshQuality { Current, Fine }

/// <summary>
/// STL 匯出：WPF 三角網格 → binary / ASCII STL。
/// STL 本身無單位，約定輸出 mm（scale=1）；inch 交接用 scale = 1/25.4。
/// </summary>
public static class StlExportService
{
    /// <summary>
    /// 三角化精度係數（相對匯入預設）：Fine 較細（三角形多、曲面平滑）。
    /// 實測 CADability 三角形數對精度非單調（0.4× 重算反而比 1× 少 — 重算網格較有效率），
    /// 要明顯更細需 ≤0.15×（test.stp：1× = 30,784 → 0.1× = 42,220 三角形）。
    /// </summary>
    public static double PrecisionFactor(StlMeshQuality q) =>
        q == StlMeshQuality.Fine ? 0.15 : 1.0;

    /// <summary>預估檔案大小：binary = 84 + 50×n；ASCII 每三角形約 230 bytes</summary>
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
    /// 把多個網格寫成一個 STL 檔。退化（零面積）三角形濾除。
    /// 回傳實際寫出的三角形數。
    /// </summary>
    public static int Write(string path, IReadOnlyList<MeshGeometry3D> meshes, bool ascii, double scale) =>
        ascii ? WriteAscii(path, meshes, scale) : WriteBinary(path, meshes, scale);

    private static int WriteBinary(string path, IReadOnlyList<MeshGeometry3D> meshes, double scale)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var bw = new BinaryWriter(fs);

        // 80-byte header（不可以 "solid" 開頭，部分讀取器會誤判為 ASCII）
        var header = new byte[80];
        Encoding.ASCII.GetBytes("STPViewer binary STL (unit: mm x scale)").CopyTo(header, 0);
        bw.Write(header);
        bw.Write(0u); // 三角形數 placeholder，寫完回頭補

        int count = 0;
        foreach (MeshGeometry3D mesh in meshes)
        {
            var pos = mesh.Positions;
            var idx = mesh.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                Point3D a = pos[idx[i]], b = pos[idx[i + 1]], c = pos[idx[i + 2]];
                if (!TryNormal(a, b, c, out Vector3D n)) continue; // 退化三角形
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

    /// <returns>false = 退化三角形（面積 ≈ 0）</returns>
    private static bool TryNormal(Point3D a, Point3D b, Point3D c, out Vector3D n)
    {
        n = Vector3D.CrossProduct(b - a, c - a);
        double len = n.Length;
        if (len < 1e-12) return false;
        n /= len;
        return true;
    }

    // ─── B-rep 重新三角化（精細/較粗匯出用）────────────────────────

    /// <summary>
    /// 由 B-rep 幾何以指定精度係數重新三角化，回傳每面一個 frozen mesh。
    /// CADability 非執行緒安全 → 呼叫端維持循序（可在單一背景執行緒）。
    /// 個別面三角化失敗時跳過（與匯入一致的容錯策略）。
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
    /// 匯入預設精度（與 StepImportService.PrecisionFor 同款 clamp）再乘 factor —
    /// 先 clamp 再乘，保證 factor&lt;1 時嚴格比匯入快取細（否則大殼在匯入被 0.5 上限截住，
    /// 直接乘在 clamp 前會算出比快取粗的值 → CADability 回傳快取、精細無效）。
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
