using Nevolution.Core.Abstractions;

namespace Nevolution.Infrastructure.Secrets;

public sealed class FallbackSecretStore : ISecretStore
{
    private readonly ISecretStore _primaryStore;
    private readonly IReadOnlyList<ISecretStore> _stores;

    public FallbackSecretStore(ISecretStore primaryStore, params ISecretStore[] fallbackStores)
    {
        ArgumentNullException.ThrowIfNull(primaryStore);
        ArgumentNullException.ThrowIfNull(fallbackStores);

        _primaryStore = primaryStore;
        _stores = new[] { primaryStore }.Concat(fallbackStores.Where(static store => store is not null)).ToArray();
    }

    public async Task SetPasswordAsync(string accountId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(password);

        try
        {
            await _primaryStore.SetPasswordAsync(accountId, password);
        }
        catch (Exception exception)
        {
            SecretStoreLog.Info($"Primary secret store '{_primaryStore.GetType().Name}' failed to persist password: {exception.Message}");
            throw;
        }
    }

    public async Task<string?> GetPasswordAsync(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        foreach (var store in _stores)
        {
            try
            {
                var password = await store.GetPasswordAsync(accountId);

                if (!string.IsNullOrWhiteSpace(password))
                {
                    if (!ReferenceEquals(store, _primaryStore))
                    {
                        SecretStoreLog.Info($"Recovered password using fallback secret store '{store.GetType().Name}'.");
                    }

                    return password;
                }
            }
            catch (Exception exception)
            {
                SecretStoreLog.Info($"Secret store '{store.GetType().Name}' failed during password lookup: {exception.Message}");
            }
        }

        return null;
    }
}
