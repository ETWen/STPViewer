using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CADability;
using CADability.GeoObject;
using HelixToolkit.Wpf;
using STPViewer.Models;

namespace STPViewer.Services;

/// <summary>角度量測的單次拾取：邊方向或面法向量</summary>
public record DirectionPick(Point3D At, Vector3D Direction, string Desc);

/// <summary>
/// 點/距離/邊/面/圓/角度/面距 量測幾何計算 + 視圖 overlay 產生。
/// 邊長、圓半徑以 B-rep 精確值為準；面積與面距用三角網格近似。
/// 所有文字以 Func&lt;UnitSystem,string&gt; 延後產生，支援 mm/inch 即時切換。
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

    // ─── 拾取輔助 ────────────────────────────────────────────────

    /// <summary>命中點吸附到最近的 B-rep 頂點（容差內），否則回傳原始表面點</summary>
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
        catch { /* 頂點存取失敗則不吸附 */ }
        return best;
    }

    /// <summary>
    /// 角度量測拾取：命中點附近有直線邊（容差內）→ 邊方向；
    /// 否則取面法向量（B-rep 精確；無 B-rep 時用網格法向量）。
    /// </summary>
    public DirectionPick PickDirection(FaceInfo fi, Point3D hit, Vector3D meshNormal, double tolerance)
    {
        if (fi.BrepFace is not null)
        {
            // 1) 近距離直線邊優先
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
                        return new DirectionPick(p, dir, "邊方向");
                    }
                }
            }
            catch { }

            // 2) 面法向量（精確）
            try
            {
                GeoPoint2D uv = fi.BrepFace.Surface.PositionOf(ToGeo(hit));
                GeoVector n = fi.BrepFace.Surface.GetNormal(uv).Normalized;
                return new DirectionPick(hit, new Vector3D(n.x, n.y, n.z), "面法向量");
            }
            catch { }
        }
        Vector3D mn = meshNormal;
        if (mn.LengthSquared < 1e-12) mn = new Vector3D(0, 0, 1);
        mn.Normalize();
        return new DirectionPick(hit, mn, "面法向量（網格）");
    }

    // ─── 點 / 距離 ───────────────────────────────────────────────

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
            DetailFor = u => $"距離 = {Units.L(d.Length, u)}\nΔX = {Units.L(d.X, u)}\nΔY = {Units.L(d.Y, u)}\nΔZ = {Units.L(d.Z, u)}\n" +
                             $"P1 {Units.P(p1, u)}\nP2 {Units.P(p2, u)}",
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

    // ─── 邊 / 圓 ─────────────────────────────────────────────────

    /// <summary>量測命中面上最近的邊。circlesOnly=true 時只找圓形邊。找不到回傳 null。</summary>
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
            DetailFor = u => $"直徑 = {Units.L(radius * 2, u)}\n半徑 = {Units.L(radius, u)}\n" +
                             $"{(full ? "周長" : "弧長")} = {Units.L(curveLen, u)}\n" +
                             $"圓心 {Units.P(center, u)}",
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
            DetailFor = u => $"長度 = {Units.L(len, u)}\nΔX = {Units.C(d.X, u)}  ΔY = {Units.C(d.Y, u)}  ΔZ = {Units.C(d.Z, u)}\n" +
                             $"起點 {Units.P(s, u)}\n終點 {Units.P(e, u)}",
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
            DetailFor = u => $"曲線長 = {Units.L(len, u)}\n類型 = {typeName}",
        };
        r.Overlays.Add(Polyline(curve, HighlightColor, 3));
        AddLabel(r, ToP3(curve.PointAt(0.5)) + new Vector3D(0, 0, markerR * 2), u => $"L={Units.L(len, u)}");
        return r;
    }

    // ─── 面 ──────────────────────────────────────────────────────

    public MeasurementResult MeasureFace(FaceInfo fi, Point3D hit, string label, double markerR)
    {
        // 面積：三角網格加總（近似值，三角化精度內）
        double area = MeshArea(fi.Mesh);
        int triCount = fi.Mesh.TriangleIndices.Count / 3;
        Func<UnitSystem, string> surfaceDesc = fi.BrepFace is not null
            ? DescribeSurface(fi.BrepFace.Surface)
            : _ => "類型 = 網格（無 B-rep）";

        var r = new MeasurementResult
        {
            Kind = MeasureMode.Face,
            TitleFor = u => $"{label}  A ≈ {Units.A(area, u)}",
            DetailFor = u => $"面積 ≈ {Units.A(area, u)}（網格近似）\n{surfaceDesc(u)}\n三角形數 = {triCount:N0}",
        };

        // overlay：外輪廓 highlight + 標籤
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
                string desc = $"類型 = 平面\n法向量 ({n.x:F3}, {n.y:F3}, {n.z:F3})";
                return _ => desc;
            }
            case CylindricalSurface cs:
            {
                GeoVector ax = cs.Axis.Normalized;
                double radius = cs.RadiusX;
                return u => $"類型 = 圓柱面\n半徑 = {Units.L(radius, u)}\n軸向 ({ax.x:F3}, {ax.y:F3}, {ax.z:F3})";
            }
            default:
            {
                string desc = $"類型 = {SurfaceTypeName(surface)}";
                return _ => desc;
            }
        }
    }

    private static string SurfaceTypeName(ISurface s) => s.GetType().Name switch
    {
        "SphericalSurface" => "球面",
        "ToroidalSurface" => "環面",
        "ConicalSurface" => "圓錐面",
        "NurbsSurface" => "NURBS 曲面",
        "SurfaceOfRevolution" => "旋轉曲面",
        "SurfaceOfLinearExtrusion" => "拉伸曲面",
        var other => other,
    };

    // ─── 角度（兩面 / 兩邊夾角）─────────────────────────────────

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
            DetailFor = _ => $"夾角 = {deg:F2}°（補角 {comp:F2}°）\n" +
                             $"方向 1：{a.Desc} ({a.Direction.X:F3}, {a.Direction.Y:F3}, {a.Direction.Z:F3})\n" +
                             $"方向 2：{b.Desc} ({b.Direction.X:F3}, {b.Direction.Y:F3}, {b.Direction.Z:F3})\n" +
                             "（兩面夾角以法向量計，互補角請參考補角值）",
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

    // ─── 面到面最短距離（網格近似）──────────────────────────────

    public MeasurementResult MeasureFaceDistance(FaceInfo a, FaceInfo b, string label, double markerR)
    {
        (Point3D pa, Point3D pb) = MeshMinDistance(a.Mesh, b.Mesh);
        Vector3D d = pb - pa;
        var r = new MeasurementResult
        {
            Kind = MeasureMode.FaceDistance,
            TitleFor = u => $"{label}  min = {Units.L(d.Length, u)}",
            DetailFor = u => $"最短距離 ≈ {Units.L(d.Length, u)}（網格近似）\n" +
                             $"ΔX = {Units.L(d.X, u)}\nΔY = {Units.L(d.Y, u)}\nΔZ = {Units.L(d.Z, u)}\n" +
                             $"點 1 {Units.P(pa, u)}\n點 2 {Units.P(pb, u)}",
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

    /// <summary>兩網格最近點對：頂點→三角形雙向（頂點過多時抽樣，結果為近似值）</summary>
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
                    Point3D q = ClosestPointOnTriangle(v, tp[ti[t]], tp[ti[t + 1]], tp[ti[t + 2]]);
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

    /// <summary>點到三角形最近點（Ericson, Real-Time Collision Detection）</summary>
    private static Point3D ClosestPointOnTriangle(Point3D p, Point3D a, Point3D b, Point3D c)
    {
        Vector3D ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vector3D.DotProduct(ab, ap);
        double d2 = Vector3D.DotProduct(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        Vector3D bp = p - b;
        double d3 = Vector3D.DotProduct(ab, bp);
        double d4 = Vector3D.DotProduct(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return a + ab * (d1 / (d1 - d3));

        Vector3D cp = p - c;
        double d5 = Vector3D.DotProduct(ab, cp);
        double d6 = Vector3D.DotProduct(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return a + ac * (d2 / (d2 - d6));

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        double denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

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
