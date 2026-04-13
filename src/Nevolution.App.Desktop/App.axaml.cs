using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Nevolution.App.Desktop.ViewModels;
using Nevolution.Core;
using Nevolution.Core.Localization;
using Nevolution.Core.Resources;
using Nevolution.Infrastructure.Mail;
using Nevolution.Infrastructure.Persistence;
using System.Diagnostics;
using System.Globalization;

namespace Nevolution.App.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        Console.WriteLine("Desktop startup: App.Initialize");
        AvaloniaXamlLoader.Load(this);
        Console.WriteLine("Desktop startup: App.Initialize completed");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("Desktop startup: OnFrameworkInitializationCompleted");
        var startupStopwatch = Stopwatch.StartNew();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                Console.WriteLine("Desktop startup: creating services");
                var dataDir = ResolveDataDirectory();
                Directory.CreateDirectory(dataDir);
                AppCulture.SetCulture(AppCulturePreferences.LoadPreferredCulture(dataDir));

                var dbPath = Path.Combine(dataDir, "mail.db");
                var repository = new SqliteEmailRepository(dbPath);
                var folderMailClient = new MailKitClient();
                var syncMailClient = new MailKitClient();
                var bodyMailClient = new MailKitClient();
                var syncService = new SyncService(syncMailClient, repository);
                var bodySyncService = new BackgroundBodySyncService(bodyMailClient, repository);
                var mainViewModel = new MainViewModel(repository, folderMailClient, syncService, bodySyncService, dataDir);

                Console.WriteLine("Desktop startup: creating MainWindow");
                var window = new MainWindow
                {
                    DataContext = mainViewModel
                };

                window.Opened += (_, _) =>
                {
                    var windowOpenedElapsedMs = startupStopwatch.ElapsedMilliseconds;
                    Console.WriteLine($"Desktop startup: MainWindow opened elapsedMs={windowOpenedElapsedMs}");
                    mainViewModel.NotifyWindowOpened(windowOpenedElapsedMs);
                    _ = RunInitialLoadAsync(mainViewModel);
                };

                desktop.MainWindow = window;
                Console.WriteLine("Desktop startup: MainWindow assigned");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Desktop startup: window creation failed: {exception}");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
        Console.WriteLine("Desktop startup: OnFrameworkInitializationCompleted completed");
    }

    private static async Task RunInitialLoadAsync(MainViewModel mainViewModel)
    {
        Console.WriteLine("Desktop startup: initial load queued");

        try
        {
            await mainViewModel.InitializeAsync();
            Console.WriteLine("Desktop startup: initial load completed");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Desktop startup: initial load failed: {exception}");
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
}
