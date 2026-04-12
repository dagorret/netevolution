using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;

namespace Nevolution.Core;

public sealed class SyncService
{
    private readonly IMailClient _mailClient;
    private readonly IEmailRepository _emailRepository;

    public SyncService(IMailClient mailClient, IEmailRepository emailRepository)
    {
        _mailClient = mailClient;
        _emailRepository = emailRepository;
    }

    public async Task SyncFolderAsync(MailAccount account, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        cancellationToken.ThrowIfCancellationRequested();
        await _mailClient.ConnectAsync(account);

        cancellationToken.ThrowIfCancellationRequested();
        var folderState = await _emailRepository.GetFolderStateAsync(account.Id, folder);
        cancellationToken.ThrowIfCancellationRequested();
        var serverUidValidity = await _mailClient.GetUidValidityAsync(folder);

        var lastUid = folderState?.LastUid ?? 0;

        if (folderState is null || folderState.UidValidity != serverUidValidity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _emailRepository.ClearFolderAsync(account.Id, folder);
            lastUid = 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emails = await _mailClient.FetchHeadersAsync(folder, lastUid);

        if (emails.Count > 0)
        {
            foreach (var email in emails)
            {
                email.AccountId = account.Id;
                email.Folder = folder;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _emailRepository.SaveHeadersAsync(emails);
            lastUid = emails.Max(email => email.ImapUid);
        }

        var updatedState = new FolderState
        {
            AccountId = account.Id,
            Folder = folder,
            LastUid = lastUid,
            UidValidity = serverUidValidity
        };

        cancellationToken.ThrowIfCancellationRequested();
        await _emailRepository.SaveFolderStateAsync(updatedState);
    }
}
