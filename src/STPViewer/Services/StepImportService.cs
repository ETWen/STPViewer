using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CADability;
using CADability.GeoObject;
using IOPath = System.IO.Path; // CADability.GeoObject.Path（曲線）撞名

namespace STPViewer.Services;

/// <summary>單一 B-rep Face 的三角網格（mesh 已 Freeze，可跨執行緒）；STL 來源 BrepFace 為 null</summary>
public record ImportedFace(Face? BrepFace, MeshGeometry3D Mesh);

/// <summary>匯入後的階層節點：group（裝配）或 leaf（零件幾何）</summary>
public class ImportedNode
{
    public string Name { get; set; } = "";
    public List<ImportedNode> Children { get; } = new();

    // ── leaf 幾何 ──
    public List<ImportedFace> Faces { get; } = new();
    public Point3DCollection? EdgeSegments { get; set; }   // 輪廓線（成對端點，已 Freeze）
    public bool HasBrep { get; set; } = true;              // STL / 純曲線 = false

    /// <summary>來源 B-rep 物件（Solid/Shell/Face/曲線）— 零件平移時整體 Modify 用</summary>
    public List<CADability.GeoObject.IGeoObject> SourceGeos { get; } = new();

    // ── 統計（group 節點由 Aggregate 彙總）──
    public int SolidCount { get; set; }
    public int FaceCount { get; set; }
    public int TriangleCount { get; set; }
    public Rect3D Bounds { get; set; } = Rect3D.Empty;

    public bool IsLeaf => Children.Count == 0;
}

public record ImportedFileData(string FilePath, ImportedNode Root);

/// <summary>
/// CAD 檔匯入（STEP / STL / DXF）。STEP 以 HierarchyToBlocks 還原裝配樹。
/// 設計為在背景執行緒呼叫；回傳的 Freezable 全部已 Freeze。
/// </summary>
public class StepImportService
{
    /// <summary>匯入階段回報（耗時分解），UI 可顯示於狀態列；null 則靜默</summary>
    public Action<string>? Progress { get; set; }

    private long _triTicks, _edgeTicks; // 三角化 / 邊取樣 累計耗時（TimeSpan ticks，避免逐面 ms 截斷歸零）
    private double TriMs => new TimeSpan(_triTicks).TotalMilliseconds;
    private double EdgeMs => new TimeSpan(_edgeTicks).TotalMilliseconds;

    /// <summary>延後的 leaf 幾何工作（三角化+邊取樣）：樹走訪時收集，之後平行執行。
    /// 平行粒度 = 整個 leaf（同 leaf 的面共用 Edge，不可面級平行）。</summary>
    private readonly List<Action> _leafWork = new();

    /// <summary>平行三角化偶發失敗的面（CADability 跨 leaf 仍有少量共享狀態）→ 平行結束後循序重試</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<(ImportedNode Leaf, HashSet<Edge> EdgeSet, List<(Face F, double Prec)> Failed)> _retry = new();

    public ImportedFileData Import(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("找不到檔案", filePath);

        _triTicks = _edgeTicks = 0;
        _leafWork.Clear();
        _retry.Clear();
        var root = new ImportedNode { Name = IOPath.GetFileNameWithoutExtension(filePath) };
        switch (IOPath.GetExtension(filePath).ToLowerInvariant())
        {
            case ".stp" or ".step": ImportStepFile(filePath, root); break;
            case ".stl": ImportStlFile(filePath, root); break;
            case ".dxf": ImportDxfFile(filePath, root); break;
            default:
                throw new InvalidDataException("不支援的格式（支援 .stp / .step / .stl / .dxf；CADability 無 IGES reader）");
        }

        if (_leafWork.Count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Progress?.Invoke($"三角化 {_leafWork.Count} 個零件（平行）…");
            System.Threading.Tasks.Parallel.ForEach(_leafWork, w =>
            {
                try { w(); }
                catch { /* 個別零件失敗不讓整檔失敗 */ }
            });
            _leafWork.Clear();

            // 平行下偶發失敗的面：競爭已結束，循序重試一輪（FinishLeaf 也延到此時才跑）
            int retried = 0;
            foreach ((ImportedNode leaf, HashSet<Edge> edgeSet, var failed) in _retry)
            {
                foreach ((Face f, double prec) in failed)
                    if (AddFace(f, prec, leaf, edgeSet)) retried++;
                FinishLeaf(leaf, edgeSet);
            }
            _retry.Clear();
            Progress?.Invoke($"幾何處理 {sw.ElapsedMilliseconds:N0} ms" +
                $"（三角化 {TriMs:N0} ms、邊取樣 {EdgeMs:N0} ms{(retried > 0 ? $"、重試補回 {retried} 面" : "")}）");
        }

        Prune(root);
        Aggregate(root);
        if (root.SolidCount == 0 && root.FaceCount == 0 && CountSegments(root) == 0)
            throw new InvalidDataException("檔案內沒有可顯示的幾何");
        return new ImportedFileData(filePath, root);
    }

    public static bool IsSupported(string path) =>
        IOPath.GetExtension(path).ToLowerInvariant() is ".stp" or ".step" or ".stl" or ".dxf";

    // ─── STEP ────────────────────────────────────────────────────

    private void ImportStepFile(string path, ImportedNode root)
    {
        Progress?.Invoke("解析 STEP（大檔可能需數分鐘）…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var importer = new ImportStep { HierarchyToBlocks = true };
        GeoObjectList list = importer.Read(path)
            ?? throw new InvalidDataException("CADability 無法解析此 STEP 檔");
        Progress?.Invoke($"STEP 解析 {sw.ElapsedMilliseconds:N0} ms");
        BuildChildren(root, EnumerateGeo(list));
    }

    // ─── STL（純網格，無 B-rep）──
    // 直接解析三角形（StlMeshReader），**不經過 CADability**。CADability 的 ImportSTL 會為每個
    // 三角形建一個 B-rep Face（含 Edge/Vertex 拓樸），百萬三角形需數十分鐘且吃光記憶體
    // （實測 100k 三角形：CADability ~41 s，直接解析 ~11 ms）。STL 本就無 B-rep，讀三角形即可。
    // ⚠️ 因此 STL leaf 沒有 SourceGeos（CADability B-rep）→ 不進 STEP 匯出（本就非 STEP B-rep 幾何）；
    //    剛體變換仍走網格層（TransformRoot 的 SourceGeos 迴圈空轉、mesh 照樣變換），量測走網格近似不受影響。

    private void ImportStlFile(string path, ImportedNode root)
    {
        Progress?.Invoke("解析 STL（直接讀三角形）…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<StlMeshReader.MeshGroup> groups = StlMeshReader.Read(path);
        if (groups.Count == 0)
            throw new InvalidDataException("STL 檔內沒有三角形");

        foreach (StlMeshReader.MeshGroup g in groups)
        {
            if (g.Positions.Count == 0) continue;
            g.Positions.Freeze();
            g.Indices.Freeze();
            var mesh = new MeshGeometry3D { Positions = g.Positions, TriangleIndices = g.Indices };
            mesh.Freeze();
            int tris = g.Indices.Count / 3;
            var leaf = new ImportedNode
            {
                Name = g.Name,
                HasBrep = false,
                SolidCount = 1,
                FaceCount = tris,      // STL 無 B-rep 面 → 以三角形數代表
                TriangleCount = tris,
                Bounds = BoundsOfMesh(mesh),
            };
            leaf.Faces.Add(new ImportedFace(null, mesh));
            root.Children.Add(leaf);
        }
        Progress?.Invoke($"STL 解析 {sw.ElapsedMilliseconds:N0} ms（{root.Children.Count} 個網格）");
    }

    // ─── DXF（多為線架構；Solid/Face 走同一套，曲線取樣成線段）──

    private void ImportDxfFile(string path, ImportedNode root)
    {
        var import = new CADability.DXF.Import(path);
        Project project = import.Project
            ?? throw new InvalidDataException("無法解析 DXF 檔");
        Model model = project.GetActiveModel();
        BuildChildren(root, EnumerateGeo(model.AllObjects));
    }

    // ─── 階層走訪 ────────────────────────────────────────────────

    private static IEnumerable<IGeoObject> EnumerateGeo(System.Collections.IEnumerable list)
    {
        foreach (object o in list)
            if (o is IGeoObject g) yield return g;
    }

    private static IEnumerable<IGeoObject> ChildrenOf(IGeoObject go)
    {
        for (int i = 0; i < go.NumChildren; i++)
            if (go.Child(i) is IGeoObject c) yield return c;
    }

    private void BuildChildren(ImportedNode parent, IEnumerable<IGeoObject> objects)
    {
        var looseFaces = new List<Face>();
        var looseCurves = new List<IGeoObject>(); // 皆為 ICurve

        foreach (IGeoObject go in objects)
        {
            switch (go)
            {
                case Solid solid:
                    parent.Children.Add(SolidNode(solid));
                    break;
                case Shell shell:
                    parent.Children.Add(ShellNode(shell));
                    break;
                case Face face:
                    looseFaces.Add(face);
                    break;
                case Block block:
                {
                    ImportedNode? n = BlockNode(block);
                    if (n is not null) parent.Children.Add(n);
                    break;
                }
                default:
                    if (go is ICurve) looseCurves.Add(go);
                    else if (go.NumChildren > 0) BuildChildren(parent, ChildrenOf(go));
                    break;
            }
        }

        if (looseFaces.Count > 0)
        {
            var leaf = new ImportedNode { Name = "面" };
            leaf.SourceGeos.AddRange(looseFaces);
            _leafWork.Add(() =>
            {
                var edgeSet = new HashSet<Edge>();
                var failed = new List<(Face, double)>();
                foreach (Face f in looseFaces)
                {
                    double prec = PrecisionFor(SafeBounds(() => f.GetBoundingCube()));
                    if (!AddFace(f, prec, leaf, edgeSet)) failed.Add((f, prec));
                }
                if (failed.Count > 0) _retry.Add((leaf, edgeSet, failed));
                else FinishLeaf(leaf, edgeSet);
            });
            parent.Children.Add(leaf); // 三角化延後，空 leaf 由 Prune 收掉
        }
        if (looseCurves.Count > 0)
        {
            Point3DCollection segs = SampleCurves(looseCurves.OfType<ICurve>());
            if (segs.Count > 0)
            {
                segs.Freeze();
                var leaf = new ImportedNode
                {
                    Name = "曲線",
                    HasBrep = false,
                    EdgeSegments = segs,
                    Bounds = BoundsOfPoints(segs),
                };
                leaf.SourceGeos.AddRange(looseCurves);
                parent.Children.Add(leaf);
            }
        }
    }

    private ImportedNode? BlockNode(Block block)
    {
        // Block 只有單一同名 Solid → 折疊成 leaf（STEP 常見的 product→solid 包裝）
        if (block.NumChildren == 1 && block.Child(0) is Solid s)
        {
            ImportedNode leaf = SolidNode(s);
            if (!string.IsNullOrWhiteSpace(block.Name)) leaf.Name = block.Name;
            return leaf;
        }

        var node = new ImportedNode
        {
            Name = string.IsNullOrWhiteSpace(block.Name) ? "組件" : block.Name,
        };
        BuildChildren(node, ChildrenOf(block));
        if (node.Children.Count == 0) return null;

        // 單鏈折疊：group 只有一個子節點且名稱相同/空白 → 去掉中間層
        if (node.Children.Count == 1)
        {
            ImportedNode only = node.Children[0];
            if (string.IsNullOrWhiteSpace(only.Name) || only.Name == node.Name)
            {
                only.Name = node.Name;
                return only;
            }
        }
        return node;
    }

    private ImportedNode SolidNode(Solid solid)
    {
        var leaf = new ImportedNode
        {
            Name = string.IsNullOrWhiteSpace(solid.NameOrEmpty) ? "Solid" : solid.NameOrEmpty,
            SolidCount = 1,
        };
        leaf.SourceGeos.Add(solid);
        _leafWork.Add(() =>
        {
            var edgeSet = new HashSet<Edge>();
            var failed = new List<(Face, double)>();
            foreach (Shell sh in solid.Shells)
            {
                double precision = PrecisionFor(SafeBounds(() => sh.GetBoundingCube()));
                foreach (Face f in sh.Faces)
                    if (!AddFace(f, precision, leaf, edgeSet)) failed.Add((f, precision));
            }
            if (failed.Count > 0) _retry.Add((leaf, edgeSet, failed));
            else FinishLeaf(leaf, edgeSet);
        });
        return leaf;
    }

    private ImportedNode ShellNode(Shell shell)
    {
        var leaf = new ImportedNode
        {
            Name = string.IsNullOrWhiteSpace(shell.NameOrEmpty) ? "Shell" : shell.NameOrEmpty,
        };
        leaf.SourceGeos.Add(shell);
        _leafWork.Add(() =>
        {
            var edgeSet = new HashSet<Edge>();
            var failed = new List<(Face, double)>();
            double precision = PrecisionFor(SafeBounds(() => shell.GetBoundingCube()));
            foreach (Face f in shell.Faces)
                if (!AddFace(f, precision, leaf, edgeSet)) failed.Add((f, precision));
            if (failed.Count > 0) _retry.Add((leaf, edgeSet, failed));
            else FinishLeaf(leaf, edgeSet);
        });
        return leaf;
    }

    // ─── 幾何處理 ────────────────────────────────────────────────

    private static BoundingCube SafeBounds(Func<BoundingCube> get)
    {
        try { return get(); }
        catch { return new BoundingCube(); }
    }

    /// <summary>三角化精度依物件大小調整：對角線 × 0.0015，限制在 [0.02, 0.5] mm</summary>
    private static double PrecisionFor(BoundingCube bc)
    {
        double diag;
        try { diag = bc.DiagonalLength; }
        catch { diag = 100; }
        if (double.IsNaN(diag) || double.IsInfinity(diag) || diag <= 0) diag = 100;
        return Math.Clamp(diag * 0.0015, 0.02, 0.5);
    }

    /// <returns>false = 三角化失敗（平行下偶發，可循序重試）</returns>
    private bool AddFace(Face face, double precision, ImportedNode leaf, HashSet<Edge> edgeSet)
    {
        GeoPoint[] points; int[] indices;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            face.GetTriangulation(precision, out points, out _, out indices, out _);
        }
        catch
        {
            return false; // 個別面三角化失敗時跳過，不讓整檔失敗
        }
        finally
        {
            System.Threading.Interlocked.Add(ref _triTicks, sw.Elapsed.Ticks);
        }
        if (points is null || indices is null || indices.Length < 3) return false;

        var positions = new Point3DCollection(points.Length);
        foreach (GeoPoint p in points)
            positions.Add(new Point3D(p.x, p.y, p.z));

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = new Int32Collection(indices),
        };
        mesh.Freeze();
        leaf.Faces.Add(new ImportedFace(face, mesh));

        try
        {
            foreach (Edge e in face.AllEdges) edgeSet.Add(e);
        }
        catch { /* 邊收集失敗不影響面渲染 */ }
        return true;
    }

    /// <summary>leaf 收尾：邊取樣 + 統計 + 邊界</summary>
    private void FinishLeaf(ImportedNode leaf, HashSet<Edge> edgeSet)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segments = new Point3DCollection();
        foreach (Edge e in edgeSet)
        {
            ICurve? curve;
            try { curve = e.Curve3D; }
            catch { continue; }
            if (curve is null) continue;
            AppendCurveSegments(segments, curve);
        }
        segments.Freeze();
        leaf.EdgeSegments = segments;
        System.Threading.Interlocked.Add(ref _edgeTicks, sw.Elapsed.Ticks);

        leaf.FaceCount = leaf.Faces.Count;
        int tri = 0;
        Rect3D bounds = Rect3D.Empty;
        foreach (ImportedFace f in leaf.Faces)
        {
            tri += f.Mesh.TriangleIndices.Count / 3;
            bounds.Union(BoundsOfMesh(f.Mesh));
        }
        leaf.TriangleCount = tri;
        leaf.Bounds = bounds;
    }

    private static Point3DCollection SampleCurves(IEnumerable<ICurve> curves)
    {
        var segments = new Point3DCollection();
        foreach (ICurve c in curves)
            AppendCurveSegments(segments, c);
        return segments;
    }

    /// <summary>把曲線取樣成線段對（直線 1 段、曲線 12 段）</summary>
    private static void AppendCurveSegments(Point3DCollection segments, ICurve curve)
    {
        int n = curve is Line ? 1 : 12;
        try
        {
            GeoPoint prev = curve.PointAt(0);
            for (int i = 1; i <= n; i++)
            {
                GeoPoint cur = curve.PointAt(i / (double)n);
                segments.Add(new Point3D(prev.x, prev.y, prev.z));
                segments.Add(new Point3D(cur.x, cur.y, cur.z));
                prev = cur;
            }
        }
        catch { /* 個別曲線取樣失敗跳過 */ }
    }

    private static Rect3D BoundsOfMesh(MeshGeometry3D mesh) => BoundsOfPoints(mesh.Positions);

    private static Rect3D BoundsOfPoints(Point3DCollection points)
    {
        if (points.Count == 0) return Rect3D.Empty;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (Point3D p in points)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        return new Rect3D(minX, minY, minZ,
            Math.Max(maxX - minX, 1e-6), Math.Max(maxY - minY, 1e-6), Math.Max(maxZ - minZ, 1e-6));
    }

    /// <summary>移除沒有任何幾何的節點（延後三角化全失敗的 leaf、因此變空的 group）</summary>
    private static void Prune(ImportedNode node)
    {
        for (int i = node.Children.Count - 1; i >= 0; i--)
        {
            ImportedNode c = node.Children[i];
            Prune(c);
            bool empty = c.Children.Count == 0 && c.Faces.Count == 0 && (c.EdgeSegments?.Count ?? 0) == 0;
            if (empty) node.Children.RemoveAt(i);
        }
    }

    /// <summary>group 節點統計彙總（bottom-up）</summary>
    private static void Aggregate(ImportedNode node)
    {
        if (node.IsLeaf) return;
        int solids = node.SolidCount, faces = node.FaceCount, tris = node.TriangleCount;
        Rect3D bounds = node.Bounds;
        foreach (ImportedNode c in node.Children)
        {
            Aggregate(c);
            solids += c.SolidCount;
            faces += c.FaceCount;
            tris += c.TriangleCount;
            bounds.Union(c.Bounds);
        }
        node.SolidCount = solids;
        node.FaceCount = faces;
        node.TriangleCount = tris;
        node.Bounds = bounds;
    }

    private static int CountSegments(ImportedNode node)
    {
        int n = node.EdgeSegments?.Count ?? 0;
        foreach (ImportedNode c in node.Children) n += CountSegments(c);
        return n;
    }
}
