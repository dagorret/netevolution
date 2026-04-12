namespace Nevolution.Core.Models;

public sealed class MailFolderInfo
{
    public MailFolderKind Kind { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string ImapFolderName { get; init; } = string.Empty;
}
