using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STPViewer.Services;

namespace STPViewer.ViewModels;

/// <summary>快速列單一按鈕的顯示開關（選單「快速列 → 自訂按鈕」的 checkbox 項）</summary>
public partial class QuickBarItemViewModel : ObservableObject
{
    public string Key { get; }
    public string Label => LocalizationService.Get(_labelKey);
    private readonly string _labelKey;
    public bool Default { get; }

    [ObservableProperty]
    private bool isChecked;

    public QuickBarItemViewModel(string key, string label, bool @default)
    {
        Key = key;
        _labelKey = label;
        Default = @default;
        isChecked = @default;
    }

    public void RefreshText() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 快速工具列自訂：所有功能都在選單列，快速列只放使用者勾選的常用按鈕。
/// XAML 以索引子綁定：Visibility="{Binding QuickBar[Point].IsChecked, Converter=…}"。
/// 勾選狀態隨 settings.json（QuickBarKeys）保存；null = 用預設。
/// </summary>
public partial class QuickBarViewModel : ObservableObject
{
    /// <summary>註冊表 = 快速列順序 + 選單顯示名 + 是否預設顯示（一般 user 常用）</summary>
    private static readonly (string Key, string Label, bool Default)[] Registry =
    {
        ("Import",       "QuickBar.Import",        true),
        ("Recent",       "QuickBar.Recent",        true),
        ("ZoomAll",      "QuickBar.ZoomAll",       true),
        ("ViewIso",      "QuickBar.ViewIso",       true),
        ("ViewFront",    "QuickBar.ViewFront",     false),
        ("ViewTop",      "QuickBar.ViewTop",       false),
        ("ViewRight",    "QuickBar.ViewRight",     false),
        ("Ortho",        "QuickBar.Ortho",         true),
        ("Point",        "QuickBar.Point",         true),
        ("Distance",     "QuickBar.Distance",      true),
        ("Edge",         "QuickBar.Edge",          false),
        ("Face",         "QuickBar.Face",          false),
        ("Circle",       "QuickBar.Circle",        true),
        ("Angle",        "QuickBar.Angle",         true),
        ("FaceDistance", "QuickBar.FaceDistance",  false),
        ("Align",        "QuickBar.Align",         false),
        ("Align3",       "QuickBar.Align3",        false),
        ("RotateX",      "QuickBar.RotateX",       false),
        ("RotateY",      "QuickBar.RotateY",       false),
        ("RotateZ",      "QuickBar.RotateZ",       false),
        ("Drag",         "QuickBar.Drag",          false),
        ("Gizmo",        "QuickBar.Gizmo",         false),
        ("Interference", "QuickBar.Interference",  true),
        ("Section",      "QuickBar.Section",       true),
        ("Clear",        "QuickBar.Clear",         true),
        ("Unit",         "QuickBar.Unit",          true),
        ("ExportCsv",    "QuickBar.ExportCsv",     false),
        ("Screenshot",   "QuickBar.Screenshot",    false),
        ("ExportStep",   "QuickBar.ExportStep",    false),
        ("ExportStl",    "QuickBar.ExportStl",     false),
        ("Language",     "QuickBar.Language",      true),
    };

    private readonly Dictionary<string, QuickBarItemViewModel> _byKey;

    /// <summary>依快速列順序的全部項目（選單自訂清單用）</summary>
    public IReadOnlyList<QuickBarItemViewModel> Items { get; }

    public QuickBarViewModel()
    {
        Items = Registry.Select(r => new QuickBarItemViewModel(r.Key, r.Label, r.Default)).ToList();
        _byKey = Items.ToDictionary(i => i.Key);
    }

    /// <summary>XAML 索引子綁定入口：QuickBar[Point].IsChecked</summary>
    public QuickBarItemViewModel this[string key] => _byKey[key];

    /// <summary>套用保存的勾選清單；null = 維持預設（含未知 key 容錯：新版本新增按鈕用預設值）</summary>
    public void Load(List<string>? visibleKeys)
    {
        if (visibleKeys is null) return;
        var set = new HashSet<string>(visibleKeys, StringComparer.Ordinal);
        foreach (QuickBarItemViewModel item in Items)
            item.IsChecked = set.Contains(item.Key);
    }

    /// <summary>目前勾選清單（存 settings.json）</summary>
    public List<string> ToKeys() =>
        Items.Where(i => i.IsChecked).Select(i => i.Key).ToList();

    public void RefreshText()
    {
        foreach (QuickBarItemViewModel item in Items)
            item.RefreshText();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (QuickBarItemViewModel item in Items)
            item.IsChecked = item.Default;
    }
}
