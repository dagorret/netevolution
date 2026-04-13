using Nevolution.Core.Resources;

namespace Nevolution.Core.Models;

public static class MailFolderCatalog
{
    public static IReadOnlyList<MailFolderInfo> Defaults { get; } =
    [
        new MailFolderInfo { Kind = MailFolderKind.Inbox, DisplayName = Strings.Folder_Inbox, ImapFolderName = "INBOX" },
        new MailFolderInfo { Kind = MailFolderKind.Sent, DisplayName = Strings.Folder_Sent, ImapFolderName = "Sent" },
        new MailFolderInfo { Kind = MailFolderKind.Drafts, DisplayName = Strings.Folder_Drafts, ImapFolderName = "Drafts" },
        new MailFolderInfo { Kind = MailFolderKind.Trash, DisplayName = Strings.Folder_Trash, ImapFolderName = "Trash" },
        new MailFolderInfo { Kind = MailFolderKind.Archive, DisplayName = Strings.Folder_Archive, ImapFolderName = "Archive" }
    ];

    public static MailFolderKind ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MailFolderKind.Inbox;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "inbox" => MailFolderKind.Inbox,
            "sent" or "sentitems" or "sent-items" => MailFolderKind.Sent,
            "drafts" => MailFolderKind.Drafts,
            "trash" or "bin" or "deleted" => MailFolderKind.Trash,
            "archive" or "allmail" or "all-mail" => MailFolderKind.Archive,
            _ => Enum.TryParse<MailFolderKind>(value, true, out var parsedKind) ? parsedKind : MailFolderKind.Inbox
        };
    }

    public static MailFolderInfo GetDefault(MailFolderKind kind)
    {
        return Defaults.First(folder => folder.Kind == kind);
    }
}
