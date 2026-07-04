using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using STPViewer.Models;
using STPViewer.Services;

namespace STPViewer.ViewModels;

// ─── 匯出（量測 CSV / 視圖 PNG 截圖 / STEP / STL）────────────────────
public partial class MainViewModel
{
    [RelayCommand]
    private void ExportCsv()
    {
        if (Measurements.Count == 0)
        {
            StatusText = "沒有量測結果可匯出";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "匯出量測結果",
            Filter = "CSV 檔案 (*.csv)|*.csv",
            FileName = $"measurements_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("標籤,類型,明細");
        foreach (MeasurementResult m in Measurements)
        {
            static string Esc(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
            sb.AppendLine($"{Esc(m.Title)},{m.Kind},{Esc(m.Detail.Replace("\n", " | "))}");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM：Excel 中文不亂碼
        StatusText = $"已匯出 {Measurements.Count} 筆量測 → {Path.GetFileName(dlg.FileName)}";
    }

    /// <summary>
    /// 把可見檔案（目前位置，含對齊/旋轉/拖曳結果）匯出成「新的」STEP 檔 —
    /// 對齊插合後的配合結果可交接給 CAD；不動任何原始檔（唯讀原則不變）。
    /// 只含 B-rep Solid/Shell（STL 網格、DXF 線架構無法進 STEP B-rep）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ExportStepFile()
    {
        var geos = Roots.Where(r => r.IsVisible)
            .SelectMany(r => r.Leaves()).Where(l => l.IsVisible)
            .SelectMany(l => l.SourceGeos)
            .Where(g => g is CADability.GeoObject.Solid or CADability.GeoObject.Shell)
            .ToList();
        if (geos.Count == 0)
        {
            StatusText = "沒有可匯出的 B-rep 幾何（STEP 匯出僅含 Solid/Shell；請勾選要匯出的 STEP 檔案）";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "匯出目前場景為 STEP（零件在目前對齊位置）",
            Filter = "STEP 檔案 (*.stp)|*.stp|STEP 檔案 (*.step)|*.step",
            FileName = $"assembly_{DateTime.Now:yyyyMMdd_HHmmss}.stp",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // B-rep 已含目前位置（TransformRoot 對 Solid/Shell 整體 Modify）→ 直接寫出
            CADability.Project project = CADability.Project.CreateSimpleProject();
            CADability.Model model = project.GetActiveModel();
            foreach (CADability.GeoObject.IGeoObject g in geos) model.Add(g);
            new CADability.ExportStep().WriteToFile(dlg.FileName, project);
            StatusText = $"已匯出 STEP（{geos.Count} 個實體，目前位置）→ {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"STEP 匯出失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 匯出可見檔案（目前位置）為 STL 網格檔 — 先開參數對話框（範圍/格式/單位/精度 + 三角形數與大小預估）
    /// 確認後才選路徑寫檔。精細/較粗 = 由 B-rep 重新三角化（背景執行緒）；STL/DXF 來源一律用現有網格。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ExportStlFile()
    {
        // 可匯出 = 可見 leaf 且有網格（DXF 純線架構無網格 → 排除）
        var groups = Roots.Where(r => r.IsVisible)
            .Select(r => (Root: r, Leaves: r.Leaves()
                .Where(l => l.IsVisible && GetMergedMesh(l) is not null).ToList()))
            .Where(g => g.Leaves.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            StatusText = "沒有可匯出的網格（請勾選至少一個含實體網格的檔案）";
            return;
        }

        long triangles = groups.SelectMany(g => g.Leaves)
            .Sum(l => GetMergedMesh(l)!.TriangleIndices.Count / 3L);
        bool hasBrep = groups.SelectMany(g => g.Leaves).Any(l => l.HasBrep && l.SourceGeos.Count > 0);

        // 參數對話框（確認再匯出）
        var vm = new StlExportViewModel(groups.Count, triangles, hasBrep);
        var dialog = new StlExportDialog(vm) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || vm.Scale is not double scale) return;

        var dlg = new SaveFileDialog
        {
            Title = vm.PerFile ? "匯出 STL（每個檔案各一個，以此為基底檔名）" : "匯出 STL（合併單一檔）",
            Filter = "STL 檔案 (*.stl)|*.stl",
            FileName = vm.PerFile
                ? $"stl_{DateTime.Now:yyyyMMdd_HHmmss}.stl"
                : $"assembly_{DateTime.Now:yyyyMMdd_HHmmss}.stl",
        };
        if (dlg.ShowDialog() != true) return;

        // UI 執行緒先快照：GeometryModel3D/Model3DGroup 是 DispatcherObject 不可跨執行緒，
        // 背景只能碰 frozen MeshGeometry3D 與 CADability B-rep（SourceGeos）
        var snapshot = groups
            .Select(g => (g.Root.Name, Leaves: g.Leaves
                .Select(l => (Mesh: GetMergedMesh(l)!, l.HasBrep, l.SourceGeos))
                .ToList()))
            .ToList();

        IsBusy = true;
        StatusText = vm.Quality == StlMeshQuality.Current
            ? "匯出 STL 中…"
            : "重新三角化 + 匯出 STL 中…（大組件需要時間）";
        try
        {
            double factor = StlExportService.PrecisionFactor(vm.Quality);
            var written = await Task.Run(() =>
            {
                var results = new List<(string File, int Triangles)>();
                if (vm.PerFile)
                {
                    string dir = Path.GetDirectoryName(dlg.FileName)!;
                    string baseName = Path.GetFileNameWithoutExtension(dlg.FileName);
                    foreach ((string rootName, var leaves) in snapshot)
                    {
                        string file = Path.Combine(dir, $"{baseName}_{SafeFileName(rootName)}.stl");
                        int n = StlExportService.Write(file, CollectMeshes(leaves, vm.Quality, factor), vm.Ascii, scale);
                        results.Add((file, n));
                    }
                }
                else
                {
                    var meshes = CollectMeshes(snapshot.SelectMany(g => g.Leaves).ToList(), vm.Quality, factor);
                    int n = StlExportService.Write(dlg.FileName, meshes, vm.Ascii, scale);
                    results.Add((dlg.FileName, n));
                }
                return results;
            });

            long total = written.Sum(w => (long)w.Triangles);
            StatusText = written.Count == 1
                ? $"已匯出 STL（{total:N0} 三角形）→ {Path.GetFileName(written[0].File)}"
                : $"已匯出 {written.Count} 個 STL（共 {total:N0} 三角形）→ {Path.GetDirectoryName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"STL 匯出失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>leaf 的合併網格（frozen、未剖切、目前位置）— STL 匯出的「目前顯示網格」來源</summary>
    private static MeshGeometry3D? GetMergedMesh(ModelNodeViewModel leaf) =>
        leaf.MergedContent?.Children.Count > 0
            ? (leaf.MergedContent.Children[0] as GeometryModel3D)?.Geometry as MeshGeometry3D
            : null;

    /// <summary>
    /// 收集要寫進 STL 的網格（背景執行緒；輸入是 UI 快照）：
    /// 精細且有 B-rep → 重新三角化（循序，CADability 非執行緒安全）；
    /// 其餘（目前網格 / STL 來源）→ 快照的合併網格（frozen，可安全跨執行緒讀取）。
    /// </summary>
    private static List<MeshGeometry3D> CollectMeshes(
        IReadOnlyList<(MeshGeometry3D Mesh, bool HasBrep, List<CADability.GeoObject.IGeoObject> SourceGeos)> leaves,
        StlMeshQuality quality, double precisionFactor)
    {
        var meshes = new List<MeshGeometry3D>();
        foreach ((MeshGeometry3D mesh, bool hasBrep, var sourceGeos) in leaves)
        {
            if (quality != StlMeshQuality.Current && hasBrep && sourceGeos.Count > 0)
            {
                List<MeshGeometry3D> tess = StlExportService.Tessellate(sourceGeos, precisionFactor);
                if (tess.Count > 0) { meshes.AddRange(tess); continue; }
                // 重新三角化全數失敗 → 退回現有網格，不讓零件憑空消失
            }
            meshes.Add(mesh);
        }
        return meshes;
    }

    private static string SafeFileName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem)) stem = name;
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        return stem;
    }

    [RelayCommand]
    private void SaveScreenshot()
    {
        if (_viewport is null || _viewport.ActualWidth < 1) return;
        var dlg = new SaveFileDialog
        {
            Title = "儲存視圖截圖",
            Filter = "PNG 圖片 (*.png)|*.png",
            FileName = $"stpviewer_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;

        const double scale = 2.0; // 2x 解析度
        var rtb = new RenderTargetBitmap(
            (int)(_viewport.ActualWidth * scale), (int)(_viewport.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(_viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(dlg.FileName);
        encoder.Save(fs);
        StatusText = $"已儲存截圖 → {Path.GetFileName(dlg.FileName)}";
    }
}
