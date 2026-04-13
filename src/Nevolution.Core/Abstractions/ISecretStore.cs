namespace Nevolution.Core.Abstractions;

public interface ISecretStore
{
    Task SetPasswordAsync(string accountId, string password);

    Task<string?> GetPasswordAsync(string accountId);
}
