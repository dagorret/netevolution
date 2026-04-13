using System.ComponentModel;
using Nevolution.Core.Localization;
using Nevolution.Core.Resources;

namespace Nevolution.App.Desktop.Localization;

public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    private Localizer()
    {
        AppCulture.CultureChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Strings.ResourceManager.GetString(key, Strings.Culture) ?? $"!{key}!";
}
