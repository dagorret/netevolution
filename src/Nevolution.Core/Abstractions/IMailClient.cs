using Nevolution.Core.Models;

namespace Nevolution.Core.Abstractions;

public interface IMailClient
{
    Task<IReadOnlyList<MailFolderInfo>> GetKnownFoldersAsync(MailAccount account);

    Task<SyncHeadersResult> SyncHeadersAsync(MailAccount account, string folder, uint fromUid, CancellationToken cancellationToken = default);

    Task<EmailBody> GetBodyAsync(MailAccount account, string folder, uint uid, CancellationToken cancellationToken = default);
}
