using Nevolution.Core.Models;

namespace Nevolution.Core;

public sealed class ImapMessageNotFoundException : Exception
{
    public ImapMessageNotFoundException(MailAccount account, string folder, uint uid, Exception innerException)
        : base($"IMAP message not found for account '{account.Id}', folder '{folder}', uid '{uid}'.", innerException)
    {
        AccountId = account.Id;
        Folder = folder;
        Uid = uid;
    }

    public string AccountId { get; }

    public string Folder { get; }

    public uint Uid { get; }
}
