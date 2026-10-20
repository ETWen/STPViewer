using System;
using System.Globalization;
using System.Windows.Data;
using STPViewer.Models;

namespace STPViewer.Converters;

/// <summary>
/// Convert MeasureMode to ToggleButton.IsChecked using the mode name
/// in ConverterParameter. Unchecking returns to None.
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s
            ? Enum.Parse<MeasureMode>(s)
            : MeasureMode.None;
}
