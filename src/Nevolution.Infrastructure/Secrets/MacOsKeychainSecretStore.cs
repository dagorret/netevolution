using Nevolution.Core.Abstractions;
using System.Diagnostics;
using System.Text;

namespace Nevolution.Infrastructure.Secrets;

public sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string SecurityToolPath = "/usr/bin/security";
    private const string ServiceName = "nevolution";

    public static bool IsAvailable()
    {
        return File.Exists(SecurityToolPath);
    }

    public async Task SetPasswordAsync(string accountId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(password);

        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var result = await RunSecurityAsync(
            "add-generic-password",
            "-a", accountId,
            "-s", ServiceName,
            "-l", $"Nevolution {accountId}",
            "-w", encodedPassword,
            "-U");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to store password for account '{accountId}' in macOS Keychain. {result.StandardError}".Trim());
        }
    }

    public async Task<string?> GetPasswordAsync(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var result = await RunSecurityAsync(
            "find-generic-password",
            "-a", accountId,
            "-s", ServiceName,
            "-w");

        if (result.ExitCode == 0)
        {
            var value = result.StandardOutput.Trim();

            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return TryDecodeBase64(value, out var decodedPassword)
                ? decodedPassword
                : value;
        }

        if (result.ExitCode == 44 || result.StandardError.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new InvalidOperationException($"Unable to read password for account '{accountId}' from macOS Keychain. {result.StandardError}".Trim());
    }

    private static bool TryDecodeBase64(string value, out string decodedValue)
    {
        try
        {
            decodedValue = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            decodedValue = string.Empty;
            return false;
        }
    }

    private static async Task<ProcessResult> RunSecurityAsync(string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = SecurityToolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(command);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
