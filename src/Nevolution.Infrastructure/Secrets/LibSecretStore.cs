using DBus.Services.Secrets;
using Nevolution.Core.Abstractions;
using System.Text;
using Tmds.DBus.Protocol;

namespace Nevolution.Infrastructure.Secrets;

public sealed class LibSecretStore : ISecretStore
{
    private const string CollectionName = "nevolution";
    private const string AccountIdAttribute = "account-id";
    private const string ContentType = "text/plain; charset=utf-8";

    public async Task SetPasswordAsync(string accountId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(password);

        var collection = await GetCollectionAsync(createIfMissing: true);
        if (collection is null)
        {
            throw new InvalidOperationException($"Unable to open the '{CollectionName}' secret collection.");
        }

        var attributes = CreateAttributes(accountId);
        var item = await collection.CreateItemAsync(
            $"Nevolution {accountId}",
            attributes,
            Encoding.UTF8.GetBytes(password),
            ContentType,
            replace: true);

        if (item is null)
        {
            throw new InvalidOperationException($"Unable to store password for account '{accountId}' in libsecret.");
        }
    }

    public async Task<string?> GetPasswordAsync(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var collection = await GetCollectionAsync(createIfMissing: false);

        if (collection is null)
        {
            return null;
        }

        var items = await collection.SearchItemsAsync(CreateAttributes(accountId));
        var item = items.FirstOrDefault();

        if (item is null)
        {
            return null;
        }

        var secret = await item.GetSecretAsync();
        return Encoding.UTF8.GetString(secret);
    }

    private static Dictionary<string, string> CreateAttributes(string accountId) =>
        new()
        {
            [AccountIdAttribute] = accountId
        };

    private static async Task<Collection?> GetCollectionAsync(bool createIfMissing)
    {
        var secretService = await SecretService.ConnectAsync(EncryptionType.Dh);
        var collections = await secretService.GetAllCollectionsAsync();

        foreach (var candidate in collections)
        {
            var label = await candidate.GetLabelAsync();

            if (string.Equals(label, CollectionName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        var collection = await secretService.GetCollectionByAliasAsync(CollectionName);

        if (collection is not null)
        {
            return collection;
        }

        var defaultCollection = await secretService.GetDefaultCollectionAsync();
        if (defaultCollection is not null)
        {
            return defaultCollection;
        }

        if (!createIfMissing)
        {
            return null;
        }

        try
        {
            return await secretService.CreateCollectionAsync(CollectionName, CollectionName);
        }
        catch (DBusException exception) when (exception.Message.Contains("Only the 'default' alias is supported", StringComparison.OrdinalIgnoreCase))
        {
            SecretStoreLog.Info("Secret Service does not support custom collection aliases. Falling back to the default collection.");
            return await secretService.GetDefaultCollectionAsync();
        }
    }
}
