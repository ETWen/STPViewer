using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using STPViewer.Models;

namespace STPViewer.ViewModels;

// ─── 匯出（量測 CSV / 視圖 PNG 截圖）────────────────────────────────
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
