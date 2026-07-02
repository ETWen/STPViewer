using System;
using System.IO;
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
