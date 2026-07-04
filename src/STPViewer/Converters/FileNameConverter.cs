using System;
using System.Globalization;
using System.Windows.Data;

namespace STPViewer.Converters;

/// <summary>完整路徑 → 檔名（選單「最近檔案」顯示用；tooltip 仍給完整路徑）</summary>
public class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? System.IO.Path.GetFileName(s) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
