using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nevolution.App.Desktop.ViewModels;
using Nevolution.Core;
using Nevolution.Core.Abstractions;
using Nevolution.Core.Localization;
using Nevolution.Core.Resources;
using Nevolution.Infrastructure.DependencyInjection;
using Nevolution.Infrastructure.Mail;
using Nevolution.Infrastructure.Persistence;
using System.Globalization;

namespace Nevolution.App.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        Console.WriteLine("Desktop startup: App.Initialize");
        AvaloniaXamlLoader.Load(this);
        Console.WriteLine("Desktop startup: App.Initialize completed");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("[Startup] Framework initialization started");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var dataDir = ResolveDataDirectory();
                Directory.CreateDirectory(dataDir);
                AppCulture.SetCulture(AppCulturePreferences.LoadPreferredCulture(dataDir));

                var dbPath = Path.Combine(dataDir, "mail.db");
                _services = ConfigureServices(dbPath, dataDir).BuildServiceProvider();
                var mainViewModel = _services.GetRequiredService<MainViewModel>();
                desktop.Exit += (_, _) => _services?.Dispose();

                var window = new MainWindow
                {
                    DataContext = mainViewModel
                };

                window.Opened += (_, _) =>
                {
                    var windowOpenedElapsedMs = Program.StartupStopwatch.ElapsedMilliseconds;
                    Console.WriteLine($"[Startup] Window shown: {windowOpenedElapsedMs} ms");
                    mainViewModel.NotifyWindowOpened(windowOpenedElapsedMs);
                    Dispatcher.UIThread.Post(
                        () => _ = RunInitialLoadAsync(mainViewModel),
                        DispatcherPriority.Background);
                };

                desktop.MainWindow = window;
                Console.WriteLine("[Startup] MainWindow assigned");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Startup] Window creation failed: {exception}");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
        Console.WriteLine("[Startup] Framework initialization completed");
    }

    private static async Task RunInitialLoadAsync(MainViewModel mainViewModel)
    {
        Console.WriteLine("[Startup] Initial load queued after first render");

        try
        {
            await mainViewModel.InitializeAsync();
            Console.WriteLine("[Startup] Initial load completed");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Startup] Initial load failed: {exception}");
            mainViewModel.SetStartupError(Strings.Status_InitialLoadFailedImap);
        }
    }

    private static string ResolveDataDirectory()
    {
        var dataDir = Environment.GetEnvironmentVariable("NEVOLUTION_DATA_PATH");

        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new Exception("Project root not found");
        }

        return Path.Combine(dir.FullName, "data");
    }

    private static ServiceCollection ConfigureServices(string databasePath, string dataDir)
    {
        var services = new ServiceCollection();

        services.AddNevolutionSecretStore();
        services.AddSingleton(sp => new SqliteEmailRepository(databasePath, sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<ImapOperationCoordinator>();
        services.AddSingleton(sp => new SyncService(
            new MailKitClient(),
            sp.GetRequiredService<SqliteEmailRepository>(),
            sp.GetRequiredService<ImapOperationCoordinator>()));
        services.AddSingleton(sp => new BackgroundBodySyncService(
            new MailKitClient(),
            sp.GetRequiredService<SqliteEmailRepository>(),
            sp.GetRequiredService<ImapOperationCoordinator>()));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<SqliteEmailRepository>(),
            new MailKitClient(),
            sp.GetRequiredService<ImapOperationCoordinator>(),
            sp.GetRequiredService<SyncService>(),
            sp.GetRequiredService<BackgroundBodySyncService>(),
            dataDir));

        return services;
    }
}
