// 無 UI smoke test：驗證 CAD 檔解析 → 三角化 → 裝配樹 整條匯入管線
using System.Diagnostics;
using System.IO;
using STPViewer.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("用法: SmokeTest [--tree] <file.stp|.stl|.dxf> [...]");
    return 1;
}

if (args[0] == "--tree")
{
    foreach (string p in args.Skip(1)) SmokeTest.TreeDump.Run(p);
    return 0;
}

if (args[0] == "--make-dxf")
{
    SmokeTest.MakeDxf.Run(args[1]);
    return 0;
}

if (args[0] == "--clip-test")
    return SmokeTest.ClipTest.Run() == 0 ? 0 : 2;

if (args[0] == "--interference-test")
    return SmokeTest.InterferenceTest.Run() == 0 ? 0 : 2;

if (args[0] == "--align-test")
    return SmokeTest.AlignTest.Run() == 0 ? 0 : 2;

var service = new StepImportService { Progress = s => Console.WriteLine($"  [階段] {s}") };
int failed = 0;

foreach (string path in args)
{
    Console.WriteLine($"=== {Path.GetFileName(path)} ===");
    var sw = Stopwatch.StartNew();
    try
    {
        ImportedFileData data = service.Import(path);
        sw.Stop();
        ImportedNode r = data.Root;
        Console.WriteLine($"  Solids    : {r.SolidCount}");
        Console.WriteLine($"  Faces     : {r.FaceCount}");
        Console.WriteLine($"  Triangles : {r.TriangleCount:N0}");
        Console.WriteLine($"  Bounds    : {r.Bounds.SizeX:F2} x {r.Bounds.SizeY:F2} x {r.Bounds.SizeZ:F2} mm");
        Console.WriteLine($"  Time      : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("  --- 裝配樹 ---");
        PrintNode(r, 1);
    }
    catch (Exception ex)
    {
        sw.Stop();
        failed++;
        Console.WriteLine($"  FAILED ({sw.ElapsedMilliseconds} ms): {ex.GetType().Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 2;

static void PrintNode(ImportedNode n, int indent)
{
    string seg = n.EdgeSegments is null ? "" : $", edgeSegs={n.EdgeSegments.Count / 2:N0}";
    Console.WriteLine($"{new string(' ', indent * 2)}{n.Name}  " +
        $"[solids={n.SolidCount}, faces={n.FaceCount}, tris={n.TriangleCount:N0}{seg}{(n.HasBrep ? "" : ", 無B-rep")}]");
    foreach (ImportedNode c in n.Children) PrintNode(c, indent + 1);
}
