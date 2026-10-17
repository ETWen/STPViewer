using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Wpf;
using STPViewer.Services;

namespace STPViewer.ViewModels;

/// <summary>
/// Assembly tree node: imported file, STEP group, or geometry leaf.
/// Visibility, edges, and color cascade to children.
/// </summary>
public partial class ModelNodeViewModel : ObservableObject
{
    /// <summary>Palette used when assigning and cycling leaf colors.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0x90, 0xA4, 0xAE),
        Color.FromRgb(0x4F, 0xC3, 0xF7),
        Color.FromRgb(0xFF, 0xB7, 0x4D),
        Color.FromRgb(0x81, 0xC7, 0x84),
        Color.FromRgb(0xBA, 0x68, 0xC8),
        Color.FromRgb(0xE5, 0x73, 0x73),
        Color.FromRgb(0xFF, 0xD5, 0x4F),
        Color.FromRgb(0x4D, 0xB6, 0xAC),
        Color.FromRgb(0xA1, 0x88, 0x7F),
        Color.FromRgb(0x79, 0x86, 0xCB),
    };

    [ObservableProperty]
    private bool isVisible = true;

    /// <summary>Outline visibility.</summary>
    [ObservableProperty]
    private bool showEdges = true;

    [ObservableProperty]
    private bool isExpanded = true;

    [ObservableProperty]
    private Color color;

    public string Name
    {
        get => NameKey is null ? _name : NameNumber > 0 && NameKey == "Runtime.MeshNameNumbered"
            ? LocalizationService.Format(NameKey, NameNumber) : LocalizationService.Get(NameKey);
        init => _name = value;
    }
    private string _name = "";
    public string? NameKey { get; init; }
    public int NameNumber { get; init; }

    /// <summary>Set only on imported file roots.</summary>
    public string? FilePath { get; init; }
    public bool IsRoot => FilePath is not null;

    public ObservableCollection<ModelNodeViewModel> Children { get; } = new();
    public bool IsLeafNode => Children.Count == 0;

    public int SolidCount { get; init; }
    public int FaceCount { get; init; }
    public int TriangleCount { get; init; }

    /// <summary>World-space bounds updated after part transforms.</summary>
    public Rect3D Bounds
    {
        get => _bounds;
        set
        {
            if (_bounds == value) return;
            _bounds = value;
            OnPropertyChanged(nameof(Stats));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }
    private Rect3D _bounds = Rect3D.Empty;

    /// <summary>Axis-aligned size in millimeters.</summary>
    public string SizeText => Bounds.IsEmpty
        ? ""
        : $"{Bounds.SizeX:F1} × {Bounds.SizeY:F1} × {Bounds.SizeZ:F1} mm";

    /// <summary>Source B-rep objects transformed as whole parts.</summary>
    public List<CADability.GeoObject.IGeoObject> SourceGeos { get; } = new();

    /// <summary>False for STL or curve-only sources without B-rep geometry.</summary>
    public bool HasBrep { get; init; } = true;

    public string Stats =>
        LocalizationService.Format("Runtime.NodeStats", SolidCount, FaceCount, TriangleCount) +
        (HasBrep ? "" : LocalizationService.Get("Runtime.NoBrep")) +
        (Bounds.IsEmpty ? "" : LocalizationService.Format("Runtime.OutlineSize", SizeText));

    public string ToolTipText => (FilePath is not null ? FilePath + "\n" : "") + Stats;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Stats));
        OnPropertyChanged(nameof(ToolTipText));
        foreach (ModelNodeViewModel child in Children) child.RefreshLocalizedText();
    }

    // Tree filtering does not change viewport visibility.

    [ObservableProperty]
    private bool isFilterVisible = true;

    /// <summary>
    /// Shows matching nodes, their ancestors and descendants; expands matching branches.
    /// </summary>
    public bool ApplyFilter(string? filter, bool ancestorMatched = false)
    {
        bool empty = string.IsNullOrWhiteSpace(filter);
        bool selfMatch = !empty && Name.Contains(filter!, StringComparison.OrdinalIgnoreCase);
        bool anyChild = false;
        foreach (ModelNodeViewModel c in Children)
            anyChild |= c.ApplyFilter(filter, ancestorMatched || selfMatch);
        IsFilterVisible = empty || selfMatch || ancestorMatched || anyChild;
        if (!empty && anyChild) IsExpanded = true;
        return IsFilterVisible;
    }

    // Viewport objects
    public ModelVisual3D? BodyVisual { get; set; }
    public Model3DGroup? FacesContent { get; set; }
    public Model3DGroup? MergedContent { get; set; }
    public LinesVisual3D? EdgeVisual { get; set; }
    public DiffuseMaterial? SharedMaterial { get; set; }

    /// <summary>Unclipped edge endpoints used for section restoration.</summary>
    public Point3DCollection? OriginalEdgePoints { get; set; }

    /// <summary>Signals the ViewModel to update leaf viewport objects.</summary>
    public event Action<ModelNodeViewModel>? VisualStateChanged;

    private int _paletteIndex;

    public ModelNodeViewModel(int paletteIndex)
    {
        _paletteIndex = paletteIndex % Palette.Length;
        color = Palette[_paletteIndex];
    }

    /// <summary>All descendant leaves, including this node when it is a leaf.</summary>
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
