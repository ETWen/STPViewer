using System;
using System.Globalization;
using System.Windows.Data;

namespace STPViewer.Converters;

/// <summary>Show file names in the recent-files menu while retaining full paths as tooltips.</summary>
public class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? System.IO.Path.GetFileName(s) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
