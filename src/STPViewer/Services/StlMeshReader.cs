using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace STPViewer.Services;

/// <summary>
/// 直接解析 STL 三角網格（binary / ASCII）→ WPF mesh，**不經過 CADability**。
/// CADability 的 ImportSTL 會為每個三角形建一個 B-rep Face（含 Edge/Vertex 拓樸），
/// 大檔（百萬三角形）需數十分鐘且吃光記憶體；STL 本就無 B-rep，直接讀三角形即可。
/// 實測 100k 三角形：CADability 約 41,000 ms，直接解析約 11 ms。
/// </summary>
public static class StlMeshReader
{
    /// <summary>一個 solid 區塊的網格（頂點未焊接：每三角形 3 個獨立頂點，與 STL 原生一致）</summary>
    public record MeshGroup(string Name, Point3DCollection Positions, Int32Collection Indices);

    public static List<MeshGroup> Read(string path)
    {
        return IsBinary(path) ? ReadBinary(path) : ReadAscii(path);
    }

    /// <summary>
    /// binary STL 判定：檔案大小必須剛好等於 84 + 50×count（count = offset 80 的 uint32）。
    /// 這是最可靠的判別法 — binary 的 80-byte header 常也以 "solid" 開頭，不能只看開頭字串。
    /// </summary>
    private static bool IsBinary(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 84) return false; // 太小，當 ASCII 讓後續拋有意義的錯
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
            if (off + 50 > bytes.Length) break; // 檔案被截斷：保住已解析的部分
            off += 12; // 跳過 normal（渲染用不到；WPF 自算法向）
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
        return new List<MeshGroup> { new("網格", positions, indices) };
    }

    /// <summary>
    /// ASCII STL：逐行掃 "vertex x y z"，每 3 個湊成一個三角形；"solid &lt;name&gt;" 起新群組。
    /// ASCII 大檔罕見（同樣三角數約為 binary 的 5 倍體積），故以正確性為主。
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
                groups.Add(new MeshGroup(groups.Count == 0 ? "網格" : $"網格 {groups.Count + 1}", pos, idx));
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
