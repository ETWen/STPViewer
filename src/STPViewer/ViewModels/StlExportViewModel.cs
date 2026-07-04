using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using STPViewer.Services;

namespace STPViewer.ViewModels;

/// <summary>
/// STL 匯出參數對話框的 ViewModel：範圍 / 格式 / 單位縮放 / 網格精度 + 即時摘要（三角形數、預估大小）。
/// 對話框按「匯出」後由 MainViewModel.Export 讀取選項執行。
/// </summary>
public partial class StlExportViewModel : ObservableObject
{
    /// <summary>可見（可匯出）檔案數</summary>
    public int FileCount { get; }

    /// <summary>目前顯示網格的三角形總數（精細/較粗會重新三角化，實際數量以結果為準）</summary>
    public long TriangleCount { get; }

    /// <summary>是否有 B-rep 來源（全 STL/DXF 時精度選項無意義 → 停用）</summary>
    public bool HasBrep { get; }

    public StlExportViewModel(int fileCount, long triangleCount, bool hasBrep)
    {
        FileCount = fileCount;
        TriangleCount = triangleCount;
        HasBrep = hasBrep;
    }

    /// <summary>0 = 全部可見合併成單一 STL；1 = 每個可見檔案各一個 STL</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int scopeIndex;

    /// <summary>0 = Binary（建議，檔案小）；1 = ASCII（文字可讀，檔案大）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int formatIndex;

    /// <summary>0 = mm（1:1）；1 = inch（÷25.4）；2 = 自訂縮放</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsCustomScale))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int unitIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string customScaleText = "1.0";

    /// <summary>0 = 目前顯示網格；1 = 精細（B-rep 重新三角化）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int qualityIndex;

    public bool IsCustomScale => UnitIndex == 2;

    public bool Ascii => FormatIndex == 1;
    public bool PerFile => ScopeIndex == 1;
    public StlMeshQuality Quality => (StlMeshQuality)QualityIndex;

    /// <summary>座標縮放；自訂值無效時回 null（擋確認）</summary>
    public double? Scale => UnitIndex switch
    {
        0 => 1.0,
        1 => 1.0 / 25.4,
        _ => double.TryParse(CustomScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
             && s > 0 && double.IsFinite(s) ? s : null,
    };

    public bool CanConfirm => Scale is not null;

    public string Summary
    {
        get
        {
            long est = StlExportService.EstimateBytes(TriangleCount, Ascii);
            string size = StlExportService.FormatBytes(est);
            string basis = QualityIndex == 0
                ? $"預估 {size}"
                : $"以目前網格預估 {size}（實際依重新三角化結果）";
            return $"{FileCount} 個可見檔案 · {TriangleCount:N0} 三角形 · {basis}";
        }
    }
}
