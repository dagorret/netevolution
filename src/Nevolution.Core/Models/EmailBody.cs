namespace Nevolution.Core.Models;

public sealed class EmailBody
{
    public string TextBody { get; init; } = string.Empty;

    public string HtmlBody { get; init; } = string.Empty;

    public bool HasContent => !string.IsNullOrWhiteSpace(TextBody) || !string.IsNullOrWhiteSpace(HtmlBody);
}
