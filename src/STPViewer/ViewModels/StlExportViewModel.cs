using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using STPViewer.Services;

namespace STPViewer.ViewModels;

/// <summary>
/// Options and live size estimate for the STL export dialog.
/// </summary>
public partial class StlExportViewModel : ObservableObject
{
    /// <summary>Number of visible files available for export.</summary>
    public int FileCount { get; }

    /// <summary>Triangle count of the current display meshes.</summary>
    public long TriangleCount { get; }

    /// <summary>Whether B-rep geometry is available for retessellation.</summary>
    public bool HasBrep { get; }

    public StlExportViewModel(int fileCount, long triangleCount, bool hasBrep)
    {
        FileCount = fileCount;
        TriangleCount = triangleCount;
        HasBrep = hasBrep;
    }

    /// <summary>0 = merged STL; 1 = one STL per visible file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int scopeIndex;

    /// <summary>0 = binary; 1 = ASCII.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int formatIndex;

    /// <summary>0 = millimeters; 1 = inches; 2 = custom scale.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsCustomScale))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int unitIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string customScaleText = "1.0";

    /// <summary>0 = current mesh; 1 = finer B-rep tessellation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int qualityIndex;

    public bool IsCustomScale => UnitIndex == 2;

    public bool Ascii => FormatIndex == 1;
    public bool PerFile => ScopeIndex == 1;
    public StlMeshQuality Quality => (StlMeshQuality)QualityIndex;

    /// <summary>Coordinate scale, or null for an invalid custom value.</summary>
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
                ? LocalizationService.Format("Ui.StlExport.EstimatedSize", size)
                : LocalizationService.Format("Ui.StlExport.EstimatedSizeFine", size);
            return LocalizationService.Format("Ui.StlExport.Summary", FileCount, TriangleCount.ToString("N0"), basis);
        }
    }
}
