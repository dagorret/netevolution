using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;

namespace Nevolution.Core;

public sealed class SyncService
{
    private readonly IMailClient _mailClient;
    private readonly IEmailRepository _emailRepository;
    private readonly ImapOperationCoordinator _imapOperationCoordinator;

    public SyncService(IMailClient mailClient, IEmailRepository emailRepository, ImapOperationCoordinator imapOperationCoordinator)
    {
        _mailClient = mailClient;
        _emailRepository = emailRepository;
        _imapOperationCoordinator = imapOperationCoordinator;
    }

    public async Task<SyncFolderResult> SyncFolderAsync(MailAccount account, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return await _imapOperationCoordinator.RunAsync(
            account,
            "folder_sync",
            folder,
            async token =>
            {
                token.ThrowIfCancellationRequested();
                var folderState = await _emailRepository.GetFolderStateAsync(account.Id, folder);
                var previousLastUid = folderState?.LastUid ?? 0;
                var lastUid = previousLastUid;
                var resetFolder = false;
                var softDeletedCount = 0;
                var restoredCount = 0;
                var backfillTriggered = false;
                var backfilledHeadersCount = 0;
                var localVisibleCountBefore = await _emailRepository.GetVisibleEmailCountAsync(account.Id, folder);
                var localVisibleCountAfter = localVisibleCountBefore;
                var serverVisibleCount = 0;

                token.ThrowIfCancellationRequested();
                var syncResult = await _mailClient.SyncHeadersAsync(account, folder, lastUid, token);
                var serverUidValidity = syncResult.UidValidity;
                serverVisibleCount = syncResult.ServerUids.Count;

                if (folderState is null || folderState.UidValidity != serverUidValidity)
                {
                    token.ThrowIfCancellationRequested();
                    await _emailRepository.ClearFolderAsync(account.Id, folder);
                    lastUid = 0;
                    resetFolder = true;
                    syncResult = await _mailClient.SyncHeadersAsync(account, folder, lastUid, token);
                    serverUidValidity = syncResult.UidValidity;
                }

                token.ThrowIfCancellationRequested();
                restoredCount = await _emailRepository.RestoreSoftDeletedEmailsAsync(account.Id, folder, syncResult.ServerUids);
                softDeletedCount = await _emailRepository.SoftDeleteMissingEmailsAsync(account.Id, folder, syncResult.ServerUids);

                if (syncResult.NewEmails.Count > 0)
                {
                    foreach (var email in syncResult.NewEmails)
                    {
                        email.AccountId = account.Id;
                        email.Folder = folder;
                        email.DeletedOnServer = false;
                        email.BodyUnavailable = false;
                    }

                    token.ThrowIfCancellationRequested();
                    await _emailRepository.SaveHeadersAsync(syncResult.NewEmails);
                    lastUid = syncResult.NewEmails.Max(email => email.ImapUid);
                }

                if (ShouldRunBackfill(previousLastUid, localVisibleCountBefore, serverVisibleCount, syncResult.NewEmails.Count, resetFolder))
                {
                    backfillTriggered = true;
                    Console.WriteLine(
                        $"[SyncRecovery] Backfill triggered: accountId={account.Id}, folder={folder}, localVisibleBefore={localVisibleCountBefore}, serverVisible={serverVisibleCount}, previousLastUid={previousLastUid}");

                    token.ThrowIfCancellationRequested();
                    var backfillResult = await _mailClient.SyncHeadersAsync(account, folder, fromUid: 0, token);

                    if (backfillResult.NewEmails.Count > 0)
                    {
                        foreach (var email in backfillResult.NewEmails)
                        {
                            email.AccountId = account.Id;
                            email.Folder = folder;
                            email.DeletedOnServer = false;
                            email.BodyUnavailable = false;
                        }

                        token.ThrowIfCancellationRequested();
                        await _emailRepository.SaveHeadersAsync(backfillResult.NewEmails);
                        backfilledHeadersCount = backfillResult.NewEmails.Count;
                        lastUid = Math.Max(lastUid, backfillResult.NewEmails.Max(email => email.ImapUid));
                    }

                    token.ThrowIfCancellationRequested();
                    restoredCount += await _emailRepository.RestoreSoftDeletedEmailsAsync(account.Id, folder, backfillResult.ServerUids);
                    softDeletedCount += await _emailRepository.SoftDeleteMissingEmailsAsync(account.Id, folder, backfillResult.ServerUids);
                    serverVisibleCount = backfillResult.ServerUids.Count;
                }

                localVisibleCountAfter = await _emailRepository.GetVisibleEmailCountAsync(account.Id, folder);

                var updatedState = new FolderState
                {
                    AccountId = account.Id,
                    Folder = folder,
                    LastUid = lastUid,
                    UidValidity = serverUidValidity
                };

                token.ThrowIfCancellationRequested();
                await _emailRepository.SaveFolderStateAsync(updatedState);

                return new SyncFolderResult
                {
                    AccountId = account.Id,
                    Folder = folder,
                    PreviousLastUid = previousLastUid,
                    NewLastUid = lastUid,
                    FetchedHeadersCount = syncResult.NewEmails.Count,
                    ResetFolder = resetFolder,
                    SoftDeletedCount = softDeletedCount,
                    RestoredCount = restoredCount,
                    BackfillTriggered = backfillTriggered,
                    BackfilledHeadersCount = backfilledHeadersCount,
                    LocalVisibleCountBefore = localVisibleCountBefore,
                    LocalVisibleCountAfter = localVisibleCountAfter,
                    ServerVisibleCount = serverVisibleCount
                };
            },
            cancellationToken);
    }

    private static bool ShouldRunBackfill(
        uint previousLastUid,
        int localVisibleCountBefore,
        int serverVisibleCount,
        int fetchedHeadersCount,
        bool resetFolder)
    {
        if (resetFolder || previousLastUid == 0 || fetchedHeadersCount > 0)
        {
            return false;
        }

        if (serverVisibleCount == 0)
        {
            return false;
        }

        return localVisibleCountBefore < serverVisibleCount;
    }
}
