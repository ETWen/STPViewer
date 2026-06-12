// 干涉檢查數學驗證：相交 / 分離 / 貼合三種情境
using System.Windows.Media;
using System.Windows.Media.Media3D;
using STPViewer.Services;

namespace SmokeTest;

internal static class InterferenceTest
{
    public static int Run()
    {
        int failures = 0;

        // 兩個互穿的四面體（B 比 A 平移 +5，邊長 10 → 必相交）
        MeshGeometry3D a = Tetra(new Vector3D(0, 0, 0));
        MeshGeometry3D b = Tetra(new Vector3D(5, 0, 0));
        InterferenceResult r1 = InterferenceService.Check(new[] { a }, new[] { b });
        failures += Check("互穿 → 相交", r1.Intersects, true);
        failures += Check("互穿 → 有交線段", r1.Segments.Count > 0, true);

        // 分離（B 平移 +30，gap 應 ≈ 30 - 10 = 20）
        MeshGeometry3D c = Tetra(new Vector3D(30, 0, 0));
        InterferenceResult r2 = InterferenceService.Check(new[] { a }, new[] { c });
        failures += Check("分離 → 不相交", r2.Intersects, false);
        bool gapOk = Math.Abs(r2.GapDistance - 20.0) < 0.01;
        Console.WriteLine($"  {(gapOk ? "PASS" : "FAIL")}: 分離 gap = {r2.GapDistance:F4}（期望 ≈ 20）");
        if (!gapOk) failures++;

        // 貼合（B 平移 +10，頂點剛好接觸 → 不算穿透、gap ≈ 0）
        MeshGeometry3D d = Tetra(new Vector3D(10, 0, 0));
        InterferenceResult r3 = InterferenceService.Check(new[] { a }, new[] { d });
        failures += Check("貼合 → 不算穿透", r3.Intersects, false);
        bool touchOk = r3.GapDistance < 0.01;
        Console.WriteLine($"  {(touchOk ? "PASS" : "FAIL")}: 貼合 gap = {r3.GapDistance:F6}（期望 ≈ 0）");
        if (!touchOk) failures++;

        Console.WriteLine(failures == 0 ? "InterferenceTest: 全部通過" : $"InterferenceTest: {failures} 項失敗");
        return failures;
    }

    private static int Check(string name, bool actual, bool expected)
    {
        bool ok = actual == expected;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}（{actual}）");
        return ok ? 0 : 1;
    }

    /// <summary>邊長 10 的四面體，offset 平移</summary>
    private static MeshGeometry3D Tetra(Vector3D offset)
    {
        Point3D P(double x, double y, double z) => new Point3D(x, y, z) + offset;
        var m = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                P(0, 0, 0), P(10, 0, 0), P(0, 10, 0), P(0, 0, 10),
            },
            TriangleIndices = new Int32Collection
            {
                0, 1, 2,  0, 1, 3,  0, 2, 3,  1, 2, 3,
            },
        };
        m.Freeze();
        return m;
    }
}
