using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CADability;
using CADability.GeoObject;
using HelixToolkit.Wpf;
using STPViewer.Models;
using static STPViewer.Services.LocalizationService;

namespace STPViewer.Services;

/// <summary>One direction selected for angle measurement.</summary>
public record DirectionPick(Point3D At, Vector3D Direction, string Desc);

/// <summary>
/// Computes measurements and their viewport overlays.
/// B-rep provides exact edge lengths and radii; mesh values are approximate.
/// Text is deferred so unit and language changes update existing results.
/// </summary>
public class MeasurementService
{
    private static readonly Brush MarkerBrush = Brushes.OrangeRed;
    private static readonly Color HighlightColor = Colors.Gold;
    private static readonly Brush LabelFg = Brushes.Black;
    private static readonly Brush LabelBg = new SolidColorBrush(Color.FromArgb(200, 255, 255, 210));

    static MeasurementService() => LabelBg.Freeze();

    private static GeoPoint ToGeo(Point3D p) => new(p.X, p.Y, p.Z);
    private static Point3D ToP3(GeoPoint p) => new(p.x, p.y, p.z);

    // Picking

    /// <summary>
    /// Snaps to the nearest vertex or circular edge within tolerance.
    /// A circular edge snaps to its center for hole pitch measurements.
    /// </summary>
    public Point3D Snap(FaceInfo fi, Point3D hit, double tolerance)
    {
        if (fi.BrepFace is null) return hit;
        Point3D best = hit;
        double bestD = tolerance;
        try
        {
            foreach (Vertex v in fi.BrepFace.Vertices)
            {
                Point3D p = ToP3(v.Position);
                double d = (p - hit).Length;
                if (d < bestD) { bestD = d; best = p; }
            }
        }
        catch { }

        // Compare distance to the circle edge before selecting its center.
        try
        {
            foreach (Edge e in fi.BrepFace.AllEdges)
            {
                if (e.Curve3D is not Ellipse { IsCircle: true } el) continue;
                double pos;
                try { pos = el.PositionOf(ToGeo(hit)); } catch { continue; }
                if (double.IsNaN(pos)) continue;
                Point3D onCurve = ToP3(el.PointAt(Math.Clamp(pos, 0, 1)));
                double d = (onCurve - hit).Length;
                if (d < bestD) { bestD = d; best = ToP3(el.Center); }
            }
        }
        catch { }
        return best;
    }

    /// <summary>
    /// Uses a nearby straight edge for direction, otherwise a face normal.
    /// </summary>
    public DirectionPick PickDirection(FaceInfo fi, Point3D hit, Vector3D meshNormal, double tolerance)
    {
        if (fi.BrepFace is not null)
        {
            // Prefer a nearby straight edge.
            try
            {
                foreach (Edge e in fi.BrepFace.AllEdges)
                {
                    if (e.Curve3D is not Line ln) continue;
                    double pos = Math.Clamp(ln.PositionOf(ToGeo(hit)), 0, 1);
                    Point3D p = ToP3(ln.PointAt(pos));
                    if ((p - hit).Length < tolerance)
                    {
                        Vector3D dir = ToP3(ln.EndPoint) - ToP3(ln.StartPoint);
                        dir.Normalize();
                        return new DirectionPick(p, dir, "Runtime.DirectionEdge");
                    }
                }
            }
            catch { }

            // Use the B-rep face normal when available.
            try
            {
                GeoPoint2D uv = fi.BrepFace.Surface.PositionOf(ToGeo(hit));
                GeoVector n = fi.BrepFace.Surface.GetNormal(uv).Normalized;
                return new DirectionPick(hit, new Vector3D(n.x, n.y, n.z), "Runtime.DirectionFace");
            }
            catch { }
        }
        Vector3D mn = meshNormal;
        if (mn.LengthSquared < 1e-12) mn = new Vector3D(0, 0, 1);
        mn.Normalize();
        return new DirectionPick(hit, mn, "Runtime.DirectionMeshFace");
    }

    // Point and distance

    public MeasurementResult MeasurePoint(Point3D p, string label, double markerR)
    {
        var r = new MeasurementResult
        {
            Kind = MeasureMode.Point,
            TitleFor = u => $"{label}  {Units.P(p, u)}",
            DetailFor = u => $"X = {Units.L(p.X, u)}\nY = {Units.L(p.Y, u)}\nZ = {Units.L(p.Z, u)}",
        };
        r.Overlays.Add(Sphere(p, markerR));
        AddLabel(r, p + new Vector3D(0, 0, markerR * 2), _ => label);
        return r;
    }

    public MeasurementResult MeasureDistance(Point3D p1, Point3D p2, string label, double markerR)
    {
        Vector3D d = p2 - p1;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.Distance,
            TitleFor = u => $"{label}  {Units.L(d.Length, u)}",
            DetailFor = u => Format("Runtime.DistanceDetail", Units.L(d.Length, u), Units.L(d.X, u), Units.L(d.Y, u), Units.L(d.Z, u), Units.P(p1, u), Units.P(p2, u)),
        };
        r.Overlays.Add(Sphere(p1, markerR));
        r.Overlays.Add(Sphere(p2, markerR));
        r.Overlays.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { p1, p2 },
            Color = Colors.OrangeRed,
            Thickness = 2,
        });
        AddLabel(r, p1 + d / 2 + new Vector3D(0, 0, markerR * 2), u => Units.L(d.Length, u));
        return r;
    }

    // Edge and circle

    /// <summary>Measures the nearest edge, or the nearest circular edge when circlesOnly is true.</summary>
    public MeasurementResult? MeasureEdge(FaceInfo fi, Point3D hit, string label, double markerR, bool circlesOnly)
    {
        if (fi.BrepFace is null) return null;
        Edge? best = null;
        double bestD = double.MaxValue;
        foreach (Edge e in fi.BrepFace.AllEdges)
        {
            ICurve? c;
            try { c = e.Curve3D; } catch { continue; }
            if (c is null) continue;
            if (circlesOnly && c is not Ellipse { IsCircle: true }) continue;

            double pos;
            try { pos = c.PositionOf(ToGeo(hit)); } catch { continue; }
            if (double.IsNaN(pos)) pos = 0.5;
            pos = Math.Clamp(pos, 0, 1);
            Point3D p;
            try { p = ToP3(c.PointAt(pos)); } catch { continue; }
            double d = (p - hit).Length;
            if (d < bestD) { bestD = d; best = e; }
        }
        if (best?.Curve3D is not ICurve curve) return null;

        return curve switch
        {
            Ellipse el when el.IsCircle => CircleResult(el, curve, label, markerR),
            Line ln => LineResult(ln, label, markerR),
            _ => GenericCurveResult(curve, label, markerR),
        };
    }

    private MeasurementResult CircleResult(Ellipse el, ICurve curve, string label, double markerR)
    {
        Point3D center = ToP3(el.Center);
        double radius = el.Radius;
        double curveLen = curve.Length;
        bool full = !el.IsArc;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.Circle,
            TitleFor = u => $"{label}  ⌀{Units.L(radius * 2, u)}",
            DetailFor = u => Format("Runtime.CircleDetail", Units.L(radius * 2, u), Units.L(radius, u), Get(full ? "Runtime.Circumference" : "Runtime.ArcLength"), Units.L(curveLen, u), Units.P(center, u)),
        };
        r.Overlays.Add(Polyline(curve, HighlightColor, 3));
        r.Overlays.Add(Sphere(center, markerR));
        Point3D rim = ToP3(curve.PointAt(0));
        r.Overlays.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { center, rim },
            Color = Colors.OrangeRed,
            Thickness = 1.5,
        });
        AddLabel(r, center + new Vector3D(0, 0, markerR * 2), u => $"{label} ⌀{Units.L(radius * 2, u)}");
        return r;
    }

    private MeasurementResult LineResult(Line ln, string label, double markerR)
    {
        Point3D s = ToP3(ln.StartPoint), e = ToP3(ln.EndPoint);
        Vector3D d = e - s;
        double len = ln.Length;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.Edge,
            TitleFor = u => $"{label}  L = {Units.L(len, u)}",
            DetailFor = u => Format("Runtime.LineDetail", Units.L(len, u), Units.C(d.X, u), Units.C(d.Y, u), Units.C(d.Z, u), Units.P(s, u), Units.P(e, u)),
        };
        r.Overlays.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { s, e },
            Color = HighlightColor,
            Thickness = 3,
        });
        AddLabel(r, s + d / 2 + new Vector3D(0, 0, markerR * 2), u => $"L={Units.L(len, u)}");
        return r;
    }

    private MeasurementResult GenericCurveResult(ICurve curve, string label, double markerR)
    {
        double len;
        try { len = curve.Length; } catch { len = double.NaN; }
        string typeName = curve.GetType().Name;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.Edge,
            TitleFor = u => $"{label}  L = {Units.L(len, u)}",
            DetailFor = u => Format("Runtime.CurveDetail", Units.L(len, u), typeName),
        };
        r.Overlays.Add(Polyline(curve, HighlightColor, 3));
        AddLabel(r, ToP3(curve.PointAt(0.5)) + new Vector3D(0, 0, markerR * 2), u => $"L={Units.L(len, u)}");
        return r;
    }

    // Face

    public MeasurementResult MeasureFace(FaceInfo fi, Point3D hit, string label, double markerR)
    {
        // Area is estimated from the triangle mesh.
        double area = MeshArea(fi.Mesh);
        int triCount = fi.Mesh.TriangleIndices.Count / 3;
        Func<UnitSystem, string> surfaceDesc = fi.BrepFace is not null
            ? DescribeSurface(fi.BrepFace.Surface)
            : _ => Get("Runtime.MeshSurface");

        var r = new MeasurementResult
        {
            Kind = MeasureMode.Face,
            TitleFor = u => $"{label}  A ≈ {Units.A(area, u)}",
            DetailFor = u => Format("Runtime.FaceDetail", Units.A(area, u), surfaceDesc(u), triCount),
        };

        // Highlight the outer edges and add a label.
        if (fi.BrepFace is not null)
        {
            try
            {
                foreach (Edge e in fi.BrepFace.OutlineEdges)
                    if (e.Curve3D is ICurve c)
                        r.Overlays.Add(Polyline(c, HighlightColor, 3));
            }
            catch { }
        }
        AddLabel(r, hit + new Vector3D(0, 0, markerR * 2), u => $"{label} A≈{Units.A(area, u)}");
        return r;
    }

    private static double MeshArea(MeshGeometry3D mesh)
    {
        double area = 0;
        Point3DCollection pos = mesh.Positions;
        Int32Collection idx = mesh.TriangleIndices;
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            Vector3D a = pos[idx[i + 1]] - pos[idx[i]];
            Vector3D b = pos[idx[i + 2]] - pos[idx[i]];
            area += Vector3D.CrossProduct(a, b).Length / 2;
        }
        return area;
    }

    private static Func<UnitSystem, string> DescribeSurface(ISurface surface)
    {
        switch (surface)
        {
            case PlaneSurface ps:
            {
                GeoVector n = ps.Normal.Normalized;
                return _ => Format("Runtime.PlaneSurface", n.x, n.y, n.z);
            }
            case CylindricalSurface cs:
            {
                GeoVector ax = cs.Axis.Normalized;
                double radius = cs.RadiusX;
                return u => Format("Runtime.CylinderSurface", Units.L(radius, u), ax.x, ax.y, ax.z);
            }
            default:
            {
                return _ => Format("Runtime.SurfaceType", SurfaceTypeName(surface));
            }
        }
    }

    private static string SurfaceTypeName(ISurface s) => s.GetType().Name switch
    {
        "SphericalSurface" => Get("Runtime.SphericalSurface"),
        "ToroidalSurface" => Get("Runtime.ToroidalSurface"),
        "ConicalSurface" => Get("Runtime.ConicalSurface"),
        "NurbsSurface" => Get("Runtime.NurbsSurface"),
        "SurfaceOfRevolution" => Get("Runtime.RevolutionSurface"),
        "SurfaceOfLinearExtrusion" => Get("Runtime.ExtrusionSurface"),
        var other => other,
    };

    // Angle

    public MeasurementResult MeasureAngle(DirectionPick a, DirectionPick b, string label, double markerR, double arrowLen)
    {
        double cos = Math.Clamp(Vector3D.DotProduct(a.Direction, b.Direction)
                     / (a.Direction.Length * b.Direction.Length), -1, 1);
        double deg = Math.Acos(cos) * 180 / Math.PI;
        double comp = 180 - deg;

        var r = new MeasurementResult
        {
            Kind = MeasureMode.Angle,
            TitleFor = _ => $"{label}  ∠ {deg:F2}°",
            DetailFor = _ => Format("Runtime.AngleDetail", deg, comp, Get(a.Desc), a.Direction.X, a.Direction.Y, a.Direction.Z, Get(b.Desc), b.Direction.X, b.Direction.Y, b.Direction.Z),
        };
        r.Overlays.Add(Sphere(a.At, markerR));
        r.Overlays.Add(Sphere(b.At, markerR));
        r.Overlays.Add(DirectionLine(a, arrowLen));
        r.Overlays.Add(DirectionLine(b, arrowLen));
        Point3D mid = a.At + (b.At - a.At) / 2;
        AddLabel(r, mid + new Vector3D(0, 0, markerR * 2), _ => $"∠{deg:F2}°");
        return r;
    }

    private static LinesVisual3D DirectionLine(DirectionPick p, double len) => new()
    {
        Points = new Point3DCollection
        {
            p.At - p.Direction * len * 0.2,
            p.At + p.Direction * len,
        },
        Color = Colors.MediumBlue,
        Thickness = 2,
    };

    // Face distance

    public MeasurementResult MeasureFaceDistance(FaceInfo a, FaceInfo b, string label, double markerR)
    {
        (Point3D pa, Point3D pb) = MeshMinDistance(a.Mesh, b.Mesh);
        Vector3D d = pb - pa;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.FaceDistance,
            TitleFor = u => $"{label}  min = {Units.L(d.Length, u)}",
            DetailFor = u => Format("Runtime.FaceDistanceDetail", Units.L(d.Length, u), Units.L(d.X, u), Units.L(d.Y, u), Units.L(d.Z, u), Units.P(pa, u), Units.P(pb, u)),
        };
        r.Overlays.Add(Sphere(pa, markerR));
        r.Overlays.Add(Sphere(pb, markerR));
        r.Overlays.Add(new LinesVisual3D
        {
            Points = new Point3DCollection { pa, pb },
            Color = Colors.OrangeRed,
            Thickness = 2,
        });
        AddLabel(r, pa + d / 2 + new Vector3D(0, 0, markerR * 2), u => $"min {Units.L(d.Length, u)}");
        return r;
    }

    /// <summary>Approximates the closest points by probing vertices against triangles in both directions.</summary>
    private static (Point3D a, Point3D b) MeshMinDistance(MeshGeometry3D ma, MeshGeometry3D mb)
    {
        double best = double.MaxValue;
        Point3D pa = default, pb = default;

        void Probe(MeshGeometry3D verts, MeshGeometry3D tris, bool swap)
        {
            Point3DCollection vp = verts.Positions;
            Point3DCollection tp = tris.Positions;
            Int32Collection ti = tris.TriangleIndices;
            int vStride = Math.Max(1, vp.Count / 3000);
            for (int i = 0; i < vp.Count; i += vStride)
            {
                Point3D v = vp[i];
                for (int t = 0; t + 2 < ti.Count; t += 3)
                {
                    Point3D q = InterferenceService.ClosestPointOnTriangle(v, tp[ti[t]], tp[ti[t + 1]], tp[ti[t + 2]]);
                    double d = (q - v).Length;
                    if (d < best)
                    {
                        best = d;
                        (pa, pb) = swap ? (q, v) : (v, q);
                    }
                }
            }
        }

        Probe(ma, mb, swap: false);
        Probe(mb, ma, swap: true);
        return (pa, pb);
    }

    // Volume and centroid

    /// <summary>
    /// Computes signed volume and centroid from origin-based tetrahedra.
    /// The result is reliable only for closed meshes.
    /// </summary>
    public static (double VolumeMm3, Point3D Centroid, bool Reliable) MeshVolume(
        System.Collections.Generic.IEnumerable<MeshGeometry3D> meshes)
    {
        double vol6 = 0;
        double absVol6 = 0;
        double cx = 0, cy = 0, cz = 0;
        foreach (MeshGeometry3D mesh in meshes)
        {
            Point3DCollection pos = mesh.Positions;
            Int32Collection idx = mesh.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                Point3D a = pos[idx[i]], b = pos[idx[i + 1]], c = pos[idx[i + 2]];
                // 6V = a · (b × c)
                double v = a.X * (b.Y * c.Z - b.Z * c.Y)
                         + a.Y * (b.Z * c.X - b.X * c.Z)
                         + a.Z * (b.X * c.Y - b.Y * c.X);
                vol6 += v;
                absVol6 += Math.Abs(v);
                // Tetrahedron centroid = (a + b + c) / 4.
                cx += (a.X + b.X + c.X) / 4 * v;
                cy += (a.Y + b.Y + c.Y) / 4 * v;
                cz += (a.Z + b.Z + c.Z) / 4 * v;
            }
        }
        double volume = Math.Abs(vol6) / 6;
        // Large signed cancellation suggests an open mesh or inconsistent normals.
        bool reliable = absVol6 > 0 && Math.Abs(vol6) / absVol6 > 0.01 && volume > 1e-9;
        Point3D centroid = Math.Abs(vol6) > 1e-12
            ? new Point3D(cx / vol6, cy / vol6, cz / vol6)
            : default;
        return (volume, centroid, reliable);
    }

    // Shares the triangle closest-point calculation with InterferenceService.

    // ─── overlay helpers ─────────────────────────────────────────

    private static SphereVisual3D Sphere(Point3D center, double radius) => new()
    {
        Center = center,
        Radius = radius,
        Fill = MarkerBrush,
    };

    private static void AddLabel(MeasurementResult r, Point3D position, Func<UnitSystem, string> textFor)
    {
        var label = new BillboardTextVisual3D
        {
            Position = position,
            Text = textFor(UnitSystem.Millimeter),
            Foreground = LabelFg,
            Background = LabelBg,
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 14,
        };
        r.Overlays.Add(label);
        r.DynamicLabels.Add((label, textFor));
    }

    private static LinesVisual3D Polyline(ICurve curve, Color color, double thickness)
    {
        int n = curve is Line ? 1 : 64;
        var pts = new Point3DCollection(n * 2);
        try
        {
            GeoPoint prev = curve.PointAt(0);
            for (int i = 1; i <= n; i++)
            {
                GeoPoint cur = curve.PointAt(i / (double)n);
                pts.Add(ToP3(prev));
                pts.Add(ToP3(cur));
                prev = cur;
            }
        }
        catch { }
        return new LinesVisual3D { Points = pts, Color = color, Thickness = thickness };
    }
}
