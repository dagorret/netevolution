using Nevolution.Core.Resources;

namespace Nevolution.Core.Models;

public sealed class ImapConnectionException : InvalidOperationException
{
    public ImapConnectionException(
        ImapFailureKind failureKind,
        string message,
        string accountId,
        string email,
        string username,
        string host,
        int port,
        string? folder = null,
        uint? uid = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        AccountId = accountId;
        Email = email;
        Username = username;
        Host = host;
        Port = port;
        Folder = folder;
        Uid = uid;
    }

    public ImapFailureKind FailureKind { get; }

    public string AccountId { get; }

    public string Email { get; }

    public string Username { get; }

    public string Host { get; }

    public int Port { get; }

    public string? Folder { get; }

    public uint? Uid { get; }

    public bool IsAuthenticationFailure => FailureKind == ImapFailureKind.Authentication;

    public string UserMessage =>
        FailureKind switch
        {
            ImapFailureKind.Authentication => Strings.UserError_ImapAuthentication,
            ImapFailureKind.InvalidAccountConfiguration => Strings.UserError_ImapInvalidAccountConfiguration,
            ImapFailureKind.HostResolution => Strings.UserError_ImapHostResolution,
            ImapFailureKind.Security => Strings.UserError_ImapSecurity,
            ImapFailureKind.Connection => Strings.UserError_ImapConnection,
            _ => Strings.UserError_ImapGeneric
        };

    public string ToDiagnosticString()
    {
        var folderPart = string.IsNullOrWhiteSpace(Folder) ? "-" : Folder;
        var uidPart = Uid?.ToString() ?? "-";

        return $"AccountId={AccountId}, Email={Email}, Username={Username}, Host={Host}, Port={Port}, Folder={folderPart}, Uid={uidPart}";
    }
}
