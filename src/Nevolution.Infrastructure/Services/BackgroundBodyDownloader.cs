using Nevolution.Core;
using Nevolution.Core.Models;

namespace Nevolution.Infrastructure.Services;

public sealed class BackgroundBodyDownloader
{
    private readonly BackgroundBodySyncService _backgroundBodySyncService;
    private readonly MailAccount _account;
    private readonly string _folder;

    public BackgroundBodyDownloader(
        BackgroundBodySyncService backgroundBodySyncService,
        MailAccount account,
        string folder)
    {
        _backgroundBodySyncService = backgroundBodySyncService;
        _account = account;
        _folder = folder;
    }

    public Task StartAsync(CancellationToken token)
    {
        return _backgroundBodySyncService.RunAsync(_account, _folder, cancellationToken: token);
    }
}
