using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Nevolution.App.Desktop.Localization;

public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Mode = BindingMode.OneWay,
            Source = Localizer.Instance
        };
    }
}
