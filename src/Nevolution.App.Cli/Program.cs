using Nevolution.Core;
using Nevolution.Core.Models;
using Nevolution.Infrastructure.Mail;
using Nevolution.Infrastructure.Persistence;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Nevolution.App.Cli;

internal static class Program
{
    private const string DefaultFolder = "INBOX";

    public static async Task<int> Main(string[] args)
    {
        var dataDir = ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);

        var databasePath = Path.Combine(dataDir, "mail.db");
        Console.WriteLine($"DB PATH: {databasePath}");

        var repository = new SqliteEmailRepository(databasePath);

        if (args.Length > 0 && string.Equals(args[0], "account", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAccountCommandAsync(repository, args.Skip(1).ToArray());
        }

        return await RunSyncAsync(repository, args);
    }

    private static async Task<int> HandleAccountCommandAsync(SqliteEmailRepository repository, string[] args)
    {
        if (args.Length == 0)
        {
            PrintAccountUsage();
            return 1;
        }

        var action = args[0];

        return action.ToLowerInvariant() switch
        {
            "add" => await AddAccountAsync(repository, args.Skip(1).ToArray()),
            "list" => await ListAccountsAsync(repository),
            "use" => await UseAccountAsync(repository, args.Skip(1).ToArray()),
            _ => PrintUnknownAccountCommand(action)
        };
    }

    private static int PrintUnknownAccountCommand(string action)
    {
        Console.WriteLine($"Comando de cuenta no reconocido: {action}");
        PrintAccountUsage();
        return 1;
    }

    private static async Task<int> AddAccountAsync(SqliteEmailRepository repository, string[] args)
    {
        var email = GetRequiredOption(args, "--email");
        var password = GetRequiredOption(args, "--password");
        var username = GetOption(args, "--username") ?? email;
        var account = new MailAccount
        {
            Id = GetOption(args, "--id") ?? email,
            DisplayName = GetOption(args, "--display-name") ?? email,
            Email = email,
            ImapHost = GetOption(args, "--imap-host") ?? "imap.gmail.com",
            ImapPort = GetIntOption(args, "--imap-port", 993),
            Username = username,
            Password = password,
            IsActive = HasFlag(args, "--active")
        };

        await repository.SaveAccountAsync(account);
        Console.WriteLine($"Cuenta guardada: {account.Email}");

        return 0;
    }

    private static async Task<int> ListAccountsAsync(SqliteEmailRepository repository)
    {
        var accounts = await repository.GetAccountsAsync();

        if (accounts.Count == 0)
        {
            Console.WriteLine("No hay cuentas configuradas.");
            return 0;
        }

        foreach (var account in accounts)
        {
            var marker = account.IsActive ? "*" : " ";
            Console.WriteLine($"{marker} {account.Id} | {account.DisplayName} | {account.Email} | {account.ImapHost}:{account.ImapPort}");
        }

        return 0;
    }

    private static async Task<int> UseAccountAsync(SqliteEmailRepository repository, string[] args)
    {
        var id = GetRequiredOption(args, "--id");
        var account = await repository.GetAccountAsync(id);

        if (account is null)
        {
            Console.WriteLine($"No existe la cuenta '{id}'.");
            return 1;
        }

        await repository.SetActiveAccountAsync(id);
        Console.WriteLine($"Cuenta activa: {account.Email}");
        return 0;
    }

    private static async Task<int> RunSyncAsync(SqliteEmailRepository repository, string[] args)
    {
        var account = await repository.GetActiveAccountAsync();

        if (account is null)
        {
            Console.WriteLine("No hay una cuenta activa en SQLite. Usa `account add` y luego `account use`.");
            return 1;
        }

        try
        {
            var folderOption = GetOption(args, "--folder") ?? DefaultFolder;
            var mailClient = new MailKitClient();
            var bodyMailClient = new MailKitClient();
            var syncService = new SyncService(mailClient, repository);
            var bodySyncService = new BackgroundBodySyncService(bodyMailClient, repository);
            var folders = await mailClient.GetKnownFoldersAsync(account);
            var folder = ResolveFolder(folderOption, folders).ImapFolderName;

            Console.WriteLine($"Resolviendo host IMAP {account.ImapHost}...");

            if (!await CanResolveHost(account.ImapHost))
            {
                Console.WriteLine("ERROR: No se pudo resolver el host");
                return 1;
            }

            Console.WriteLine($"Sincronizando carpeta {folder} para {account.Email}...");
            await syncService.SyncFolderAsync(account, folder);
            await bodySyncService.DownloadMissingBodiesAsync(account, folder, batchSize: 25);

            var emails = await repository.GetEmailsAsync(account.Id, folder, 500);
            Console.WriteLine($"Correos sincronizados: {emails.Count}");

            return 0;
        }
        catch (Exception exception)
        {
            PrintConnectionError(exception);
            return 1;
        }
    }

    private static MailFolderInfo ResolveFolder(string value, IEnumerable<MailFolderInfo> folders)
    {
        var requestedKind = MailFolderCatalog.ParseKind(value);

        return folders.FirstOrDefault(folder => folder.Kind == requestedKind)
               ?? folders.FirstOrDefault(folder => string.Equals(folder.ImapFolderName, value, StringComparison.OrdinalIgnoreCase))
               ?? MailFolderCatalog.GetDefault(requestedKind);
    }

    private static async Task<bool> CanResolveHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        try
        {
            await Dns.GetHostEntryAsync(host);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void PrintConnectionError(Exception exception)
    {
        if (exception is ImapConnectionException imapException)
        {
            Console.WriteLine($"ERROR: {imapException.UserMessage}");
            Console.WriteLine($"DETAIL: {imapException.ToDiagnosticString()}");
            return;
        }

        var rootException = exception.GetBaseException();

        if (rootException is SocketException socketException)
        {
            if (socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            {
                Console.WriteLine("ERROR: No se pudo resolver el host");
                return;
            }

            Console.WriteLine($"ERROR: No se pudo establecer la conexión ({socketException.Message})");
            return;
        }

        if (rootException is AuthenticationException)
        {
            Console.WriteLine("ERROR: Falló la autenticación IMAP. Verifica el email completo y el password.");
            return;
        }

        Console.WriteLine($"ERROR: {exception.Message}");
    }

    private static string GetRequiredOption(IReadOnlyList<string> args, string option)
    {
        return GetOption(args, option)
               ?? throw new InvalidOperationException($"Falta el argumento requerido {option}.");
    }

    private static string? GetOption(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int GetIntOption(IReadOnlyList<string> args, string option, int defaultValue)
    {
        var value = GetOption(args, option);
        return int.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag)
    {
        return args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintAccountUsage()
    {
        Console.WriteLine("Uso:");
        Console.WriteLine("  account add --email <mail> --password <password> [--display-name <name>] [--imap-host <host>] [--imap-port <port>] [--username <user>] [--active]");
        Console.WriteLine("  account list");
        Console.WriteLine("  account use --id <account-id>");
    }

    private static string ResolveDataDirectory()
    {
        var dataDir = Environment.GetEnvironmentVariable("NEVOLUTION_DATA_PATH");

        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new Exception("Project root not found");
        }

        return Path.Combine(dir.FullName, "data");
    }
}
