using System;
using System.Globalization;
using System.Windows.Data;
using STPViewer.Models;

namespace STPViewer.Converters;

/// <summary>
/// MeasureMode ↔ ToggleButton.IsChecked。
/// ConverterParameter 為模式名稱字串；取消勾選時回到 None。
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
