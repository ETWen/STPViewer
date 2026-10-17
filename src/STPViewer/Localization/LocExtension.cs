using System.Windows.Data;
using System.Windows.Markup;
using STPViewer.Services;

namespace STPViewer.Localization;

[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
