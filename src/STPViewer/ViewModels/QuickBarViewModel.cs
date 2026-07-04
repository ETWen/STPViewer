using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace STPViewer.ViewModels;

/// <summary>快速列單一按鈕的顯示開關（選單「快速列 → 自訂按鈕」的 checkbox 項）</summary>
public partial class QuickBarItemViewModel : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public bool Default { get; }

    [ObservableProperty]
    private bool isChecked;

    public QuickBarItemViewModel(string key, string label, bool @default)
    {
        Key = key;
        Label = label;
        Default = @default;
        isChecked = @default;
    }
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
        ("Import",       "📂 匯入",        true),
        ("Recent",       "▾ 最近檔案",     true),
        ("ZoomAll",      "🔍 全覽",        true),
        ("ViewIso",      "視圖：等角",     true),
        ("ViewFront",    "視圖：前",       false),
        ("ViewTop",      "視圖：上",       false),
        ("ViewRight",    "視圖：右",       false),
        ("Ortho",        "正交投影",       true),
        ("Point",        "📍 點",          true),
        ("Distance",     "📏 距離",        true),
        ("Edge",         "📐 邊",          false),
        ("Face",         "⬛ 面",          false),
        ("Circle",       "⭕ 圓",          true),
        ("Angle",        "∠ 角度",         true),
        ("FaceDistance", "⇔ 面距",         false),
        ("Align",        "🎯 兩點對齊",    false),
        ("Align3",       "🎯 三點對齊",    false),
        ("RotateX",      "旋轉 ↻X",        false),
        ("RotateY",      "旋轉 ↻Y",        false),
        ("RotateZ",      "旋轉 ↻Z",        false),
        ("Drag",         "🖐 拖曳",        false),
        ("Gizmo",        "⊹ 操作器",       false),
        ("Interference", "🧩 干涉",        true),
        ("Section",      "✂ 剖面",         true),
        ("Clear",        "🧹 清除量測",    true),
        ("Unit",         "mm ⇄ in",        true),
        ("ExportCsv",    "💾 匯出 CSV",    false),
        ("Screenshot",   "📷 截圖",        false),
        ("ExportStep",   "📤 匯出 STEP",   false),
        ("ExportStl",    "📐 匯出 STL",    false),
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

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (QuickBarItemViewModel item in Items)
            item.IsChecked = item.Default;
    }
}
