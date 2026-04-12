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
            ImapFailureKind.Authentication => "No se pudo autenticar la cuenta activa. Revisa email/app password.",
            ImapFailureKind.InvalidAccountConfiguration => "La cuenta activa esta incompleta. Revisa host, puerto, usuario y app password.",
            ImapFailureKind.HostResolution => "No se pudo resolver el host IMAP configurado para la cuenta activa.",
            ImapFailureKind.Security => "No se pudo establecer una conexion segura con el servidor IMAP.",
            ImapFailureKind.Connection => "No se pudo conectar al servidor IMAP configurado para la cuenta activa.",
            _ => "Fallo la conexion IMAP de la cuenta activa."
        };

    public string ToDiagnosticString()
    {
        var folderPart = string.IsNullOrWhiteSpace(Folder) ? "-" : Folder;
        var uidPart = Uid?.ToString() ?? "-";

        return $"AccountId={AccountId}, Email={Email}, Username={Username}, Host={Host}, Port={Port}, Folder={folderPart}, Uid={uidPart}";
    }
}
