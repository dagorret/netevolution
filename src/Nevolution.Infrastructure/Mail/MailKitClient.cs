using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;
using Nevolution.Core.Resources;
using System.Net.Sockets;

namespace Nevolution.Infrastructure.Mail;

public sealed class MailKitClient : IMailClient
{
    private static readonly (MailFolderKind Kind, SpecialFolder? SpecialFolder, string DisplayName, string[] FallbackNames)[] KnownFolders =
    [
        (MailFolderKind.Inbox, null, Strings.Folder_Inbox, ["INBOX"]),
        (MailFolderKind.Sent, SpecialFolder.Sent, Strings.Folder_Sent, ["Sent", "Sent Items", "Sent Mail", "Enviados"]),
        (MailFolderKind.Drafts, SpecialFolder.Drafts, Strings.Folder_Drafts, ["Drafts", "Borradores"]),
        (MailFolderKind.Trash, SpecialFolder.Trash, Strings.Folder_Trash, ["Trash", "Deleted Items", "Bin", "Papelera"]),
        (MailFolderKind.Archive, SpecialFolder.Archive, Strings.Folder_Archive, ["Archive", "All Mail", "Archivados"])
    ];

    private readonly ImapClient _imapClient = new();
    private MailAccount? _connectedAccount;

    public async Task ConnectAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAccount(account);
        var username = ResolveUsername(account);

        try
        {
            if (_imapClient.IsConnected)
            {
                await _imapClient.DisconnectAsync(true);
            }

            _connectedAccount = account;

            Console.WriteLine($"IMAP connect: {FormatContext(account, username)}");
            await _imapClient.ConnectAsync(account.ImapHost, account.ImapPort, GetSecureSocketOptions(account.ImapPort));

            Console.WriteLine($"IMAP authenticate: {FormatContext(account, username)}");
            await _imapClient.AuthenticateAsync(username, account.Password);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
        {
            throw CreateException(
                ImapFailureKind.HostResolution,
                $"IMAP host resolution failed for '{account.ImapHost}'.",
                account,
                username,
                innerException: exception);
        }
        catch (MailKit.Security.AuthenticationException exception)
        {
            throw CreateException(
                ImapFailureKind.Authentication,
                $"IMAP authentication failed for '{username}'.",
                account,
                username,
                innerException: exception);
        }
        catch (System.Security.Authentication.AuthenticationException exception)
        {
            throw CreateException(
                ImapFailureKind.Security,
                $"Secure IMAP connection failed for '{account.ImapHost}:{account.ImapPort}'.",
                account,
                username,
                innerException: exception);
        }
        catch (CommandException exception) when (IsAuthenticationFailure(exception))
        {
            throw CreateException(
                ImapFailureKind.Authentication,
                $"IMAP authentication failed for '{username}'.",
                account,
                username,
                innerException: exception);
        }
        catch (Exception exception) when (exception is not ImapConnectionException)
        {
            throw CreateException(
                ImapFailureKind.Connection,
                $"IMAP connection failed for '{username}' at '{account.ImapHost}:{account.ImapPort}'.",
                account,
                username,
                innerException: exception);
        }
    }

    public async Task<IReadOnlyList<MailFolderInfo>> GetKnownFoldersAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!_imapClient.IsConnected
            || !_imapClient.IsAuthenticated
            || _connectedAccount is null
            || !string.Equals(_connectedAccount.Id, account.Id, StringComparison.Ordinal))
        {
            await ConnectAsync(account);
        }

        EnsureConnected();

        var allFolders = await GetAllFoldersAsync();
        var folders = new List<MailFolderInfo>(KnownFolders.Length);

        foreach (var definition in KnownFolders)
        {
            var resolvedFolder = ResolveFolder(definition.SpecialFolder, definition.FallbackNames, allFolders);

            folders.Add(new MailFolderInfo
            {
                Kind = definition.Kind,
                DisplayName = definition.DisplayName,
                ImapFolderName = resolvedFolder?.FullName ?? definition.FallbackNames[0]
            });
        }

        return folders;
    }

    public async Task<uint> GetUidValidityAsync(string folder)
    {
        var mailFolder = await OpenFolderAsync(folder);
        return mailFolder.UidValidity;
    }

    public async Task<IList<EmailMessage>> FetchHeadersAsync(string folder, uint fromUid)
    {
        var mailFolder = await OpenFolderAsync(folder);
        var uids = await SearchUidsAsync(mailFolder, fromUid);

        if (uids.Count == 0)
        {
            return [];
        }

        var summaries = await mailFolder.FetchAsync(
            uids,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId);

        var accountId = _connectedAccount?.Id ?? string.Empty;
        var emails = new List<EmailMessage>(summaries.Count);

        foreach (var summary in summaries)
        {
            var envelope = summary.Envelope;

            emails.Add(new EmailMessage
            {
                AccountId = accountId,
                Folder = folder,
                ImapUid = summary.UniqueId.Id,
                Subject = envelope?.Subject ?? string.Empty,
                From = envelope?.From?.ToString() ?? string.Empty,
                Date = envelope?.Date?.DateTime ?? DateTime.MinValue,
                HasBody = false
            });
        }

        return emails;
    }

    public async Task<EmailBody> GetBodyAsync(MailAccount account, string folder, uint uid)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAccount(account, folder, uid);
        var username = ResolveUsername(account);
        Console.WriteLine(
            $"GetBodyAsync: {FormatContext(account, username, folder, uid)}");

        if (!_imapClient.IsConnected
            || !_imapClient.IsAuthenticated
            || _connectedAccount is null
            || !string.Equals(_connectedAccount.Id, account.Id, StringComparison.Ordinal))
        {
            Console.WriteLine("GetBodyAsync: connecting IMAP client");
            await ConnectAsync(account);
            Console.WriteLine("GetBodyAsync: connected");
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "GetBodyAsync received an empty folder.",
                account,
                username,
                folder,
                uid);
        }

        if (uid == 0)
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "GetBodyAsync received uid=0.",
                account,
                username,
                folder,
                uid);
        }

        EnsureConnected();
        var mailFolder = await _imapClient.GetFolderAsync(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly);
        Console.WriteLine($"GetBodyAsync: opened folder {folder}");
        Console.WriteLine($"GetBodyAsync: fetching UID {uid}");
        var message = await mailFolder.GetMessageAsync(new UniqueId(uid));
        Console.WriteLine($"GetBodyAsync: message found for UID {uid}");
        var body = GetMessageBody(message);
        Console.WriteLine(
            $"GetBodyAsync: text length={body.TextBody.Length}, html length={body.HtmlBody.Length}");

        return body;
    }

    private async Task<IMailFolder> OpenFolderAsync(string folder)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var mailFolder = await _imapClient.GetFolderAsync(folder);

        if (!mailFolder.IsOpen)
        {
            await mailFolder.OpenAsync(FolderAccess.ReadOnly);
        }

        return mailFolder;
    }

    private async Task<IList<IMailFolder>> GetAllFoldersAsync()
    {
        var personalNamespace = _imapClient.PersonalNamespaces.FirstOrDefault();
        var rootFolder = personalNamespace is not null
            ? _imapClient.GetFolder(personalNamespace)
            : _imapClient.Inbox;

        var result = new List<IMailFolder>();
        await CollectFoldersAsync(rootFolder, result);
        return result;
    }

    private async Task CollectFoldersAsync(IMailFolder folder, ICollection<IMailFolder> result)
    {
        result.Add(folder);

        foreach (var child in await folder.GetSubfoldersAsync(false))
        {
            await CollectFoldersAsync(child, result);
        }
    }

    private IMailFolder? ResolveFolder(
        SpecialFolder? specialFolder,
        IReadOnlyCollection<string> fallbackNames,
        IList<IMailFolder> allFolders)
    {
        if (specialFolder.HasValue)
        {
            try
            {
                var special = _imapClient.GetFolder(specialFolder.Value);

                if (special is not null)
                {
                    return special;
                }
            }
            catch
            {
            }
        }

        return allFolders.FirstOrDefault(folder =>
            fallbackNames.Any(name =>
                string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder.FullName, name, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<IList<UniqueId>> SearchUidsAsync(IMailFolder mailFolder, uint fromUid)
    {
        if (fromUid == 0)
        {
            return await mailFolder.SearchAsync(SearchQuery.All);
        }

        if (fromUid == uint.MaxValue)
        {
            return [];
        }

        var startUid = new UniqueId(fromUid + 1);
        var range = new UniqueIdRange(startUid, UniqueId.MaxValue);

        return await mailFolder.SearchAsync(SearchQuery.Uids(range));
    }

    private void EnsureConnected()
    {
        if (!_imapClient.IsConnected || !_imapClient.IsAuthenticated || _connectedAccount is null)
        {
            throw new InvalidOperationException("IMAP client is not connected.");
        }
    }

    private static SecureSocketOptions GetSecureSocketOptions(int port)
    {
        return port switch
        {
            993 => SecureSocketOptions.SslOnConnect,
            143 => SecureSocketOptions.StartTlsWhenAvailable,
            _ => SecureSocketOptions.Auto
        };
    }

    private static bool IsAuthenticationFailure(CommandException exception)
    {
        return exception.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    private static EmailBody GetMessageBody(MimeMessage message)
    {
        return new EmailBody
        {
            TextBody = message.TextBody ?? string.Empty,
            HtmlBody = message.HtmlBody ?? string.Empty
        };
    }

    private static void ValidateAccount(MailAccount account, string? folder = null, uint? uid = null)
    {
        var username = ResolveUsername(account);

        if (string.IsNullOrWhiteSpace(account.Id))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account is missing AccountId.",
                account,
                username,
                folder,
                uid);
        }

        if (string.IsNullOrWhiteSpace(account.Email))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account is missing Email.",
                account,
                username,
                folder,
                uid);
        }

        if (string.IsNullOrWhiteSpace(account.ImapHost))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account is missing IMAP host.",
                account,
                username,
                folder,
                uid);
        }

        if (account.ImapPort <= 0 || account.ImapPort > 65535)
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account has an invalid IMAP port.",
                account,
                username,
                folder,
                uid);
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account is missing IMAP username.",
                account,
                username,
                folder,
                uid);
        }

        if (string.IsNullOrWhiteSpace(account.Password))
        {
            throw CreateException(
                ImapFailureKind.InvalidAccountConfiguration,
                "Active account is missing IMAP app password.",
                account,
                username,
                folder,
                uid);
        }
    }

    private static string ResolveUsername(MailAccount account)
    {
        return string.IsNullOrWhiteSpace(account.Username)
            ? account.Email.Trim()
            : account.Username.Trim();
    }

    private static ImapConnectionException CreateException(
        ImapFailureKind failureKind,
        string message,
        MailAccount account,
        string username,
        string? folder = null,
        uint? uid = null,
        Exception? innerException = null)
    {
        return new ImapConnectionException(
            failureKind,
            message,
            account.Id,
            account.Email,
            username,
            account.ImapHost,
            account.ImapPort,
            folder,
            uid,
            innerException);
    }

    private static string FormatContext(MailAccount account, string username, string? folder = null, uint? uid = null)
    {
        var folderPart = string.IsNullOrWhiteSpace(folder) ? "-" : folder;
        var uidPart = uid?.ToString() ?? "-";
        return $"AccountId={account.Id}, Email={account.Email}, Username={username}, Host={account.ImapHost}, Port={account.ImapPort}, Folder={folderPart}, Uid={uidPart}";
    }
}
