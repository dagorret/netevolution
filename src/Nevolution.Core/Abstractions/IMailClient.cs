using Nevolution.Core.Models;

namespace Nevolution.Core.Abstractions;

public interface IMailClient
{
    Task ConnectAsync(MailAccount account);

    Task<IReadOnlyList<MailFolderInfo>> GetKnownFoldersAsync(MailAccount account);

    Task<uint> GetUidValidityAsync(string folder);

    Task<IList<EmailMessage>> FetchHeadersAsync(string folder, uint fromUid);

    Task<EmailBody> GetBodyAsync(MailAccount account, string folder, uint uid);
}
