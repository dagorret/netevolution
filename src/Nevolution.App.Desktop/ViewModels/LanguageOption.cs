using System.Globalization;

namespace Nevolution.App.Desktop.ViewModels;

public sealed class LanguageOption
{
    public required CultureInfo Culture { get; init; }

    public required string DisplayName { get; init; }
}
