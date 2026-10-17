using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CADability;
using CADability.GeoObject;
using STPViewer.Services;
using IOPath = System.IO.Path; // Avoid the CADability.GeoObject.Path name collision.

namespace SmokeTest;

/// <summary>
/// Headless STEP export round trip: import, export through the same CADability path,
/// then import the result and compare solid counts.
/// </summary>
public static class ExportTest
{
    public static int Run(string sourcePath, string outPath)
    {
        Console.WriteLine($"=== ExportTest: {IOPath.GetFileName(sourcePath)} → {IOPath.GetFileName(outPath)} ===");
        ImportedFileData data = new StepImportService().Import(sourcePath);
        var geos = Collect(data.Root).Where(g => g is Solid or Shell).ToList();
        Console.WriteLine($"  Source Solid/Shell objects = {geos.Count} (solids = {data.Root.SolidCount})");

        Project project = Project.CreateSimpleProject();
        Model model = project.GetActiveModel();
        foreach (IGeoObject g in geos) model.Add(g);
        new ExportStep().WriteToFile(outPath, project);
        var fi = new FileInfo(outPath);
        Console.WriteLine($"  Written ({fi.Length:N0} bytes)");
        if (fi.Length < 100)
        {
            Console.WriteLine("ExportTest: FAIL (output is nearly empty)");
            return 1;
        }

        ImportedFileData back = new StepImportService().Import(outPath);
        Console.WriteLine($"  Reimported solids = {back.Root.SolidCount}, faces = {back.Root.FaceCount}");
        bool ok = back.Root.SolidCount == data.Root.SolidCount && back.Root.FaceCount > 0;
        Console.WriteLine(ok
            ? "ExportTest: PASS (solid counts match)"
            : $"ExportTest: FAIL (source solids {data.Root.SolidCount} vs reimported {back.Root.SolidCount})");
        return ok ? 0 : 1;
    }

    private static IEnumerable<IGeoObject> Collect(ImportedNode n)
    {
        foreach (IGeoObject g in n.SourceGeos) yield return g;
        foreach (ImportedNode c in n.Children)
            foreach (IGeoObject g in Collect(c))
                yield return g;
    }
}
