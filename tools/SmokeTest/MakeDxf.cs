// 測試輔助：用 CADability 內嵌的 netDxf 產生標準 DXF 測試檔
using netDxf;
using netDxf.Entities;

namespace SmokeTest;

internal static class MakeDxf
{
    public static void Run(string path)
    {
        var doc = new DxfDocument();
        doc.Entities.Add(new Line(new Vector3(0, 0, 0), new Vector3(100, 50, 0)));
        doc.Entities.Add(new Circle(new Vector3(50, 25, 0), 10));
        doc.Save(path);
        Console.WriteLine($"已產生 {path}");
    }
}
