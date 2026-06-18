// One-shot diagnostic: print per-leaf world-space bounding boxes so we can
// figure out each connector's mating-face normal (axis the guide posts / holes point).
using STPViewer.Services;

namespace SmokeTest;

internal static class BBoxDump
{
    public static int Run(string path)
    {
        var svc = new StepImportService();
        ImportedFileData data = svc.Import(path);
        System.Console.WriteLine($"=== {System.IO.Path.GetFileName(path)} ===");
        Print(data.Root, 0);
        return 0;
    }

    private static void Print(ImportedNode n, int depth)
    {
        var b = n.Bounds;
        string box = b.IsEmpty
            ? "(empty)"
            : $"X[{b.X,8:F1}..{b.X + b.SizeX,8:F1}] Y[{b.Y,8:F1}..{b.Y + b.SizeY,8:F1}] Z[{b.Z,8:F1}..{b.Z + b.SizeZ,8:F1}]  " +
              $"size {b.SizeX,7:F1} x {b.SizeY,7:F1} x {b.SizeZ,7:F1}";
        System.Console.WriteLine($"{new string(' ', depth * 2)}{n.Name,-28} {box}");
        foreach (ImportedNode c in n.Children) Print(c, depth + 1);
    }
}
