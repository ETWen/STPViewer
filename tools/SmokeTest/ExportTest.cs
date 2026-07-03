using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CADability;
using CADability.GeoObject;
using STPViewer.Services;
using IOPath = System.IO.Path; // CADability.GeoObject.Path（曲線）撞名

namespace SmokeTest;

/// <summary>
/// STEP 匯出往返驗證（v0.5.0 匯出 STEP 功能的無 UI 驗證）：
/// 匯入來源 STEP → 走與 MainViewModel.ExportStepFile 相同路徑寫出新檔 → 重新匯入比對實體數。
/// </summary>
public static class ExportTest
{
    public static int Run(string sourcePath, string outPath)
    {
        Console.WriteLine($"=== ExportTest: {IOPath.GetFileName(sourcePath)} → {IOPath.GetFileName(outPath)} ===");
        ImportedFileData data = new StepImportService().Import(sourcePath);
        var geos = Collect(data.Root).Where(g => g is Solid or Shell).ToList();
        Console.WriteLine($"  來源 Solid/Shell 物件 = {geos.Count}（Solids = {data.Root.SolidCount}）");

        Project project = Project.CreateSimpleProject();
        Model model = project.GetActiveModel();
        foreach (IGeoObject g in geos) model.Add(g);
        new ExportStep().WriteToFile(outPath, project);
        var fi = new FileInfo(outPath);
        Console.WriteLine($"  已寫出（{fi.Length:N0} bytes）");
        if (fi.Length < 100)
        {
            Console.WriteLine("ExportTest: FAIL（輸出檔近乎空白）");
            return 1;
        }

        ImportedFileData back = new StepImportService().Import(outPath);
        Console.WriteLine($"  回讀 Solids = {back.Root.SolidCount}, Faces = {back.Root.FaceCount}");
        bool ok = back.Root.SolidCount == data.Root.SolidCount && back.Root.FaceCount > 0;
        Console.WriteLine(ok
            ? "ExportTest: PASS（實體數一致）"
            : $"ExportTest: FAIL（來源 Solids {data.Root.SolidCount} ↔ 回讀 {back.Root.SolidCount}）");
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
