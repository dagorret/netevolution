using Microsoft.Data.Sqlite;
using Nevolution.Core.Abstractions;
using Nevolution.Core.Models;

namespace Nevolution.Infrastructure.Persistence;

public sealed class SqliteEmailRepository : IEmailRepository
{
    private readonly string _connectionString;

    public SqliteEmailRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = $"Data Source={databasePath}";
        Console.WriteLine($"Using SQLite DB: {_connectionString}");
        InitializeDatabase();
    }

    public async Task SaveHeadersAsync(IEnumerable<EmailMessage> emails)
    {
        ArgumentNullException.ThrowIfNull(emails);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO Emails (
                Id,
                AccountId,
                Folder,
                Uid,
                Subject,
                FromAddress,
                ToAddress,
                Date,
                HasBody,
                Body,
                TextBody,
                HtmlBody,
                IsRead
            )
            VALUES (
                $id,
                $accountId,
                $folder,
                $uid,
                $subject,
                $fromAddress,
                $toAddress,
                $date,
                $hasBody,
                $body,
                $textBody,
                $htmlBody,
                $isRead
            );
            """;

        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$id";
        command.Parameters.Add(idParameter);

        var accountIdParameter = command.CreateParameter();
        accountIdParameter.ParameterName = "$accountId";
        command.Parameters.Add(accountIdParameter);

        var folderParameter = command.CreateParameter();
        folderParameter.ParameterName = "$folder";
        command.Parameters.Add(folderParameter);

        var uidParameter = command.CreateParameter();
        uidParameter.ParameterName = "$uid";
        command.Parameters.Add(uidParameter);

        var subjectParameter = command.CreateParameter();
        subjectParameter.ParameterName = "$subject";
        command.Parameters.Add(subjectParameter);

        var fromAddressParameter = command.CreateParameter();
        fromAddressParameter.ParameterName = "$fromAddress";
        command.Parameters.Add(fromAddressParameter);

        var toAddressParameter = command.CreateParameter();
        toAddressParameter.ParameterName = "$toAddress";
        command.Parameters.Add(toAddressParameter);

        var dateParameter = command.CreateParameter();
        dateParameter.ParameterName = "$date";
        command.Parameters.Add(dateParameter);

        var hasBodyParameter = command.CreateParameter();
        hasBodyParameter.ParameterName = "$hasBody";
        command.Parameters.Add(hasBodyParameter);

        var bodyParameter = command.CreateParameter();
        bodyParameter.ParameterName = "$body";
        command.Parameters.Add(bodyParameter);

        var textBodyParameter = command.CreateParameter();
        textBodyParameter.ParameterName = "$textBody";
        command.Parameters.Add(textBodyParameter);

        var htmlBodyParameter = command.CreateParameter();
        htmlBodyParameter.ParameterName = "$htmlBody";
        command.Parameters.Add(htmlBodyParameter);

        var isReadParameter = command.CreateParameter();
        isReadParameter.ParameterName = "$isRead";
        command.Parameters.Add(isReadParameter);

        foreach (var email in emails)
        {
            idParameter.Value = email.Id;
            accountIdParameter.Value = email.AccountId;
            folderParameter.Value = email.Folder;
            uidParameter.Value = (long)email.ImapUid;
            subjectParameter.Value = email.Subject;
            fromAddressParameter.Value = email.From;
            toAddressParameter.Value = DBNull.Value;
            dateParameter.Value = email.Date.ToString("O");
            hasBodyParameter.Value = 0;
            bodyParameter.Value = DBNull.Value;
            textBodyParameter.Value = DBNull.Value;
            htmlBodyParameter.Value = DBNull.Value;
            isReadParameter.Value = email.IsRead ? 1 : 0;

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task UpdateBodyAsync(string id, EmailBody body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(body);
        Console.WriteLine(
            $"Saving body for email {id}, text length={body.TextBody.Length}, html length={body.HtmlBody.Length}");

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Emails
            SET Body = CASE
                    WHEN LENGTH(TRIM($textBody)) > 0 THEN $textBody
                    WHEN LENGTH(TRIM($htmlBody)) > 0 THEN $htmlBody
                    ELSE ''
                END,
                TextBody = $textBody,
                HtmlBody = $htmlBody,
                HasBody = CASE
                    WHEN LENGTH(TRIM($textBody)) > 0 OR LENGTH(TRIM($htmlBody)) > 0 THEN 1
                    ELSE 0
                END
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$textBody", body.TextBody ?? string.Empty);
        command.Parameters.AddWithValue("$htmlBody", body.HtmlBody ?? string.Empty);

        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkAsReadAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Emails
            SET IsRead = 1
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<uint> GetLastUidAsync(string accountId, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(Uid), 0)
            FROM Emails
            WHERE AccountId = $accountId
              AND Folder = $folder;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folder", folder);

        var result = await command.ExecuteScalarAsync();
        return ConvertToUInt32(result);
    }

    public async Task<IList<EmailMessage>> GetEmailsAsync(string? accountId, string folder, int limit = 100, int offset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return await GetEmailsInternalAsync(accountId, folder, limit, offset, onlyMissingBody: false);
    }

    public async Task<List<EmailMessage>> GetEmailsWithoutBodyAsync(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AccountId, Folder, Uid, Subject, FromAddress, Date, HasBody,
                   CASE
                       WHEN COALESCE(NULLIF(TextBody, ''), '') <> '' THEN TextBody
                       WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN ''
                       WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                           Body LIKE '%<html%' OR
                           Body LIKE '%<body%' OR
                           Body LIKE '%<div%' OR
                           Body LIKE '%<p%' OR
                           Body LIKE '%<table%' OR
                           Body LIKE '%<br%' OR
                           Body LIKE '%<span%' OR
                           Body LIKE '%</%'
                       ) THEN ''
                       ELSE COALESCE(Body, '')
                   END,
                   CASE
                       WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN HtmlBody
                       WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                           Body LIKE '%<html%' OR
                           Body LIKE '%<body%' OR
                           Body LIKE '%<div%' OR
                           Body LIKE '%<p%' OR
                           Body LIKE '%<table%' OR
                           Body LIKE '%<br%' OR
                           Body LIKE '%<span%' OR
                           Body LIKE '%</%'
                       ) THEN Body
                       ELSE ''
                   END,
                   IsRead
            FROM Emails
            WHERE HasBody = 0
            ORDER BY Date DESC, Uid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var emails = new List<EmailMessage>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            emails.Add(MapEmail(reader));
        }

        return emails;
    }

    public async Task<IList<EmailMessage>> GetEmailsWithoutBodyAsync(string accountId, string folder, int limit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return await GetEmailsInternalAsync(accountId, folder, limit, 0, onlyMissingBody: true);
    }

    public async Task<EmailMessage?> GetEmailAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AccountId, Folder, Uid, Subject, FromAddress, Date, HasBody,
                   CASE
                       WHEN COALESCE(NULLIF(TextBody, ''), '') <> '' THEN TextBody
                       WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN ''
                       WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                           Body LIKE '%<html%' OR
                           Body LIKE '%<body%' OR
                           Body LIKE '%<div%' OR
                           Body LIKE '%<p%' OR
                           Body LIKE '%<table%' OR
                           Body LIKE '%<br%' OR
                           Body LIKE '%<span%' OR
                           Body LIKE '%</%'
                       ) THEN ''
                       ELSE COALESCE(Body, '')
                   END,
                   CASE
                       WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN HtmlBody
                       WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                           Body LIKE '%<html%' OR
                           Body LIKE '%<body%' OR
                           Body LIKE '%<div%' OR
                           Body LIKE '%<p%' OR
                           Body LIKE '%<table%' OR
                           Body LIKE '%<br%' OR
                           Body LIKE '%<span%' OR
                           Body LIKE '%</%'
                       ) THEN Body
                       ELSE ''
                   END,
                   IsRead
            FROM Emails
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapEmail(reader);
    }

    public async Task SaveAccountAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        if (account.IsActive)
        {
            await using var resetCommand = connection.CreateCommand();
            resetCommand.Transaction = (SqliteTransaction)transaction;
            resetCommand.CommandText =
                """
                UPDATE Accounts
                SET IsActive = 0
                WHERE Id <> $id;
                """;
            resetCommand.Parameters.AddWithValue("$id", account.Id);
            await resetCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO Accounts (Id, DisplayName, Email, ImapHost, ImapPort, Username, Password, IsActive)
            VALUES ($id, $displayName, $email, $imapHost, $imapPort, $username, $password, $isActive)
            ON CONFLICT(Id) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Email = excluded.Email,
                ImapHost = excluded.ImapHost,
                ImapPort = excluded.ImapPort,
                Username = excluded.Username,
                Password = excluded.Password,
                IsActive = excluded.IsActive;
            """;
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$displayName", account.DisplayName);
        command.Parameters.AddWithValue("$email", account.Email);
        command.Parameters.AddWithValue("$imapHost", account.ImapHost);
        command.Parameters.AddWithValue("$imapPort", account.ImapPort);
        command.Parameters.AddWithValue("$username", account.Username);
        command.Parameters.AddWithValue("$password", account.Password);
        command.Parameters.AddWithValue("$isActive", account.IsActive ? 1 : 0);

        await command.ExecuteNonQueryAsync();

        if (!account.IsActive)
        {
            await using var ensureActiveCommand = connection.CreateCommand();
            ensureActiveCommand.Transaction = (SqliteTransaction)transaction;
            ensureActiveCommand.CommandText =
                """
                UPDATE Accounts
                SET IsActive = 1
                WHERE Id = $id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Accounts
                      WHERE IsActive = 1
                  );
                """;
            ensureActiveCommand.Parameters.AddWithValue("$id", account.Id);
            await ensureActiveCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IList<MailAccount>> GetAccountsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, Email, ImapHost, ImapPort, Username, Password, IsActive
            FROM Accounts
            ORDER BY IsActive DESC, Email;
            """;

        var accounts = new List<MailAccount>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            accounts.Add(MapAccount(reader));
        }

        return accounts;
    }

    public async Task<MailAccount?> GetActiveAccountAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, Email, ImapHost, ImapPort, Username, Password, IsActive
            FROM Accounts
            ORDER BY IsActive DESC, Email
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapAccount(reader);
    }

    public async Task<MailAccount?> GetAccountAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, Email, ImapHost, ImapPort, Username, Password, IsActive
            FROM Accounts
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return MapAccount(reader);
    }

    public async Task SetActiveAccountAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        await using var resetCommand = connection.CreateCommand();
        resetCommand.Transaction = (SqliteTransaction)transaction;
        resetCommand.CommandText =
            """
            UPDATE Accounts
            SET IsActive = 0;
            """;
        await resetCommand.ExecuteNonQueryAsync();

        await using var activateCommand = connection.CreateCommand();
        activateCommand.Transaction = (SqliteTransaction)transaction;
        activateCommand.CommandText =
            """
            UPDATE Accounts
            SET IsActive = 1
            WHERE Id = $id;
            """;
        activateCommand.Parameters.AddWithValue("$id", id);
        await activateCommand.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public async Task SaveFolderStateAsync(FolderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SyncState (AccountId, Folder, LastUid, UidValidity)
            VALUES ($accountId, $folder, $lastUid, $uidValidity)
            ON CONFLICT(AccountId, Folder) DO UPDATE SET
                LastUid = excluded.LastUid,
                UidValidity = excluded.UidValidity;
            """;
        command.Parameters.AddWithValue("$accountId", state.AccountId);
        command.Parameters.AddWithValue("$folder", state.Folder);
        command.Parameters.AddWithValue("$lastUid", (long)state.LastUid);
        command.Parameters.AddWithValue("$uidValidity", (long)state.UidValidity);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<FolderState?> GetFolderStateAsync(string accountId, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT AccountId, Folder, LastUid, UidValidity
            FROM SyncState
            WHERE AccountId = $accountId
              AND Folder = $folder
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folder", folder);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new FolderState
        {
            AccountId = reader.GetString(0),
            Folder = reader.GetString(1),
            LastUid = ConvertToUInt32(reader.GetValue(2)),
            UidValidity = ConvertToUInt32(reader.GetValue(3))
        };
    }

    public async Task ClearFolderAsync(string accountId, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Emails
            WHERE AccountId = $accountId
              AND Folder = $folder;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folder", folder);

        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Emails (
                Id TEXT PRIMARY KEY,
                AccountId TEXT NOT NULL,
                Subject TEXT,
                FromAddress TEXT,
                ToAddress TEXT,
                Date TEXT,
                Body TEXT,
                TextBody TEXT,
                HtmlBody TEXT,
                HasBody INTEGER NOT NULL,
                IsRead INTEGER NOT NULL DEFAULT 0,
                Folder TEXT NOT NULL,
                Uid INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_Emails_AccountId_Folder_Uid
                ON Emails (AccountId, Folder, Uid);

            CREATE INDEX IF NOT EXISTS IX_Emails_AccountId_Folder_Date_Uid
                ON Emails (AccountId, Folder, Date DESC, Uid DESC);

            CREATE TABLE IF NOT EXISTS Accounts (
                Id TEXT PRIMARY KEY,
                DisplayName TEXT,
                Email TEXT,
                ImapHost TEXT,
                ImapPort INTEGER,
                Username TEXT,
                Password TEXT,
                IsActive INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS SyncState (
                AccountId TEXT NOT NULL,
                Folder TEXT NOT NULL,
                LastUid INTEGER NOT NULL,
                UidValidity INTEGER NOT NULL,
                PRIMARY KEY (AccountId, Folder)
            );
            """;

        command.ExecuteNonQuery();
        EnsureColumnExists(connection, "Emails", "IsRead", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "Emails", "TextBody", "TEXT");
        EnsureColumnExists(connection, "Emails", "HtmlBody", "TEXT");
        EnsureColumnExists(connection, "Accounts", "DisplayName", "TEXT");
        EnsureColumnExists(connection, "Accounts", "Password", "TEXT");
        EnsureColumnExists(connection, "Accounts", "IsActive", "INTEGER NOT NULL DEFAULT 0");
        MigrateLegacyBodyColumn(connection);
        EnsureSingleActiveAccount(connection);
    }

    private async Task<IList<EmailMessage>> GetEmailsInternalAsync(
        string? accountId,
        string folder,
        int limit,
        int offset,
        bool onlyMissingBody)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            onlyMissingBody
                ? """
                  SELECT Id, AccountId, Folder, Uid, Subject, FromAddress, Date, HasBody,
                         CASE
                             WHEN COALESCE(NULLIF(TextBody, ''), '') <> '' THEN TextBody
                             WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN ''
                             WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                                 Body LIKE '%<html%' OR
                                 Body LIKE '%<body%' OR
                                 Body LIKE '%<div%' OR
                                 Body LIKE '%<p%' OR
                                 Body LIKE '%<table%' OR
                                 Body LIKE '%<br%' OR
                                 Body LIKE '%<span%' OR
                                 Body LIKE '%</%'
                             ) THEN ''
                             ELSE COALESCE(Body, '')
                         END,
                         CASE
                             WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN HtmlBody
                             WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                                 Body LIKE '%<html%' OR
                                 Body LIKE '%<body%' OR
                                 Body LIKE '%<div%' OR
                                 Body LIKE '%<p%' OR
                                 Body LIKE '%<table%' OR
                                 Body LIKE '%<br%' OR
                                 Body LIKE '%<span%' OR
                                 Body LIKE '%</%'
                             ) THEN Body
                             ELSE ''
                         END,
                         IsRead
                  FROM Emails
                  WHERE ($accountId IS NULL OR AccountId = $accountId)
                    AND UPPER(Folder) = UPPER($folder)
                    AND HasBody = 0
                  ORDER BY Date DESC, Uid DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT Id, AccountId, Folder, Uid, Subject, FromAddress, Date, HasBody,
                         CASE
                             WHEN COALESCE(NULLIF(TextBody, ''), '') <> '' THEN TextBody
                             WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN ''
                             WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                                 Body LIKE '%<html%' OR
                                 Body LIKE '%<body%' OR
                                 Body LIKE '%<div%' OR
                                 Body LIKE '%<p%' OR
                                 Body LIKE '%<table%' OR
                                 Body LIKE '%<br%' OR
                                 Body LIKE '%<span%' OR
                                 Body LIKE '%</%'
                             ) THEN ''
                             ELSE COALESCE(Body, '')
                         END,
                         CASE
                             WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN HtmlBody
                             WHEN COALESCE(NULLIF(Body, ''), '') <> '' AND (
                                 Body LIKE '%<html%' OR
                                 Body LIKE '%<body%' OR
                                 Body LIKE '%<div%' OR
                                 Body LIKE '%<p%' OR
                                 Body LIKE '%<table%' OR
                                 Body LIKE '%<br%' OR
                                 Body LIKE '%<span%' OR
                                 Body LIKE '%</%'
                             ) THEN Body
                             ELSE ''
                         END,
                         IsRead
                  FROM Emails
                  WHERE ($accountId IS NULL OR AccountId = $accountId)
                    AND UPPER(Folder) = UPPER($folder)
                  ORDER BY Date DESC, Uid DESC
                  LIMIT $limit
                  OFFSET $offset;
                  """;
        command.Parameters.AddWithValue("$accountId", (object?)accountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var emails = new List<EmailMessage>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            emails.Add(MapEmail(reader));
        }

        return emails;
    }

    private static uint ConvertToUInt32(object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return 0;
        }

        return Convert.ToUInt32(value);
    }

    private static EmailMessage MapEmail(SqliteDataReader reader)
    {
        return new EmailMessage
        {
            Id = GetString(reader, 0),
            AccountId = GetString(reader, 1),
            Folder = GetString(reader, 2),
            ImapUid = ConvertToUInt32(reader.GetValue(3)),
            Subject = GetString(reader, 4),
            From = GetString(reader, 5),
            Date = DateTime.TryParse(GetString(reader, 6), out var date) ? date : DateTime.MinValue,
            HasBody = reader.GetInt64(7) == 1,
            TextBody = GetString(reader, 8),
            HtmlBody = GetString(reader, 9),
            IsRead = reader.GetInt64(10) == 1
        };
    }

    private static string GetString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static MailAccount MapAccount(SqliteDataReader reader)
    {
        return new MailAccount
        {
            Id = GetString(reader, 0),
            DisplayName = GetString(reader, 1),
            Email = GetString(reader, 2),
            ImapHost = GetString(reader, 3),
            ImapPort = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Username = GetString(reader, 5),
            Password = GetString(reader, 6),
            IsActive = !reader.IsDBNull(7) && reader.GetInt64(7) == 1
        };
    }

    private static void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = checkCommand.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterCommand.ExecuteNonQuery();
    }

    private static void MigrateLegacyBodyColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Emails
            SET TextBody = CASE
                    WHEN COALESCE(NULLIF(TextBody, ''), '') <> '' THEN TextBody
                    WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN TextBody
                    WHEN Body LIKE '%<html%' OR
                         Body LIKE '%<body%' OR
                         Body LIKE '%<div%' OR
                         Body LIKE '%<p%' OR
                         Body LIKE '%<table%' OR
                         Body LIKE '%<br%' OR
                         Body LIKE '%<span%' OR
                         Body LIKE '%</%' THEN TextBody
                    ELSE Body
                END,
                HtmlBody = CASE
                    WHEN COALESCE(NULLIF(HtmlBody, ''), '') <> '' THEN HtmlBody
                    WHEN Body LIKE '%<html%' OR
                         Body LIKE '%<body%' OR
                         Body LIKE '%<div%' OR
                         Body LIKE '%<p%' OR
                         Body LIKE '%<table%' OR
                         Body LIKE '%<br%' OR
                         Body LIKE '%<span%' OR
                         Body LIKE '%</%' THEN Body
                    ELSE HtmlBody
                END
            WHERE COALESCE(NULLIF(Body, ''), '') <> ''
              AND (
                  COALESCE(NULLIF(TextBody, ''), '') = '' OR
                  COALESCE(NULLIF(HtmlBody, ''), '') = ''
              );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSingleActiveAccount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Accounts
            SET IsActive = 1
            WHERE Id = (
                SELECT Id
                FROM Accounts
                ORDER BY IsActive DESC, Email
                LIMIT 1
            )
              AND NOT EXISTS (
                  SELECT 1
                  FROM Accounts
                  WHERE IsActive = 1
              );
            """;
        command.ExecuteNonQuery();
    }
}
