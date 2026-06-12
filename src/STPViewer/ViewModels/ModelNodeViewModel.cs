using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Wpf;

namespace STPViewer.ViewModels;

/// <summary>
/// 裝配樹節點：root = 匯入檔、group = STEP 裝配（Block）、leaf = 零件幾何。
/// 可見性 / 邊線 / 顏色設定向下層疊（cascade）。
/// </summary>
public partial class ModelNodeViewModel : ObservableObject
{
    /// <summary>調色盤（每個 leaf 依序取色，點色塊循環切換）</summary>
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0x90, 0xA4, 0xAE), // 藍灰（金屬感）
        Color.FromRgb(0x4F, 0xC3, 0xF7), // 淺藍
        Color.FromRgb(0xFF, 0xB7, 0x4D), // 橙
        Color.FromRgb(0x81, 0xC7, 0x84), // 綠
        Color.FromRgb(0xBA, 0x68, 0xC8), // 紫
        Color.FromRgb(0xE5, 0x73, 0x73), // 紅
        Color.FromRgb(0xFF, 0xD5, 0x4F), // 黃
        Color.FromRgb(0x4D, 0xB6, 0xAC), // 藍綠
        Color.FromRgb(0xA1, 0x88, 0x7F), // 棕
        Color.FromRgb(0x79, 0x86, 0xCB), // 靛藍
    };

    [ObservableProperty]
    private bool isVisible = true;

    /// <summary>輪廓邊線開關（邊線量過大的檔案匯入時自動關閉，避免轉動視角卡頓）</summary>
    [ObservableProperty]
    private bool showEdges = true;

    [ObservableProperty]
    private bool isExpanded = true;

    [ObservableProperty]
    private Color color;

    public string Name { get; init; } = "";

    /// <summary>root（檔案層級）才有值</summary>
    public string? FilePath { get; init; }
    public bool IsRoot => FilePath is not null;

    public ObservableCollection<ModelNodeViewModel> Children { get; } = new();
    public bool IsLeafNode => Children.Count == 0;

    public int SolidCount { get; init; }
    public int FaceCount { get; init; }
    public int TriangleCount { get; init; }

    /// <summary>世界座標邊界（零件平移後同步位移）</summary>
    public Rect3D Bounds { get; set; }

    /// <summary>來源 B-rep 物件（零件平移時整體 Modify，避免共用邊被重複位移）</summary>
    public List<CADability.GeoObject.IGeoObject> SourceGeos { get; } = new();

    /// <summary>false = STL/曲線等無 B-rep 來源（邊/面/圓量測不可用）</summary>
    public bool HasBrep { get; init; } = true;

    public string Stats =>
        $"{SolidCount} 實體 · {FaceCount} 面 · {TriangleCount:N0} △" + (HasBrep ? "" : " · 無B-rep");

    public string ToolTipText => (FilePath is not null ? FilePath + "\n" : "") + Stats;

    // ── 視覺物件 ──
    public ModelVisual3D? BodyVisual { get; set; }          // leaf：面網格容器（內容在兩種模式間切換）
    public Model3DGroup? FacesContent { get; set; }         // leaf：逐面 GeometryModel3D（量測拾取用）
    public Model3DGroup? MergedContent { get; set; }        // leaf：整零件合併成 1 個 GeometryModel3D（瀏覽用，draw call 大減）
    public LinesVisual3D? EdgeVisual { get; set; }          // root：整檔合併邊線（一檔一條，避免轉動卡頓）
    public DiffuseMaterial? SharedMaterial { get; set; }

    /// <summary>原始（未剖切）邊線端點 — 剖面還原用</summary>
    public Point3DCollection? OriginalEdgePoints { get; set; }

    /// <summary>leaf 的可見性/邊線狀態變更（MainViewModel 同步 viewport）</summary>
    public event Action<ModelNodeViewModel>? VisualStateChanged;

    private int _paletteIndex;

    public ModelNodeViewModel(int paletteIndex)
    {
        _paletteIndex = paletteIndex % Palette.Length;
        color = Palette[_paletteIndex];
    }

    /// <summary>所有 leaf 後代（含自身若為 leaf）</summary>
    public IEnumerable<ModelNodeViewModel> Leaves()
    {
        if (IsLeafNode) { yield return this; yield break; }
        foreach (ModelNodeViewModel c in Children)
            foreach (ModelNodeViewModel l in c.Leaves())
                yield return l;
    }

    partial void OnIsVisibleChanged(bool value)
    {
        foreach (ModelNodeViewModel c in Children) c.IsVisible = value;
        if (IsLeafNode) VisualStateChanged?.Invoke(this);
    }

    partial void OnShowEdgesChanged(bool value)
    {
        foreach (ModelNodeViewModel c in Children) c.ShowEdges = value;
        if (IsLeafNode) VisualStateChanged?.Invoke(this);
    }

    partial void OnColorChanged(Color value)
    {
        foreach (ModelNodeViewModel c in Children) c.Color = value;
        if (SharedMaterial is not null)
        {
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            SharedMaterial.Brush = brush;
        }
    }

    [RelayCommand]
    private void CycleColor()
    {
        _paletteIndex = (_paletteIndex + 1) % Palette.Length;
        Color = Palette[_paletteIndex];
    }
}
