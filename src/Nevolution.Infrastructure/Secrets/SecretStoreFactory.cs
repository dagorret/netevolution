using Nevolution.Core.Abstractions;
using System.Runtime.InteropServices;

namespace Nevolution.Infrastructure.Secrets;

public static class SecretStoreFactory
{
    public const string SecretStoreOverrideVariableName = "NEVOLUTION_SECRET_STORE";

    public static ISecretStore CreateDefault()
    {
        var environmentStore = new EnvironmentSecretStore();
        var overrideValue = Environment.GetEnvironmentVariable(SecretStoreOverrideVariableName);

        if (TryCreateOverrideStore(overrideValue, environmentStore, out var overrideStore))
        {
            return overrideStore;
        }

        if (OperatingSystem.IsLinux())
        {
            return CreateLinuxStore(environmentStore);
        }

        if (OperatingSystem.IsWindows())
        {
            SecretStoreLog.Info("Resolved ISecretStore: WindowsCredentialStore with EnvironmentSecretStore fallback.");
            return new FallbackSecretStore(new WindowsCredentialStore(), environmentStore);
        }

        if (OperatingSystem.IsMacOS())
        {
            return CreateMacOsStore(environmentStore);
        }

        SecretStoreLog.Info($"Resolved ISecretStore: EnvironmentSecretStore (unsupported OS: {RuntimeInformation.OSDescription}).");
        return environmentStore;
    }

    private static bool TryCreateOverrideStore(string? overrideValue, ISecretStore environmentStore, out ISecretStore store)
    {
        switch (overrideValue?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "auto":
                store = environmentStore;
                return false;
            case "environment":
                SecretStoreLog.Info("Resolved ISecretStore: EnvironmentSecretStore (explicit override).");
                store = environmentStore;
                return true;
            case "linux":
                SecretStoreLog.Info("Resolved ISecretStore: LibSecretStore with EnvironmentSecretStore fallback (explicit override).");
                store = new FallbackSecretStore(new LibSecretStore(), environmentStore);
                return true;
            case "windows":
                SecretStoreLog.Info("Resolved ISecretStore: WindowsCredentialStore with EnvironmentSecretStore fallback (explicit override).");
                store = new FallbackSecretStore(new WindowsCredentialStore(), environmentStore);
                return true;
            case "macos":
            case "osx":
                SecretStoreLog.Info("Resolved ISecretStore: MacOsKeychainSecretStore with EnvironmentSecretStore fallback (explicit override).");
                store = new FallbackSecretStore(new MacOsKeychainSecretStore(), environmentStore);
                return true;
            default:
                SecretStoreLog.Info($"Unknown {SecretStoreOverrideVariableName} value '{overrideValue}'. Falling back to auto detection.");
                store = environmentStore;
                return false;
        }
    }

    private static ISecretStore CreateLinuxStore(ISecretStore environmentStore)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            SecretStoreLog.Info("Resolved ISecretStore: EnvironmentSecretStore (Linux without DBUS session).");
            return environmentStore;
        }

        SecretStoreLog.Info("Resolved ISecretStore: LibSecretStore with EnvironmentSecretStore fallback.");
        return new FallbackSecretStore(new LibSecretStore(), environmentStore);
    }

    private static ISecretStore CreateMacOsStore(ISecretStore environmentStore)
    {
        if (!MacOsKeychainSecretStore.IsAvailable())
        {
            SecretStoreLog.Info("Resolved ISecretStore: EnvironmentSecretStore (macOS keychain tool unavailable).");
            return environmentStore;
        }

        SecretStoreLog.Info("Resolved ISecretStore: MacOsKeychainSecretStore with EnvironmentSecretStore fallback.");
        return new FallbackSecretStore(new MacOsKeychainSecretStore(), environmentStore);
    }
}
