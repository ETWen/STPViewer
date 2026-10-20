// Generate a small standard DXF test file with the bundled netDxf library.
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
        Console.WriteLine($"Generated {path}");
    }
}
