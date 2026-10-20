using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CADability;
using CADability.GeoObject;
using IOPath = System.IO.Path;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.Services;

/// <summary>A frozen triangle mesh for one B-rep face; BrepFace is null for STL.</summary>
public record ImportedFace(Face? BrepFace, MeshGeometry3D Mesh);

/// <summary>An imported assembly group or geometry leaf.</summary>
public class ImportedNode
{
    public string Name { get; set; } = "";
    public string? NameKey { get; set; }
    public int NameNumber { get; set; }
    public List<ImportedNode> Children { get; } = new();

    // Leaf geometry
    public List<ImportedFace> Faces { get; } = new();
    public Point3DCollection? EdgeSegments { get; set; }   // Frozen paired endpoints.
    public bool HasBrep { get; set; } = true;              // False for STL and curves.

    /// <summary>Source B-rep objects used for whole-part transforms.</summary>
    public List<CADability.GeoObject.IGeoObject> SourceGeos { get; } = new();

    // Group totals are calculated by Aggregate.
    public int SolidCount { get; set; }
    public int FaceCount { get; set; }
    public int TriangleCount { get; set; }
    public Rect3D Bounds { get; set; } = Rect3D.Empty;

    public bool IsLeaf => Children.Count == 0;
}

public record ImportedFileData(string FilePath, ImportedNode Root);

/// <summary>
/// Imports STEP, STL and DXF files on a background thread.
/// Returned WPF Freezables are frozen.
/// </summary>
public class StepImportService
{
    /// <summary>Import progress for the status bar.</summary>
    public Action<string>? Progress { get; set; }
    public Action<Func<string>>? ProgressTextFor { get; set; }

    private void ReportProgress(Func<string> textFor)
    {
        Progress?.Invoke(textFor());
        ProgressTextFor?.Invoke(textFor);
    }

    private long _triTicks, _edgeTicks; // Accumulated tessellation and edge sampling ticks.
    private double TriMs => new TimeSpan(_triTicks).TotalMilliseconds;
    private double EdgeMs => new TimeSpan(_edgeTicks).TotalMilliseconds;

    /// <summary>Geometry work queued per leaf, then run in parallel. Faces within a leaf share edges.</summary>
    private readonly List<Action> _leafWork = new();

    /// <summary>Faces to retry sequentially after parallel tessellation.</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<(ImportedNode Leaf, HashSet<Edge> EdgeSet, List<(Face F, double Prec)> Failed)> _retry = new();

    public ImportedFileData Import(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(Get("Runtime.FileNotFound"), filePath);

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
                throw new InvalidDataException(Get("Runtime.UnsupportedFormat"));
        }

        if (_leafWork.Count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int leafCount = _leafWork.Count;
            ReportProgress(() => Format("Runtime.Triangulating", leafCount));
            System.Threading.Tasks.Parallel.ForEach(_leafWork, w =>
            {
                try { w(); }
                catch { }
            });
            _leafWork.Clear();

            // Retry failed faces sequentially after the parallel pass.
            int retried = 0;
            foreach ((ImportedNode leaf, HashSet<Edge> edgeSet, var failed) in _retry)
            {
                foreach ((Face f, double prec) in failed)
                    if (AddFace(f, prec, leaf, edgeSet)) retried++;
                FinishLeaf(leaf, edgeSet);
            }
            _retry.Clear();
            long elapsed = sw.ElapsedMilliseconds;
            int retryCount = retried;
            ReportProgress(() => Format("Runtime.GeometryProcessed", elapsed, TriMs, EdgeMs,
                retryCount > 0 ? Format("Runtime.RetriedFaces", retryCount) : ""));
        }

        Prune(root);
        Aggregate(root);
        if (root.SolidCount == 0 && root.FaceCount == 0 && CountSegments(root) == 0)
            throw new InvalidDataException(Get("Runtime.NoGeometry"));
        return new ImportedFileData(filePath, root);
    }

    public static bool IsSupported(string path) =>
        IOPath.GetExtension(path).ToLowerInvariant() is ".stp" or ".step" or ".stl" or ".dxf";

    // ─── STEP ────────────────────────────────────────────────────

    private void ImportStepFile(string path, ImportedNode root)
    {
        ReportProgress(() => Get("Runtime.ParsingStep"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var importer = new ImportStep { HierarchyToBlocks = true };
        GeoObjectList list = importer.Read(path)
            ?? throw new InvalidDataException(Get("Runtime.StepParseFailed"));
        long elapsed = sw.ElapsedMilliseconds;
        ReportProgress(() => Format("Runtime.StepParsed", elapsed));
        BuildChildren(root, EnumerateGeo(list));
    }

    // STL is read directly as mesh geometry without B-rep objects.

    private void ImportStlFile(string path, ImportedNode root)
    {
        ReportProgress(() => Get("Runtime.ParsingStl"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<StlMeshReader.MeshGroup> groups = StlMeshReader.Read(path);
        if (groups.Count == 0)
            throw new InvalidDataException(Get("Runtime.StlNoTriangles"));

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
                NameKey = g.NameKey,
                NameNumber = g.NameNumber,
                HasBrep = false,
                SolidCount = 1,
                FaceCount = tris,
                TriangleCount = tris,
                Bounds = BoundsOfMesh(mesh),
            };
            leaf.Faces.Add(new ImportedFace(null, mesh));
            root.Children.Add(leaf);
        }
        long elapsed = sw.ElapsedMilliseconds;
        int meshCount = root.Children.Count;
        ReportProgress(() => Format("Runtime.StlParsed", elapsed, meshCount));
    }

    // DXF

    private void ImportDxfFile(string path, ImportedNode root)
    {
        var import = new CADability.DXF.Import(path);
        Project project = import.Project
            ?? throw new InvalidDataException(Get("Runtime.DxfParseFailed"));
        Model model = project.GetActiveModel();
        BuildChildren(root, EnumerateGeo(model.AllObjects));
    }

    // Assembly traversal

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
        var looseCurves = new List<IGeoObject>();

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
            var leaf = new ImportedNode { Name = Get("Runtime.FaceName"), NameKey = "Runtime.FaceName" };
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
            parent.Children.Add(leaf);
        }
        if (looseCurves.Count > 0)
        {
            Point3DCollection segs = SampleCurves(looseCurves.OfType<ICurve>());
            if (segs.Count > 0)
            {
                segs.Freeze();
                var leaf = new ImportedNode
                {
                    Name = Get("Runtime.CurveName"),
                    NameKey = "Runtime.CurveName",
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
        // Collapse a block containing one solid into a leaf.
        if (block.NumChildren == 1 && block.Child(0) is Solid s)
        {
            ImportedNode leaf = SolidNode(s);
            if (!string.IsNullOrWhiteSpace(block.Name))
            {
                leaf.Name = block.Name;
                leaf.NameKey = null;
                leaf.NameNumber = 0;
            }
            return leaf;
        }

        var node = new ImportedNode
        {
            Name = string.IsNullOrWhiteSpace(block.Name) ? Get("Runtime.ComponentName") : block.Name,
            NameKey = string.IsNullOrWhiteSpace(block.Name) ? "Runtime.ComponentName" : null,
        };
        BuildChildren(node, ChildrenOf(block));
        if (node.Children.Count == 0) return null;

        // Drop a redundant group when it has one child with the same name.
        if (node.Children.Count == 1)
        {
            ImportedNode only = node.Children[0];
            if (string.IsNullOrWhiteSpace(only.Name) || only.Name == node.Name)
            {
                if (node.NameKey is null || only.NameKey is not null || string.IsNullOrWhiteSpace(only.Name))
                {
                    only.Name = node.Name;
                    only.NameKey = node.NameKey;
                    only.NameNumber = node.NameNumber;
                }
                return only;
            }
        }
        return node;
    }

    private ImportedNode SolidNode(Solid solid)
    {
        var leaf = new ImportedNode
        {
            Name = string.IsNullOrWhiteSpace(solid.NameOrEmpty) ? Get("Runtime.SolidName") : solid.NameOrEmpty,
            NameKey = string.IsNullOrWhiteSpace(solid.NameOrEmpty) ? "Runtime.SolidName" : null,
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
            Name = string.IsNullOrWhiteSpace(shell.NameOrEmpty) ? Get("Runtime.ShellName") : shell.NameOrEmpty,
            NameKey = string.IsNullOrWhiteSpace(shell.NameOrEmpty) ? "Runtime.ShellName" : null,
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

    // Geometry processing

    private static BoundingCube SafeBounds(Func<BoundingCube> get)
    {
        try { return get(); }
        catch { return new BoundingCube(); }
    }

    /// <summary>Tessellation precision: bounding diagonal × 0.0015, clamped to [0.02, 0.5] mm.</summary>
    private static double PrecisionFor(BoundingCube bc)
    {
        double diag;
        try { diag = bc.DiagonalLength; }
        catch { diag = 100; }
        if (double.IsNaN(diag) || double.IsInfinity(diag) || diag <= 0) diag = 100;
        return Math.Clamp(diag * 0.0015, 0.02, 0.5);
    }

    /// <returns>False when tessellation fails and the face needs a retry.</returns>
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
            return false;
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
        catch { }
        return true;
    }

    /// <summary>Completes edge sampling, counts and bounds for one leaf.</summary>
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

    /// <summary>Samples curves into paired line endpoints.</summary>
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
        catch { }
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

    /// <summary>Removes nodes left empty after tessellation.</summary>
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

    /// <summary>Aggregates counts and bounds from children.</summary>
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
