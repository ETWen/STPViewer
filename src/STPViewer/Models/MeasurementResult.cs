using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.Wpf;

namespace STPViewer.Models;

/// <summary>
/// 一筆量測結果。內部以 mm 儲存原始值，Title/Detail 依目前單位即時產生；
/// 切換單位時 3D 標籤（DynamicLabels）也同步改字。
/// </summary>
public class MeasurementResult : ObservableObject
{
    public MeasureMode Kind { get; init; }

    /// <summary>依單位產生標題（清單粗體列）</summary>
    public required Func<UnitSystem, string> TitleFor { get; init; }

    /// <summary>依單位產生多行明細</summary>
    public required Func<UnitSystem, string> DetailFor { get; init; }

    /// <summary>視圖中的標記（刪除量測時一併移除）</summary>
    public List<Visual3D> Overlays { get; } = new();

    /// <summary>單位相關的 3D 文字標籤（切換單位時更新 Text）</summary>
    public List<(BillboardTextVisual3D Label, Func<UnitSystem, string> TextFor)> DynamicLabels { get; } = new();

    private UnitSystem _unit = UnitSystem.Millimeter;

    public string Title => TitleFor(_unit);
    public string Detail => DetailFor(_unit);

    public void SetUnit(UnitSystem unit)
    {
        if (_unit == unit) return;
        _unit = unit;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        foreach ((BillboardTextVisual3D label, Func<UnitSystem, string> textFor) in DynamicLabels)
            label.Text = textFor(unit);
    }
}
