using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace STPViewer.Services;

/// <summary>
/// 干涉檢查結果：Intersects=true 時 Segments 為干涉交線；
/// false 時 GapA/GapB 為最近點對（網格近似）。
/// </summary>
public record InterferenceResult(
    bool Intersects,
    int PairCount,
    List<(Point3D A, Point3D B)> Segments,
    Point3D GapA,
    Point3D GapB,
    double GapDistance);

/// <summary>
/// 兩組三角網格的干涉檢查：
/// 三角形-三角形相交（區間法，回傳交線段）+ 均勻網格空間加速。
/// 共面接觸（無穿透）不會被判為干涉（近似限制）。
/// 設計為在背景執行緒呼叫。
/// </summary>
public static class InterferenceService
{
    private const int MaxSegments = 20_000;

    public static InterferenceResult Check(
        IReadOnlyList<MeshGeometry3D> meshesA, IReadOnlyList<MeshGeometry3D> meshesB)
    {
        Triangle[] trisA = Flatten(meshesA);
        Triangle[] trisB = Flatten(meshesB);
        if (trisA.Length == 0 || trisB.Length == 0)
            return new InterferenceResult(false, 0, new(), default, default, double.NaN);

        var grid = new TriGrid(trisB);
        var segments = new List<(Point3D, Point3D)>();
        int pairCount = 0;
        var candidates = new HashSet<int>();

        foreach (Triangle ta in trisA)
        {
            candidates.Clear();
            grid.Query(ta.Min, ta.Max, candidates);
            foreach (int bi in candidates)
            {
                Triangle tb = trisB[bi];
                if (!AabbOverlap(ta, tb)) continue;
                if (TriTriIntersect(ta, tb, out Point3D s0, out Point3D s1))
                {
                    pairCount++;
                    if (segments.Count < MaxSegments) segments.Add((s0, s1));
                }
            }
        }

        if (pairCount > 0)
            return new InterferenceResult(true, pairCount, segments, default, default, 0);

        (Point3D ga, Point3D gb, double d) = ApproxMinDistance(trisA, trisB);
        return new InterferenceResult(false, 0, segments, ga, gb, d);
    }

    // ─── 資料結構 ────────────────────────────────────────────────

    private readonly struct Triangle
    {
        public readonly Point3D A, B, C;
        public readonly Point3D Min, Max;

        public Triangle(Point3D a, Point3D b, Point3D c)
        {
            A = a; B = b; C = c;
            Min = new Point3D(Math.Min(a.X, Math.Min(b.X, c.X)),
                              Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                              Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            Max = new Point3D(Math.Max(a.X, Math.Max(b.X, c.X)),
                              Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                              Math.Max(a.Z, Math.Max(b.Z, c.Z)));
        }
    }

    private static Triangle[] Flatten(IReadOnlyList<MeshGeometry3D> meshes)
    {
        var list = new List<Triangle>();
        foreach (MeshGeometry3D m in meshes)
        {
            Point3DCollection p = m.Positions;
            Int32Collection idx = m.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
                list.Add(new Triangle(p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]));
        }
        return list.ToArray();
    }

    private static bool AabbOverlap(in Triangle a, in Triangle b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
        a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
        a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    /// <summary>均勻網格：cell → 三角形索引</summary>
    private sealed class TriGrid
    {
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly double _cell;

        public TriGrid(Triangle[] tris)
        {
            // cell 大小：整體對角線 / 64（夾限避免極端值）
            Point3D min = tris[0].Min, max = tris[0].Max;
            foreach (Triangle t in tris)
            {
                min = new Point3D(Math.Min(min.X, t.Min.X), Math.Min(min.Y, t.Min.Y), Math.Min(min.Z, t.Min.Z));
                max = new Point3D(Math.Max(max.X, t.Max.X), Math.Max(max.Y, t.Max.Y), Math.Max(max.Z, t.Max.Z));
            }
            double diag = (max - min).Length;
            _cell = Math.Clamp(diag / 64, 1e-3, 1e6);

            for (int i = 0; i < tris.Length; i++)
                ForEachCell(tris[i].Min, tris[i].Max, key =>
                {
                    if (!_cells.TryGetValue(key, out List<int>? list))
                        _cells[key] = list = new List<int>();
                    list.Add(i);
                });
        }

        public void Query(Point3D min, Point3D max, HashSet<int> result) =>
            ForEachCell(min, max, key =>
            {
                if (_cells.TryGetValue(key, out List<int>? list))
                    foreach (int i in list) result.Add(i);
            });

        private void ForEachCell(Point3D min, Point3D max, Action<(int, int, int)> action)
        {
            int x0 = (int)Math.Floor(min.X / _cell), x1 = (int)Math.Floor(max.X / _cell);
            int y0 = (int)Math.Floor(min.Y / _cell), y1 = (int)Math.Floor(max.Y / _cell);
            int z0 = (int)Math.Floor(min.Z / _cell), z1 = (int)Math.Floor(max.Z / _cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        action((x, y, z));
        }
    }

    // ─── 三角形相交（區間法，回傳交線段）────────────────────────

    private static bool TriTriIntersect(in Triangle ta, in Triangle tb, out Point3D s0, out Point3D s1)
    {
        s0 = s1 = default;

        Vector3D n2 = Vector3D.CrossProduct(tb.B - tb.A, tb.C - tb.A);
        if (n2.LengthSquared < 1e-24) return false;
        double da0 = Vector3D.DotProduct(n2, ta.A - tb.A);
        double da1 = Vector3D.DotProduct(n2, ta.B - tb.A);
        double da2 = Vector3D.DotProduct(n2, ta.C - tb.A);
        if ((da0 > 0 && da1 > 0 && da2 > 0) || (da0 < 0 && da1 < 0 && da2 < 0)) return false;

        Vector3D n1 = Vector3D.CrossProduct(ta.B - ta.A, ta.C - ta.A);
        if (n1.LengthSquared < 1e-24) return false;
        double db0 = Vector3D.DotProduct(n1, tb.A - ta.A);
        double db1 = Vector3D.DotProduct(n1, tb.B - ta.A);
        double db2 = Vector3D.DotProduct(n1, tb.C - ta.A);
        if ((db0 > 0 && db1 > 0 && db2 > 0) || (db0 < 0 && db1 < 0 && db2 < 0)) return false;

        Vector3D dir = Vector3D.CrossProduct(n1, n2);
        if (dir.LengthSquared < 1e-24) return false; // 共面：不視為穿透（近似）

        if (!PlaneCrossSegment(ta, da0, da1, da2, out Point3D p0, out Point3D p1)) return false;
        if (!PlaneCrossSegment(tb, db0, db1, db2, out Point3D q0, out Point3D q1)) return false;

        // 投影到交線方向取重疊區間
        double tp0 = Vector3D.DotProduct(dir, (Vector3D)p0);
        double tp1 = Vector3D.DotProduct(dir, (Vector3D)p1);
        double tq0 = Vector3D.DotProduct(dir, (Vector3D)q0);
        double tq1 = Vector3D.DotProduct(dir, (Vector3D)q1);
        if (tp0 > tp1) { (tp0, tp1) = (tp1, tp0); (p0, p1) = (p1, p0); }
        if (tq0 > tq1) { (tq0, tq1) = (tq1, tq0); }

        double lo = Math.Max(tp0, tq0);
        double hi = Math.Min(tp1, tq1);
        if (lo >= hi) return false; // 無重疊（或僅點接觸）

        s0 = LerpOnSegment(p0, p1, tp0, tp1, lo);
        s1 = LerpOnSegment(p0, p1, tp0, tp1, hi);
        return true;
    }

    /// <summary>三角形與平面（頂點符號 d0..d2 已算好）的交線段（兩個交點）</summary>
    private static bool PlaneCrossSegment(in Triangle t, double d0, double d1, double d2,
        out Point3D p0, out Point3D p1)
    {
        Span<Point3D> pts = stackalloc Point3D[3];
        int n = 0;
        AddCrossing(t.A, t.B, d0, d1, pts, ref n);
        AddCrossing(t.B, t.C, d1, d2, pts, ref n);
        AddCrossing(t.C, t.A, d2, d0, pts, ref n);
        if (n < 2) { p0 = p1 = default; return false; }
        p0 = pts[0];
        p1 = pts[1];
        return true;
    }

    private static void AddCrossing(Point3D a, Point3D b, double da, double db,
        Span<Point3D> pts, ref int n)
    {
        if (n >= 3) return;
        if (da == 0 && db == 0) return;        // 邊在平面上 → 共面情形，略過
        if (da == 0) { pts[n++] = a; return; } // 頂點剛好在平面上
        if (da * db < 0)
            pts[n++] = a + (b - a) * (da / (da - db));
    }

    private static Point3D LerpOnSegment(Point3D p0, Point3D p1, double t0, double t1, double t)
    {
        double span = t1 - t0;
        double f = Math.Abs(span) < 1e-30 ? 0 : (t - t0) / span;
        return p0 + (p1 - p0) * f;
    }

    // ─── 無干涉時的近似最小間隙 ─────────────────────────────────

    private static (Point3D a, Point3D b, double d) ApproxMinDistance(Triangle[] trisA, Triangle[] trisB)
    {
        // 1) 抽樣頂點對頂點求初值
        double best = double.MaxValue;
        Point3D pa = default, pb = default;
        int strideA = Math.Max(1, trisA.Length / 2000);
        int strideB = Math.Max(1, trisB.Length / 2000);
        for (int i = 0; i < trisA.Length; i += strideA)
            for (int j = 0; j < trisB.Length; j += strideB)
            {
                double d = (trisB[j].A - trisA[i].A).Length;
                if (d < best) { best = d; pa = trisA[i].A; pb = trisB[j].A; }
            }

        // 2) 以最佳點為中心，精修：點 → 對方所有三角形
        Point3D RefineAgainst(Point3D p, Triangle[] tris, ref double bestD, Point3D current)
        {
            Point3D bestPt = current;
            foreach (Triangle t in tris)
            {
                Point3D q = ClosestPointOnTriangle(p, t.A, t.B, t.C);
                double d = (q - p).Length;
                if (d < bestD) { bestD = d; bestPt = q; }
            }
            return bestPt;
        }
        pb = RefineAgainst(pa, trisB, ref best, pb);
        pa = RefineAgainst(pb, trisA, ref best, pa);
        return (pa, pb, best);
    }

    /// <summary>點到三角形最近點（Ericson, Real-Time Collision Detection）</summary>
    internal static Point3D ClosestPointOnTriangle(Point3D p, Point3D a, Point3D b, Point3D c)
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
}
