using Nevolution.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nevolution.Core;

public sealed class ImapOperationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);

    public async Task RunAsync(
        MailAccount account,
        string operation,
        string? folder,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        await RunAsync<object?>(
            account,
            operation,
            folder,
            async token =>
            {
                await action(token);
                return null;
            },
            cancellationToken);
    }

    public async Task<T> RunAsync<T>(
        MailAccount account,
        string operation,
        string? folder,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);

        var lockKey = string.IsNullOrWhiteSpace(account.Id) ? account.Email : account.Id;
        var gate = _accountLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        var waitStopwatch = Stopwatch.StartNew();

        Console.WriteLine(
            $"[IMAP] wait operation={operation} accountId={account.Id} folder={folder ?? "-"}");
        await gate.WaitAsync(cancellationToken);
        waitStopwatch.Stop();

        Console.WriteLine(
            $"[IMAP] acquire operation={operation} accountId={account.Id} folder={folder ?? "-"} waitMs={waitStopwatch.ElapsedMilliseconds}");

        var runStopwatch = Stopwatch.StartNew();

        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
            Console.WriteLine(
                $"[IMAP] release operation={operation} accountId={account.Id} folder={folder ?? "-"} durationMs={runStopwatch.ElapsedMilliseconds}");
        }
    }
}
