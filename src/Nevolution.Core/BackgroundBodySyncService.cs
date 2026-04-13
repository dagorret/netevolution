using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;

namespace Nevolution.Core;

public sealed class BackgroundBodySyncService
{
    private readonly IMailClient _mailClient;
    private readonly IEmailRepository _emailRepository;
    private readonly ImapOperationCoordinator _imapOperationCoordinator;
    private readonly object _inFlightLock = new();
    private readonly Dictionary<string, Task<EmailBody>> _inFlightDownloads = [];
    private readonly Dictionary<string, ImapConnectionException> _blockedAccounts = [];

    public BackgroundBodySyncService(IMailClient mailClient, IEmailRepository emailRepository, ImapOperationCoordinator imapOperationCoordinator)
    {
        _mailClient = mailClient;
        _emailRepository = emailRepository;
        _imapOperationCoordinator = imapOperationCoordinator;
    }

    public event Action<string, EmailBody>? BodyDownloaded;
    public event Action<ImapConnectionException>? DownloadFailed;

    public async Task<int> DownloadMissingBodiesAsync(
        MailAccount account,
        string folder,
        int batchSize = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var emails = await _emailRepository.GetEmailsWithoutBodyAsync(account.Id, folder, batchSize);
        var processedCount = 0;

        foreach (var email in emails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = await EnsureBodyDownloadedAsync(account, email, cancellationToken: cancellationToken);

            if (body.HasContent)
            {
                processedCount++;
            }
        }

        return processedCount;
    }

    public async Task RunAsync(
        MailAccount account,
        string folder,
        int batchSize = 10,
        TimeSpan? idleDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var effectiveDelay = idleDelay ?? TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryGetBlockedAccountFailure(account, out var blockedFailure))
            {
                await Task.Delay(effectiveDelay, cancellationToken);
                continue;
            }

            var processed = await DownloadMissingBodiesAsync(account, folder, batchSize, cancellationToken);

            if (processed == 0)
            {
                await Task.Delay(effectiveDelay, cancellationToken);
            }
        }
    }

    public async Task<EmailBody> EnsureBodyDownloadedAsync(
        MailAccount account,
        EmailMessage email,
        bool allowBlockedAccountRetry = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(email);

        if (!allowBlockedAccountRetry && TryGetBlockedAccountFailure(account, out var blockedFailure))
        {
            throw blockedFailure;
        }

        if (email.HasBody || !string.IsNullOrWhiteSpace(email.TextBody) || !string.IsNullOrWhiteSpace(email.HtmlBody))
        {
            return new EmailBody
            {
                TextBody = email.TextBody,
                HtmlBody = email.HtmlBody
            };
        }

        if (email.BodyUnavailable)
        {
            Console.WriteLine(
                $"[IMAP] skip missing message operation=body_download accountId={account.Id} folder={email.Folder} uid={email.ImapUid} emailId={email.Id}");
            return new EmailBody();
        }

        if (email.DeletedOnServer)
        {
            Console.WriteLine(
                $"[IMAP] skip deleted message operation=body_download accountId={account.Id} folder={email.Folder} uid={email.ImapUid} emailId={email.Id}");
            return new EmailBody();
        }

        var persistedEmail = await _emailRepository.GetEmailAsync(email.Id);

        if (persistedEmail is not null
            && (persistedEmail.HasBody
                || !string.IsNullOrWhiteSpace(persistedEmail.TextBody)
                || !string.IsNullOrWhiteSpace(persistedEmail.HtmlBody)
                || persistedEmail.BodyUnavailable))
        {
            return new EmailBody
            {
                TextBody = persistedEmail.TextBody,
                HtmlBody = persistedEmail.HtmlBody
            };
        }

        if (persistedEmail?.DeletedOnServer == true)
        {
            return new EmailBody();
        }

        Task<EmailBody> downloadTask;

        lock (_inFlightLock)
        {
            if (_inFlightDownloads.TryGetValue(email.Id, out var existingTask))
            {
                downloadTask = existingTask;
            }
            else
            {
                downloadTask = DownloadAndPersistBodyAsync(account, email, cancellationToken);
                _inFlightDownloads[email.Id] = downloadTask;
            }
        }

        try
        {
            return await downloadTask.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_inFlightLock)
            {
                if (_inFlightDownloads.TryGetValue(email.Id, out var currentTask)
                    && ReferenceEquals(currentTask, downloadTask)
                    && downloadTask.IsCompleted)
                {
                    _inFlightDownloads.Remove(email.Id);
                }
            }
        }
    }

    private async Task<EmailBody> DownloadAndPersistBodyAsync(
        MailAccount account,
        EmailMessage email,
        CancellationToken cancellationToken)
    {
        try
        {
            var persistedEmail = await _emailRepository.GetEmailAsync(email.Id);

            if (persistedEmail is not null
                && (persistedEmail.HasBody
                    || !string.IsNullOrWhiteSpace(persistedEmail.TextBody)
                    || !string.IsNullOrWhiteSpace(persistedEmail.HtmlBody)
                    || persistedEmail.BodyUnavailable))
            {
                return new EmailBody
                {
                    TextBody = persistedEmail.TextBody,
                    HtmlBody = persistedEmail.HtmlBody
                };
            }

            if (persistedEmail?.DeletedOnServer == true)
            {
                return new EmailBody();
            }

            var folder = string.IsNullOrWhiteSpace(email.Folder) ? "INBOX" : email.Folder;
            var body = await _imapOperationCoordinator.RunAsync(
                account,
                "body_download",
                folder,
                token => _mailClient.GetBodyAsync(account, folder, email.ImapUid, token),
                cancellationToken);
            ClearBlockedAccount(account);

            if (!body.HasContent)
            {
                return body;
            }

            await _emailRepository.UpdateBodyAsync(email.Id, body);
            BodyDownloaded?.Invoke(email.Id, body);
            return body;
        }
        catch (ImapConnectionException exception)
        {
            if (exception.IsAuthenticationFailure)
            {
                BlockAccount(account, exception);
            }

            NotifyDownloadFailed(exception);
            throw;
        }
        catch (ImapMessageNotFoundException)
        {
            await _emailRepository.MarkBodyUnavailableAsync(email.Id);
            email.BodyUnavailable = true;

            Console.WriteLine(
                $"[IMAP] message missing operation=body_download accountId={account.Id} folder={email.Folder} uid={email.ImapUid} emailId={email.Id}");
            return new EmailBody();
        }
    }

    private bool TryGetBlockedAccountFailure(MailAccount account, out ImapConnectionException exception)
    {
        lock (_inFlightLock)
        {
            return _blockedAccounts.TryGetValue(GetAccountKey(account), out exception!);
        }
    }

    private void BlockAccount(MailAccount account, ImapConnectionException exception)
    {
        lock (_inFlightLock)
        {
            _blockedAccounts[GetAccountKey(account)] = exception;
        }
    }

    private void ClearBlockedAccount(MailAccount account)
    {
        lock (_inFlightLock)
        {
            _blockedAccounts.Remove(GetAccountKey(account));
        }
    }

    private void NotifyDownloadFailed(ImapConnectionException exception)
    {
        DownloadFailed?.Invoke(exception);
    }

    private static string GetAccountKey(MailAccount account)
    {
        return $"{account.Id}|{account.Email}|{account.ImapHost}|{account.ImapPort}|{account.Username}";
    }
}
