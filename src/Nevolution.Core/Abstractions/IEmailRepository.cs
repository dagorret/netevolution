using Nevolution.Core.Models;

namespace Nevolution.Core.Abstractions;

public interface IEmailRepository
{
    Task SaveHeadersAsync(IEnumerable<EmailMessage> emails);

    Task UpdateBodyAsync(string id, EmailBody body);

    Task MarkAsReadAsync(string id);

    Task<uint> GetLastUidAsync(string accountId, string folder);

    Task<IList<EmailMessage>> GetEmailsAsync(string? accountId, string folder, int limit = 100, int offset = 0);

    Task<List<EmailMessage>> GetEmailsWithoutBodyAsync(int limit);

    Task<IList<EmailMessage>> GetEmailsWithoutBodyAsync(string accountId, string folder, int limit = 100);

    Task<EmailMessage?> GetEmailAsync(string id);

    Task SaveAccountAsync(MailAccount account);

    Task<IList<MailAccount>> GetAccountsAsync();

    Task<MailAccount?> GetActiveAccountAsync();

    Task<MailAccount?> GetAccountAsync(string id);

    Task SetActiveAccountAsync(string id);

    Task SaveFolderStateAsync(FolderState state);

    Task<FolderState?> GetFolderStateAsync(string accountId, string folder);

    Task ClearFolderAsync(string accountId, string folder);
}
