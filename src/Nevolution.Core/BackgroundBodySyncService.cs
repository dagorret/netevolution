using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;

namespace Nevolution.Core;

public sealed class BackgroundBodySyncService
{
    private readonly IMailClient _mailClient;
    private readonly IEmailRepository _emailRepository;
    private readonly SemaphoreSlim _imapLock = new(1, 1);
    private readonly object _inFlightLock = new();
    private readonly Dictionary<string, Task<EmailBody>> _inFlightDownloads = [];
    private readonly Dictionary<string, ImapConnectionException> _blockedAccounts = [];

    public BackgroundBodySyncService(IMailClient mailClient, IEmailRepository emailRepository)
    {
        _mailClient = mailClient;
        _emailRepository = emailRepository;
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

        var persistedEmail = await _emailRepository.GetEmailAsync(email.Id);

        if (persistedEmail is not null
            && (persistedEmail.HasBody
                || !string.IsNullOrWhiteSpace(persistedEmail.TextBody)
                || !string.IsNullOrWhiteSpace(persistedEmail.HtmlBody)))
        {
            return new EmailBody
            {
                TextBody = persistedEmail.TextBody,
                HtmlBody = persistedEmail.HtmlBody
            };
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
            return await downloadTask;
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
        await _imapLock.WaitAsync(cancellationToken);

        try
        {
            var persistedEmail = await _emailRepository.GetEmailAsync(email.Id);

            if (persistedEmail is not null
                && (persistedEmail.HasBody
                    || !string.IsNullOrWhiteSpace(persistedEmail.TextBody)
                    || !string.IsNullOrWhiteSpace(persistedEmail.HtmlBody)))
            {
                return new EmailBody
                {
                    TextBody = persistedEmail.TextBody,
                    HtmlBody = persistedEmail.HtmlBody
                };
            }

            var folder = string.IsNullOrWhiteSpace(email.Folder) ? "INBOX" : email.Folder;
            var body = await _mailClient.GetBodyAsync(account, folder, email.ImapUid);
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
        finally
        {
            _imapLock.Release();
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
