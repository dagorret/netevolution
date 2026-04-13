using System.Globalization;

namespace Nevolution.Core.Localization;

public static class AppCulturePreferences
{
    private const string PreferencesFileName = "language.txt";
    public const string DefaultCultureName = "es";

    private static readonly HashSet<string> SupportedCultureNames = ["es", "en"];

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
    [
        CultureInfo.GetCultureInfo("es"),
        CultureInfo.GetCultureInfo("en")
    ];

    public static CultureInfo LoadPreferredCulture(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var path = GetPreferencesPath(dataDirectory);

        if (!File.Exists(path))
        {
            return CultureInfo.GetCultureInfo(DefaultCultureName);
        }

        var cultureName = File.ReadAllText(path).Trim();
        return NormalizeCulture(cultureName);
    }

    public static void SavePreferredCulture(string dataDirectory, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(culture);

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(GetPreferencesPath(dataDirectory), NormalizeCulture(culture.Name).Name);
    }

    public static CultureInfo NormalizeCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return CultureInfo.GetCultureInfo(DefaultCultureName);
        }

        var normalizedName = cultureName.Trim().ToLowerInvariant();
        return SupportedCultureNames.Contains(normalizedName)
            ? CultureInfo.GetCultureInfo(normalizedName)
            : CultureInfo.GetCultureInfo(DefaultCultureName);
    }

    private static string GetPreferencesPath(string dataDirectory)
    {
        return Path.Combine(dataDirectory, PreferencesFileName);
    }
}
