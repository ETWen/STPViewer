using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STPViewer.Services;

namespace STPViewer.ViewModels;

/// <summary>Visibility toggle for one quick bar button.</summary>
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
/// Quick bar button registry and saved visibility selections.
/// </summary>
public partial class QuickBarViewModel : ObservableObject
{
    /// <summary>Button order, label key, and default visibility.</summary>
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

    /// <summary>All buttons in quick bar order.</summary>
    public IReadOnlyList<QuickBarItemViewModel> Items { get; }

    public QuickBarViewModel()
    {
        Items = Registry.Select(r => new QuickBarItemViewModel(r.Key, r.Label, r.Default)).ToList();
        _byKey = Items.ToDictionary(i => i.Key);
    }

    /// <summary>Indexer used by XAML visibility bindings.</summary>
    public QuickBarItemViewModel this[string key] => _byKey[key];

    /// <summary>Loads saved visibility keys; null retains defaults.</summary>
    public void Load(List<string>? visibleKeys)
    {
        if (visibleKeys is null) return;
        var set = new HashSet<string>(visibleKeys, StringComparer.Ordinal);
        foreach (QuickBarItemViewModel item in Items)
            item.IsChecked = set.Contains(item.Key);
    }

    /// <summary>Selected keys saved in settings.json.</summary>
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
