using Nevolution.Core.Models;

namespace Nevolution.Core.Abstractions;

public interface IEmailRepository
{
    Task SaveHeadersAsync(IEnumerable<EmailMessage> emails);

    Task<int> SoftDeleteMissingEmailsAsync(string accountId, string folder, IReadOnlyCollection<uint> serverUids);

    Task<int> RestoreSoftDeletedEmailsAsync(string accountId, string folder, IReadOnlyCollection<uint> serverUids);

    Task UpdateBodyAsync(string id, EmailBody body);

    Task MarkBodyUnavailableAsync(string id);

    Task MarkAsReadAsync(string id);

    Task<uint> GetLastUidAsync(string accountId, string folder);

    Task<int> GetVisibleEmailCountAsync(string accountId, string folder);

    Task<FolderLoadStats> GetFolderLoadStatsAsync(string accountId, string folder);

    Task<IList<EmailMessage>> GetEmailsAsync(string? accountId, string folder, int limit = 100, int offset = 0);

    Task<List<EmailMessage>> GetEmailsWithoutBodyAsync(int limit);

    Task<IList<EmailMessage>> GetEmailsWithoutBodyAsync(string accountId, string folder, int limit = 100);

    Task<EmailMessage?> GetEmailAsync(string id);

    Task SaveAccountAsync(MailAccount account);

    Task<IList<MailAccount>> GetAccountsAsync();

    Task<MailAccount?> GetActiveAccountAsync();

    Task<IList<string>> GetKnownFoldersAsync(string accountId);

    Task<MailAccount?> GetAccountAsync(string id);

    Task SetActiveAccountAsync(string id);

    Task SetPreferredFolderAsync(string accountId, string folder);

    Task SaveFolderStateAsync(FolderState state);

    Task<FolderState?> GetFolderStateAsync(string accountId, string folder);

    Task ClearFolderAsync(string accountId, string folder);
}
