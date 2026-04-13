using Nevolution.Core.Abstractions;

namespace Nevolution.Infrastructure.Secrets;

public sealed class EnvironmentSecretStore : ISecretStore
{
    public const string PasswordVariableName = "NEVOLUTION_PASSWORD";

    public Task SetPasswordAsync(string accountId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(password);

        SecretStoreLog.Info("EnvironmentSecretStore is read-only. Password was not persisted and must be provided through NEVOLUTION_PASSWORD.");
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var password = Environment.GetEnvironmentVariable(PasswordVariableName);
        return Task.FromResult<string?>(string.IsNullOrWhiteSpace(password) ? null : password);
    }
}
