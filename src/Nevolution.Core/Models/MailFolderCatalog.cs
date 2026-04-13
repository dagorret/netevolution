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
        return TryResolveKind(value, out var kind)
            ? kind
            : MailFolderKind.Inbox;
    }

    public static bool TryResolveKind(string? value, out MailFolderKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = MailFolderKind.Inbox;
            return false;
        }

        var normalized = value.Trim();
        var leaf = normalized.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;

        kind = leaf.Trim().ToLowerInvariant() switch
        {
            "inbox" => MailFolderKind.Inbox,
            "sent" or "sentitems" or "sent-items" or "sent mail" or "sent items" or "enviados" => MailFolderKind.Sent,
            "drafts" or "borradores" => MailFolderKind.Drafts,
            "trash" or "bin" or "deleted" or "deleted items" or "papelera" => MailFolderKind.Trash,
            "archive" or "allmail" or "all-mail" => MailFolderKind.Archive,
            _ when Enum.TryParse<MailFolderKind>(leaf, true, out var parsedKind) => parsedKind,
            _ => default
        };

        return normalized.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            || kind is MailFolderKind.Sent or MailFolderKind.Drafts or MailFolderKind.Trash or MailFolderKind.Archive
            || string.Equals(leaf, "inbox", StringComparison.OrdinalIgnoreCase);
    }

    public static MailFolderInfo GetDefault(MailFolderKind kind)
    {
        return Defaults.First(folder => folder.Kind == kind);
    }
}
