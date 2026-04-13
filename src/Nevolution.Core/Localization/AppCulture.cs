using System.Globalization;
using Nevolution.Core.Resources;

namespace Nevolution.Core.Localization;

public static class AppCulture
{
    private static CultureInfo _current = CultureInfo.CurrentUICulture;

    public static event EventHandler<CultureInfo>? CultureChanged;

    public static CultureInfo Current => _current;

    public static void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Strings.Culture = culture;

        if (Equals(_current, culture))
        {
            return;
        }

        _current = culture;
        CultureChanged?.Invoke(null, culture);
    }
}
