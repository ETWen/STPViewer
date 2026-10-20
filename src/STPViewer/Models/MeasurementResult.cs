using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.Wpf;

namespace STPViewer.Models;

/// <summary>
/// A measurement stored in millimeters. Its title, details, and 3D labels
/// update when the display unit changes.
/// </summary>
public class MeasurementResult : ObservableObject
{
    public MeasureMode Kind { get; init; }

    /// <summary>Create the list title for a display unit.</summary>
    public required Func<UnitSystem, string> TitleFor { get; init; }

    /// <summary>Create the detailed text for a display unit.</summary>
    public required Func<UnitSystem, string> DetailFor { get; init; }

    /// <summary>View markers removed with this measurement.</summary>
    public List<Visual3D> Overlays { get; } = new();

    /// <summary>3D labels whose text changes with the display unit.</summary>
    public List<(BillboardTextVisual3D Label, Func<UnitSystem, string> TextFor)> DynamicLabels { get; } = new();

    private UnitSystem _unit = UnitSystem.Millimeter;

    public string Title => TitleFor(_unit);
    public string Detail => DetailFor(_unit);

    public void SetUnit(UnitSystem unit)
    {
        if (_unit == unit) return;
        _unit = unit;
        RefreshText();
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        foreach ((BillboardTextVisual3D label, Func<UnitSystem, string> textFor) in DynamicLabels)
            label.Text = textFor(_unit);
    }
}
